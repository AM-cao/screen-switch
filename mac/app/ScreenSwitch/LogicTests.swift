// --run-tests：界面背后的逻辑（docs/settings-ui-design.md 第 12 节）——菜单项的生成、向导主屏页的检查、
// 输入口表的检查、快捷键写法、文字表。不碰真实的显示器和配置。

import AppKit

func runLogicTests() -> Bool {
    var pass = 0, fail = 0
    func check(_ ok: Bool, _ what: String) {
        if ok { pass += 1; print("  PASS \(what)") } else { fail += 1; print("  FAIL \(what)") }
    }
    func titles(_ e: [MenuEntry]) -> [String] {
        e.map { $0.isSeparator ? "--" : $0.title + ($0.checked ? " ✓" : "") + ($0.enabled ? "" : " (off)") + ($0.hotkey.map { " " + $0.display } ?? "") }
    }

    print("== 文字表")
    check(Strings.table.count > 100, "读到文字表（\(Strings.table.count) 行）")
    let required = ["menu.take-back", "menu.give-to", "menu.extend", "menu.main-only", "menu.settings", "menu.quit",
                    "tab.ports", "tab.peer", "tab.screens", "tab.hotkeys", "tab.general", "wizard.title", "app.name"]
    let missing = required.filter { !Strings.has($0) }
    check(missing.isEmpty, "菜单、标签页、向导的编号都在（缺：\(missing)）")
    check(L("menu.give-to", "Switch") != "menu.give-to" && L("menu.give-to", "Switch").contains("Switch"), "占位 {0} 换成参数")
    check(L("no.such.key") == "no.such.key", "表里没有的编号原样返回")

    print("== 菜单")
    var info = MenuInfo()
    info.peer = "Windows"
    info.devices = [Device(code: 17, name: "Switch", hotkey: "")]
    let zh = Strings.isChinese
    let take = L("menu.take-back"), toWin = L("menu.give-to", "Windows"), toSwitch = L("menu.give-to", "Switch")
    let ext = L("menu.extend"), only = L("menu.main-only"), set = L("menu.settings"), quit = L("menu.quit")
    var e = buildMenuEntries(own: .notOwned, busy: false, info: info, problems: [], hotkeys: [:])
    check(titles(e) == [take, toSwitch, "--", ext + " ✓", only, "--", set, quit], "主屏不在本机：拿回主屏 / 设备 / 布局 / 设置 / 退出（\(titles(e))）")
    e = buildMenuEntries(own: .owned, busy: false, info: info, problems: [], hotkeys: [:])
    check(titles(e).first == toWin, "主屏在本机：第一项是切给对方")
    var noPeer = info; noPeer.peer = ""
    e = buildMenuEntries(own: .owned, busy: false, info: noPeer, problems: [], hotkeys: [:])
    check(titles(e).first == toSwitch, "没配置对方：主屏在本机时没有切给对方")
    e = buildMenuEntries(own: .notOwned, busy: false, info: noPeer, problems: [], hotkeys: [:])
    check(titles(e).first == take, "没配置对方：拿回主屏一直都有")
    var noDev = noPeer; noDev.devices = []
    e = buildMenuEntries(own: .owned, busy: false, info: noDev, problems: [], hotkeys: [:])
    check(titles(e) == [ext + " ✓", only, "--", set, quit], "没有对方也没有设备：没有切换项，也没有多余的分隔线（\(titles(e))）")
    var mainOnly = info; mainOnly.ownedLayout = "external"
    e = buildMenuEntries(own: .notOwned, busy: false, info: mainOnly, problems: [], hotkeys: [:])
    check(titles(e).contains(only + " ✓") && titles(e).contains(ext), "只用主屏：对号在只用主屏")
    var solo = info; solo.layout = "none"
    e = buildMenuEntries(own: .notOwned, busy: false, info: solo, problems: [], hotkeys: [:])
    check(!titles(e).contains(where: { $0.hasPrefix(ext) || $0.hasPrefix(only) }), "只有主屏：不显示扩展 / 只用主屏")
    var external = info; external.secondBuiltin = false
    e = buildMenuEntries(own: .notOwned, busy: false, info: external, problems: [], hotkeys: [:])
    check(!titles(e).contains(where: { $0.hasPrefix(ext) || $0.hasPrefix(only) }), "另一块屏是外接屏：不显示扩展 / 只用主屏")
    e = buildMenuEntries(own: .notOwned, busy: true, info: info, problems: [], hotkeys: [:])
    check(titles(e).prefix(2) == [take + " (off)", toSwitch + " (off)"], "切换进行中：切换项变灰")
    check(!titles(e).contains(where: { $0.contains(zh ? "请先打开设备" : "turn on") }), "设备项没有\u{201C}请先打开设备\u{201D}")
    let hk = HotKey.parse("ctrl+opt+1")!
    e = buildMenuEntries(own: .notOwned, busy: false, info: info, problems: [], hotkeys: [.peer: hk])
    check(e.first?.hotkey == hk && e[1].hotkey == nil, "只有设了快捷键的项才显示快捷键")
    e = buildMenuEntries(own: .daemonDown, busy: false, info: info, problems: [.daemonDown], hotkeys: [:])
    check(titles(e).first == "⚠ " + L("mac.menu.problem.daemonDown") && titles(e)[1] == "--" && titles(e)[2] == take && titles(e)[3] == toWin,
          "有问题：最上面一行 ⚠ 说明；守护进程没在运行时两个切换项都给（\(titles(e).prefix(4))）")
    check(e.first?.action == .problem(.daemonDown), "点 ⚠ 那一行打开对应位置")
    var blank = MenuInfo(); blank.configured = false
    e = buildMenuEntries(own: .daemonDown, busy: false, info: blank, problems: [.notConfigured], hotkeys: [:])
    check(titles(e) == ["⚠ " + L("problem.not-configured"), "--", set, quit], "还没设置过：只有 ⚠、设置、退出")
    check(!titles(buildMenuEntries(own: .owned, busy: false, info: info, problems: [], hotkeys: [:])).contains(where: { $0.hasPrefix("⚠") }),
          "没有问题时没有 ⚠ 那一行")
    check(Problem.hotkeyFailed(.device(17), "⌃⌥2").id == "hotkey-failed:17" && Problem.notificationsOff.id == "notifications-off",
          "问题的英文标识")

    print("== 快捷键写法")
    check(HotKey.parse("Ctrl+Alt+1")?.spec == "ctrl+opt+1" && HotKey.parse("ctrl+alt+1")?.display == "⌃⌥1", "Ctrl+Alt+1 → ctrl+opt+1，显示 ⌃⌥1")
    check(HotKey.parse("cmd+shift+p")?.spec == "shift+cmd+p" && HotKey.parse("cmd+shift+p")?.display == "⇧⌘P", "修饰键按固定顺序")
    check(PeerEditor.maskedKey("ssh-ed25519 AAAAC3Nza screenctl@mac") == "ssh-ed25519 ••••••••••••", "公钥打码：只留类型")
    check(!PeerEditor.maskedKey("ssh-ed25519 AAAAC3Nza screenctl@mac").contains("AAAA"), "打码后看不到公钥内容")
    check(HotKey.parse("shift+1") == nil && HotKey.parse("1") == nil, "只带 Shift 或不带修饰键：不接受")
    check(HotKey.parse("x") == nil && HotKey.parse("ctrl+f1") == nil && HotKey.parse("hyper+1") == nil, "写法不对返回 nil")

    print("== 向导：主屏页")
    func disp(_ n: String, _ u: String, ctl: Bool, code: Int? = nil, builtin: Bool = false) -> DisplayEntry {
        DisplayEntry(name: n, title: n, uuid: u, builtin: builtin, controllable: ctl, code: code)
    }
    var scan = DisplayScan()
    scan.displays = [disp("P275MV", "A", ctl: true, code: 15), disp("Built-in", "L", ctl: false, builtin: true)]
    check(MainPageState.autoSelect(scan, previous: "", previousName: "") == "A", "只有一块能控制：自动选中")
    var st = MainPageState(scan: scan, selected: "A")
    check(st.blocker == nil, "选中后可以下一步")
    check(st.notes.count == 2 && st.notes[1] == L("wizard.main.other", "Built-in"), "说明本机接在哪个口、主屏切走时桌面放到哪块屏")
    scan.displays.insert(disp("SIDE24", "B", ctl: true, code: 15), at: 1)
    check(MainPageState.autoSelect(scan, previous: "", previousName: "") == "", "几块都能控制：不自动选，让用户点选")
    check(MainPageState.autoSelect(scan, previous: "", previousName: "SIDE24") == "B", "上次选过的（按名字）仍选中")
    st = MainPageState(scan: scan, selected: "")
    check(st.blocker == L("wizard.main.pick"), "没选时停在这一页")
    st = MainPageState(scan: scan, selected: "A")
    check(st.blocker == L("mac.err.tooManyScreens"), "三块及以上的屏：停在这一页")
    scan.displays = [disp("P275MV", "A", ctl: false)]
    st = MainPageState(scan: scan, selected: "")
    check(st.blocker == L("mac.err.noMonitor"), "一块都不能控制：说明原因，停在这一页")
    scan.lidClosed = true
    check(MainPageState(scan: scan, selected: "").blocker == L("mac.wizard.lidClosed"), "合着盖子：要求开盖")
    scan = DisplayScan(); scan.displays = [disp("P275MV", "A", ctl: true, code: 16)]
    st = MainPageState(scan: scan, selected: "A")
    check(st.blocker == nil && st.notes.contains(L("wizard.main.only")), "只有主屏：可以下一步，说明只有主屏")

    print("== 向导：输入口与对方")
    let ports = [Port(code: 15, name: "DisplayPort 1", use: "self", device: "", fromMonitor: true),
                 Port(code: 17, name: "HDMI 1", use: "device", device: "", fromMonitor: true)]
    check(PortsEditor.validate(ports) != nil, "选了设备没填名字：不能下一步")
    var named = ports; named[1].device = "Switch"
    check(PortsEditor.validate(named) == nil, "填了名字就行")
    named.append(Port(code: 18, name: "HDMI 2", use: "device", device: "Switch", fromMonitor: true))
    check(PortsEditor.validate(named) != nil, "两个设备同名：不能下一步")
    check(PeerFields(host: "pc.local").complete == false && PeerFields(host: "pc.local", user: "bob").complete, "对方电脑要地址和用户名都填")
    check(PeerFields(name: "PC", os: "mac", host: "h", user: "u").args == ["--os", "mac", "--host", "h", "--user", "u", "--name", "PC"],
          "peer set 的参数")
    let ok = ScriptResult(code: 0, output: "{\"name\":\"PC\",\"code\":16,\"codeSaved\":true}", error: "")
    var (text, warn, code) = PeerEditor.describeTest(ok, expected: 16)
    check(!warn && code == 16 && text == L("peer.reached", "PC", L("port.label", "DisplayPort 2", "16")), "测试连接：已连上 X，它接在 Y")
    (text, warn, code) = PeerEditor.describeTest(ok, expected: 17)
    check(warn && text.contains(L("port.label", "HDMI 1", "17")), "与输入口页标的不一致时提醒")
    (text, warn, _) = PeerEditor.describeTest(ScriptResult(code: 1, output: "", error: "error: 连不上 PC：timeout\n"), expected: nil)
    check(warn && text == "连不上 PC：timeout", "连不上时显示原因")
    check(ScriptResult(code: 1, output: "", error: "error: x\n  P275MV  P275MV\n  SIDE24  SIDE 24\n").candidates == ["P275MV", "SIDE24"],
          "detect 的候选（型号）")

    var d = WizardDraft()
    d.ports = [DraftPort(ports[0]), DraftPort(Port(code: 16, name: "", use: "peer", device: "", fromMonitor: true)),
               DraftPort(Port(code: 17, name: "", use: "device", device: "Switch", fromMonitor: true))]
    check(d.peerPort == 16 && d.devices.map { $0.code } == [17], "草稿：对方的口与设备")

    print("\nPASS=\(pass) FAIL=\(fail)")
    return fail == 0
}
