using System.Collections.Generic;

namespace ScreenSwitch.Tests
{
    public static class PortsTests
    {
        static Config Cfg()
        {
            var c = new Config();
            c.SelfCode = 16; c.PeerCode = 15;
            var sw = new Device("Switch", 17); sw.Hotkey = "Ctrl+Alt+2";
            c.Devices.Add(sw);
            return c;
        }

        static string Describe(List<PortRow> rows)
        {
            var parts = new List<string>();
            foreach (PortRow r in rows) parts.Add(r.Code + ":" + r.Use + (r.Use == PortUse.Device ? "=" + r.DeviceName : "") + (r.FromMonitor ? "" : "*"));
            return string.Join(" ", parts.ToArray());
        }

        public static void Test_Label_StandardNameAndCode()
        {
            Assert.Eq("HDMI 1（17）", Ports.Label(17), "known code");
            Assert.Eq("27", Ports.Label(27), "unknown code");
        }

        public static void Test_Build_MergesMonitorListAndConfig()
        {
            var rows = Ports.Build(new List<uint> { 18, 17, 16, 15 }, Cfg());
            Assert.Eq("15:Peer 16:Self 17:Device=Switch 18:None", Describe(rows), "sorted, uses from config");
        }

        public static void Test_Build_UnknownCapabilities_OnlyRegistered()
        {
            var rows = Ports.Build(null, Cfg());
            Assert.Eq("15:Peer* 16:Self* 17:Device=Switch*", Describe(rows), "only what the config knows, marked not from the monitor");
        }

        public static void Test_Build_RegisteredCodeMissingFromMonitorList_StillShown()
        {
            var rows = Ports.Build(new List<uint> { 15, 16 }, Cfg());
            Assert.Eq("15:Peer 16:Self 17:Device=Switch*", Describe(rows), "device kept although the monitor did not list it");
        }

        public static void Test_Apply_WritesPeerAndDevices_KeepsHotkeys()
        {
            Config c = Cfg();
            var rows = Ports.Build(new List<uint> { 15, 16, 17, 18 }, c);
            Ports.Find(rows, 18).Use = PortUse.Device; Ports.Find(rows, 18).DeviceName = " PS5 ";
            Ports.Find(rows, 17).DeviceName = "任天堂 Switch";
            Assert.Eq(null, Ports.Apply(c, rows), "accepted");
            Assert.Eq((uint)15, c.PeerCode, "peer kept");
            Assert.Eq(2, c.Devices.Count, "two devices");
            Assert.Eq("任天堂 Switch", c.Devices[0].Name, "renamed");
            Assert.Eq("Ctrl+Alt+2", c.Devices[0].Hotkey, "hotkey kept by code");
            Assert.Eq("PS5", c.Devices[1].Name, "trimmed");
            Assert.Eq("", c.Devices[1].Hotkey, "new device has no hotkey");
        }

        public static void Test_Apply_PeerMovedOrRemoved()
        {
            Config c = Cfg();
            var rows = Ports.Build(new List<uint> { 15, 16, 17, 18 }, c);
            Ports.Find(rows, 15).Use = PortUse.None;
            Ports.Find(rows, 18).Use = PortUse.Peer;
            Assert.Eq(null, Ports.Apply(c, rows), "accepted");
            Assert.Eq((uint)18, c.PeerCode, "peer moved");
            Ports.Find(rows, 18).Use = PortUse.None;
            Assert.Eq(null, Ports.Apply(c, rows), "accepted");
            Assert.Eq((uint)0, c.PeerCode, "no peer port = unknown");
        }

        public static void Test_Apply_Rejections_LeaveConfigUnchanged()
        {
            Config c = Cfg();
            var rows = Ports.Build(new List<uint> { 15, 16, 17, 18 }, c);
            Ports.Find(rows, 18).Use = PortUse.Peer;
            Assert.True(Ports.Apply(c, rows) != null, "two peers rejected");
            Assert.Eq((uint)15, c.PeerCode, "unchanged");

            rows = Ports.Build(new List<uint> { 15, 16, 17, 18 }, c);
            Ports.Find(rows, 18).Use = PortUse.Device; Ports.Find(rows, 18).DeviceName = "  ";
            Assert.True(Ports.Apply(c, rows) != null, "empty name rejected");
            Assert.Eq(1, c.Devices.Count, "unchanged");

            rows = Ports.Build(new List<uint> { 15, 16, 17 }, c);
            rows.Add(new PortRow(17, PortUse.None, "", false));
            Assert.True(Ports.Apply(c, rows) != null, "duplicate code rejected");

            rows = Ports.Build(new List<uint> { 15, 16, 17 }, c);
            Ports.Find(rows, 16).Use = PortUse.None;
            Assert.True(Ports.Apply(c, rows) != null, "self row cannot be dropped");
        }
    }
}
