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
            string s = WizardLogic.Summary(c);
            Assert.True(s.Contains("LHC9059") && s.Contains("DisplayPort 2") && s.Contains("只有主屏") && s.Contains("Switch") && s.Contains("无"), s);
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
    }
}
