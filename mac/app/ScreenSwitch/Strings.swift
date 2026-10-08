// 中英文（docs/settings-ui-design.md 第 8 节）：文字取自文字表 strings.tsv（每行"编号<Tab>中文<Tab>英文"）。
// 文字表在 .app 的 Resources 里；从仓库直接运行时用 SCREENSWITCH_STRINGS 或 ~/screenctl/strings.tsv。
// 语言跟随系统界面语言：zh 开头用中文，否则英文；SCREENSWITCH_LANG=zh|en 可以指定。

import Foundation

enum Strings {
    static let isChinese: Bool = {
        if let l = ProcessInfo.processInfo.environment["SCREENSWITCH_LANG"], !l.isEmpty { return l.hasPrefix("zh") }
        return (Locale.preferredLanguages.first ?? "en").hasPrefix("zh")
    }()

    static var langCode: String { isChinese ? "zh" : "en" }

    static let table: [String: (zh: String, en: String)] = {
        var paths: [String] = []
        if let p = ProcessInfo.processInfo.environment["SCREENSWITCH_STRINGS"] { paths.append(p) }
        if let p = Bundle.main.path(forResource: "strings", ofType: "tsv") { paths.append(p) }
        paths.append(screenctlDir + "/strings.tsv")
        var t: [String: (zh: String, en: String)] = [:]
        for p in paths {
            guard let text = try? String(contentsOfFile: p, encoding: .utf8) else { continue }
            for line in text.split(separator: "\n", omittingEmptySubsequences: true) {
                if line.hasPrefix("#") { continue }
                let f = line.split(separator: "\t", omittingEmptySubsequences: false).map(String.init)
                guard f.count >= 3, !f[0].isEmpty else { continue }
                t[f[0]] = (f[1], f[2])
            }
            if !t.isEmpty { break }
        }
        return t
    }()

    static func has(_ key: String) -> Bool { table[key] != nil }
}

// L("menu.switchTo", "Switch")：取当前语言的文字，{0} {1}… 换成参数，\n 换成换行；表里没有时返回编号本身
func L(_ key: String, _ args: String...) -> String {
    guard let row = Strings.table[key] else { return key }
    var s = Strings.isChinese ? row.zh : row.en
    for (i, a) in args.enumerated() { s = s.replacingOccurrences(of: "{\(i)}", with: a) }
    return s.replacingOccurrences(of: "\\n", with: "\n")
}
