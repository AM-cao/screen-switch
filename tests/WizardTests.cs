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

        // 换界面语言：向导整个重画，停在原来那一页，内存里的工作配置和这一页还没提交的输入都留着
        public static void Test_Form_Relanguage_KeepsPageWorkAndInput()
        {
            string d = SettingsServiceTests.TempDir();
            var env = new FakeSetupEnv();
            var s = new SettingsService(d, env, new Autostart(System.IO.Path.Combine(d, "S"), "x", d));
            try
            {
                using (var f = new WizardForm(s))
                {
                    f.GoTo(3);
                    Config work = f.Work;
                    work.SelfCode = 16;
                    PeerPanel old = f.Peer;
                    old.HostBox.Text = "m.local"; old.UserBox.Text = "alice"; old.OsBox.SelectedIndex = 0;
                    old.PeerKeyBox.Text = "ssh-ed25519 AAAAPEER mac@book";
                    old.MyKey = env.Key;
                    Tr.Use("en");
                    f.Relanguage();
                    Assert.Eq(3, f.PageIndex, "same page");
                    Assert.True(object.ReferenceEquals(work, f.Work), "same work config");
                    Assert.Eq((uint)16, f.Work.SelfCode, "work untouched");
                    Assert.Eq(Tr.T("wizard.title"), f.Text, "title in the new language");
                    Assert.True(!object.ReferenceEquals(old, f.Peer), "panel rebuilt");
                    Assert.Eq("m.local", f.Peer.HostBox.Text, "host kept");
                    Assert.Eq("alice", f.Peer.UserBox.Text, "user kept");
                    Assert.Eq("windows", f.Peer.PeerOs, "os kept");
                    Assert.Eq("ssh-ed25519 AAAAPEER mac@book", f.Peer.PeerKeyBox.Text, "their key kept");
                    Assert.Eq(env.Key, f.Peer.MyKey, "my key kept");
                    Assert.Eq(PeerPanel.MaskKey(env.Key), f.Peer.MyKeyBox.Text, "still masked");
                    f.GoTo(4);
                    Assert.Eq(4, f.PageIndex, "navigation still works");
                }
            }
            finally { Tr.Use("zh"); }
            Assert.Eq(0, System.IO.Directory.GetFileSystemEntries(d).Length, "nothing written");
            System.IO.Directory.Delete(d, true);
        }

        // 换语言（已写入 config.json）后：工作配置跟上文件里的语言，文件时间重新记下，点"完成"时不误问"被修改过"
        public static void Test_Form_Relanguage_TakesLanguageAndStampFromDisk()
        {
            string d = SettingsServiceTests.TempDir();
            int r;
            var s = SettingsServiceTests.Make(d, new FakeSetupEnv(), out r);
            string p = System.IO.Path.Combine(d, "config.json");
            System.IO.File.SetLastWriteTimeUtc(p, new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc));
            try
            {
                using (var f = new WizardForm(s))
                {
                    System.DateTime before = f.Stamp;
                    Assert.Eq(null, s.SetOption("language", new string[] { "en" }), "language changed in the settings window");
                    Assert.True(WizardLogic.ConfigStamp(p) != before, "file changed");
                    Tr.Use("en");
                    f.Relanguage();
                    Assert.Eq("en", f.Work.UiLanguage, "work follows the file");
                    Assert.Eq(WizardLogic.ConfigStamp(p), f.Stamp, "stamp refreshed: no overwrite question");
                }
            }
            finally { Tr.Use("zh"); }
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
