// 设置窗口与向导共用的小部件：带回调的按钮、勾选框、下拉框、文字框，以及排版用的竖排 / 横排。

import AppKit

final class ActionButton: NSButton {
    var onClick: (() -> Void)?

    convenience init(_ title: String, _ onClick: @escaping () -> Void) {
        self.init(frame: .zero)
        self.title = title
        bezelStyle = .rounded
        setButtonType(.momentaryPushIn)
        self.onClick = onClick
        target = self
        action = #selector(clicked)
    }

    @objc private func clicked() { onClick?() }
}

final class CheckBox: NSButton {
    var onChange: ((Bool) -> Void)?

    convenience init(_ title: String, checked: Bool, _ onChange: @escaping (Bool) -> Void) {
        self.init(frame: .zero)
        self.title = title
        setButtonType(.switch)
        state = checked ? .on : .off
        self.onChange = onChange
        target = self
        action = #selector(changed)
    }

    var isChecked: Bool { state == .on }

    @objc private func changed() { onChange?(state == .on) }
}

final class Radio: NSButton {
    var onSelect: (() -> Void)?

    convenience init(_ title: String, selected: Bool, _ onSelect: @escaping () -> Void) {
        self.init(frame: .zero)
        self.title = title
        setButtonType(.radio)
        state = selected ? .on : .off
        self.onSelect = onSelect
        target = self
        action = #selector(Radio.chosen)
    }

    @objc private func chosen() { onSelect?() }
}

final class PopUp: NSPopUpButton {
    var onChange: ((Int) -> Void)?

    convenience init(_ items: [String], selected: Int, _ onChange: @escaping (Int) -> Void) {
        self.init(frame: .zero, pullsDown: false)
        addItems(withTitles: items)
        selectItem(at: selected)
        self.onChange = onChange
        target = self
        action = #selector(changed)
    }

    @objc private func changed() { onChange?(indexOfSelectedItem) }
}

// 文字框：回车或失去焦点时生效（docs/settings-ui-design.md 第 5 节），内容没变不触发
final class TextBox: NSTextField, NSTextFieldDelegate {
    var onCommit: ((String) -> Void)?
    private var committed = ""

    convenience init(_ value: String, placeholder: String = "", width: CGFloat = 220, _ onCommit: @escaping (String) -> Void) {
        self.init(frame: .zero)
        stringValue = value
        committed = value
        placeholderString = placeholder
        self.onCommit = onCommit
        delegate = self
        lineBreakMode = .byTruncatingTail
        widthAnchor.constraint(equalToConstant: width).isActive = true
    }

    func controlTextDidEndEditing(_ obj: Notification) { commit() }

    func commit() {
        let v = stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        guard v != committed else { return }
        committed = v
        onCommit?(v)
    }

    // 外部改了值（比如重新读配置）时同步，不触发回调
    func setValueQuietly(_ v: String) {
        stringValue = v
        committed = v
    }
}

func label(_ text: String, bold: Bool = false, small: Bool = false, secondary: Bool = false, wraps: Bool = false,
           width: CGFloat? = nil) -> NSTextField {
    let l = wraps ? NSTextField(wrappingLabelWithString: text) : NSTextField(labelWithString: text)
    let size = small ? NSFont.smallSystemFontSize : NSFont.systemFontSize
    l.font = bold ? .boldSystemFont(ofSize: size) : .systemFont(ofSize: size)
    if secondary { l.textColor = .secondaryLabelColor }
    l.isSelectable = wraps
    if let w = width {
        l.preferredMaxLayoutWidth = w
        l.widthAnchor.constraint(lessThanOrEqualToConstant: w).isActive = true
    }
    return l
}

// 小号灰字说明，自动换行
func helpText(_ text: String, width: CGFloat = 470) -> NSTextField {
    label(text, small: true, secondary: true, wraps: true, width: width)
}

func vstack(_ views: [NSView], spacing: CGFloat = 8, alignment: NSLayoutConstraint.Attribute = .leading) -> NSStackView {
    let s = NSStackView(views: views)
    s.orientation = .vertical
    s.alignment = alignment
    s.spacing = spacing
    return s
}

func hstack(_ views: [NSView], spacing: CGFloat = 8) -> NSStackView {
    let s = NSStackView(views: views)
    s.orientation = .horizontal
    s.alignment = .firstBaseline
    s.spacing = spacing
    return s
}

func separatorLine() -> NSBox {
    let b = NSBox()
    b.boxType = .separator
    return b
}

// 把 stack 放进一个固定边距的容器（标签页、向导页的内容区）
func padded(_ content: NSView, inset: CGFloat = 20) -> NSView {
    let v = NSView()
    content.translatesAutoresizingMaskIntoConstraints = false
    v.addSubview(content)
    NSLayoutConstraint.activate([
        content.topAnchor.constraint(equalTo: v.topAnchor, constant: inset),
        content.leadingAnchor.constraint(equalTo: v.leadingAnchor, constant: inset),
        content.trailingAnchor.constraint(lessThanOrEqualTo: v.trailingAnchor, constant: -inset),
        content.bottomAnchor.constraint(lessThanOrEqualTo: v.bottomAnchor, constant: -inset),
    ])
    return v
}

// 内容替换：清空容器后放入新内容，四边贴齐
func replaceContent(of container: NSView, with view: NSView) {
    container.subviews.forEach { $0.removeFromSuperview() }
    view.translatesAutoresizingMaskIntoConstraints = false
    container.addSubview(view)
    NSLayoutConstraint.activate([
        view.topAnchor.constraint(equalTo: container.topAnchor),
        view.leadingAnchor.constraint(equalTo: container.leadingAnchor),
        view.trailingAnchor.constraint(equalTo: container.trailingAnchor),
        view.bottomAnchor.constraint(equalTo: container.bottomAnchor),
    ])
}

func showError(_ message: String, in window: NSWindow?) {
    let a = NSAlert()
    a.alertStyle = .warning
    a.messageText = message
    a.addButton(withTitle: L("common.ok"))
    if let w = window { a.beginSheetModal(for: w) } else { a.runModal() }
}

// 一个带文字框的对话框（添加输入口时问编码）；取消返回 nil
func promptText(_ message: String, in window: NSWindow?, _ done: @escaping (String?) -> Void) {
    let a = NSAlert()
    a.messageText = message
    a.addButton(withTitle: L("common.ok"))
    a.addButton(withTitle: L("wizard.cancel"))
    let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 200, height: 24))
    a.accessoryView = field
    a.window.initialFirstResponder = field
    let handler: (NSApplication.ModalResponse) -> Void = { r in
        done(r == .alertFirstButtonReturn ? field.stringValue.trimmingCharacters(in: .whitespaces) : nil)
    }
    if let w = window { a.beginSheetModal(for: w, completionHandler: handler) } else { handler(a.runModal()) }
}
