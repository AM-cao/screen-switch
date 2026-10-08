// ScreenSwitch 菜单栏程序：菜单栏图标 + 随状态变化的菜单（设计文档第 8 节）+ 全局快捷键 + 通知。
// 只做入口：读 ~/screenctl/state 显示状态，用 screenctl.sh menu-info 取对方和各设备的名字，
// 执行 screenctl.sh give peer | self | code <n>，切换逻辑在守护进程里。
// 快捷键用 Carbon RegisterEventHotKey（公开 API，无需辅助功能权限）。
// 通知：守护进程把文字追加到 ~/screenctl/notify.queue，本程序取走后用 UserNotifications 弹出；
// launchd 后台进程自己用 osascript 发的通知会被系统丢弃。
// 由 mac/install.sh 编译：swiftc -swift-version 5 -O ScreenSwitchMenu.swift -o ScreenSwitch
// 自检（不显示图标，打印菜单与快捷键注册结果）：ScreenSwitch --self-test

import AppKit
import Carbon.HIToolbox
import UserNotifications

let screenctlDir = ProcessInfo.processInfo.environment["SCREENCTL_DIR"]
    ?? (NSHomeDirectory() as NSString).appendingPathComponent("screenctl")
let scriptPath = screenctlDir + "/screenctl.sh"
let statePath = screenctlDir + "/state"
let busyPath = screenctlDir + "/busy"
let logPath = screenctlDir + "/screenctl.log"
let notifyQueuePath = screenctlDir + "/notify.queue"
let menuPidPath = screenctlDir + "/menu.pid"
let displayHelperPath = screenctlDir + "/bin/display-helper"
// 只有在 .app 包里运行时才能用 UserNotifications，直接运行裸二进制会崩
let inAppBundle = Bundle.main.bundleIdentifier != nil
let staleSeconds: TimeInterval = 10   // 与 4.5 一致：状态文件超过 10 秒视为守护进程未运行

enum Ownership { case owned, notOwned, daemonDown }

func readOwnership() -> Ownership {
    guard let text = try? String(contentsOfFile: statePath, encoding: .utf8) else { return .daemonDown }
    let parts = text.split(whereSeparator: { $0 == " " || $0 == "\n" })
    guard parts.count >= 2, let ts = TimeInterval(parts[1]),
          Date().timeIntervalSince1970 - ts <= staleSeconds else { return .daemonDown }
    return parts[0] == "owned=1" ? .owned : .notOwned
}

// screenctl.sh menu-info 的输出（设计文档第 8 节）：名字、设备、快捷键与布局模式。
// 配置由脚本解释（含旧版配置的名字映射），本程序不自己解析 config.sh
struct Device {
    let code: String
    let name: String
    let hotkey: String
}

struct MenuInfo {
    var peer = ""               // 对方电脑的名字；没有配置对方时为空
    var layout = "second"       // second = 本机另有一块屏；none = 只有主屏，不显示布局相关的菜单
    var secondBuiltin = true    // 另一块屏是笔记本自带屏时才提供"仅大屏"
    var ownedLayout = "extend"
    var autoSave = true
    var hotkeyPeer = ""
    var devices: [Device] = []
}

func readMenuInfo() -> MenuInfo {
    var info = MenuInfo()
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/bin/bash")
    p.arguments = [scriptPath, "menu-info"]
    let pipe = Pipe()
    p.standardOutput = pipe
    p.standardError = FileHandle.nullDevice
    guard (try? p.run()) != nil else { return info }
    let data = pipe.fileHandleForReading.readDataToEndOfFile()
    p.waitUntilExit()
    guard let text = String(data: data, encoding: .utf8) else { return info }
    for line in text.split(separator: "\n") {
        guard let eq = line.firstIndex(of: "=") else { continue }
        let key = String(line[..<eq])
        let value = String(line[line.index(after: eq)...])
        switch key {
        case "peer": info.peer = value
        case "layout": info.layout = value
        case "second_builtin": info.secondBuiltin = value == "1"
        case "owned_layout": info.ownedLayout = value
        case "auto_save": info.autoSave = value != "0"
        case "hotkey_peer": info.hotkeyPeer = value
        case "device":
            let f = value.split(separator: "\t", omittingEmptySubsequences: false).map(String.init)
            if f.count >= 2, !f[0].isEmpty {
                info.devices.append(Device(code: f[0], name: f[1], hotkey: f.count > 2 ? f[2] : ""))
            }
        default: break
        }
    }
    return info
}

struct HotKey {
    let keyCode: UInt32
    let carbonModifiers: UInt32
    let menuKey: String
    let menuModifiers: NSEvent.ModifierFlags
    let display: String
}

let keyCodes: [String: Int] = [
    "0": kVK_ANSI_0, "1": kVK_ANSI_1, "2": kVK_ANSI_2, "3": kVK_ANSI_3, "4": kVK_ANSI_4,
    "5": kVK_ANSI_5, "6": kVK_ANSI_6, "7": kVK_ANSI_7, "8": kVK_ANSI_8, "9": kVK_ANSI_9,
    "a": kVK_ANSI_A, "b": kVK_ANSI_B, "c": kVK_ANSI_C, "d": kVK_ANSI_D, "e": kVK_ANSI_E,
    "f": kVK_ANSI_F, "g": kVK_ANSI_G, "h": kVK_ANSI_H, "i": kVK_ANSI_I, "j": kVK_ANSI_J,
    "k": kVK_ANSI_K, "l": kVK_ANSI_L, "m": kVK_ANSI_M, "n": kVK_ANSI_N, "o": kVK_ANSI_O,
    "p": kVK_ANSI_P, "q": kVK_ANSI_Q, "r": kVK_ANSI_R, "s": kVK_ANSI_S, "t": kVK_ANSI_T,
    "u": kVK_ANSI_U, "v": kVK_ANSI_V, "w": kVK_ANSI_W, "x": kVK_ANSI_X, "y": kVK_ANSI_Y,
    "z": kVK_ANSI_Z,
]

// "ctrl+opt+1" → HotKey；至少一个修饰键
func parseHotKey(_ spec: String) -> HotKey? {
    let parts = spec.lowercased().split(separator: "+").map { $0.trimmingCharacters(in: .whitespaces) }
    guard let key = parts.last, let code = keyCodes[key], parts.count >= 2 else { return nil }
    var carbon: UInt32 = 0
    var flags: NSEvent.ModifierFlags = []
    var display = ""
    var seen = Set<String>()
    for m in parts.dropLast() {
        switch m {
        case "ctrl", "control": seen.insert("⌃"); carbon |= UInt32(controlKey); flags.insert(.control)
        case "opt", "option", "alt": seen.insert("⌥"); carbon |= UInt32(optionKey); flags.insert(.option)
        case "shift": seen.insert("⇧"); carbon |= UInt32(shiftKey); flags.insert(.shift)
        case "cmd", "command": seen.insert("⌘"); carbon |= UInt32(cmdKey); flags.insert(.command)
        default: return nil
        }
    }
    for s in ["⌃", "⌥", "⇧", "⌘"] where seen.contains(s) { display += s }
    return HotKey(keyCode: UInt32(code), carbonModifiers: carbon, menuKey: key,
                  menuModifiers: flags, display: display + key.uppercased())
}

var appDelegate: AppDelegate!

// 写到 stderr（launchd 重定向到 ~/screenctl/menu.log），不经缓冲
func logLine(_ text: String) {
    let f = DateFormatter()
    f.dateFormat = "yyyy-MM-dd HH:mm:ss"
    FileHandle.standardError.write("\(f.string(from: Date())) \(text)\n".data(using: .utf8)!)
}

func authName(_ s: UNAuthorizationStatus) -> String {
    switch s {
    case .notDetermined: return "notDetermined"
    case .denied: return "denied"
    case .authorized: return "authorized"
    case .provisional: return "provisional"
    default: return "other(\(s.rawValue))"
    }
}

let hotKeyHandler: EventHandlerUPP = { _, event, _ in
    var id = EventHotKeyID()
    let status = GetEventParameter(event, EventParamName(kEventParamDirectObject),
                                   EventParamType(typeEventHotKeyID), nil,
                                   MemoryLayout<EventHotKeyID>.size, nil, &id)
    if status == noErr {
        let n = Int(id.id)
        DispatchQueue.main.async { appDelegate.hotKeyPressed(n) }
    }
    return noErr
}

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate, UNUserNotificationCenterDelegate {
    var statusItem: NSStatusItem?
    let menu = NSMenu()
    var hotKeys: [Int: HotKey] = [:]      // 1 = 拿回主屏 / 切给对方（随状态）；100 + i = 切给第 i 个设备
    var hotKeyDevices: [Int: String] = [:]   // 快捷键编号 → 设备编码（注册时的设备列表）
    var hotKeyRefs: [EventHotKeyRef?] = []
    var hotKeyErrors: [String] = []
    var running: Set<Process> = []
    var menuInfo = MenuInfo()
    var notifyStatus: UNAuthorizationStatus?

    func applicationDidFinishLaunching(_ notification: Notification) {
        let others = NSRunningApplication.runningApplications(
            withBundleIdentifier: Bundle.main.bundleIdentifier ?? "")
            .filter { $0.processIdentifier != getpid() }
        if !others.isEmpty { NSApp.terminate(nil); return }

        menu.delegate = self
        menu.autoenablesItems = false
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.menu = menu
        statusItem = item
        updateIcon()
        Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            self?.updateIcon()
            self?.refreshNotifyStatus()
        }
        registerHotKeys()
        try? "\(getpid())\n".write(toFile: menuPidPath, atomically: true, encoding: .utf8)
        if inAppBundle {
            let center = UNUserNotificationCenter.current()
            center.delegate = self
            center.requestAuthorization(options: [.alert, .sound]) { [weak self] granted, error in
                logLine("notify auth: granted=\(granted) error=\(error.map { "\($0)" } ?? "nil")")
                DispatchQueue.main.async { self?.refreshNotifyStatus() }
            }
        }
        Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in self?.drainNotifyQueue() }
    }

    func applicationWillTerminate(_ notification: Notification) {
        if (try? String(contentsOfFile: menuPidPath, encoding: .utf8))?
            .trimmingCharacters(in: .whitespacesAndNewlines) == "\(getpid())" {
            try? FileManager.default.removeItem(atPath: menuPidPath)
        }
    }

    // 先改名再读，守护进程之后追加的内容会写进新文件，不会丢
    func drainNotifyQueue() {
        let fm = FileManager.default
        guard fm.fileExists(atPath: notifyQueuePath) else { return }
        let taken = notifyQueuePath + ".taken"
        try? fm.removeItem(atPath: taken)
        guard (try? fm.moveItem(atPath: notifyQueuePath, toPath: taken)) != nil else { return }
        let text = (try? String(contentsOfFile: taken, encoding: .utf8)) ?? ""
        try? fm.removeItem(atPath: taken)
        for line in text.split(separator: "\n") where !line.isEmpty { postNotification(String(line)) }
    }

    func postNotification(_ body: String) {
        guard inAppBundle else { print("notify: \(body)"); return }
        let content = UNMutableNotificationContent()
        content.title = "屏幕切换"
        content.body = body
        let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
        UNUserNotificationCenter.current().add(request) { error in
            if let error = error { logLine("notify failed: \(error) body=\(body)") }
        }
    }

    func refreshNotifyStatus() {
        guard inAppBundle else { return }
        UNUserNotificationCenter.current().getNotificationSettings { [weak self] settings in
            DispatchQueue.main.async { self?.notifyStatus = settings.authorizationStatus }
        }
    }

    // 菜单栏程序没有"前台"概念，让通知始终以横幅显示
    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .list])
    }

    func updateIcon() {
        guard let button = statusItem?.button else { return }
        let own = readOwnership()
        let symbol = own == .daemonDown ? "exclamationmark.triangle" : "display"
        let image = NSImage(systemSymbolName: symbol, accessibilityDescription: "屏幕切换")
        image?.isTemplate = true
        button.image = image
        button.appearsDisabled = own == .notOwned
        button.toolTip = "屏幕切换：" + statusText(own)
    }

    func statusText(_ own: Ownership) -> String {
        switch own {
        case .owned: return "主屏在本机"
        case .notOwned: return "主屏不在本机"
        case .daemonDown: return "守护进程未运行"
        }
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        buildMenu(menu)
    }

    func buildMenu(_ menu: NSMenu) {
        menu.removeAllItems()
        let own = readOwnership()
        var status = statusText(own)
        if FileManager.default.fileExists(atPath: busyPath) { status += "，正在切换中…" }
        addInfo(menu, status)
        if !FileManager.default.isExecutableFile(atPath: scriptPath) { addInfo(menu, "未找到 " + scriptPath) }
        menu.addItem(.separator())
        let info = readMenuInfo()
        menuInfo = info
        // 没有配置对方电脑时不显示"切给对方"；"拿回主屏"仍然有（显示器肯听的话可以直接切回来）
        let peerTitle = "切给 " + info.peer
        switch own {
        case .owned:
            if !info.peer.isEmpty { addAction(menu, peerTitle, ["peer"], hotKeys[1]) }
        case .notOwned:
            addAction(menu, "拿回主屏", ["self"], hotKeys[1])
        case .daemonDown:
            addAction(menu, "拿回主屏", ["self"], hotKeys[1])
            if !info.peer.isEmpty { addAction(menu, peerTitle, ["peer"], nil) }
        }
        for d in info.devices {
            let id = hotKeyDevices.first(where: { $0.value == d.code })?.key
            addAction(menu, "切给 " + d.name + "（请先打开设备）", ["code", d.code], id.flatMap { hotKeys[$0] })
        }
        menu.addItem(.separator())
        // 只有主屏的电脑不调整布局，这几项都不显示
        if info.layout != "none" {
            addInfo(menu, "拿回后的布局")
            let external = info.ownedLayout == "external" && info.secondBuiltin
            let extendItem = NSMenuItem(title: info.secondBuiltin ? "扩展（大屏为主）" : "扩展",
                                        action: #selector(setOwnedLayout(_:)), keyEquivalent: "")
            extendItem.target = self
            extendItem.representedObject = "extend"
            extendItem.state = external ? .off : .on
            extendItem.indentationLevel = 1
            menu.addItem(extendItem)
            // "仅大屏"靠关闭笔记本自带屏实现，另一块屏是外接屏时不提供
            if info.secondBuiltin {
                let externalItem = NSMenuItem(title: "仅大屏（关闭笔记本屏）", action: #selector(setOwnedLayout(_:)), keyEquivalent: "")
                externalItem.target = self
                externalItem.representedObject = "external"
                externalItem.state = external ? .on : .off
                externalItem.indentationLevel = 1
                externalItem.isEnabled = FileManager.default.isExecutableFile(atPath: displayHelperPath)
                menu.addItem(externalItem)
            }
            let autoSave = NSMenuItem(title: "自动记住屏幕排列", action: #selector(toggleAutoSave), keyEquivalent: "")
            autoSave.target = self
            autoSave.state = info.autoSave ? .on : .off
            menu.addItem(autoSave)
            let save = NSMenuItem(title: "立即保存当前排列", action: #selector(saveLayout), keyEquivalent: "")
            save.target = self
            save.isEnabled = own == .owned
            menu.addItem(save)
            menu.addItem(.separator())
        }
        for e in hotKeyErrors { addInfo(menu, e) }
        if notifyStatus == .denied { addInfo(menu, "通知未开启：系统设置 → 通知 → 屏幕切换") }
        let log = NSMenuItem(title: "打开日志", action: #selector(openLog), keyEquivalent: "")
        log.target = self
        menu.addItem(log)
        let quit = NSMenuItem(title: "退出", action: #selector(quit), keyEquivalent: "")
        quit.target = self
        menu.addItem(quit)
    }

    func addInfo(_ menu: NSMenu, _ text: String) {
        let item = NSMenuItem(title: text, action: nil, keyEquivalent: "")
        item.isEnabled = false
        menu.addItem(item)
    }

    func addAction(_ menu: NSMenu, _ title: String, _ target: [String], _ hk: HotKey?) {
        let item = NSMenuItem(title: title, action: #selector(menuAction(_:)), keyEquivalent: hk?.menuKey ?? "")
        if let hk = hk { item.keyEquivalentModifierMask = hk.menuModifiers }
        item.target = self
        item.representedObject = target
        menu.addItem(item)
    }

    @objc func menuAction(_ sender: NSMenuItem) {
        if let target = sender.representedObject as? [String] { give(target) }
    }

    // 快捷键 1：占有时切给对方，未占有时拿回；100 + i：切给第 i 个设备
    func hotKeyPressed(_ id: Int) {
        if id == 1 {
            give(readOwnership() == .owned ? ["peer"] : ["self"])
        } else if let code = hotKeyDevices[id] {
            give(["code", code])
        }
    }

    // target = ["peer"] | ["self"] | ["code", "<编码>"]
    func give(_ target: [String]) {
        runScript(["give"] + target)
    }

    @objc func toggleAutoSave() {
        runScript(["set", "AUTO_SAVE_LAYOUT", menuInfo.autoSave ? "0" : "1"])
    }

    @objc func setOwnedLayout(_ sender: NSMenuItem) {
        if let value = sender.representedObject as? String { runScript(["set", "OWNED_LAYOUT", value]) }
    }

    @objc func saveLayout() {
        runScript(["save-layout"])
    }

    func runScript(_ args: [String]) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/bash")
        p.arguments = [scriptPath] + args
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        p.terminationHandler = { [weak self] proc in
            DispatchQueue.main.async { self?.running.remove(proc) }
        }
        do {
            try p.run()
            running.insert(p)
        } catch {
            NSSound.beep()
        }
    }

    func registerHotKeys() {
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), hotKeyHandler, 1, &spec, nil, nil)
        // 默认不注册任何快捷键：配置里给对方（HOTKEY_PEER）或某个设备（HOTKEY_DEVICES）填了才注册
        let info = readMenuInfo()
        menuInfo = info
        var wanted: [(Int, String, String)] = [(1, "HOTKEY_PEER", info.hotkeyPeer)]
        for (i, d) in info.devices.enumerated() {
            wanted.append((100 + i, "\(d.name) 的快捷键", d.hotkey))
            hotKeyDevices[100 + i] = d.code
        }
        for (id, name, text) in wanted {
            if text.isEmpty { continue }   // 留空表示不注册
            guard let hk = parseHotKey(text) else {
                hotKeyErrors.append("快捷键 \(name)=\(text) 无法识别")
                continue
            }
            var ref: EventHotKeyRef?
            let status = RegisterEventHotKey(hk.keyCode, hk.carbonModifiers,
                                             EventHotKeyID(signature: OSType(0x5343_5357), id: UInt32(id)),
                                             GetApplicationEventTarget(), 0, &ref)
            if status == noErr {
                hotKeys[id] = hk
                hotKeyRefs.append(ref)
            } else {
                hotKeyErrors.append("快捷键 \(hk.display) 已被占用，未生效")
            }
        }
    }

    @objc func openLog() {
        NSWorkspace.shared.open(URL(fileURLWithPath: logPath))
    }

    @objc func quit() {
        NSApp.terminate(nil)
    }

    func selfTest() {
        if inAppBundle {
            let done = DispatchSemaphore(value: 0)
            UNUserNotificationCenter.current().getNotificationSettings { st in
                print("notifications: \(authName(st.authorizationStatus)) alert=\(st.alertSetting.rawValue) style=\(st.alertStyle.rawValue)")
                done.signal()
            }
            _ = done.wait(timeout: .now() + 3)
        } else {
            print("notifications: n/a (not running from ScreenSwitch.app)")
        }
        registerHotKeys()
        print("state: \(statusText(readOwnership()))")
        for (id, hk) in hotKeys.sorted(by: { $0.key < $1.key }) { print("hotkey \(id): \(hk.display) registered") }
        for e in hotKeyErrors { print("hotkey error: \(e)") }
        let m = NSMenu()
        buildMenu(m)
        for item in m.items {
            if item.isSeparatorItem { print("  ----"); continue }
            var line = "  " + item.title
            if !item.keyEquivalent.isEmpty,
               let hk = hotKeys.values.first(where: { $0.menuKey == item.keyEquivalent }) { line += "    " + hk.display }
            if let t = item.representedObject as? [String] { line += "    → give \(t.joined(separator: " "))" }
            else if let t = item.representedObject as? String { line += "    → set \(t)" }
            if !item.isEnabled { line += "    (info)" }
            print(line)
        }
    }
}

let app = NSApplication.shared
appDelegate = AppDelegate()
if CommandLine.arguments.contains("--self-test") {
    appDelegate.selfTest()
    exit(0)
}
app.setActivationPolicy(.accessory)
app.delegate = appDelegate
app.run()
