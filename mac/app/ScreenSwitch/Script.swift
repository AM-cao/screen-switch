// 调用命令层 screenctl.sh（docs/settings-ui-design.md 第 7 节）：界面只负责显示和转发，
// 设置的读写、检测、配对都在脚本里，菜单、设置窗口、向导与 Agent 用的是同一套命令。

import Foundation

let screenctlDir = ProcessInfo.processInfo.environment["SCREENCTL_DIR"]
    ?? (NSHomeDirectory() as NSString).appendingPathComponent("screenctl")
let scriptPath = ProcessInfo.processInfo.environment["SCREENCTL_SCRIPT"] ?? screenctlDir + "/screenctl.sh"
let configPath = screenctlDir + "/config.sh"
let statePath = screenctlDir + "/state"
let busyPath = screenctlDir + "/busy"
let logPath = screenctlDir + "/screenctl.log"
let notifyQueuePath = screenctlDir + "/notify.queue"
let menuPidPath = screenctlDir + "/menu.pid"
let menuProblemsPath = screenctlDir + "/menu.problems"
let appVersion = "3.1"

struct ScriptResult {
    let code: Int32
    let output: String     // 标准输出
    let error: String      // 标准错误
    var ok: Bool { code == 0 }
    // 失败的原因："error: <原因>" 一行，去掉前缀；没有时取最后一行输出
    var message: String {
        let lines = (error + "\n" + output).split(separator: "\n").map(String.init)
        if let e = lines.first(where: { $0.hasPrefix("error: ") }) { return String(e.dropFirst(7)) }
        return lines.last(where: { !$0.isEmpty }) ?? "exit \(code)"
    }
    // 出错时 error 那行之后的候选（detect 几块都能控制时，每行"  <型号>  <名字>"）
    var candidates: [String] {
        error.split(separator: "\n").map(String.init).filter { $0.hasPrefix("  ") }
            .map { $0.trimmingCharacters(in: .whitespaces).components(separatedBy: "  ").first ?? "" }
            .filter { !$0.isEmpty }
    }
}

enum Script {
    // 同步运行 screenctl.sh <参数…>；界面线程里只用于很快的命令（status、ports、menu-info、set 等）
    @discardableResult
    static func run(_ args: [String]) -> ScriptResult {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/bash")
        p.arguments = [scriptPath] + args
        var env = ProcessInfo.processInfo.environment
        env["SCREENSWITCH_LANG"] = Strings.langCode   // 命令的提示与界面同一种语言
        p.environment = env
        let out = Pipe(), err = Pipe()
        p.standardOutput = out
        p.standardError = err
        p.standardInput = FileHandle.nullDevice
        do { try p.run() } catch { return ScriptResult(code: 127, output: "", error: "error: \(error)") }
        // 两个管道都要读，任何一个写满都会卡住子进程
        var errData = Data()
        let group = DispatchGroup()
        group.enter()
        DispatchQueue.global().async {
            errData = err.fileHandleForReading.readDataToEndOfFile()
            group.leave()
        }
        let outData = out.fileHandleForReading.readDataToEndOfFile()
        group.wait()
        p.waitUntilExit()
        return ScriptResult(code: p.terminationStatus,
                            output: String(decoding: outData, as: UTF8.self),
                            error: String(decoding: errData, as: UTF8.self))
    }

    // 在后台运行，完成后回到主线程
    static func runAsync(_ args: [String], _ done: @escaping (ScriptResult) -> Void) {
        DispatchQueue.global(qos: .userInitiated).async {
            let r = run(args)
            DispatchQueue.main.async { done(r) }
        }
    }

    // 依次运行几条命令，遇到失败就停；完成后回到主线程
    static func runSequence(_ commands: [[String]], _ done: @escaping (ScriptResult?) -> Void) {
        DispatchQueue.global(qos: .userInitiated).async {
            var failure: ScriptResult?
            for c in commands {
                let r = run(c)
                if !r.ok { failure = r; break }
            }
            DispatchQueue.main.async { done(failure) }
        }
    }

    static func json(_ r: ScriptResult) -> [String: Any]? {
        guard r.ok, let d = r.output.data(using: .utf8) else { return nil }
        return (try? JSONSerialization.jsonObject(with: d)) as? [String: Any]
    }
}

// ---- status

struct Device: Equatable {
    var code: Int
    var name: String
    var hotkey: String
}

struct Status {
    var configured = false
    var mainScreen = ""
    var selfName = ""
    var selfCode: Int?
    var layout = "second"
    var ownedLayout = "extend"
    var autoSave = true
    var directTake = true
    var peerConfigured = false
    var peerName = ""
    var peerOS = ""
    var peerHost = ""
    var peerUser = ""
    var peerCode: Int?
    var devices: [Device] = []
    var trayRunning = false
    var owned: Bool?
    var autostart = false
    var problems: [String] = []

    init() {}

    init(json j: [String: Any]) {
        configured = j["configured"] as? Bool ?? false
        mainScreen = j["mainScreen"] as? String ?? ""
        let me = j["self"] as? [String: Any] ?? [:]
        selfName = me["name"] as? String ?? ""
        selfCode = me["code"] as? Int
        layout = j["layout"] as? String ?? "second"
        ownedLayout = j["ownedLayout"] as? String ?? "extend"
        autoSave = j["autoSave"] as? Bool ?? true
        directTake = j["directTake"] as? Bool ?? true
        let p = j["peer"] as? [String: Any] ?? [:]
        peerConfigured = p["configured"] as? Bool ?? false
        peerName = p["name"] as? String ?? ""
        peerOS = p["os"] as? String ?? ""
        peerHost = p["host"] as? String ?? ""
        peerUser = p["user"] as? String ?? ""
        peerCode = p["code"] as? Int
        devices = (j["devices"] as? [[String: Any]] ?? []).compactMap { d in
            guard let c = d["code"] as? Int else { return nil }
            return Device(code: c, name: d["name"] as? String ?? "", hotkey: d["hotkey"] as? String ?? "")
        }
        trayRunning = j["trayRunning"] as? Bool ?? false
        owned = j["owned"] as? Bool
        autostart = j["autostart"] as? Bool ?? false
        problems = j["problems"] as? [String] ?? []
    }

    static func load() -> Status {
        guard let j = Script.json(Script.run(["status"])) else { return Status() }
        return Status(json: j)
    }
}

// ---- 输入口

struct Port: Equatable {
    var code: Int
    var name: String            // 标准名（15–18），其余为空
    var use: String             // self | peer | device | none
    var device: String
    var fromMonitor: Bool

    // "DisplayPort 1（15）"；没有标准名时"编码 27"
    var title: String {
        name.isEmpty ? L("ports.code", "\(code)") : L("ports.named", name, "\(code)")
    }

    static func standardName(_ code: Int) -> String {
        switch code {
        case 15: return "DisplayPort 1"
        case 16: return "DisplayPort 2"
        case 17: return "HDMI 1"
        case 18: return "HDMI 2"
        default: return ""
        }
    }
}

struct PortList {
    var monitorKnown = false
    var ports: [Port] = []

    init() {}

    init(json j: [String: Any]) {
        monitorKnown = j["monitorKnown"] as? Bool ?? false
        ports = (j["ports"] as? [[String: Any]] ?? []).compactMap { p in
            guard let c = p["code"] as? Int else { return nil }
            return Port(code: c, name: p["name"] as? String ?? "", use: p["use"] as? String ?? "none",
                        device: p["device"] as? String ?? "", fromMonitor: p["fromMonitor"] as? Bool ?? false)
        }
    }

    static func load(main: String? = nil) -> PortList {
        let r = Script.run(main.map { ["ports", "--main", $0] } ?? ["ports"])
        guard let j = Script.json(r) else { return PortList() }
        return PortList(json: j)
    }
}

// ---- 显示器（向导第 2 页）

struct DisplayEntry: Equatable {
    var name: String            // m1ddc 里的名字（detect --main 用它）
    var title: String           // 系统里的名字
    var uuid: String
    var builtin: Bool
    var controllable: Bool
    var code: Int?
}

struct DisplayScan {
    var lidClosed = false
    var displays: [DisplayEntry] = []
    var failed = false

    init() {}

    init(json j: [String: Any]) {
        lidClosed = j["lidClosed"] as? Bool ?? false
        displays = (j["displays"] as? [[String: Any]] ?? []).map { d in
            DisplayEntry(name: d["name"] as? String ?? "", title: d["title"] as? String ?? "",
                         uuid: d["uuid"] as? String ?? "", builtin: d["builtin"] as? Bool ?? false,
                         controllable: d["controllable"] as? Bool ?? false, code: d["code"] as? Int)
        }
    }

    static func load() -> DisplayScan {
        guard let j = Script.json(Script.run(["displays"])) else { var s = DisplayScan(); s.failed = true; return s }
        return DisplayScan(json: j)
    }
}

// ---- menu-info：菜单与快捷键用（每行 键=值；设备为 device=<编码><Tab><名字><Tab><快捷键>）

struct MenuInfo {
    var configured = true
    var peer = ""               // 对方电脑的名字；没有配置对方时为空
    var layout = "second"       // second = 本机另有一块屏；none = 只有主屏
    var secondBuiltin = true    // 另一块屏是笔记本自带屏时才有"只用主屏"
    var ownedLayout = "extend"  // extend | external（只用主屏）
    var hotkeyPeer = ""
    var devices: [Device] = []

    static func parse(_ text: String) -> MenuInfo {
        var info = MenuInfo()
        for line in text.split(separator: "\n") {
            guard let eq = line.firstIndex(of: "=") else { continue }
            let key = String(line[..<eq])
            let value = String(line[line.index(after: eq)...])
            switch key {
            case "configured": info.configured = value == "1"
            case "peer": info.peer = value
            case "layout": info.layout = value
            case "second_builtin": info.secondBuiltin = value == "1"
            case "owned_layout": info.ownedLayout = value
            case "hotkey_peer": info.hotkeyPeer = value
            case "device":
                let f = value.split(separator: "\t", omittingEmptySubsequences: false).map(String.init)
                if f.count >= 2, let c = Int(f[0]) {
                    info.devices.append(Device(code: c, name: f[1], hotkey: f.count > 2 ? f[2] : ""))
                }
            default: break
            }
        }
        return info
    }

    static func load() -> MenuInfo {
        let r = Script.run(["menu-info"])
        guard r.ok else { var i = MenuInfo(); i.configured = FileManager.default.fileExists(atPath: configPath); return i }
        return parse(r.output)
    }
}
