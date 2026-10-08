using System.Collections.Generic;

namespace ScreenSwitch.Tests
{
    public static class TrayMenuTests
    {
        static Config Cfg()
        {
            var c = TestCfg.Legacy();
            c.PeerName = "MacBook";
            c.Devices.Clear();
            c.Devices.Add(new Device("Switch", 17));
            return c;
        }

        static string Labels(List<MenuEntry> l)
        {
            var parts = new List<string>();
            foreach (MenuEntry e in l) parts.Add(e.Kind == MenuKind.Separator ? "-" : e.Label + (e.Right.Length > 0 ? "[" + e.Right + "]" : "") + (e.Enabled ? "" : "(off)"));
            return string.Join("|", parts.ToArray());
        }

        public static void Test_NotOwned_Layout()
        {
            Assert.Eq("拿回主屏|切给 Switch|-|扩展屏幕[✓]|只用主屏|-|设置…|退出",
                Labels(TrayMenu.Build(Cfg(), false, false, OwnedLayout.Extend, null)), "no status line, check on the right");
        }

        public static void Test_Owned_GivesToPeer_MainOnlyChecked()
        {
            Assert.Eq("切给 MacBook|切给 Switch|-|扩展屏幕|只用主屏[✓]|-|设置…|退出",
                Labels(TrayMenu.Build(Cfg(), true, false, OwnedLayout.MainOnly, null)), "owned");
        }

        public static void Test_NoPeer_NoGiveToPeer_TakeBackStays()
        {
            Config c = Cfg(); c.PeerHost = "";
            Assert.Eq("切给 Switch|-|扩展屏幕[✓]|只用主屏|-|设置…|退出", Labels(TrayMenu.Build(c, true, false, OwnedLayout.Extend, null)), "owned, no peer");
            Assert.Eq("拿回主屏|切给 Switch|-|扩展屏幕[✓]|只用主屏|-|设置…|退出", Labels(TrayMenu.Build(c, false, false, OwnedLayout.Extend, null)), "not owned, no peer");
        }

        public static void Test_FreshInstall_NoDevices_NoLeadingSeparator()
        {
            var c = new Config();
            Assert.Eq("扩展屏幕[✓]|只用主屏|-|设置…|退出", Labels(TrayMenu.Build(c, true, false, OwnedLayout.Extend, null)), "nothing to switch to");
        }

        public static void Test_LayoutNone_HidesLayoutChoice()
        {
            Config c = Cfg(); c.Layout = "none";
            Assert.Eq("拿回主屏|切给 Switch|-|设置…|退出", Labels(TrayMenu.Build(c, false, false, OwnedLayout.Extend, null)), "single screen");
        }

        public static void Test_Hotkeys_ShownOnlyWhenSet()
        {
            Config c = Cfg(); c.PeerHotkey = "Ctrl+Alt+1"; c.Devices[0].Hotkey = "Ctrl+Alt+2";
            Assert.Eq("拿回主屏[Ctrl+Alt+1]|切给 Switch[Ctrl+Alt+2]|-|扩展屏幕[✓]|只用主屏|-|设置…|退出",
                Labels(TrayMenu.Build(c, false, false, OwnedLayout.Extend, null)), "shortcuts on the right");
        }

        public static void Test_Busy_DisablesSwitchItems()
        {
            Assert.Eq("拿回主屏(off)|切给 Switch(off)|-|扩展屏幕[✓]|只用主屏|-|设置…|退出",
                Labels(TrayMenu.Build(Cfg(), false, true, OwnedLayout.Extend, null)), "busy");
        }

        public static void Test_Problems_OnTop()
        {
            var l = TrayMenu.Build(Cfg(), false, false, OwnedLayout.Extend, new List<string> { "快捷键 Ctrl+Alt+1 没能注册" });
            Assert.Eq(MenuKind.Problem, l[0].Kind, "problem first");
            Assert.True(l[0].Label.Contains("Ctrl+Alt+1"), "text");
            Assert.Eq(MenuKind.Separator, l[1].Kind, "separated from the switch items");
        }
    }
}
