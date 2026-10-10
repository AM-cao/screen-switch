using System.Collections.Generic;
using System.Windows.Forms;

namespace ScreenSwitch.Tests
{
    public static class UiPartsTests
    {
        public static void Test_HotkeyBox_FormatRoundTripsWithParser()
        {
            string s = HotkeyBox.Format(true, true, false, false, HotkeyBox.KeyName(Keys.D1));
            Assert.Eq("Ctrl+Alt+1", s, "format");
            uint mods, vk;
            Assert.True(TrayApp.ParseHotkey(s, out mods, out vk), "parser accepts it");
            Assert.Eq("Shift+Win+F5", HotkeyBox.Format(false, false, true, true, HotkeyBox.KeyName(Keys.F5)), "F key");
            Assert.Eq("Ctrl+K", HotkeyBox.Format(true, false, false, false, HotkeyBox.KeyName(Keys.K)), "letter");
            Assert.Eq(null, HotkeyBox.KeyName(Keys.ControlKey), "modifier alone is not a key");
            Assert.Eq(null, HotkeyBox.KeyName(Keys.Space), "unsupported key");
        }

        public static void Test_PortTable_RowsRoundTrip()
        {
            var t = new PortTable();
            var rows = new List<PortRow> { new PortRow(15, PortUse.Peer, "", true), new PortRow(16, PortUse.Self, "", true), new PortRow(17, PortUse.Device, "Switch", true) };
            t.SetRows(rows);
            List<PortRow> back = t.GetRows();
            Assert.Eq(3, back.Count, "three rows");
            Assert.Eq(PortUse.Device, back[2].Use, "device kept");
            Assert.Eq("Switch", back[2].DeviceName, "name kept");
            t.AddPort(18);
            t.AddPort(18);
            Assert.Eq(4, t.GetRows().Count, "added once");
            rows[2].DeviceName = "changed";
            Assert.Eq("Switch", t.GetRows()[2].DeviceName, "table holds its own copies");
            t.Dispose();
        }

        // 这台电脑的公钥只显示类型和 12 个圆点；没有类型时只显示圆点
        public static void Test_MaskKey()
        {
            string dots = new string('•', 12);
            Assert.Eq("ssh-ed25519 " + dots, PeerPanel.MaskKey("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExample screen-switch@PC"), "type kept");
            Assert.Eq("ssh-rsa " + dots, PeerPanel.MaskKey("  ssh-rsa AAAAB3Nza x@y\r\n"), "trimmed");
            Assert.Eq(dots, PeerPanel.MaskKey("AAAAC3NzaC1lZDI1NTE5"), "no type: nothing of the key shown");
            Assert.Eq(dots, PeerPanel.MaskKey("AAAAC3NzaC1lZDI1NTE5 x@y"), "key first, then comment: nothing shown");
            Assert.Eq("", PeerPanel.MaskKey(""), "empty");
            Assert.Eq("", PeerPanel.MaskKey(null), "null");
        }

        // 框里是遮住的，完整的公钥留在面板上给"复制"用
        public static void Test_PeerPanel_KeepsFullKeyShowsMasked()
        {
            string key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExample screen-switch@PC";
            using (var p = new PeerPanel())
            {
                p.SetValues(new Config(), key);
                Assert.Eq(key, p.MyKey, "full key kept");
                Assert.Eq(PeerPanel.MaskKey(key), p.MyKeyBox.Text, "box masked");
                Assert.True(!p.MyKeyBox.Text.Contains("AAAA"), "no key material in the box");
                p.MyKey = null;
                Assert.Eq("", p.MyKey, "cleared");
                Assert.Eq("", p.MyKeyBox.Text, "box cleared");
            }
        }

        // 录快捷键：要带 Ctrl、Alt 或 Win；Shift 单独、没有修饰键、不支持的键都不收
        public static void Test_HotkeyBox_ComboNeedsCtrlAltOrWin()
        {
            Assert.Eq(null, HotkeyBox.Combo(false, false, true, false, "Q"), "shift alone ignored");
            Assert.Eq(null, HotkeyBox.Combo(false, false, false, false, "Q"), "no modifier ignored");
            Assert.Eq(null, HotkeyBox.Combo(true, false, false, false, null), "unsupported key ignored");
            Assert.Eq("Ctrl+Shift+Q", HotkeyBox.Combo(true, false, true, false, "Q"), "ctrl+shift");
            Assert.Eq("Alt+1", HotkeyBox.Combo(false, true, false, false, "1"), "alt");
            Assert.Eq("Shift+Win+F5", HotkeyBox.Combo(false, false, true, true, "F5"), "shift+win");
        }
    }
}
