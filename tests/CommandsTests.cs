using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace ScreenSwitch.Tests
{
    public static class CommandsTests
    {
        class Out { public int Rc; public string Text; }

        static Out Run(SettingsService s, params string[] args)
        {
            var w = new StringWriter();
            var o = new Out();
            o.Rc = Commands.Run(args, s, w);
            o.Text = w.ToString();
            return o;
        }

        static SettingsService Svc(out string dir, out FakeSetupEnv env)
        {
            dir = SettingsServiceTests.TempDir();
            env = new FakeSetupEnv();
            int r;
            return SettingsServiceTests.Make(dir, env, out r);
        }

        static Dictionary<string, object> Parse(string json) { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); }

        public static void Test_NotMine_ForOtherCommands()
        {
            string d; FakeSetupEnv env;
            var s = Svc(out d, out env);
            Assert.Eq(Commands.NotMine, Run(s, "request", "info").Rc, "request is Cli's");
            Assert.Eq(Commands.NotMine, Run(s, "setup").Rc, "setup is Cli's");
            Assert.Eq(Commands.NotMine, Run(s).Rc, "no args");
            Directory.Delete(d, true);
        }

        public static void Test_Status_IsJson()
        {
            string d; FakeSetupEnv env;
            var s = Svc(out d, out env);
            Out o = Run(s, "status");
            Assert.Eq(0, o.Rc, "ok");
            Dictionary<string, object> st = Parse(o.Text);
            Assert.Eq("LHC9059", st["mainScreen"], "main");
            Assert.Eq(1, Run(s, "status", "x").Rc, "extra argument");
            Directory.Delete(d, true);
        }

        public static void Test_DeviceAdd_NameWithSpacesAndQuotes()
        {
            string d; FakeSetupEnv env;
            var s = Svc(out d, out env);
            Assert.Eq(0, Run(s, "device", "add", "18", "任天堂", "\"PS5\" Pro").Rc, "added");
            Config c = Config.Load(Path.Combine(d, "config.json"));
            Assert.Eq("任天堂 \"PS5\" Pro", c.FindDevice(18).Name, "name joined from the remaining arguments, quotes kept");
            Out o = Run(s, "status");
            Dictionary<string, object> st = Parse(o.Text);
            var devices = (System.Collections.ArrayList)st["devices"];
            Assert.Eq("任天堂 \"PS5\" Pro", ((Dictionary<string, object>)devices[1])["name"], "valid JSON round trip");
            Assert.Eq(0, Run(s, "device", "rename", "18", "PS5").Rc, "renamed");
            Assert.Eq(0, Run(s, "device", "remove", "18").Rc, "removed");
            Out bad = Run(s, "device", "add", "16", "x");
            Assert.Eq(1, bad.Rc, "self code rejected");
            Assert.True(bad.Text.StartsWith("error: "), "one error line: " + bad.Text);
            Assert.Eq(1, Run(s, "device", "add", "abc", "x").Rc, "bad code");
            Assert.Eq(1, Run(s, "device", "frob").Rc, "bad sub-command");
            Directory.Delete(d, true);
        }

        public static void Test_Ports_MonitorUnknown()
        {
            string d; FakeSetupEnv env;
            var s = Svc(out d, out env);
            Out o = Run(s, "ports");
            Assert.Eq(0, o.Rc, "ok even when capabilities are unreadable");
            Dictionary<string, object> p = Parse(o.Text);
            Assert.Eq(false, p["monitorKnown"], "flag");
            var ports = (System.Collections.ArrayList)p["ports"];
            Assert.Eq(3, ports.Count, "registered ports");
            Assert.Eq("self", ((Dictionary<string, object>)ports[1])["use"], "16 is this computer");
            Directory.Delete(d, true);
        }

        public static void Test_Detect_ChooseThenMain()
        {
            string d; FakeSetupEnv env;
            var s = Svc(out d, out env);
            env.Mons.Add(new SetupMonitor("\\\\?\\DISPLAY#AAA0001#5&1&0&UID1#{x}", "A", true, 0x10));
            env.Mons.Add(new SetupMonitor("\\\\?\\DISPLAY#BBB0002#5&1&0&UID2#{x}", "B", true, 7));
            Out o = Run(s, "detect");
            Assert.Eq(1, o.Rc, "must choose");
            Assert.True(o.Text.Contains("AAA0001") && o.Text.Contains("BBB0002"), "candidates listed: " + o.Text);
            Assert.Eq(0, Run(s, "detect", "--main", "AAA0001").Rc, "chosen");
            Assert.Eq("AAA0001", Config.Load(Path.Combine(d, "config.json")).MainEdid, "written");
            Directory.Delete(d, true);
        }

        public static void Test_Detect_SameModelTwice_ListsUniqueIds()
        {
            string d; FakeSetupEnv env;
            var s = Svc(out d, out env);
            env.Mons.Add(new SetupMonitor("\\\\?\\DISPLAY#AAA0001#5&1&0&UID1#{x}", "A", true, 0x10));
            env.Mons.Add(new SetupMonitor("\\\\?\\DISPLAY#AAA0001#5&1&0&UID2#{x}", "A", true, 0x0F));
            Out o = Run(s, "detect");
            Assert.Eq(1, o.Rc, "must choose");
            Assert.True(o.Text.Contains("AAA0001#5&1&0&UID1") && o.Text.Contains("AAA0001#5&1&0&UID2"), "unique ids listed: " + o.Text);
            Assert.Eq(1, Run(s, "detect", "--main", "AAA0001").Rc, "model alone is ambiguous");
            Assert.Eq(0, Run(s, "detect", "--main", "AAA0001#5&1&0&UID2").Rc, "the listed id picks one");
            Config c = Config.Load(Path.Combine(d, "config.json"));
            Assert.Eq("AAA0001#5&1&0&UID2", c.MainEdid, "the second one written");
            Assert.Eq((uint)15, c.SelfCode, "its code");
            Directory.Delete(d, true);
        }

        public static void Test_Peer_SetClearTest()
        {
            string d; FakeSetupEnv env;
            var s = Svc(out d, out env);
            Assert.Eq(0, Run(s, "peer", "set", "--os", "windows", "--host", "pc.local", "--user", "bob", "--name", "书房 PC").Rc, "set");
            Config c = Config.Load(Path.Combine(d, "config.json"));
            Assert.Eq("书房 PC", c.PeerName, "name with a space");
            Assert.Eq(1, Run(s, "peer", "set", "--os", "windows").Rc, "host and user required");
            env.Info = new PeerInfo(); env.Info.Code = 15; env.Info.Name = "PC";
            Out t = Run(s, "peer", "test");
            Assert.Eq(0, t.Rc, "test ok");
            Assert.Eq(15, Parse(t.Text)["code"], "JSON with the code");
            env.Info = null;
            Assert.Eq(1, Run(s, "peer", "test").Rc, "unreachable");
            Assert.Eq(0, Run(s, "peer", "clear").Rc, "cleared");
            Directory.Delete(d, true);
        }

        public static void Test_Pubkey_Authorize_Set_Autostart()
        {
            string d; FakeSetupEnv env;
            var s = Svc(out d, out env);
            Out k = Run(s, "pubkey");
            Assert.Eq(0, k.Rc, "pubkey");
            Assert.True(k.Text.StartsWith("ssh-ed25519 "), "key line");
            Out a = Run(s, "authorize", "ssh-ed25519 AAAAPEER mac@book");
            Assert.Eq(1, a.Rc, "not admin");
            Assert.True(a.Text.Contains("setup-sshd.ps1"), "says what to run");
            env.Admin = true;
            Assert.Eq(0, Run(s, "authorize", "ssh-ed25519 AAAAPEER mac@book").Rc, "admin");
            Assert.Eq(0, Run(s, "set", "owned-layout", "main-only").Rc, "set");
            Assert.Eq(1, Run(s, "set", "owned-layout").Rc, "missing value");
            Assert.Eq(0, Run(s, "set", "hotkey", "17", "Ctrl+Alt+2").Rc, "hotkey");
            Assert.Eq(0, Run(s, "set", "after-take", "schtasks", "/run", "/tn", "X").Rc, "after-take joins the rest");
            Assert.Eq("schtasks /run /tn X", Config.Load(Path.Combine(d, "config.json")).AfterTakeCommand, "command");
            Assert.Eq(0, Run(s, "autostart", "on").Rc, "autostart on");
            Assert.Eq(true, s.AutostartEnabled, "on");
            Assert.Eq(1, Run(s, "autostart", "maybe").Rc, "bad value");
            Directory.Delete(d, true);
        }
    }
}
