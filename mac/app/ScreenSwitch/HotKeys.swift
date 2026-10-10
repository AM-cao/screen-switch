// 全局快捷键：Carbon RegisterEventHotKey（公开 API，无需辅助功能权限）。默认一个都不注册，
// 只有用户在设置里给"切给对方 / 拿回主屏"或某个设备设了才注册（docs/settings-ui-design.md 第 3、5 节）。
// 写法与命令层相同：ctrl/opt/cmd 至少一个（可以再加 shift），加一个数字或字母，用 + 连接，如 ctrl+opt+1。

import AppKit
import Carbon.HIToolbox

struct HotKey: Equatable {
    let spec: String            // 规范写法，如 ctrl+opt+1
    let keyCode: UInt32
    let carbonModifiers: UInt32
    let menuKey: String
    let menuModifiers: NSEvent.ModifierFlags
    let display: String         // ⌃⌥1

    static let keyCodes: [String: Int] = [
        "0": kVK_ANSI_0, "1": kVK_ANSI_1, "2": kVK_ANSI_2, "3": kVK_ANSI_3, "4": kVK_ANSI_4,
        "5": kVK_ANSI_5, "6": kVK_ANSI_6, "7": kVK_ANSI_7, "8": kVK_ANSI_8, "9": kVK_ANSI_9,
        "a": kVK_ANSI_A, "b": kVK_ANSI_B, "c": kVK_ANSI_C, "d": kVK_ANSI_D, "e": kVK_ANSI_E,
        "f": kVK_ANSI_F, "g": kVK_ANSI_G, "h": kVK_ANSI_H, "i": kVK_ANSI_I, "j": kVK_ANSI_J,
        "k": kVK_ANSI_K, "l": kVK_ANSI_L, "m": kVK_ANSI_M, "n": kVK_ANSI_N, "o": kVK_ANSI_O,
        "p": kVK_ANSI_P, "q": kVK_ANSI_Q, "r": kVK_ANSI_R, "s": kVK_ANSI_S, "t": kVK_ANSI_T,
        "u": kVK_ANSI_U, "v": kVK_ANSI_V, "w": kVK_ANSI_W, "x": kVK_ANSI_X, "y": kVK_ANSI_Y,
        "z": kVK_ANSI_Z,
    ]

    // "ctrl+opt+1" → HotKey；写法不对返回 nil
    static func parse(_ text: String) -> HotKey? {
        let parts = text.lowercased().split(separator: "+").map { $0.trimmingCharacters(in: .whitespaces) }
        guard parts.count >= 2, let key = parts.last, let code = keyCodes[key] else { return nil }
        var carbon: UInt32 = 0
        var flags: NSEvent.ModifierFlags = []
        for m in parts.dropLast() {
            switch m {
            case "ctrl", "control": carbon |= UInt32(controlKey); flags.insert(.control)
            case "opt", "option", "alt": carbon |= UInt32(optionKey); flags.insert(.option)
            case "shift": carbon |= UInt32(shiftKey); flags.insert(.shift)
            case "cmd", "command", "win": carbon |= UInt32(cmdKey); flags.insert(.command)
            default: return nil
            }
        }
        guard !flags.intersection([.control, .option, .command]).isEmpty else { return nil }   // 只带 Shift 不行
        return make(key: key, keyCode: UInt32(code), flags: flags, carbon: carbon)
    }

    static func make(key: String, keyCode: UInt32, flags: NSEvent.ModifierFlags, carbon: UInt32) -> HotKey {
        var spec = "", display = ""
        if flags.contains(.control) { spec += "ctrl+"; display += "⌃" }
        if flags.contains(.option) { spec += "opt+"; display += "⌥" }
        if flags.contains(.shift) { spec += "shift+"; display += "⇧" }
        if flags.contains(.command) { spec += "cmd+"; display += "⌘" }
        return HotKey(spec: spec + key, keyCode: keyCode, carbonModifiers: carbon, menuKey: key,
                      menuModifiers: flags, display: display + key.uppercased())
    }

    // 录制：按下的键 → HotKey；没有修饰键或不是数字字母时返回 nil
    static func from(event: NSEvent) -> HotKey? {
        guard let key = keyCodes.first(where: { $0.value == Int(event.keyCode) })?.key else { return nil }
        let flags = event.modifierFlags.intersection([.control, .option, .shift, .command])
        guard !flags.intersection([.control, .option, .command]).isEmpty else { return nil }   // 只带 Shift 不行
        var carbon: UInt32 = 0
        if flags.contains(.control) { carbon |= UInt32(controlKey) }
        if flags.contains(.option) { carbon |= UInt32(optionKey) }
        if flags.contains(.shift) { carbon |= UInt32(shiftKey) }
        if flags.contains(.command) { carbon |= UInt32(cmdKey) }
        return make(key: key, keyCode: UInt32(event.keyCode), flags: flags, carbon: carbon)
    }

    static func displayText(_ spec: String) -> String {
        parse(spec)?.display ?? spec
    }
}

// 快捷键的动作：peer = 切给对方 / 拿回主屏（按当前状态）；device(编码) = 切给某个设备
enum HotKeyTarget: Hashable {
    case peer
    case device(Int)

    var id: UInt32 {
        switch self {
        case .peer: return 1
        case .device(let c): return 1000 + UInt32(c)
        }
    }

    // menu.problems 与 status 里用的标识：hotkey-failed:peer / hotkey-failed:<编码>
    var problemID: String {
        switch self {
        case .peer: return "hotkey-failed:peer"
        case .device(let c): return "hotkey-failed:\(c)"
        }
    }
}

private let hotKeyHandler: EventHandlerUPP = { _, event, _ in
    var id = EventHotKeyID()
    let status = GetEventParameter(event, EventParamName(kEventParamDirectObject),
                                   EventParamType(typeEventHotKeyID), nil,
                                   MemoryLayout<EventHotKeyID>.size, nil, &id)
    if status == noErr {
        let n = id.id
        DispatchQueue.main.async { HotKeyManager.shared.fire(n) }
    }
    return noErr
}

final class HotKeyManager {
    static let shared = HotKeyManager()

    private(set) var registered: [HotKeyTarget: HotKey] = [:]
    private(set) var failures: [HotKeyTarget: String] = [:]   // 动作 → 没生效的组合（⌃⌥1）
    private var refs: [EventHotKeyRef] = []
    private var installed = false
    private var suspended = false
    private var wanted: [(HotKeyTarget, String)] = []
    private var didUpdate = false
    var onFire: ((HotKeyTarget) -> Void)?
    var onChange: (() -> Void)?

    // 按 menu-info 重新注册
    func update(_ info: MenuInfo) {
        var w: [(HotKeyTarget, String)] = []
        if !info.hotkeyPeer.isEmpty { w.append((.peer, info.hotkeyPeer)) }
        for d in info.devices where !d.hotkey.isEmpty { w.append((.device(d.code), d.hotkey)) }
        if didUpdate && w.map({ "\($0.0)=\($0.1)" }) == wanted.map({ "\($0.0)=\($0.1)" }) { return }
        didUpdate = true
        wanted = w
        if !suspended { registerAll() }
    }

    // 录制快捷键时先全部注销，否则按下已有的组合会直接触发切换
    func suspend() {
        guard !suspended else { return }
        suspended = true
        unregisterAll()
    }

    func resume() {
        guard suspended else { return }
        suspended = false
        registerAll()
    }

    func fire(_ id: UInt32) {
        if let t = registered.keys.first(where: { $0.id == id }) { onFire?(t) }
    }

    private func unregisterAll() {
        for r in refs { UnregisterEventHotKey(r) }
        refs = []
        registered = [:]
    }

    private func registerAll() {
        if !installed {
            var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
            InstallEventHandler(GetApplicationEventTarget(), hotKeyHandler, 1, &spec, nil, nil)
            installed = true
        }
        unregisterAll()
        failures = [:]
        for (target, text) in wanted {
            guard let hk = HotKey.parse(text) else { failures[target] = text; continue }
            var ref: EventHotKeyRef?
            let status = RegisterEventHotKey(hk.keyCode, hk.carbonModifiers,
                                             EventHotKeyID(signature: OSType(0x5343_5357), id: target.id),
                                             GetApplicationEventTarget(), 0, &ref)
            if status == noErr, let ref = ref {
                registered[target] = hk
                refs.append(ref)
            } else {
                failures[target] = hk.display
                logLine("hotkey \(hk.spec) for \(target) failed: \(status)")
            }
        }
        onChange?()
    }
}

// 录制快捷键的按钮：点一下开始录，按下组合键即录下；Esc 取消
final class HotKeyRecorder: NSButton {
    var spec = "" { didSet { refreshTitle() } }
    var onRecord: ((String) -> Void)?
    private var recording = false

    convenience init(spec: String) {
        self.init(frame: .zero)
        bezelStyle = .rounded
        setButtonType(.momentaryPushIn)
        target = self
        action = #selector(startRecording)
        self.spec = spec
        refreshTitle()
        widthAnchor.constraint(greaterThanOrEqualToConstant: 150).isActive = true
    }

    override var acceptsFirstResponder: Bool { true }

    private func refreshTitle() {
        if recording { title = L("mac.hotkeys.recording") }
        else if spec.isEmpty { title = L("mac.hotkeys.placeholder") }
        else { title = HotKey.displayText(spec) }
    }

    @objc private func startRecording() {
        recording = true
        HotKeyManager.shared.suspend()
        window?.makeFirstResponder(self)
        refreshTitle()
    }

    private func stop() {
        guard recording else { return }
        recording = false
        HotKeyManager.shared.resume()
        refreshTitle()
    }

    override func keyDown(with event: NSEvent) {
        guard recording else { super.keyDown(with: event); return }
        if event.keyCode == UInt16(kVK_Escape) { stop(); return }
        guard let hk = HotKey.from(event: event) else { NSSound.beep(); return }
        stop()
        onRecord?(hk.spec)
    }

    // 带 ⌘ 的组合会先走 performKeyEquivalent
    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        guard recording, window?.firstResponder === self else { return super.performKeyEquivalent(with: event) }
        keyDown(with: event)
        return true
    }

    override func resignFirstResponder() -> Bool {
        stop()
        return super.resignFirstResponder()
    }
}
