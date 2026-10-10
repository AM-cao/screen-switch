// 设置窗口与向导共用的两块：输入口表（第 4 节第 3 页、第 5 节"输入口"）和对方电脑表单（第 4 页、"对方电脑"）。
// 它们只管显示和收集改动；改动立即写配置（设置窗口）还是先记在草稿里（向导），由使用者决定。

import AppKit

// ---- 输入口表：一口一行，选它接的是什么

final class PortsEditor {
    var ports: [Port]
    var peerName: String
    // 某一行改了（old → new）。选"设备"后要等名字填好才回调
    var onChange: ((Port, Port) -> Void)?
    var onAdd: ((Int) -> Void)?
    weak var window: NSWindow?
    let view = NSStackView()

    // 下拉框的选项（这台电脑那一行只有一个固定项，不能改）
    static let uses = ["peer", "device", "none"]

    init(ports: [Port], peerName: String) {
        self.ports = ports
        self.peerName = peerName
        view.orientation = .vertical
        view.alignment = .leading
        view.spacing = 8
        render()
    }

    func render() {
        view.arrangedSubviews.forEach { $0.removeFromSuperview() }
        let header = hstack([label(L("mac.ports.header.port"), bold: true, width: 190), label(L("mac.ports.header.use"), bold: true)])
        (header.arrangedSubviews[0] as? NSTextField)?.widthAnchor.constraint(equalToConstant: 190).isActive = true
        view.addArrangedSubview(header)
        for (i, p) in ports.enumerated() { view.addArrangedSubview(row(i, p)) }
        view.addArrangedSubview(ActionButton(L("ports.add")) { [weak self] in self?.askAdd() })
    }

    private func row(_ i: Int, _ p: Port) -> NSView {
        let name = label(p.title)
        name.widthAnchor.constraint(equalToConstant: 190).isActive = true
        if p.use == "self" {
            let popup = PopUp([L("port.use.self")], selected: 0) { _ in }
            popup.isEnabled = false
            return hstack([name, popup])
        }
        let titles = [peerName.isEmpty ? L("port.use.peer") : L("mac.ports.use.peerNamed", peerName),
                      L("port.use.device"), L("port.use.none")]
        let selected = Self.uses.firstIndex(of: p.use) ?? 2
        let field = TextBox(p.device, placeholder: L("mac.ports.deviceName"), width: 170) { [weak self] v in
            self?.deviceNamed(i, v)
        }
        field.isHidden = p.use != "device"
        let popup = PopUp(titles, selected: selected) { [weak self] idx in
            self?.useChanged(i, Self.uses[idx], field)
        }
        return hstack([name, popup, field])
    }

    // 改了某一行的用途。选"设备"时先等名字（记下这一行原来的样子，名字填好时一起交给 onChange）
    private func useChanged(_ i: Int, _ use: String, _ field: TextBox) {
        let before = pendingFrom.removeValue(forKey: i) ?? ports[i]
        var new = ports[i]
        new.use = use
        new.device = use == "device" ? before.device : ""
        ports[i] = new
        field.isHidden = use != "device"
        if use == "device" {
            field.setValueQuietly(new.device)
            window?.makeFirstResponder(field)
            if new.device.isEmpty { pendingFrom[i] = before; return }
        }
        // 对方电脑只能有一个口：别的口原来是对方的改成没接
        if use == "peer" {
            for j in ports.indices where j != i && ports[j].use == "peer" { ports[j].use = "none" }
        }
        if before.use != use || before.device != new.device { onChange?(before, new) }
        if use == "peer" { render() }
    }

    private func deviceNamed(_ i: Int, _ name: String) {
        guard i < ports.count, ports[i].use == "device", !name.isEmpty else { return }
        let before = pendingFrom.removeValue(forKey: i) ?? ports[i]
        ports[i].device = name
        if before.use != "device" || before.device != name { onChange?(before, ports[i]) }
    }

    // 选了"设备"但还没填名字的行：原来的样子
    private var pendingFrom: [Int: Port] = [:]

    private func askAdd() {
        promptText(L("ports.add.prompt"), in: window) { [weak self] text in
            guard let self = self, let t = text, !t.isEmpty else { return }
            guard let c = Int(t), (1...255).contains(c) else { showError(L("mac.ports.badCode", t), in: self.window); return }
            guard !self.ports.contains(where: { $0.code == c }) else { showError(L("mac.ports.exists", t), in: self.window); return }
            self.ports.append(Port(code: c, name: Port.standardName(c), use: "none", device: "", fromMonitor: false))
            self.ports.sort { $0.code < $1.code }
            self.onAdd?(c)
            self.render()
        }
    }

    // 向导的检查：设备都要有名字，名字不能重复
    static func validate(_ ports: [Port]) -> String? {
        var names = Set<String>()
        for p in ports where p.use == "device" {
            let n = p.device.trimmingCharacters(in: .whitespaces)
            if n.isEmpty { return L("ports.err.name", p.title) }
            if names.contains(n) { return L("mac.ports.dupName", n) }
            names.insert(n)
        }
        return nil
    }
}

// ---- 对方电脑表单

struct PeerFields: Equatable {
    var name = ""
    var os = "windows"
    var host = ""
    var user = ""

    var complete: Bool { !host.isEmpty && !user.isEmpty }
    var empty: Bool { host.isEmpty && user.isEmpty && name.isEmpty }

    // peer set / peer test 的参数
    var args: [String] {
        var a = ["--os", os, "--host", host, "--user", user]
        if !name.isEmpty { a += ["--name", name] }
        return a
    }
}

final class PeerEditor {
    // 公钥打码：类型 + 圆点
    static func maskedKey(_ key: String) -> String {
        let type = key.split(separator: " ").first.map(String.init) ?? ""
        return (type.hasPrefix("ssh-") || type.hasPrefix("ecdsa-") ? type + " " : "") + String(repeating: "•", count: 12)
    }

    var fields: PeerFields
    let inWizard: Bool
    var onFieldsChanged: ((PeerFields) -> Void)?
    // 测试连接：使用者负责运行命令，结果（文字，是否警告）交回来显示
    var onTest: ((@escaping (String, Bool) -> Void) -> Void)?
    var onClear: (() -> Void)?
    weak var window: NSWindow?
    let view: NSStackView
    private let keyLabel = label("", small: true, wraps: true, width: 380)
    private let authResult = helpText("")
    private let openSharing: ActionButton
    private let testResult = label("", wraps: true, width: 380)
    private let incompleteHint = helpText("")

    init(fields: PeerFields, inWizard: Bool) {
        self.fields = fields
        self.inWizard = inWizard
        view = vstack([], spacing: 10)
        openSharing = ActionButton(L("mac.peer.openSharing")) {
            NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.Sharing-Settings.extension")!)
        }
        build()
    }

    private func fieldRow(_ title: String, _ control: NSView, _ note: String = "") -> NSView {
        let l = label(title)
        l.widthAnchor.constraint(equalToConstant: 175).isActive = true
        var views: [NSView] = [l, control]
        if !note.isEmpty { views.append(label(note, small: true, secondary: true)) }
        return hstack(views)
    }

    private func build() {
        let nameBox = TextBox(fields.name, placeholder: fields.os == "mac" ? "Mac" : "Windows") { [weak self] v in
            self?.fields.name = v; self?.changed()
        }
        let osPopup = PopUp(["Windows", "Mac"], selected: fields.os == "mac" ? 1 : 0) { [weak self] i in
            self?.fields.os = i == 1 ? "mac" : "windows"
            nameBox.placeholderString = i == 1 ? "Mac" : "Windows"
            self?.changed()
        }
        let hostBox = TextBox(fields.host, placeholder: "my-pc.local") { [weak self] v in self?.fields.host = v; self?.changed() }
        let userBox = TextBox(fields.user) { [weak self] v in self?.fields.user = v; self?.changed() }

        view.addArrangedSubview(fieldRow(L("peer.name"), nameBox))
        view.addArrangedSubview(fieldRow(L("peer.os"), osPopup))
        view.addArrangedSubview(fieldRow(L("peer.host"), hostBox))
        view.addArrangedSubview(fieldRow(L("peer.user"), userBox))
        if !inWizard {
            incompleteHint.stringValue = L("mac.peer.incomplete")
            incompleteHint.isHidden = fields.complete || fields.empty
            view.addArrangedSubview(incompleteHint)
        }
        view.addArrangedSubview(separatorLine())

        // 这台电脑的公钥：没有就生成
        view.addArrangedSubview(label(L("mac.peer.selfKey"), bold: true))
        let pub = Script.run(["pubkey"])
        let key = pub.ok ? pub.output.trimmingCharacters(in: .whitespacesAndNewlines) : pub.message
        // 不明文显示，只留类型（如 ssh-ed25519 ••••••••••••）；"复制"照常复制完整公钥
        keyLabel.stringValue = pub.ok ? Self.maskedKey(key) : key
        keyLabel.font = .monospacedSystemFont(ofSize: NSFont.smallSystemFontSize, weight: .regular)
        let copy = ActionButton(L("peer.copy")) { [weak self] in
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(key, forType: .string)
            self?.showCopied()
        }
        copy.isEnabled = pub.ok
        view.addArrangedSubview(hstack([keyLabel, copy]))
        view.addArrangedSubview(helpText(L("mac.peer.selfKeyHelp")))

        // 对方公钥 + 授权
        let peerKey = NSTextField()
        peerKey.placeholderString = "ssh-ed25519 AAAA…"
        peerKey.widthAnchor.constraint(equalToConstant: 380).isActive = true
        let authorize = ActionButton(L("peer.authorize")) { [weak self] in
            guard let self = self else { return }
            let k = peerKey.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
            let r = Script.run(["authorize", k])
            if r.ok {
                self.authResult.stringValue = L("peer.authorized") + " " + L("mac.peer.remoteLogin")
                self.openSharing.isHidden = false
                peerKey.stringValue = ""
            } else {
                self.authResult.stringValue = r.message
                self.openSharing.isHidden = true
            }
            self.authResult.isHidden = false
        }
        view.addArrangedSubview(label(L("peer.theirkey"), bold: true))
        view.addArrangedSubview(hstack([peerKey, authorize]))
        authResult.isHidden = true
        openSharing.isHidden = true
        view.addArrangedSubview(authResult)
        view.addArrangedSubview(openSharing)
        view.addArrangedSubview(separatorLine())

        // 测试连接
        let test = ActionButton(L("peer.test")) {}
        test.onClick = { [weak self, weak test] in
            guard let self = self else { return }
            self.view.window?.makeFirstResponder(nil)   // 正在编辑的文字框先生效
            guard self.fields.complete else { self.showTest(L("mac.peer.needHost"), warn: true); return }
            test?.isEnabled = false
            self.showTest(L("peer.testing"), warn: false)
            self.onTest? { text, warn in
                test?.isEnabled = true
                self.showTest(text, warn: warn)
            }
        }
        var bottom: [NSView] = [test]
        if !inWizard {
            bottom.append(ActionButton(L("peer.clear")) { [weak self] in self?.onClear?() })
        }
        view.addArrangedSubview(hstack(bottom))
        testResult.isHidden = true
        view.addArrangedSubview(testResult)
    }

    private func showCopied() {
        authResult.stringValue = L("peer.copied")
        authResult.isHidden = false
        openSharing.isHidden = true
    }

    private func showTest(_ text: String, warn: Bool) {
        testResult.stringValue = text
        testResult.textColor = warn ? .systemOrange : .labelColor
        testResult.isHidden = false
    }

    private func changed() {
        incompleteHint.isHidden = fields.complete || fields.empty
        onFieldsChanged?(fields)
    }

    // "已连上 X，它接在 Y"；与输入口页标的不一致时提醒。expected = 输入口页标为对方的口
    static func describeTest(_ r: ScriptResult, expected: Int?) -> (String, Bool, Int?) {
        guard let j = Script.json(r) else { return (r.message, true, nil) }
        let name = j["name"] as? String ?? ""
        guard let code = j["code"] as? Int, code > 0 else { return (L("peer.reached-nocode", name), false, nil) }
        let port = Port(code: code, name: Port.standardName(code), use: "peer", device: "", fromMonitor: false).title
        var text = L("peer.reached", name, port)
        var warn = false
        if let e = expected, e != code {
            let was = Port(code: e, name: Port.standardName(e), use: "peer", device: "", fromMonitor: false).title
            text += "\n" + L("mac.peer.testMismatch", was, port)
            warn = true
        }
        return (text, warn, code)
    }
}
