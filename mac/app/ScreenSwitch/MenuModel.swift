// 右键菜单的内容（docs/settings-ui-design.md 第 3 节）。这里只决定"有哪些项、什么状态"，
// 怎么画在 AppDelegate 里；--run-tests 直接测这里。
//
//   ⚠ <需要用户处理的问题>        只在有问题时出现，点它打开设置窗口对应位置
//   拿回主屏 | 切给 <对方>          随归属变；没配置对方时没有"切给 <对方>"
//   切给 <设备>…                   每个已登记的设备一项
//   ──
//   扩展屏幕            ✓          只在另一块屏是笔记本自带屏时出现
//   只用主屏
//   ──
//   设置…
//   退出

import Foundation

enum Ownership { case owned, notOwned, daemonDown }

// 需要用户处理的问题
enum Problem: Equatable {
    case notConfigured
    case daemonDown
    case hotkeyFailed(HotKeyTarget, String)     // 动作，组合（⌃⌥1）
    case notificationsOff

    // menu.problems 文件与 status 的 problems 里的标识（英文，不翻译）
    var id: String {
        switch self {
        case .notConfigured: return "not-configured"
        case .daemonDown: return "daemon-not-running"
        case .hotkeyFailed(let t, _): return t.problemID
        case .notificationsOff: return "notifications-off"
        }
    }

    var text: String {
        switch self {
        case .notConfigured: return L("menu.problem.notConfigured")
        case .daemonDown: return L("menu.problem.daemonDown")
        case .hotkeyFailed(_, let combo): return L("menu.problem.hotkey", combo)
        case .notificationsOff: return L("mac.menu.problem.notifications")
        }
    }

    // 菜单栏程序自己发现、要写进 menu.problems 给 status 看的（另两个 status 自己能查）
    var reportedByMenu: Bool {
        switch self {
        case .hotkeyFailed, .notificationsOff: return true
        default: return false
        }
    }
}

enum MenuAction: Equatable {
    case give([String])          // screenctl.sh give peer | self | code <n>
    case setLayout(String)       // extend | main-only
    case problem(Problem)
    case settings
    case quit
}

struct MenuEntry: Equatable {
    var title = ""
    var action: MenuAction?
    var enabled = true
    var checked = false          // ✓ 画在文字右边
    var hotkey: HotKey?
    var isSeparator = false

    static let separator = MenuEntry(isSeparator: true)
}

func buildMenuEntries(own: Ownership, busy: Bool, info: MenuInfo, problems: [Problem],
                      hotkeys: [HotKeyTarget: HotKey]) -> [MenuEntry] {
    var e: [MenuEntry] = []
    for p in problems {
        e.append(MenuEntry(title: "⚠ " + p.text, action: .problem(p)))
    }
    e.append(.separator)
    if info.configured {
        // 第一项随状态变；守护进程没在运行时不知道归属，两项都给
        let peerItem = MenuEntry(title: L("menu.switchTo", info.peer), action: .give(["peer"]), enabled: !busy)
        let takeItem = MenuEntry(title: L("menu.takeBack"), action: .give(["self"]), enabled: !busy)
        switch own {
        case .owned:
            if !info.peer.isEmpty { var i = peerItem; i.hotkey = hotkeys[.peer]; e.append(i) }
        case .notOwned:
            var i = takeItem; i.hotkey = hotkeys[.peer]; e.append(i)
        case .daemonDown:
            var i = takeItem; i.hotkey = hotkeys[.peer]; e.append(i)
            if !info.peer.isEmpty { e.append(peerItem) }
        }
        for d in info.devices {
            e.append(MenuEntry(title: L("menu.switchTo", d.name), action: .give(["code", "\(d.code)"]),
                               enabled: !busy, hotkey: hotkeys[.device(d.code)]))
        }
        e.append(.separator)
        // 只有主屏、或另一块屏是外接屏（只有扩展一种）时不显示
        if info.layout != "none" && info.secondBuiltin {
            let mainOnly = info.ownedLayout == "external"
            e.append(MenuEntry(title: L("menu.extend"), action: .setLayout("extend"), checked: !mainOnly))
            e.append(MenuEntry(title: L("menu.mainOnly"), action: .setLayout("main-only"), checked: mainOnly))
            e.append(.separator)
        }
    }
    e.append(MenuEntry(title: L("menu.settings"), action: .settings))
    e.append(MenuEntry(title: L("menu.quit"), action: .quit))
    // 去掉开头、结尾和连续的分隔线
    var out: [MenuEntry] = []
    for item in e {
        if item.isSeparator && (out.isEmpty || out.last!.isSeparator) { continue }
        out.append(item)
    }
    while out.last?.isSeparator == true { out.removeLast() }
    return out
}
