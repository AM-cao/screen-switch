// --ui-dump <目录> [dark]：把设置窗口的五个标签页、向导的五页和菜单画成 PNG（docs/settings-ui-design.md 第 12 节），
// 不安装也能审阅。数据取自当前的配置（SCREENCTL_DIR 可以指向别的目录）；向导不读写草稿。
// 菜单没法离屏绘制，menu-*.png 是按同样的菜单项画的示意图。

import AppKit

var dumpMode = false

func uiDump(to dir: String, dark: Bool) {
    dumpMode = true
    try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
    let appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
    NSApp.appearance = appearance
    var written: [String] = []
    func save(_ window: NSWindow, _ name: String) {
        window.appearance = appearance
        // 放到屏幕外显示出来，标签栏等控件才按"活动窗口"画
        window.setFrameOrigin(NSPoint(x: -20000, y: -20000))
        window.orderFrontRegardless()
        defer { window.orderOut(nil) }
        guard let view = window.contentView else { return }
        window.layoutIfNeeded()
        view.layoutSubtreeIfNeeded()
        // cacheDisplay 不画窗口背景：临时垫一层窗口背景色
        let bg = BackgroundView(frame: view.bounds)
        view.addSubview(bg, positioned: .below, relativeTo: nil)
        defer { bg.removeFromSuperview() }
        guard let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return }
        view.cacheDisplay(in: view.bounds, to: rep)
        guard let png = rep.representation(using: .png, properties: [:]) else { return }
        let path = (dir as NSString).appendingPathComponent(name + ".png")
        try? png.write(to: URL(fileURLWithPath: path))
        written.append(path)
    }

    // 设置窗口
    let settings = SettingsWindowController()
    settings.reloadAll()
    for t in SettingsTab.allCases {
        settings.tabView.selectTabViewItem(at: t.rawValue)
        if t == .general { settings.advancedOpen = true; settings.render(.general) }
        save(settings.window!, String(format: "settings-%d-%@", t.rawValue + 1, "\(t)"))
    }

    // 向导：从现有设置开始，同步读显示器和输入口
    let wizard = WizardWindowController()
    wizard.draft = WizardDraft.fromCurrent()
    wizard.scan = DisplayScan.load()
    if wizard.draft.mainUUID.isEmpty {
        wizard.draft.mainUUID = MainPageState.autoSelect(wizard.scan!, previous: "", previousName: wizard.draft.mainName)
        if let d = wizard.scan!.displays.first(where: { $0.uuid == wizard.draft.mainUUID }) { wizard.draft.selfCode = d.code }
    }
    wizard.mergePorts(PortList.load(main: wizard.draft.mainUUID), uuid: wizard.draft.mainUUID)
    for p in WizardPage.allCases {
        wizard.page = p
        wizard.render()
        save(wizard.window!, String(format: "wizard-%d-%@", p.rawValue + 1, "\(p)"))
    }

    // 菜单示意：主屏在本机 / 不在本机 / 有问题
    let info = MenuInfo.load()
    let samples: [(String, Ownership, [Problem])] = [("menu-1-owned", .owned, []), ("menu-2-not-owned", .notOwned, []),
                                                    ("menu-3-problem", .notOwned, [.hotkeyFailed(.peer, "⌃⌥1")])]
    for (name, own, problems) in samples {
        let entries = buildMenuEntries(own: own, busy: false, info: info, problems: problems,
                                       hotkeys: HotKeyManager.shared.registered)
        if let png = drawMenu(entries, appearance: appearance) {
            let path = (dir as NSString).appendingPathComponent(name + ".png")
            try? png.write(to: URL(fileURLWithPath: path))
            written.append(path)
        }
    }
    for p in written { print(p) }
}

final class BackgroundView: NSView {
    override func draw(_ dirtyRect: NSRect) {
        NSColor.windowBackgroundColor.setFill()
        dirtyRect.fill()
    }
}

// 按菜单项画一张示意图：文字左对齐，不留勾选列，对号和快捷键靠右
func drawMenu(_ entries: [MenuEntry], appearance: NSAppearance?) -> Data? {
    let font = NSFont.menuFont(ofSize: 0)
    let rowH: CGFloat = 22, sepH: CGFloat = 11, padX: CGFloat = 14
    let widest = entries.map { ($0.title as NSString).size(withAttributes: [.font: font]).width }.max() ?? 100
    let width = max(widest + 36, 160) + padX * 2 + 40
    let height = entries.reduce(CGFloat(10)) { $0 + ($1.isSeparator ? sepH : rowH) }
    let image = NSImage(size: NSSize(width: width, height: height), flipped: true) { rect in
        appearance?.performAsCurrentDrawingAppearance {
            NSColor.windowBackgroundColor.setFill()
            NSBezierPath(roundedRect: rect, xRadius: 8, yRadius: 8).fill()
            var y: CGFloat = 5
            for e in entries {
                if e.isSeparator {
                    NSColor.separatorColor.setFill()
                    NSRect(x: padX, y: y + 5, width: rect.width - padX * 2, height: 1).fill()
                    y += sepH
                    continue
                }
                let color: NSColor = e.enabled ? .labelColor : .disabledControlTextColor
                let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color]
                (e.title as NSString).draw(at: NSPoint(x: padX, y: y + 3), withAttributes: attrs)
                var right = rect.width - padX
                if let hk = e.hotkey {
                    let s = hk.display as NSString
                    let w = s.size(withAttributes: attrs).width
                    s.draw(at: NSPoint(x: right - w, y: y + 3), withAttributes: [.font: font, .foregroundColor: NSColor.secondaryLabelColor])
                    right -= w + 12
                }
                if e.checked {
                    let s = "✓" as NSString
                    s.draw(at: NSPoint(x: right - s.size(withAttributes: attrs).width, y: y + 3), withAttributes: attrs)
                }
                y += rowH
            }
        }
        return true
    }
    guard let tiff = image.tiffRepresentation, let bmp = NSBitmapImageRep(data: tiff) else { return nil }
    return bmp.representation(using: .png, properties: [:])
}
