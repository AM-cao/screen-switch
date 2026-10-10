// 菜单栏图标与右键菜单（docs/settings-ui-design.md 第 3 节）、全局快捷键、通知。
// 切换逻辑在守护进程里，这里只读 ~/screenctl/state 显示归属，执行 screenctl.sh give …。
// 通知：守护进程把文字追加到 ~/screenctl/notify.queue，本程序取走后用 UserNotifications 弹出；
// launchd 后台进程自己用 osascript 发的通知会被系统丢弃。

import AppKit
import UserNotifications

// 只有在 .app 包里运行时才能用 UserNotifications，直接运行裸二进制会崩
let inAppBundle = Bundle.main.bundleIdentifier != nil
let staleSeconds: TimeInterval = 10   // 状态文件超过 10 秒视为守护进程未运行

func readOwnership() -> Ownership {
    guard let text = try? String(contentsOfFile: statePath, encoding: .utf8) else { return .daemonDown }
    let parts = text.split(whereSeparator: { $0 == " " || $0 == "\n" })
    guard parts.count >= 2, let ts = TimeInterval(parts[1]),
          Date().timeIntervalSince1970 - ts <= staleSeconds else { return .daemonDown }
    return parts[0] == "owned=1" ? .owned : .notOwned
}

// 写到 stderr（launchd 重定向到 ~/screenctl/menu.log），不经缓冲
func logLine(_ text: String) {
    let f = DateFormatter()
    f.dateFormat = "yyyy-MM-dd HH:mm:ss"
    FileHandle.standardError.write("\(f.string(from: Date())) \(text)\n".data(using: .utf8)!)
}

func fileMTime(_ path: String) -> Date? {
    (try? FileManager.default.attributesOfItem(atPath: path))?[.modificationDate] as? Date
}

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate, UNUserNotificationCenterDelegate {
    var statusItem: NSStatusItem?
    let menu = NSMenu()
    var running: Set<Process> = []
    var menuInfo: MenuInfo = { var i = MenuInfo(); i.configured = false; return i }()
    var notifyStatus: UNAuthorizationStatus?
    var configMTime: Date?
    var lastProblemIDs: [String]?
    var timers: [Timer] = []

    func applicationDidFinishLaunching(_ notification: Notification) {
        let others = NSRunningApplication.runningApplications(
            withBundleIdentifier: Bundle.main.bundleIdentifier ?? "")
            .filter { $0.processIdentifier != getpid() }
        if !others.isEmpty { NSApp.terminate(nil); return }

        installEditMenu()
        menu.delegate = self
        menu.autoenablesItems = false
        menu.showsStateColumn = false   // 不留左侧勾选列，对号自己画在右边
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.menu = menu
        statusItem = item

        HotKeyManager.shared.onFire = { [weak self] t in self?.hotKeyPressed(t) }
        HotKeyManager.shared.onChange = { [weak self] in
            self?.updateIcon()
            SettingsWindowController.current?.hotKeysChanged()
        }
        reloadConfig()
        try? "\(getpid())\n".write(toFile: menuPidPath, atomically: true, encoding: .utf8)
        if inAppBundle {
            let center = UNUserNotificationCenter.current()
            center.delegate = self
            center.requestAuthorization(options: [.alert, .sound]) { [weak self] granted, error in
                logLine("notify auth: granted=\(granted) error=\(error.map { "\($0)" } ?? "nil")")
                DispatchQueue.main.async { self?.refreshNotifyStatus() }
            }
        }
        ensureDaemonLoaded()
        timers.append(Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            self?.checkConfigChanged()
            self?.updateIcon()
            self?.refreshNotifyStatus()
        })
        timers.append(Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in self?.drainNotifyQueue() })
        updateIcon()
        // 还没设置过：自动打开向导
        if !menuInfo.configured { WizardWindowController.show() }
    }

    func applicationWillTerminate(_ notification: Notification) {
        if (try? String(contentsOfFile: menuPidPath, encoding: .utf8))?
            .trimmingCharacters(in: .whitespacesAndNewlines) == "\(getpid())" {
            try? FileManager.default.removeItem(atPath: menuPidPath)
            try? FileManager.default.removeItem(atPath: menuProblemsPath)
        }
    }

    // 关掉开机自动运行后，手动打开本程序时守护进程可能没有加载：从 LaunchAgents 或 ~/screenctl/launchd 里的模板加载
    func ensureDaemonLoaded() {
        guard inAppBundle else { return }
        let domain = "gui/\(getuid())"
        DispatchQueue.global().async {
            if Self.launchctl(["print", "\(domain)/com.user.screenctl"]) == 0 { return }
            let agents = (NSHomeDirectory() as NSString).appendingPathComponent("Library/LaunchAgents/com.user.screenctl.plist")
            let template = screenctlDir + "/launchd/com.user.screenctl.plist"
            let plist = FileManager.default.fileExists(atPath: agents) ? agents : template
            guard FileManager.default.fileExists(atPath: plist) else { return }
            let rc = Self.launchctl(["bootstrap", domain, plist])
            logLine("daemon was not loaded; bootstrap \(plist) → \(rc)")
        }
    }

    @discardableResult
    static func launchctl(_ args: [String]) -> Int32 {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        p.arguments = args
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        guard (try? p.run()) != nil else { return -1 }
        p.waitUntilExit()
        return p.terminationStatus
    }

    // ---- 配置

    // config.sh 改了（设置窗口、命令行、Agent）：重读名字和设备，重新注册快捷键
    func checkConfigChanged() {
        let m = fileMTime(configPath)
        if m != configMTime { reloadConfig() }
    }

    func reloadConfig() {
        configMTime = fileMTime(configPath)
        let wasConfigured = menuInfo.configured
        menuInfo = MenuInfo.load()
        applyLanguage()
        // 向导还停在欢迎页时，设置已经用命令（AI 助手按 AGENTS.md）做完了：关掉向导
        if !wasConfigured && menuInfo.configured, let w = WizardWindowController.current, w.page == .welcome, !w.finishing {
            WizardDraft.clear()
            w.finishing = true
            w.close()
        }
        HotKeyManager.shared.update(menuInfo)
        updateIcon()
    }

    // 界面语言改了（设置窗口、命令或 AI 助手改的）：已打开的窗口立即换语言；菜单每次打开时现画
    func applyLanguage() {
        guard menuInfo.language != Strings.preference else { return }
        let before = Strings.isChinese
        Strings.preference = menuInfo.language
        guard Strings.isChinese != before else { return }
        SettingsWindowController.current?.languageChanged()
        WizardWindowController.current?.languageChanged()
        updateIcon()
    }

    // ---- 问题

    func currentProblems() -> [Problem] {
        var p: [Problem] = []
        if !menuInfo.configured { return [.notConfigured] }
        if readOwnership() == .daemonDown { p.append(.daemonDown) }
        for (t, combo) in HotKeyManager.shared.failures.sorted(by: { $0.key.id < $1.key.id }) {
            p.append(.hotkeyFailed(t, HotKey.displayText(combo)))
        }
        if notifyStatus == .denied { p.append(.notificationsOff) }
        return p
    }

    // 菜单栏程序发现的问题写进 menu.problems，status 命令读它
    func reportProblems(_ problems: [Problem]) {
        let ids = problems.filter { $0.reportedByMenu }.map { $0.id }
        guard ids != lastProblemIDs else { return }
        lastProblemIDs = ids
        try? (ids.map { $0 + "\n" }.joined()).write(toFile: menuProblemsPath, atomically: true, encoding: .utf8)
    }

    // ---- 通知

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
        content.title = L("app.name")
        content.body = body
        let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
        UNUserNotificationCenter.current().add(request) { error in
            if let error = error { logLine("notify failed: \(error) body=\(body)") }
        }
    }

    func refreshNotifyStatus() {
        guard inAppBundle else { return }
        UNUserNotificationCenter.current().getNotificationSettings { [weak self] settings in
            DispatchQueue.main.async {
                guard let self = self, self.notifyStatus != settings.authorizationStatus else { return }
                self.notifyStatus = settings.authorizationStatus
                self.updateIcon()
            }
        }
    }

    // 菜单栏程序没有"前台"概念，让通知始终以横幅显示
    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .list])
    }

    // ---- 图标与菜单

    // 主屏在本机：亮；不在：灰；有要处理的问题：右下角加小感叹号
    func updateIcon() {
        let problems = currentProblems()
        reportProblems(problems)
        guard let button = statusItem?.button else { return }
        let own = readOwnership()
        button.image = Self.icon(problem: !problems.isEmpty)
        button.appearsDisabled = own == .notOwned
        var tip = L("app.name")
        if let first = problems.first { tip += " — " + first.text }
        button.toolTip = tip
    }

    static func icon(problem: Bool) -> NSImage? {
        guard let base = NSImage(systemSymbolName: "display", accessibilityDescription: L("app.name")) else { return nil }
        guard problem, let badge = NSImage(systemSymbolName: "exclamationmark.circle.fill", accessibilityDescription: nil) else {
            base.isTemplate = true
            return base
        }
        let size = NSSize(width: 20, height: 18)
        let image = NSImage(size: size, flipped: false) { rect in
            let b = base.size
            base.draw(in: NSRect(x: 0, y: (rect.height - b.height) / 2 + 1, width: b.width, height: b.height))
            let r = NSRect(x: rect.width - 10, y: 0, width: 10, height: 10)
            // 先挖空一圈，感叹号在显示器图形上也看得清
            NSGraphicsContext.current?.compositingOperation = .destinationOut
            NSBezierPath(ovalIn: r.insetBy(dx: -1.5, dy: -1.5)).fill()
            NSGraphicsContext.current?.compositingOperation = .sourceOver
            badge.draw(in: r)
            return true
        }
        image.isTemplate = true
        image.accessibilityDescription = L("app.name")
        return image
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menuInfo = MenuInfo.load()
        applyLanguage()
        buildMenu(menu)
    }

    func currentEntries() -> [MenuEntry] {
        buildMenuEntries(own: readOwnership(), busy: FileManager.default.fileExists(atPath: busyPath),
                         info: menuInfo, problems: currentProblems(), hotkeys: HotKeyManager.shared.registered)
    }

    func buildMenu(_ menu: NSMenu) {
        menu.removeAllItems()
        let entries = currentEntries()
        // 对号所在的右侧制表位：最宽一项的文字宽度再留一点空
        let font = NSFont.menuFont(ofSize: 0)
        let widest = entries.map { ($0.title as NSString).size(withAttributes: [.font: font]).width }.max() ?? 100
        let tabStop = max(widest + 36, 160)
        for e in entries {
            if e.isSeparator { menu.addItem(.separator()); continue }
            let item = NSMenuItem(title: e.title, action: #selector(menuAction(_:)), keyEquivalent: e.hotkey?.menuKey ?? "")
            if let hk = e.hotkey { item.keyEquivalentModifierMask = hk.menuModifiers }
            item.target = self
            item.representedObject = MenuActionBox(e.action)
            item.isEnabled = e.enabled
            if e.checked { item.attributedTitle = Self.titleWithCheck(e.title, tabStop: tabStop, font: font) }
            menu.addItem(item)
        }
    }

    static func titleWithCheck(_ title: String, tabStop: CGFloat, font: NSFont) -> NSAttributedString {
        let ps = NSMutableParagraphStyle()
        ps.tabStops = [NSTextTab(textAlignment: .right, location: tabStop)]
        return NSAttributedString(string: title + "\t✓", attributes: [.font: font, .paragraphStyle: ps])
    }

    @objc func menuAction(_ sender: NSMenuItem) {
        guard let action = (sender.representedObject as? MenuActionBox)?.action else { return }
        switch action {
        case .give(let target): give(target)
        case .setLayout(let v): runScript(["set", "owned-layout", v])
        case .problem(let p): openProblem(p)
        case .settings: SettingsWindowController.show(tab: nil)
        case .quit: NSApp.terminate(nil)
        }
    }

    func openProblem(_ p: Problem) {
        switch p {
        case .notConfigured: WizardWindowController.show()
        case .hotkeyFailed: SettingsWindowController.show(tab: .hotkeys)
        case .daemonDown, .notificationsOff: SettingsWindowController.show(tab: .general)
        }
    }

    // 快捷键 peer：占有时切给对方，否则拿回；device：切给那个设备
    func hotKeyPressed(_ t: HotKeyTarget) {
        if FileManager.default.fileExists(atPath: busyPath) { NSSound.beep(); return }
        switch t {
        case .peer: give(readOwnership() == .owned ? ["peer"] : ["self"])
        case .device(let c): give(["code", "\(c)"])
        }
    }

    // target = ["peer"] | ["self"] | ["code", "<编码>"]
    func give(_ target: [String]) {
        runScript(["give"] + target)
    }

    func runScript(_ args: [String]) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/bash")
        p.arguments = [scriptPath] + args
        var env = ProcessInfo.processInfo.environment
        env["SCREENSWITCH_LANG"] = Strings.langCode
        p.environment = env
        let err = Pipe()
        p.standardOutput = FileHandle.nullDevice
        p.standardError = err
        p.terminationHandler = { [weak self] proc in
            let msg = String(decoding: err.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
            DispatchQueue.main.async {
                self?.running.remove(proc)
                if proc.terminationStatus != 0 {
                    let r = ScriptResult(code: proc.terminationStatus, output: "", error: msg)
                    self?.postNotification(r.message)
                }
                self?.checkConfigChanged()
            }
        }
        do {
            try p.run()
            running.insert(p)
        } catch {
            NSSound.beep()
        }
    }

    // 自检（不显示图标）：打印菜单与快捷键注册结果
    func selfTest() {
        menuInfo = MenuInfo.load()
        HotKeyManager.shared.update(menuInfo)
        if inAppBundle {
            let done = DispatchSemaphore(value: 0)
            UNUserNotificationCenter.current().getNotificationSettings { st in
                self.notifyStatus = st.authorizationStatus
                print("notifications: \(st.authorizationStatus.rawValue) alert=\(st.alertSetting.rawValue)")
                done.signal()
            }
            _ = done.wait(timeout: .now() + 3)
        } else {
            print("notifications: n/a (not running from ScreenSwitch.app)")
        }
        print("language: \(Strings.langCode), strings: \(Strings.table.count)")
        print("ownership: \(readOwnership())")
        for (t, hk) in HotKeyManager.shared.registered { print("hotkey \(t): \(hk.display) registered") }
        for (t, combo) in HotKeyManager.shared.failures { print("hotkey \(t): \(combo) FAILED") }
        for e in currentEntries() {
            if e.isSeparator { print("  ────"); continue }
            var line = "  " + e.title
            if e.checked { line += "    ✓" }
            if let hk = e.hotkey { line += "    " + hk.display }
            if !e.enabled { line += "    (disabled)" }
            print(line)
        }
    }
}

// NSMenuItem.representedObject 要放对象
// 菜单栏程序没有菜单栏，文字框里的 ⌘V ⌘C ⌘X ⌘A ⌘Z 要靠一个不显示的"编辑"菜单才生效
func installEditMenu() {
    let main = NSMenu()
    let editItem = NSMenuItem()
    let edit = NSMenu(title: "Edit")
    edit.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
    let redo = edit.addItem(withTitle: "Redo", action: Selector(("redo:")), keyEquivalent: "z")
    redo.keyEquivalentModifierMask = [.command, .shift]
    edit.addItem(.separator())
    edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
    edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
    edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
    edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
    editItem.submenu = edit
    main.addItem(editItem)
    NSApp.mainMenu = main
}

final class MenuActionBox: NSObject {
    let action: MenuAction?
    init(_ a: MenuAction?) { action = a }
}
