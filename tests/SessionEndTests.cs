using System;

namespace ScreenSwitch.Tests
{
    public static class SessionEndTests
    {
        // 只有 WM_ENDSESSION 且 wParam 非 0（会话确实要结束）才算；wParam 为 0 表示注销/关机被取消，
        // WM_QUERYENDSESSION 只是询问（可能被别的程序否决），都不动作。
        public static void Test_IsSessionEnd_OnlyConfirmedEndSession()
        {
            Assert.Eq(true, TrayApp.IsSessionEnd(0x0016, new IntPtr(1)), "WM_ENDSESSION, session ending");
            Assert.Eq(false, TrayApp.IsSessionEnd(0x0016, IntPtr.Zero), "WM_ENDSESSION, end cancelled");
            Assert.Eq(false, TrayApp.IsSessionEnd(0x0011, new IntPtr(1)), "WM_QUERYENDSESSION is only a query");
            Assert.Eq(false, TrayApp.IsSessionEnd(0x0312, new IntPtr(1)), "WM_HOTKEY");
        }
    }
}