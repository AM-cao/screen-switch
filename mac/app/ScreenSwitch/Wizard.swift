// 首次启动向导（docs/settings-ui-design.md 第 4 节）：还没设置过时自动打开。
// 欢迎 → 主屏 → 输入口 → 对方电脑（可跳过）→ 完成。中途关掉不写配置，下次打开时已填的内容还在；
// 最后一页点"完成"才依次运行 detect、peer、device、autostart 写入。

import AppKit

struct DraftPort: Codable, Equatable {
    var code: Int
    var use: String
    var device: String
    var fromMonitor: Bool

    var port: Port { Port(code: code, name: Port.standardName(code), use: use, device: device, fromMonitor: fromMonitor) }
    init(_ p: Port) { code = p.code; use = p.use; device = p.device; fromMonitor = p.fromMonitor }
}

// 向导已填的内容，存在 UserDefaults 里，关掉再打开还在
struct WizardDraft: Codable, Equatable {
    var page = 0
    var mainName = ""
    var mainUUID = ""
    var selfCode: Int?
    var portsFor = ""            // ports 是哪块显示器的
    var ports: [DraftPort] = []
    var peerName = ""
    var peerOS = "windows"
    var peerHost = ""
    var peerUser = ""
    var autostart = true

    static let key = "wizardDraft"

    static func load() -> WizardDraft? {
        guard let d = UserDefaults.standard.data(forKey: key) else { return nil }
        return try? JSONDecoder().decode(WizardDraft.self, from: d)
    }

    func save() {
        guard !dumpMode else { return }
        if let d = try? JSONEncoder().encode(self) { UserDefaults.standard.set(d, forKey: Self.key) }
    }

    static func clear() { UserDefaults.standard.removeObject(forKey: key) }

    // 重新运行向导时，从现有设置填好
    static func fromCurrent() -> WizardDraft {
        var d = WizardDraft()
        let s = Status.load()
        guard s.configured else { d.autostart = true; return d }
        d.mainName = s.mainScreen
        d.selfCode = s.selfCode
        d.peerName = s.peerHost.isEmpty ? "" : s.peerName
        d.peerOS = s.peerOS.isEmpty ? "windows" : s.peerOS
        d.peerHost = s.peerHost
        d.peerUser = s.peerUser
        d.autostart = s.autostart
        d.ports = PortList.load().ports.map(DraftPort.init)
        return d
    }

    var peer: PeerFields { PeerFields(name: peerName, os: peerOS, host: peerHost, user: peerUser) }
    var peerPort: Int? { ports.first(where: { $0.use == "peer" })?.code }
    var devices: [DraftPort] { ports.filter { $0.use == "device" && !$0.device.isEmpty } }
}

enum WizardPage: Int, CaseIterable {
    case welcome, main, ports, peer, done

    var title: String {
        switch self {
        case .welcome: return L("wizard.welcome.title")
        case .main: return L("wizard.main.title")
        case .ports: return L("wizard.ports.title")
        case .peer: return L("wizard.peer.title")
        case .done: return L("wizard.done.title")
        }
    }
}

// 主屏这一页的判断（--run-tests 测这里）：能不能下一步，以及要显示的说明
struct MainPageState {
    var scan: DisplayScan
    var selected: String          // 选中的显示器 uuid

    var externals: [DisplayEntry] { scan.displays.filter { !$0.builtin } }
    var controllable: [DisplayEntry] { externals.filter { $0.controllable } }
    var chosen: DisplayEntry? { controllable.first(where: { $0.uuid == selected }) }
    var others: [DisplayEntry] { scan.displays.filter { $0.uuid != selected } }

    // 只有一块能控制时自动选中
    static func autoSelect(_ scan: DisplayScan, previous: String, previousName: String) -> String {
        let ctl = scan.displays.filter { !$0.builtin && $0.controllable }
        if let p = ctl.first(where: { $0.uuid == previous || $0.name == previousName }) { return p.uuid }
        return ctl.count == 1 ? ctl[0].uuid : ""
    }

    // 停在这一页的原因；nil = 可以下一步
    var blocker: String? {
        if scan.failed { return L("mac.wizard.main.scanFailed") }
        if scan.lidClosed { return L("mac.wizard.lidClosed") }
        if controllable.isEmpty { return L("mac.err.noMonitor") }
        guard chosen != nil else { return L("wizard.main.pick") }
        if others.count >= 2 { return L("mac.err.tooManyScreens") }
        return nil
    }

    var notes: [String] {
        guard let c = chosen else { return [] }
        var n: [String] = []
        if let code = c.code {
            n.append(L("wizard.main.self", Port(code: code, name: Port.standardName(code), use: "self", device: "", fromMonitor: true).title))
        }
        if others.isEmpty { n.append(L("wizard.main.only")) }
        else if others.count == 1 { n.append(L("wizard.main.other", others[0].title)) }
        return n
    }
}

final class WizardWindowController: NSWindowController, NSWindowDelegate {
    // 两张表的对象要留住：界面里的控件只弱引用它们
    var portsEditor: PortsEditor?
    var peerEditor: PeerEditor?
    static var current: WizardWindowController?

    var draft: WizardDraft
    var page: WizardPage
    let body = NSView()
    let titleLabel = label("", bold: true)
    let stepLabel = label("", small: true, secondary: true)
    var backButton: ActionButton!
    var nextButton: ActionButton!
    var skipButton: ActionButton!
    var scan: DisplayScan?
    var scanning = false
    var loadingPorts = false
    var finishing = false
    var pageError = ""

    static func show() {
        let c = current ?? WizardWindowController()
        current = c
        NSApp.activate(ignoringOtherApps: true)
        c.showWindow(nil)
        c.window?.makeKeyAndOrderFront(nil)
    }

    convenience init() {
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 640, height: 560),
                         styleMask: [.titled, .closable], backing: .buffered, defer: false)
        w.title = L("wizard.title")
        w.isReleasedWhenClosed = false
        self.init(window: w)
        w.delegate = self
        buildFrame()
        show(page)
        w.center()
    }

    override init(window: NSWindow?) {
        // 有上次没走完的草稿就接着走；否则从现有设置开始（重新运行向导）
        draft = WizardDraft.load() ?? WizardDraft.fromCurrent()
        page = WizardPage(rawValue: draft.page) ?? .welcome
        super.init(window: window)
    }

    required init?(coder: NSCoder) { fatalError() }

    func windowWillClose(_ notification: Notification) {
        if !finishing { draft.page = page.rawValue; draft.save() }
        Self.current = nil
    }

    func buildFrame() {
        backButton = ActionButton(L("wizard.back")) { [weak self] in self?.go(-1) }
        nextButton = ActionButton(L("wizard.next")) { [weak self] in self?.next() }
        nextButton.keyEquivalent = "\r"
        skipButton = ActionButton(L("wizard.skip")) { [weak self] in self?.skipPeer() }
        let spacer = NSView()
        spacer.setContentHuggingPriority(.defaultLow, for: .horizontal)
        let bar = NSStackView(views: [stepLabel, spacer, skipButton, backButton, nextButton])
        bar.orientation = .horizontal
        bar.spacing = 8
        titleLabel.font = .boldSystemFont(ofSize: 18)
        let content = NSView()
        for v in [titleLabel, body, bar] as [NSView] {
            v.translatesAutoresizingMaskIntoConstraints = false
            content.addSubview(v)
        }
        let line = separatorLine()
        line.translatesAutoresizingMaskIntoConstraints = false
        content.addSubview(line)
        NSLayoutConstraint.activate([
            titleLabel.topAnchor.constraint(equalTo: content.topAnchor, constant: 20),
            titleLabel.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 24),
            body.topAnchor.constraint(equalTo: titleLabel.bottomAnchor, constant: 4),
            body.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 4),
            body.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -4),
            body.bottomAnchor.constraint(equalTo: line.topAnchor, constant: -8),
            line.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            line.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            line.bottomAnchor.constraint(equalTo: bar.topAnchor, constant: -12),
            bar.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 24),
            bar.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -20),
            bar.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -16),
        ])
        window?.contentView = content
    }

    func show(_ p: WizardPage) {
        page = p
        pageError = ""
        render()
        // 进入页面时要读的东西
        if p == .main && scan == nil { rescan() }
        if p == .ports && draft.portsFor != draft.mainUUID { loadPorts() }
    }

    func render() {
        titleLabel.stringValue = page.title
        stepLabel.stringValue = L("mac.wizard.step", "\(page.rawValue + 1)", "\(WizardPage.allCases.count)")
        backButton.isHidden = page == .welcome
        skipButton.isHidden = page != .peer
        nextButton.title = page == .done ? L("wizard.finish") : L("wizard.next")
        nextButton.isEnabled = canGoNext && !finishing
        replaceContent(of: body, with: padded(pageView()))
    }

    var canGoNext: Bool {
        switch page {
        case .main: return !scanning && scan.map { MainPageState(scan: $0, selected: draft.mainUUID).blocker == nil } ?? false
        case .ports: return !loadingPorts && !draft.ports.isEmpty
        default: return true
        }
    }

    func go(_ delta: Int) {
        window?.makeFirstResponder(nil)   // 正在编辑的文字框先生效
        guard let p = WizardPage(rawValue: page.rawValue + delta) else { return }
        draft.page = p.rawValue
        draft.save()
        show(p)
    }

    func next() {
        window?.makeFirstResponder(nil)
        switch page {
        case .ports:
            if let e = PortsEditor.validate(draft.ports.map { $0.port }) { pageError = e; render(); return }
        case .peer:
            let f = draft.peer
            if !f.empty && !f.complete { pageError = L("mac.peer.needHost"); render(); return }
        case .done:
            finish(); return
        default: break
        }
        go(1)
    }

    func skipPeer() {
        draft.peerName = ""; draft.peerHost = ""; draft.peerUser = ""
        for i in draft.ports.indices where draft.ports[i].use == "peer" { draft.ports[i].use = "none" }
        go(1)
    }

    func pageView() -> NSView {
        var views: [NSView]
        switch page {
        case .welcome: views = welcomePage()
        case .main: views = mainPage()
        case .ports: views = portsPage()
        case .peer: views = peerPage()
        case .done: views = donePage()
        }
        if !pageError.isEmpty {
            let l = label("⚠ " + pageError, wraps: true, width: 560)
            l.textColor = .systemOrange
            views.append(l)
        }
        return vstack(views, spacing: 12)
    }

    // ---- 1 欢迎

    func welcomePage() -> [NSView] {
        [label(L("wizard.welcome.text"), wraps: true, width: 560),
         label(L("wizard.welcome.before"), bold: true, wraps: true, width: 560)]
    }

    // ---- 2 主屏

    func rescan() {
        scanning = true
        render()
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let s = DisplayScan.load()
            DispatchQueue.main.async {
                guard let self = self else { return }
                self.scanning = false
                self.scan = s
                let sel = MainPageState.autoSelect(s, previous: self.draft.mainUUID, previousName: self.draft.mainName)
                self.select(sel, render: false)
                self.render()
            }
        }
    }

    func select(_ uuid: String, render again: Bool = true) {
        draft.mainUUID = uuid
        if let d = scan?.displays.first(where: { $0.uuid == uuid }) {
            draft.mainName = d.name
            draft.selfCode = d.code
        }
        draft.save()
        if again { render() }
    }

    func mainPage() -> [NSView] {
        var views: [NSView] = [label(L("mac.wizard.main.intro"), wraps: true, width: 560)]
        let redetect = ActionButton(L("ports.detect")) { [weak self] in self?.rescan() }
        guard let scan = scan, !scanning else {
            views.append(hstack([label(L("mac.ports.detecting"), secondary: true)]))
            return views
        }
        let state = MainPageState(scan: scan, selected: draft.mainUUID)
        for d in state.externals {
            let text = d.title + "  —  " + (d.controllable ? L("wizard.main.ddc-yes") : L("wizard.main.ddc-no"))
            let r = Radio(text, selected: d.uuid == draft.mainUUID) { [weak self] in self?.select(d.uuid) }
            r.isEnabled = d.controllable
            views.append(r)
        }
        if let b = scan.displays.first(where: { $0.builtin }) {
            views.append(label("     " + b.title, secondary: true))
        }
        if let blocker = state.blocker {
            let l = label("⚠ " + blocker, wraps: true, width: 560)
            l.textColor = .systemOrange
            views.append(l)
        } else {
            for n in state.notes { views.append(label(n, wraps: true, width: 560)) }
        }
        views.append(redetect)
        return views
    }

    // ---- 3 输入口

    func loadPorts() {
        loadingPorts = true
        render()
        let uuid = draft.mainUUID
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let list = PortList.load(main: uuid)
            DispatchQueue.main.async {
                guard let self = self else { return }
                self.loadingPorts = false
                self.mergePorts(list, uuid: uuid)
                self.render()
            }
        }
    }

    // 显示器读出来的口 + 草稿里已经标过的（重新运行向导、或从上一页回来）
    func mergePorts(_ list: PortList, uuid: String) {
        var result: [DraftPort] = list.ports.map(DraftPort.init)
        let selfCode = draft.selfCode ?? list.ports.first(where: { $0.use == "self" })?.code
        for old in draft.ports where old.use == "peer" || old.use == "device" {
            if old.code == selfCode { continue }
            if let i = result.firstIndex(where: { $0.code == old.code }) {
                result[i].use = old.use; result[i].device = old.device
            } else {
                result.append(old)
            }
        }
        if let s = selfCode, !result.contains(where: { $0.code == s }) {
            result.append(DraftPort(Port(code: s, name: "", use: "self", device: "", fromMonitor: false)))
        }
        for i in result.indices where result[i].code == selfCode { result[i].use = "self"; result[i].device = "" }
        result.sort { $0.code < $1.code }
        draft.ports = result
        draft.portsFor = uuid
        monitorKnown = list.monitorKnown
        draft.save()
    }

    var monitorKnown = true

    func portsPage() -> [NSView] {
        var views: [NSView] = [label(L("wizard.ports.text"), wraps: true, width: 560)]
        if loadingPorts {
            views.append(label(L("ports.reading"), secondary: true))
            return views
        }
        let editor = PortsEditor(ports: draft.ports.map { $0.port }, peerName: draft.peerName)
        portsEditor = editor
        editor.window = window
        editor.onChange = { [weak self, weak editor] _, _ in
            guard let self = self, let e = editor else { return }
            self.draft.ports = e.ports.map(DraftPort.init)
            self.draft.save()
        }
        editor.onAdd = { [weak self, weak editor] _ in
            guard let self = self, let e = editor else { return }
            self.draft.ports = e.ports.map(DraftPort.init)
            self.draft.save()
            self.render()
        }
        views.append(editor.view)
        if !monitorKnown { views.append(helpText(L("ports.unknown"))) }
        views.append(helpText(L("mac.ports.help")))
        return views
    }

    // ---- 4 对方电脑

    func peerPage() -> [NSView] {
        let editor = PeerEditor(fields: draft.peer, inWizard: true)
        peerEditor = editor
        editor.window = window
        editor.onFieldsChanged = { [weak self] f in
            guard let self = self else { return }
            self.draft.peerName = f.name; self.draft.peerOS = f.os; self.draft.peerHost = f.host; self.draft.peerUser = f.user
            self.draft.save()
        }
        editor.onTest = { [weak self] done in
            guard let self = self else { return }
            let expected = self.draft.peerPort
            Script.runAsync(["peer", "test"] + self.draft.peer.args) { r in
                let (text, warn, code) = PeerEditor.describeTest(r, expected: expected)
                var t = text
                // 输入口页还没标对方、或标的不一样：按对方说的改
                if let c = code, c != expected, c != self.draft.selfCode {
                    for i in self.draft.ports.indices where self.draft.ports[i].use == "peer" { self.draft.ports[i].use = "none" }
                    if let i = self.draft.ports.firstIndex(where: { $0.code == c }) {
                        self.draft.ports[i].use = "peer"; self.draft.ports[i].device = ""
                    } else {
                        self.draft.ports.append(DraftPort(Port(code: c, name: Port.standardName(c), use: "peer", device: "", fromMonitor: false)))
                        self.draft.ports.sort { $0.code < $1.code }
                    }
                    if expected == nil { t += "\n" + L("mac.wizard.peer.portMarked") }
                    self.draft.save()
                }
                done(t, warn)
            }
        }
        return [label(L("wizard.peer.text"), wraps: true, width: 560), editor.view]
    }

    // ---- 5 完成

    func summary() -> [String] {
        var s: [String] = []
        let selfTitle = draft.selfCode.map { Port(code: $0, name: Port.standardName($0), use: "self", device: "", fromMonitor: true).title } ?? "?"
        s.append(L("mac.wizard.summary.main", draft.mainName, selfTitle))
        if draft.peer.complete {
            let name = draft.peerName.isEmpty ? (draft.peerOS == "mac" ? "Mac" : "Windows") : draft.peerName
            let port = draft.peerPort.map { Port(code: $0, name: Port.standardName($0), use: "peer", device: "", fromMonitor: true).title }
                ?? L("mac.wizard.summary.portUnknown")
            s.append(L("mac.wizard.summary.peer", name, "\(draft.peerUser)@\(draft.peerHost)", port))
        } else {
            s.append(L("mac.wizard.summary.noPeer"))
        }
        let devs = draft.devices.map { "\($0.device)（\($0.port.title)）" }
        s.append(devs.isEmpty ? L("mac.wizard.summary.noDevices") : L("wizard.summary.devices", devs.joined(separator: Strings.isChinese ? "、" : ", ")))
        return s
    }

    func donePage() -> [NSView] {
        var views: [NSView] = summary().map { label($0, wraps: true, width: 560) }
        views.append(separatorLine())
        views.append(CheckBox(L("general.autostart"), checked: draft.autostart) { [weak self] on in
            self?.draft.autostart = on
            self?.draft.save()
        })
        if finishing { views.append(label(L("mac.wizard.finishing"), secondary: true)) }
        return views
    }

    // 依次写入：detect → 删掉 / 改名设备 → 对方电脑 → 对方的口 → 添加设备 → 开机自动运行
    func finish() {
        finishing = true
        pageError = ""
        render()
        let d = draft
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let failure = Self.apply(d)
            DispatchQueue.main.async {
                guard let self = self else { return }
                self.finishing = false
                if let f = failure {
                    self.pageError = L("mac.wizard.finishFailed", f.message)
                    self.render()
                    return
                }
                WizardDraft.clear()
                self.finishing = true   // 关窗时不再存草稿
                self.close()
                appDelegate.reloadConfig()
                SettingsWindowController.current?.reloadAll()
            }
        }
    }

    // 在后台线程运行；返回第一条失败的命令结果
    static func apply(_ d: WizardDraft) -> ScriptResult? {
        // 按 uuid 指定：同型号的两块屏也分得开
        let detect = Script.run(["detect", "--main", d.mainUUID.isEmpty ? d.mainName : d.mainUUID])
        if !detect.ok { return detect }
        let current = Status.load()
        let wanted = d.devices
        var cmds: [[String]] = []
        for dev in current.devices {
            if let w = wanted.first(where: { $0.code == dev.code }) {
                if w.device != dev.name { cmds.append(["device", "rename", "\(dev.code)", w.device]) }
            } else {
                cmds.append(["device", "remove", "\(dev.code)"])
            }
        }
        cmds.append(d.peer.complete ? ["peer", "set"] + d.peer.args : ["peer", "clear"])
        cmds.append(["peer", "code", d.peerPort.map { "\($0)" } ?? "none"])
        for w in wanted where !current.devices.contains(where: { $0.code == w.code }) {
            cmds.append(["device", "add", "\(w.code)", w.device])
        }
        cmds.append(["autostart", d.autostart ? "on" : "off"])
        for c in cmds {
            let r = Script.run(c)
            if !r.ok { return r }
        }
        return nil
    }
}
