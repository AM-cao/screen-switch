// ScreenSwitch 菜单栏程序：菜单栏图标 + 右键菜单 + 设置窗口 + 首次启动向导 + 全局快捷键 + 通知。
// 规范：docs/settings-ui-design.md。由 mac/install.sh 编译：swiftc -swift-version 5 -O ScreenSwitch/*.swift -o ScreenSwitch
//   ScreenSwitch                         正常运行（launchd 拉起）
//   ScreenSwitch --self-test             不显示图标，打印菜单与快捷键注册结果
//   ScreenSwitch --run-tests             菜单、向导检查、快捷键写法等逻辑的自动测试
//   ScreenSwitch --ui-dump <目录> [dark]  把设置窗口每一页、向导每一页、菜单画成 PNG，不安装也能审阅

import AppKit

let app = NSApplication.shared
var appDelegate: AppDelegate!
appDelegate = AppDelegate()
let arguments = CommandLine.arguments

if arguments.contains("--run-tests") {
    exit(runLogicTests() ? 0 : 1)
}
if arguments.contains("--self-test") {
    appDelegate.selfTest()
    exit(0)
}
if let i = arguments.firstIndex(of: "--ui-dump"), i + 1 < arguments.count {
    app.setActivationPolicy(.accessory)
    uiDump(to: arguments[i + 1], dark: arguments.contains("dark"))
    exit(0)
}
app.setActivationPolicy(.accessory)
app.delegate = appDelegate
app.run()
