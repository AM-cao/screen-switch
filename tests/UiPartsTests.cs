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
    }
}
