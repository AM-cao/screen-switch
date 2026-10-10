// 设置窗口（docs/settings-ui-design.md 第 5 节）：菜单"设置…"打开，五个标签页。
// 所有改动立即生效，没有"保存 / 取消"：勾选、选择马上生效，文字框回车或失去焦点时生效。
// 每一项都是调用命令层（screenctl.sh set / device / peer …），改完重新读一遍再显示。

import AppKit

enum SettingsTab: Int, CaseIterable {
    case ports, peer, screens, hotkeys, general

    var title: String {
        switch self {
        case .ports: return L("tab.ports")
        case .peer: return L("tab.peer")
        case .screens: return L("tab.screens")
        case .hotkeys: return L("tab.hotkeys")
        case .general: return L("tab.general")
        }
    }
}

// 设置窗口一次读到的全部内容
struct SettingsData {
    var status = Status()
    var ports = PortList()
    var info = MenuInfo()

    static func load() -> SettingsData {
        SettingsData(status: Status.load(), ports: PortList.load(), info: MenuInfo.load())
    }
}

final class SettingsWindowController: NSWindowController, NSWindowDelegate {
    static var current: SettingsWindowController?

    let tabView = NSTabView()
    // 两张表的对象要留住：界面里的控件只弱引用它们，不留住的话改了也没反应
    var portsEditor: PortsEditor?
    var peerEditor: PeerEditor?
    var data = SettingsData()
    var containers: [SettingsTab: NSView] = [:]
    var extraPorts: Set<Int> = []        // 本次手动添加、还没登记用途的口（重读配置时不丢）
    var hotKeyErrors: [String: String] = [:]   // 快捷键那一行的错误：peer / 编码 → 原因
    var detecting = false
    var detectMessage = ""
    var advancedOpen = false

    static func show(tab: SettingsTab?) {
        let c = current ?? SettingsWindowController()
        current = c
        c.reloadAll()
        if let t = tab { c.tabView.selectTabViewItem(at: t.rawValue) }
        NSApp.activate(ignoringOtherApps: true)
        c.showWindow(nil)
        c.window?.makeKeyAndOrderFront(nil)
    }

    convenience init() {
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 640, height: 560),
                         styleMask: [.titled, .closable, .miniaturizable], backing: .buffered, defer: false)
        w.title = L("settings.title")
        w.isReleasedWhenClosed = false
        self.init(window: w)
        w.delegate = self
        tabView.translatesAutoresizingMaskIntoConstraints = false
        for t in SettingsTab.allCases {
            let item = NSTabViewItem(identifier: t.rawValue)
            item.label = t.title
            let v = NSView()
            item.view = v
            containers[t] = v
            tabView.addTabViewItem(item)
        }
        let content = NSView()
        content.addSubview(tabView)
        NSLayoutConstraint.activate([
            tabView.topAnchor.constraint(equalTo: content.topAnchor, constant: 12),
            tabView.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 12),
            tabView.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -12),
            tabView.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -12),
        ])
        w.contentView = content
        w.center()
    }

    func windowWillClose(_ notification: Notification) {
        HotKeyManager.shared.resume()
        Self.current = nil
    }

    func languageChanged() {
        window?.title = L("settings.title")
        for t in SettingsTab.allCases { tabView.tabViewItem(at: t.rawValue).label = t.title }
        reloadAll()
    }

    func reloadAll() {
        data = SettingsData.load()
        for t in SettingsTab.allCases { render(t) }
    }

    // 改完配置后：重读并重画所有标签页，菜单栏程序也重读（快捷键等）
    func applied(_ failure: ScriptResult?) {
        if let f = failure { showError(f.message, in: window) }
        appDelegate.reloadConfig()
        reloadAll()
    }

    func run(_ args: [String]) {
        let r = Script.run(args)
        applied(r.ok ? nil : r)
    }

    func hotKeysChanged() {
        render(.hotkeys)
    }

    func render(_ t: SettingsTab) {
        guard let c = containers[t] else { return }
        let v: NSView
        switch t {
        case .ports: v = portsTab()
        case .peer: v = peerTab()
        case .screens: v = screensTab()
        case .hotkeys: v = hotkeysTab()
        case .general: v = generalTab()
        }
        replaceContent(of: c, with: padded(v))
    }

    // ---- 输入口

    func mainScreenRow() -> NSView {
        let title = data.status.configured ? L("ports.main", data.status.mainScreen) : L("ports.main-none")
        let button = ActionButton(L("ports.detect")) { [weak self] in self?.redetect(main: nil) }
        button.isEnabled = !detecting
        var views: [NSView] = [label(title, bold: true), button]
        if detecting || !detectMessage.isEmpty {
            views.append(label(detecting ? L("mac.ports.detecting") : detectMessage, small: true, secondary: true))
        }
        return hstack(views)
    }

    // 重新检测（detect）：几块显示器都能控制时让用户选
    func redetect(main: String?) {
        detecting = true
        detectMessage = ""
        render(.ports); render(.screens)
        Script.runAsync(main.map { ["detect", "--main", $0] } ?? ["detect"]) { [weak self] r in
            guard let self = self else { return }
            self.detecting = false
            if !r.ok && !r.candidates.isEmpty && main == nil {
                self.chooseMain(r.candidates)
                return
            }
            if r.ok { self.detectMessage = L("mac.ports.detected") }
            self.applied(r.ok ? nil : r)
        }
    }

    func chooseMain(_ names: [String]) {
        let a = NSAlert()
        a.messageText = L("ports.choose")
        let popup = NSPopUpButton(frame: NSRect(x: 0, y: 0, width: 240, height: 26), pullsDown: false)
        popup.addItems(withTitles: names)
        a.accessoryView = popup
        a.addButton(withTitle: L("common.ok"))
        a.addButton(withTitle: L("wizard.cancel"))
        a.beginSheetModal(for: window!) { [weak self] r in
            if r == .alertFirstButtonReturn, let n = popup.titleOfSelectedItem { self?.redetect(main: n) }
            else { self?.applied(nil) }
        }
    }

    func portsTab() -> NSView {
        var ports = data.ports.ports
        for c in extraPorts where !ports.contains(where: { $0.code == c }) {
            ports.append(Port(code: c, name: Port.standardName(c), use: "none", device: "", fromMonitor: false))
        }
        ports.sort { $0.code < $1.code }
        let editor = PortsEditor(ports: ports, peerName: data.status.peerConfigured ? data.status.peerName : "")
        portsEditor = editor
        editor.window = window
        editor.onAdd = { [weak self] c in self?.extraPorts.insert(c) }
        editor.onChange = { [weak self] old, new in self?.applyPort(old, new) }
        var views: [NSView] = [mainScreenRow(), separatorLine(), editor.view]
        if data.status.configured && !data.ports.monitorKnown { views.append(helpText(L("ports.unknown"))) }
        views.append(helpText(L("mac.ports.help")))
        return vstack(views, spacing: 12)
    }

    func applyPort(_ old: Port, _ new: Port) {
        var cmds: [[String]] = []
        if old.use == "device" && new.use != "device" { cmds.append(["device", "remove", "\(old.code)"]) }
        if old.use == "peer" && new.use != "peer" { cmds.append(["peer", "code", "none"]) }
        switch new.use {
        case "peer": cmds.append(["peer", "code", "\(new.code)"])
        case "device":
            cmds.append(old.use == "device" ? ["device", "rename", "\(new.code)", new.device]
                                            : ["device", "add", "\(new.code)", new.device])
        default: break
        }
        Script.runSequence(cmds) { [weak self] f in self?.applied(f) }
    }

    // ---- 对方电脑

    func peerTab() -> NSView {
        let s = data.status
        let fields = PeerFields(name: s.peerHost.isEmpty ? "" : s.peerName, os: s.peerOS.isEmpty ? "windows" : s.peerOS,
                                host: s.peerHost, user: s.peerUser)
        let editor = PeerEditor(fields: fields, inWizard: false)
        peerEditor = editor
        editor.window = window
        editor.onFieldsChanged = { [weak self] f in
            guard f.complete else { return }
            let r = Script.run(["peer", "set"] + f.args)
            if !r.ok { showError(r.message, in: self?.window) }
            self?.data = SettingsData.load()
            appDelegate.reloadConfig()
            self?.render(.ports); self?.render(.hotkeys)
        }
        editor.onTest = { [weak self] done in
            let expected = self?.data.status.peerCode
            Script.runAsync(["peer", "test"]) { r in
                let (text, warn, _) = PeerEditor.describeTest(r, expected: expected)
                done(text, warn)
                self?.data = SettingsData.load()
                self?.render(.ports)
            }
        }
        editor.onClear = { [weak self] in self?.run(["peer", "clear"]) }
        return editor.view
    }

    // ---- 屏幕

    func screensTab() -> NSView {
        let info = data.info
        let combo: String
        if info.layout == "none" { combo = L("mac.screens.combo.none") }
        else if info.secondBuiltin { combo = L("mac.screens.combo.builtin") }
        else { combo = L("mac.screens.combo.external") }
        let redetect = ActionButton(L("ports.detect")) { [weak self] in self?.redetect(main: nil) }
        redetect.isEnabled = !detecting
        var views: [NSView] = [label(L("mac.screens.combo"), bold: true), hstack([label(combo), redetect])]
        if detecting { views.append(label(L("mac.ports.detecting"), small: true, secondary: true)) }
        // 只有主屏时只显示屏幕组合
        if info.layout != "none" {
            views.append(separatorLine())
            if info.secondBuiltin {
                let mainOnly = info.ownedLayout == "external"
                views.append(label(L("screens.after-take"), bold: true))
                views.append(Radio(L("menu.extend"), selected: !mainOnly) { [weak self] in self?.run(["set", "owned-layout", "extend"]) })
                views.append(Radio(L("menu.main-only"), selected: mainOnly) { [weak self] in self?.run(["set", "owned-layout", "main-only"]) })
                views.append(separatorLine())
            }
            views.append(CheckBox(L("screens.autosave"), checked: data.status.autoSave) { [weak self] on in
                self?.run(["set", "auto-save", on ? "on" : "off"])
            })
            views.append(helpText(L("screens.autosave.note")))
            let saveResult = label("", small: true, secondary: true)
            let save = ActionButton(L("screens.save-now")) {
                let r = Script.run(["save-layout"])
                saveResult.stringValue = r.ok ? L("screens.saved") : r.message
            }
            views.append(hstack([save, saveResult]))
            views.append(helpText(L("mac.screens.saveNowHelp")))
        }
        return vstack(views, spacing: 10)
    }

    // ---- 快捷键

    func hotkeysTab() -> NSView {
        var rows: [NSView] = [helpText(L("mac.hotkeys.help")), separatorLine()]
        rows.append(hotkeyRow(L("hotkey.target.peer"), who: "peer", spec: data.info.hotkeyPeer, target: .peer))
        for d in data.info.devices {
            rows.append(hotkeyRow(L("menu.give-to", d.name), who: "\(d.code)", spec: d.hotkey, target: .device(d.code)))
        }
        return vstack(rows, spacing: 12)
    }

    func hotkeyRow(_ title: String, who: String, spec: String, target: HotKeyTarget) -> NSView {
        let name = label(title)
        name.widthAnchor.constraint(equalToConstant: 200).isActive = true
        let recorder = HotKeyRecorder(spec: spec)
        recorder.onRecord = { [weak self] s in self?.setHotkey(who, s) }
        let clear = ActionButton(L("hotkeys.clear")) { [weak self] in self?.setHotkey(who, "none") }
        clear.isEnabled = !spec.isEmpty
        var views: [NSView] = [name, recorder, clear]
        var error = hotKeyErrors[who]
        if error == nil, HotKeyManager.shared.failures[target] != nil { error = L("hotkeys.failed") }
        if let e = error {
            let l = label("⚠ " + e, small: true, wraps: true, width: 200)
            l.textColor = .systemOrange
            views.append(l)
        }
        return hstack(views)
    }

    func setHotkey(_ who: String, _ spec: String) {
        let r = Script.run(["set", "hotkey", who, spec])
        hotKeyErrors[who] = r.ok ? nil : r.message
        applied(nil)
    }

    // ---- 通用

    func generalTab() -> NSView {
        var views: [NSView] = []
        // 需要处理的问题（菜单里点"⚠"那一行会打开这里）
        let problems = appDelegate.currentProblems()
        if problems.contains(.daemonDown) {
            views.append(problemRow(L("mac.menu.problem.daemonDown"), L("mac.general.restartDaemon")) { [weak self] in
                AppDelegate.launchctl(["kickstart", "-k", "gui/\(getuid())/com.user.screenctl"])
                appDelegate.ensureDaemonLoaded()
                DispatchQueue.main.asyncAfter(deadline: .now() + 3) { appDelegate.updateIcon(); self?.reloadAll() }
            })
        }
        if problems.contains(.notificationsOff) {
            views.append(problemRow(L("mac.menu.problem.notifications"), L("mac.general.openNotifications")) {
                NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.Notifications-Settings.extension")!)
            })
        }
        if !views.isEmpty { views.append(separatorLine()) }

        // 界面语言："中文""English"两种语言下都显示原文，看不懂当前语言的人也能切回来
        let langs = ["auto", "zh", "en"]
        let langPopup = PopUp([L("general.language.auto"), L("general.language.zh"), L("general.language.en")],
                              selected: langs.firstIndex(of: data.status.language) ?? 0) { [weak self] i in
            self?.run(["set", "language", langs[i]])
        }
        views.append(hstack([label(L("general.language")), langPopup]))
        views.append(CheckBox(L("general.autostart"), checked: data.status.autostart) { [weak self] on in
            self?.run(["autostart", on ? "on" : "off"])
        })
        views.append(ActionButton(L("general.wizard")) { WizardWindowController.show() })
        views.append(ActionButton(L("general.log")) {
            NSWorkspace.shared.open(URL(fileURLWithPath: logPath))
        })
        views.append(label(L("general.version", appVersion), secondary: true))
        views.append(separatorLine())

        // 折叠的"高级"
        let toggle: () -> Void = { [weak self] in
            guard let self = self else { return }
            self.advancedOpen.toggle()
            self.render(.general)
        }
        let triangle = ActionButton("", toggle)
        triangle.bezelStyle = .disclosure
        triangle.setButtonType(.pushOnPushOff)
        triangle.state = advancedOpen ? .on : .off
        let title = ActionButton(L("general.advanced"), toggle)
        title.isBordered = false
        views.append(hstack([triangle, title], spacing: 4))
        if advancedOpen {
            let direct = CheckBox(L("mac.general.directTake"), checked: data.status.directTake) { [weak self] on in
                self?.run(["set", "direct-take", on ? "on" : "off"])
            }
            let folder = ActionButton(L("general.openfolder")) {
                NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: configPath)])
            }
            let adv = vstack([direct, helpText(L("mac.general.directTakeHelp"), width: 440), folder], spacing: 8)
            views.append(padded(adv, inset: 0).withLeadingInset(22))
        }
        return vstack(views, spacing: 12)
    }

    func problemRow(_ text: String, _ button: String, _ action: @escaping () -> Void) -> NSView {
        let l = label("⚠ " + text)
        l.textColor = .systemOrange
        return hstack([l, ActionButton(button, action)])
    }
}

extension NSView {
    // 左边再缩进一点（"高级"里的内容）
    func withLeadingInset(_ inset: CGFloat) -> NSView {
        let v = NSView()
        translatesAutoresizingMaskIntoConstraints = false
        v.addSubview(self)
        NSLayoutConstraint.activate([
            topAnchor.constraint(equalTo: v.topAnchor),
            bottomAnchor.constraint(equalTo: v.bottomAnchor),
            leadingAnchor.constraint(equalTo: v.leadingAnchor, constant: inset),
            trailingAnchor.constraint(equalTo: v.trailingAnchor),
        ])
        return v
    }
}
