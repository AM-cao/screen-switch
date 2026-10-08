// display-helper：开关 MacBook 内建屏，供 screenctl.sh 的"仅大屏"模式使用。
//   display-helper list            列出在线显示器：<编号> builtin=<0|1> active=<0|1> mirror=<0|1> uuid=<UUID>
//                                  （mirror=1：处于镜像组中；uuid 即 displayplacer 的持久 id。
//                                  screenctl.sh 未占有时靠这两项低成本发现"主屏回来了但不是镜像"）
//   display-helper disable <编号>  关闭指定显示器（仅当前登录会话有效，注销后系统自动恢复）
//   display-helper enable <编号>   打开指定显示器
//   display-helper enable-mirror <内建屏编号> <外接屏编号>
//                                  打开内建屏，它一上线就让外接屏镜像它（内建屏为"优化为"）；
//                                  两步在同一进程里紧接着做，比分别调用少一段扩展状态，屏幕少闪
//                                  （放进同一个配置事务不行：内建屏还没打开时设的镜像关系会被系统丢掉）
//   display-helper solo <内建屏编号> <外接屏编号> [<宽> <高> <刷新率> [hidpi|lowdpi]]
//                                  镜像 → 仅外接屏：在同一个配置事务里取消镜像、把外接屏设为指定模式
//                                  （省略则由系统自选）、关闭内建屏。只重配一次，不经过扩展状态。
//   display-helper mode <编号> [<宽> <高> <刷新率> [hidpi|lowdpi]]
//                                  诊断：打印显示器当前模式，给了参数再打印匹配到的模式；不改任何设置
// 退出码：0 成功；2 找不到系统函数；3–5 配置失败；64 用法错误。
// 注意：开关显示器没有公开 API，这里调用的是 CoreGraphics 的未公开函数 CGSConfigureDisplayEnabled
// （displayplacer 的 enabled:false 用的也是它）。displayplacer 关掉内建屏后找不回它，所以自己实现。
// 由 mac/install.sh 编译：swiftc -O display-helper.swift -o ~/screenctl/bin/display-helper

import ColorSync   // CGDisplayCreateUUIDFromDisplayID
import CoreGraphics
import Foundation

typealias ConfigureEnabled = @convention(c) (CGDisplayConfigRef?, CGDirectDisplayID, Bool) -> CGError

func fail(_ code: Int32, _ message: String) -> Never {
    FileHandle.standardError.write((message + "\n").data(using: .utf8)!)
    exit(code)
}

func onlineDisplays() -> [CGDirectDisplayID] {
    var count: UInt32 = 0
    CGGetOnlineDisplayList(0, nil, &count)
    var ids = [CGDirectDisplayID](repeating: 0, count: Int(count))
    CGGetOnlineDisplayList(count, &ids, &count)
    return Array(ids.prefix(Int(count)))
}

// 显示器的 UUID，与 displayplacer 的 persistent screen id 相同
func displayUUID(_ id: CGDirectDisplayID) -> String {
    guard let u = CGDisplayCreateUUIDFromDisplayID(id)?.takeRetainedValue() else { return "-" }
    return CFUUIDCreateString(nil, u) as String
}

// 等某块显示器上线 / 下线，最多 3 秒
func waitOnline(_ id: CGDirectDisplayID, _ want: Bool) -> Bool {
    for _ in 0..<150 {
        if onlineDisplays().contains(id) == want { return true }
        usleep(20_000)
    }
    return false
}

func loadConfigureEnabled() -> ConfigureEnabled {
    guard let handle = dlopen("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics", RTLD_NOW),
          let symbol = dlsym(handle, "CGSConfigureDisplayEnabled") else {
        fail(2, "找不到 CGSConfigureDisplayEnabled")
    }
    return unsafeBitCast(symbol, to: ConfigureEnabled.self)
}

// displayplacer 的 "scaling:on" = 像素宽度与逻辑宽度不同（HiDPI）
func isHiDPI(_ m: CGDisplayMode) -> Bool { m.pixelWidth != m.width }

func describe(_ m: CGDisplayMode) -> String {
    "\(m.width)x\(m.height)@\(Int(m.refreshRate.rounded())) pixels=\(m.pixelWidth)x\(m.pixelHeight) "
        + (isHiDPI(m) ? "hidpi" : "lowdpi") + (m.isUsableForDesktopGUI() ? "" : " (not-gui)")
}

// 找与 displayplacer 参数对应的模式：逻辑分辨率、刷新率、是否缩放都要一致；优先桌面可用的。
// 刷新率先按四舍五入精确匹配，没有再放宽到 ±1 Hz：displayplacer 的整数刷新率与四舍五入的结果
// 在 59.94、119.88 这类刷新率上可能差 1，匹配不上时拿回会一直等主屏"就绪"
func findMode(_ display: CGDirectDisplayID, _ w: Int, _ h: Int, _ hz: Int, _ hidpi: Bool) -> CGDisplayMode? {
    let options = [kCGDisplayShowDuplicateLowResolutionModes: kCFBooleanTrue] as CFDictionary
    guard let modes = CGDisplayCopyAllDisplayModes(display, options) as? [CGDisplayMode] else { return nil }
    for tolerance in 0...1 {
        var fallback: CGDisplayMode?
        for m in modes {
            guard m.width == w, m.height == h, abs(Int(m.refreshRate.rounded()) - hz) <= tolerance,
                  isHiDPI(m) == hidpi else { continue }
            if m.isUsableForDesktopGUI() { return m }
            if fallback == nil { fallback = m }
        }
        if let m = fallback { return m }
    }
    return nil
}

// 解析 [<宽> <高> <刷新率> [hidpi|lowdpi]]，从 args[from] 开始；没给返回 nil
func parseModeSpec(_ display: CGDirectDisplayID, from: Int) -> CGDisplayMode? {
    guard args.count > from else { return nil }
    guard args.count == from + 3 || args.count == from + 4,
          let w = Int(args[from]), let h = Int(args[from + 1]), let hz = Int(args[from + 2]) else { fail(64, usage) }
    var hidpi = true
    if args.count == from + 4 {
        switch args[from + 3] {
        case "hidpi": hidpi = true
        case "lowdpi": hidpi = false
        default: fail(64, usage)
        }
    }
    guard let m = findMode(display, w, h, hz, hidpi) else {
        fail(4, "显示器 \(display) 没有 \(w)x\(h)@\(hz) \(hidpi ? "缩放" : "非缩放") 模式")
    }
    return m
}

let args = CommandLine.arguments
let usage = """
用法：display-helper list | enable <编号> | disable <编号> | enable-mirror <内建屏编号> <外接屏编号>
      | solo <内建屏编号> <外接屏编号> [<宽> <高> <刷新率> [hidpi|lowdpi]]
      | mode <编号> [<宽> <高> <刷新率> [hidpi|lowdpi]]
"""
guard args.count >= 2 else { fail(64, usage) }

switch args[1] {
case "list":
    for id in onlineDisplays() {
        print("\(id) builtin=\(CGDisplayIsBuiltin(id) != 0 ? 1 : 0) active=\(CGDisplayIsActive(id) != 0 ? 1 : 0)"
              + " mirror=\(CGDisplayIsInMirrorSet(id) != 0 ? 1 : 0) uuid=\(displayUUID(id))")
    }
case "mode":
    guard args.count >= 3, let id = UInt32(args[2]) else { fail(64, usage) }
    if let cur = CGDisplayCopyDisplayMode(id) { print("current \(describe(cur))") } else { print("current none") }
    if let m = parseModeSpec(id, from: 3) { print("match \(describe(m))") }
case "enable", "disable", "enable-mirror":
    let mirror = args[1] == "enable-mirror"
    guard args.count == (mirror ? 4 : 3), let id = UInt32(args[2]) else { fail(64, usage) }
    let external = mirror ? UInt32(args[3]) : nil
    if mirror && external == nil { fail(64, usage) }
    let configure = loadConfigureEnabled()
    var config: CGDisplayConfigRef?
    guard CGBeginDisplayConfiguration(&config) == .success else { fail(3, "CGBeginDisplayConfiguration 失败") }
    let err = configure(config, id, args[1] != "disable")
    guard err == .success else {
        CGCancelDisplayConfiguration(config)
        fail(4, "CGSConfigureDisplayEnabled(\(id)) 失败：\(err.rawValue)")
    }
    let done = CGCompleteDisplayConfiguration(config, .forSession)
    guard done == .success else { fail(5, "CGCompleteDisplayConfiguration 失败：\(done.rawValue)") }
    if let external = external {
        // 等内建屏上线（最多 3 秒），随即设镜像
        guard waitOnline(id, true) else { fail(4, "内建屏 \(id) 未上线") }
        var mirrorConfig: CGDisplayConfigRef?
        guard CGBeginDisplayConfiguration(&mirrorConfig) == .success else { fail(3, "CGBeginDisplayConfiguration 失败") }
        let m = CGConfigureDisplayMirrorOfDisplay(mirrorConfig, external, id)
        guard m == .success else {
            CGCancelDisplayConfiguration(mirrorConfig)
            fail(4, "CGConfigureDisplayMirrorOfDisplay(\(external) ← \(id)) 失败：\(m.rawValue)")
        }
        let md = CGCompleteDisplayConfiguration(mirrorConfig, .forSession)
        guard md == .success else { fail(5, "设置镜像失败：\(md.rawValue)") }
    }
case "solo":
    // 镜像 → 仅外接屏：取消镜像、设外接屏模式、关闭内建屏放进同一个事务，显示器只重配一次
    guard args.count >= 4, let builtin = UInt32(args[2]), let external = UInt32(args[3]) else { fail(64, usage) }
    let mode = parseModeSpec(external, from: 4)
    let configure = loadConfigureEnabled()
    var config: CGDisplayConfigRef?
    guard CGBeginDisplayConfiguration(&config) == .success else { fail(3, "CGBeginDisplayConfiguration 失败") }
    var err = CGConfigureDisplayMirrorOfDisplay(config, external, 0)   // 0 = kCGNullDirectDisplay，取消镜像
    guard err == .success else {
        CGCancelDisplayConfiguration(config)
        fail(4, "取消镜像失败：\(err.rawValue)")
    }
    if let mode = mode {
        err = CGConfigureDisplayWithDisplayMode(config, external, mode, nil)
        guard err == .success else {
            CGCancelDisplayConfiguration(config)
            fail(4, "设置外接屏模式 \(describe(mode)) 失败：\(err.rawValue)")
        }
    }
    err = configure(config, builtin, false)
    guard err == .success else {
        CGCancelDisplayConfiguration(config)
        fail(4, "CGSConfigureDisplayEnabled(\(builtin)) 失败：\(err.rawValue)")
    }
    let done = CGCompleteDisplayConfiguration(config, .forSession)
    guard done == .success else { fail(5, "CGCompleteDisplayConfiguration 失败：\(done.rawValue)") }
    guard waitOnline(builtin, false) else { fail(4, "内建屏 \(builtin) 仍在线") }
    if let cur = CGDisplayCopyDisplayMode(external) { print("solo: 外接屏 \(external) 现为 \(describe(cur))") }
default:
    fail(64, usage)
}
