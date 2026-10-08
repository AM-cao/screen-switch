namespace ScreenSwitch.Tests
{
    public static class WizardTests
    {
        public static void Test_ApplyPeer_ValidatesAndFills()
        {
            var c = new Config();
            Assert.Eq(null, WizardLogic.ApplyPeer(c, "", "mac", "m.local", "alice"), "ok");
            Assert.Eq("Mac", c.PeerName, "default name");
            Assert.Eq("m.local", c.PeerHost, "host");
            Assert.True(WizardLogic.ApplyPeer(c, "x", "mac", "a b", "alice") != null, "bad host");
            Assert.Eq("m.local", c.PeerHost, "unchanged on error");
            Assert.Eq(null, WizardLogic.ApplyPeer(c, "", "mac", "", ""), "both empty = skip");
            Assert.Eq(false, c.PeerConfigured, "skipped clears the peer");
        }

        public static void Test_Summary()
        {
            var c = new Config();
            c.MainEdid = "LHC9059"; c.SelfCode = 16; c.Layout = "none";
            c.Devices.Add(new Device("Switch", 17));
            string[] lines = WizardLogic.Summary(c, "").Split('\n');
            Assert.Eq(5, lines.Length, "one line per item");
            Assert.True(lines[0].Contains("LHC9059"), lines[0]);
            Assert.True(lines[1].Contains("DisplayPort 2"), lines[1]);
            Assert.True(lines[2].Contains("只有主屏"), lines[2]);
            Assert.True(lines[3].Contains("无"), "no peer: " + lines[3]);
            Assert.True(lines[4].Contains("Switch"), lines[4]);
        }

        public static void Test_Summary_ScreenCombination()
        {
            var c = new Config();
            c.MainEdid = "LHC9059"; c.SelfCode = 16; c.Layout = "second";
            Assert.True(WizardLogic.Summary(c, "DELL U2720Q").Split('\n')[2].Contains("DELL U2720Q"), "other screen's name when known");
            string unknown = WizardLogic.Summary(c, "").Split('\n')[2];
            Assert.True(unknown.Contains("另一块屏") && !unknown.Contains("…"), "generic wording when unknown: " + unknown);
        }

        public static void Test_Form_StartsOnWelcome_NoWriteUntilFinish()
        {
            string d = SettingsServiceTests.TempDir();
            var env = new FakeSetupEnv();
            var s = new SettingsService(d, env, new Autostart(System.IO.Path.Combine(d, "S"), "x", d));
            using (var f = new WizardForm(s))
            {
                Assert.Eq(0, f.PageIndex, "welcome first");
                f.GoTo(1);
                Assert.Eq(1, f.PageIndex, "main screen page");
            }
            Assert.Eq(0, System.IO.Directory.GetFileSystemEntries(d).Length, "closing writes nothing: temp dir stays empty");
            System.IO.Directory.Delete(d, true);
        }

        // 向导打开期间 config.json 被改过：点"完成"前要能看出来（不在、写过、又写过都算变了）
        // 主屏页点列表：按那一行记下的设备路径，越界和不应答 DDC/CI 的行不理会
        public static void Test_PickedMonitor_ByRowId()
        {
            var rows = new System.Collections.Generic.List<string> { "\\\\?\\DISPLAY#AAA1111#1", null, "\\\\?\\DISPLAY#AAA1111#2" };
            Assert.Eq("\\\\?\\DISPLAY#AAA1111#2", WizardLogic.PickedMonitor(rows, 2), "row 2's own id");
            Assert.Eq(null, WizardLogic.PickedMonitor(rows, 1), "no DDC on that row");
            Assert.Eq(null, WizardLogic.PickedMonitor(rows, 3), "out of range");
            Assert.Eq(null, WizardLogic.PickedMonitor(rows, -1), "nothing selected");
            Assert.Eq(null, WizardLogic.PickedMonitor(new System.Collections.Generic.List<string>(), 0), "list cleared");
            Assert.Eq(null, WizardLogic.PickedMonitor(null, 0), "no list");
        }

        public static void Test_ConfigStamp_SeesChanges()
        {
            string d = SettingsServiceTests.TempDir();
            string p = System.IO.Path.Combine(d, "config.json");
            System.DateTime missing = WizardLogic.ConfigStamp(p);
            Assert.Eq(System.DateTime.MinValue, missing, "missing file");
            new Config().Save(p);
            System.IO.File.SetLastWriteTimeUtc(p, new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc));
            System.DateTime first = WizardLogic.ConfigStamp(p);
            Assert.True(first != missing, "created counts as changed");
            Assert.Eq(first, WizardLogic.ConfigStamp(p), "untouched: same stamp");
            new Config().Save(p);
            Assert.True(WizardLogic.ConfigStamp(p) != first, "written again: changed");
            System.IO.Directory.Delete(d, true);
        }
    }
}
