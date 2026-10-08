namespace ScreenSwitch.Tests
{
    public static class HotkeyTests
    {
        public static void Test_Parse_CtrlAlt1()
        {
            uint mods, vk;
            Assert.True(TrayApp.ParseHotkey("Ctrl+Alt+1", out mods, out vk), "parsed");
            Assert.Eq((uint)(2 | 1), mods, "ctrl|alt");
            Assert.Eq((uint)0x31, vk, "vk '1'");
        }

        public static void Test_Parse_ShiftWinF5()
        {
            uint mods, vk;
            Assert.True(TrayApp.ParseHotkey("Shift+Win+F5", out mods, out vk), "parsed");
            Assert.Eq((uint)(4 | 8), mods, "shift|win");
            Assert.Eq((uint)0x74, vk, "vk F5");
        }

        public static void Test_Parse_Invalid()
        {
            uint mods, vk;
            Assert.Eq(false, TrayApp.ParseHotkey("Ctrl+", out mods, out vk), "empty key");
            Assert.Eq(false, TrayApp.ParseHotkey("", out mods, out vk), "empty");
            Assert.Eq(false, TrayApp.ParseHotkey("Ctrl+Foo", out mods, out vk), "unknown key");
        }

        // 设计文档 8：快捷键默认不注册；配置了 peer.hotkey、设备的 hotkey 才注册
        public static void Test_Bindings_NoneByDefault()
        {
            Assert.Eq(0, TrayApp.HotkeyBindings(new Config()).Count, "fresh config");
            Assert.Eq(0, TrayApp.HotkeyBindings(TestCfg.Legacy()).Count, "peer and devices without hotkeys");
        }

        public static void Test_Bindings_PeerAndDevices()
        {
            var c = TestCfg.Legacy();
            c.PeerHotkey = "Ctrl+Alt+1";
            c.Devices[1].Hotkey = " Ctrl+Alt+2 ";
            var b = TrayApp.HotkeyBindings(c);
            Assert.Eq(2, b.Count, "two bindings");
            Assert.Eq(TrayApp.SwitchKind.Peer, b[0].Kind, "peer binding");
            Assert.Eq("Ctrl+Alt+1", b[0].Hotkey, "peer hotkey");
            Assert.Eq(TrayApp.SwitchKind.Device, b[1].Kind, "device binding");
            Assert.Eq((uint)0x12, b[1].Code, "second device's code");
            Assert.Eq("Ctrl+Alt+2", b[1].Hotkey, "trimmed");
            Assert.True(b[0].Id != b[1].Id, "distinct ids");
        }

        // 旧的 hotkeys 键忽略：原来的 Ctrl+Alt+1 / Ctrl+Alt+2 不再注册
        public static void Test_Bindings_LegacyHotkeysKeyIgnored()
        {
            string p = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ss-cfg-" + System.Guid.NewGuid() + ".json");
            System.IO.File.WriteAllText(p, "{ \"codes\": { \"windows\": 16, \"mac\": 15, \"other\": [17] },"
                + " \"peer\": { \"host\": \"m.local\", \"user\": \"u\" }, \"hotkeys\": { \"primary\": \"Ctrl+Alt+1\", \"other\": \"Ctrl+Alt+2\" } }");
            var c = Config.Load(p);
            Assert.Eq("", c.PeerHotkey, "not carried over");
            Assert.Eq(0, TrayApp.HotkeyBindings(c).Count, "nothing registered");
            System.IO.File.Delete(p);
        }

        // 没有配置对方时菜单里没有"切给 <对方>"
        public static void Test_Menu_NoPeer_NoGivePeerItem()
        {
            var c = new Config();
            c.Devices.Add(new Device("Switch", 0x11));
            var owned = TrayApp.SwitchEntries(c, true);
            Assert.Eq(1, owned.Count, "only the device");
            Assert.Eq("切给 Switch", owned[0].Label, "device item");
            var away = TrayApp.SwitchEntries(c, false);
            Assert.Eq("拿回主屏", away[0].Label, "take back stays (direct switch)");
            Assert.Eq(2, away.Count, "take back + device");
        }

        public static void Test_Menu_PeerNameFromConfig()
        {
            var c = TestCfg.Legacy();
            c.PeerName = "书房 台式机";
            Assert.Eq("切给 书房 台式机", TrayApp.SwitchEntries(c, true)[0].Label, "peer name in the menu");
        }
    }
}
