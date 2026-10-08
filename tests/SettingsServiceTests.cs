using System;
using System.Collections.Generic;
using System.IO;

namespace ScreenSwitch.Tests
{
    // 命令层测试用的假环境：不碰硬件、SSH、托盘程序
    class FakeSetupEnv : ISetupEnv
    {
        public List<SetupMonitor> Mons = new List<SetupMonitor>();
        public int Connected = 2;
        public List<uint> Caps = null;
        public int SshRc = 0;
        public string SshOut = "ssh config updated";
        public string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExample screen-switch@PC";
        public string Generated = null;
        public PeerInfo Info = null;
        public int AuthRc = 0;
        public string AuthOut = "SETUP OK";
        public bool Admin = false;
        public List<string> Calls = new List<string>();
        public List<SetupMonitor> Monitors() { Calls.Add("monitors"); return Mons; }
        public int ConnectedCount() { return Connected; }
        public List<uint> InputCodes(string mainEdid) { Calls.Add("caps " + mainEdid); return Caps; }
        public int SshClientSetup(string user, string host, out string output) { Calls.Add("ssh " + user + "@" + host); output = SshOut; return SshRc; }
        public string PublicKey() { return Key; }
        public PeerInfo RequestInfo(Config cfg) { Calls.Add("info " + cfg.PeerUser + "@" + cfg.PeerHost); return Info; }
        public TrayRestart RestartTray() { Calls.Add("tray"); return TrayRestart.Restarted; }
        public string GenerateKey() { Calls.Add("keygen"); Key = Generated; return Generated; }
        public int AuthorizePeerKey(string key, bool elevate, out string output) { Calls.Add("authorize " + elevate + " " + key); output = AuthOut; return Admin || elevate ? AuthRc : 2; }
        public bool IsAdmin() { return Admin; }
    }

    public static class SettingsServiceTests
    {
        internal static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "ss-svc-" + Guid.NewGuid());
            Directory.CreateDirectory(d);
            return d;
        }

        // 一份在用的配置：本机 16，对方 MacBook 15，设备 Switch 17
        internal static SettingsService Make(string dir, FakeSetupEnv env, out int reloads)
        {
            var c = new Config();
            c.MainEdid = "LHC9059"; c.SelfName = "PC"; c.SelfCode = 16;
            c.PeerName = "MacBook"; c.PeerOs = "mac"; c.PeerHost = "my-mac.local"; c.PeerUser = "alice"; c.PeerCode = 15;
            c.Devices.Add(new Device("Switch", 17));
            c.AfterTakeCommand = "keep me";
            c.Save(Path.Combine(dir, "config.json"));
            var s = new SettingsService(dir, env, new Autostart(Path.Combine(dir, "Startup"), Path.Combine(dir, "ScreenSwitch.exe"), dir));
            int n = 0;
            s.Reload = delegate { n++; };
            reloads = 0;
            ReloadCounter = delegate { return n; };
            return s;
        }
        internal static Func<int> ReloadCounter;

        static Config Saved(string dir) { return Config.Load(Path.Combine(dir, "config.json")); }

        public static void Test_DeviceAddRenameRemove()
        {
            string d = TempDir(); int r;
            var s = Make(d, new FakeSetupEnv(), out r);
            Assert.Eq(null, s.AddDevice(18, " PS5 "), "add");
            Assert.Eq("PS5", Saved(d).FindDevice(18).Name, "saved, trimmed");
            Assert.Eq(null, s.RenameDevice(17, "任天堂 Switch"), "rename");
            Assert.Eq("任天堂 Switch", Saved(d).FindDevice(17).Name, "renamed");
            Assert.Eq(null, s.RemoveDevice(18), "remove");
            Assert.Eq(null, Saved(d).FindDevice(18), "removed");
            Assert.Eq("keep me", Saved(d).AfterTakeCommand, "unrelated field kept");
            Assert.Eq(3, ReloadCounter(), "reload after each change");
            Directory.Delete(d, true);
        }

        public static void Test_DeviceRejections()
        {
            string d = TempDir(); int r;
            var s = Make(d, new FakeSetupEnv(), out r);
            Assert.True(s.AddDevice(0, "x") != null, "code 0");
            Assert.True(s.AddDevice(256, "x") != null, "code 256");
            Assert.True(s.AddDevice(18, " ") != null, "empty name");
            Assert.True(s.AddDevice(16, "x") != null, "self code");
            Assert.True(s.AddDevice(15, "x") != null, "peer code");
            Assert.True(s.AddDevice(17, "x") != null, "taken");
            Assert.True(s.RenameDevice(19, "x") != null, "rename missing");
            Assert.True(s.RemoveDevice(19) != null, "remove missing");
            Assert.Eq(1, Saved(d).Devices.Count, "nothing changed");
            Assert.Eq(0, ReloadCounter(), "no reload on failure");
            Directory.Delete(d, true);
        }

        public static void Test_Edit_RefusesBrokenConfig()
        {
            string d = TempDir(); int r;
            var s = Make(d, new FakeSetupEnv(), out r);
            File.WriteAllText(Path.Combine(d, "config.json"), "{ \"peer\": { ");
            string err = s.AddDevice(18, "PS5");
            Assert.True(err != null && err.Contains("config.json"), "refused: " + err);
            Assert.Eq("{ \"peer\": { ", File.ReadAllText(Path.Combine(d, "config.json")), "user's file untouched");
            Directory.Delete(d, true);
        }

        public static void Test_SetPeer_SavesRunsSshClientSetup()
        {
            string d = TempDir(); int r;
            var env = new FakeSetupEnv();
            var s = Make(d, env, out r);
            string ssh;
            Assert.Eq(null, s.SetPeer("", "windows", "pc.local", "PC\\bob", out ssh), "accepted");
            Config c = Saved(d);
            Assert.Eq("Windows", c.PeerName, "default name by system");
            Assert.Eq("windows", c.PeerOs, "os");
            Assert.Eq("pc.local", c.PeerHost, "host");
            Assert.Eq("bob", c.PeerUser, "whoami prefix dropped");
            Assert.Eq((uint)0, c.PeerCode, "host changed: code unknown");
            Assert.True(env.Calls.Contains("ssh bob@pc.local"), "ssh client set up");
            Assert.Eq("ssh config updated", ssh, "script output passed back");
            Directory.Delete(d, true);
        }

        public static void Test_SetPeer_SameHostKeepsCode_Rejections()
        {
            string d = TempDir(); int r;
            var s = Make(d, new FakeSetupEnv(), out r);
            string ssh;
            Assert.Eq(null, s.SetPeer("书房 Mac", "mac", "MY-MAC.local", "alice", out ssh), "same host, other case");
            Assert.Eq((uint)15, Saved(d).PeerCode, "code kept");
            Assert.Eq("书房 Mac", Saved(d).PeerName, "name");
            Assert.True(s.SetPeer("x", "linux", "h", "u", out ssh) != null, "bad os");
            Assert.True(s.SetPeer("x", "mac", "a b", "u", out ssh) != null, "bad host");
            Assert.True(s.SetPeer("x", "mac", "h", "a b", out ssh) != null, "bad user");
            Directory.Delete(d, true);
        }

        public static void Test_SetPeer_ScriptMissing_ManualCommand()
        {
            string d = TempDir(); int r;
            var env = new FakeSetupEnv(); env.SshRc = -1;
            var s = Make(d, env, out r);
            string ssh;
            Assert.Eq(null, s.SetPeer("", "mac", "m.local", "alice", out ssh), "settings saved anyway");
            Assert.True(ssh.Contains("setup-client.ps1 -PeerUser alice -PeerHost m.local"), "manual command: " + ssh);
            Directory.Delete(d, true);
        }

        public static void Test_ClearPeer()
        {
            string d = TempDir(); int r;
            var s = Make(d, new FakeSetupEnv(), out r);
            Assert.Eq(null, s.ClearPeer(), "cleared");
            Config c = Saved(d);
            Assert.Eq(false, c.PeerConfigured, "no peer");
            Assert.Eq((uint)0, c.PeerCode, "code cleared");
            Directory.Delete(d, true);
        }

        public static void Test_SetOption_PrefsAndConfig()
        {
            string d = TempDir(); int r;
            var s = Make(d, new FakeSetupEnv(), out r);
            Assert.Eq(null, s.SetOption("owned-layout", new string[] { "main-only" }), "owned layout");
            Assert.Eq(OwnedLayout.MainOnly, Prefs.Load(Path.Combine(d, "prefs.json")).OwnedLayout, "in prefs.json");
            Assert.Eq(null, s.SetOption("auto-save", new string[] { "off" }), "auto save");
            Assert.Eq(false, Prefs.Load(Path.Combine(d, "prefs.json")).AutoSaveLayout, "in prefs.json");
            Assert.Eq(null, s.SetOption("direct-take", new string[] { "off" }), "direct take");
            Assert.Eq(false, Saved(d).DirectTake, "in config");
            Assert.Eq(null, s.SetOption("after-take", new string[] { "" }), "after take cleared");
            Assert.Eq("", Saved(d).AfterTakeCommand, "empty");
            Assert.Eq(null, s.SetOption("hotkey", new string[] { "peer", "Ctrl+Alt+1" }), "peer hotkey");
            Assert.Eq(null, s.SetOption("hotkey", new string[] { "17", "ctrl+alt+F5" }), "device hotkey");
            Assert.Eq("Ctrl+Alt+1", Saved(d).PeerHotkey, "peer hotkey saved");
            Assert.Eq("Ctrl+Alt+F5", Saved(d).FindDevice(17).Hotkey, "device hotkey saved in the normal spelling");
            Assert.Eq(null, s.SetOption("hotkey", new string[] { "peer", "none" }), "cleared");
            Assert.Eq("", Saved(d).PeerHotkey, "peer hotkey cleared");
            Directory.Delete(d, true);
        }

        public static void Test_SetOption_Rejections()
        {
            string d = TempDir(); int r;
            var s = Make(d, new FakeSetupEnv(), out r);
            Assert.True(s.SetOption("colour", new string[] { "red" }) != null, "unknown key");
            Assert.True(s.SetOption("owned-layout", new string[] { "both" }) != null, "bad value");
            Assert.True(s.SetOption("auto-save", new string[0]) != null, "missing value");
            Assert.True(s.SetOption("hotkey", new string[] { "peer", "Q" }) != null, "no modifier");
            Assert.True(s.SetOption("hotkey", new string[] { "19", "Ctrl+Alt+1" }) != null, "no such device");
            Directory.Delete(d, true);
        }

        // Shift 单独不算修饰键（会吞掉正常打字的大写字母）：要带 Ctrl、Alt 或 Win
        public static void Test_SetOption_HotkeyShiftAloneRejected()
        {
            string d = TempDir(); int r;
            var s = Make(d, new FakeSetupEnv(), out r);
            string err = s.SetOption("hotkey", new string[] { "peer", "Shift+Q" });
            Assert.True(err != null && err.Contains("Shift+Q"), "shift alone rejected: " + err);
            Assert.True(!err.Contains("Shift 或"), "message no longer offers Shift alone: " + err);
            Assert.Eq("", Saved(d).PeerHotkey, "unchanged");
            Assert.Eq(null, s.SetOption("hotkey", new string[] { "peer", "Shift+Win+Q" }), "shift with win ok");
            Assert.Eq(null, s.SetOption("hotkey", new string[] { "17", "Ctrl+Shift+F5" }), "shift with ctrl ok");
            Directory.Delete(d, true);
        }

        public static void Test_SetOption_HotkeyDuplicateRejected()
        {
            string d = TempDir(); int r;
            var s = Make(d, new FakeSetupEnv(), out r);
            Assert.Eq(null, s.SetOption("hotkey", new string[] { "peer", "Ctrl+Alt+1" }), "peer");
            string err = s.SetOption("hotkey", new string[] { "17", "alt+ctrl+1" });
            Assert.True(err != null && err.Contains("Ctrl+Alt+1"), "same combination in another order rejected: " + err);
            Assert.Eq("", Saved(d).FindDevice(17).Hotkey, "unchanged");
            Assert.Eq(null, s.SetOption("hotkey", new string[] { "peer", "Ctrl+Alt+1" }), "setting the same action again is fine");
            Directory.Delete(d, true);
        }

        public static void Test_Autostart()
        {
            string d = TempDir(); int r;
            var s = Make(d, new FakeSetupEnv(), out r);
            Assert.Eq(false, s.AutostartEnabled, "off");
            Assert.Eq(null, s.SetAutostart(true), "on");
            Assert.Eq(true, s.AutostartEnabled, "on now");
            Assert.Eq(null, s.SetAutostart(false), "off");
            Assert.Eq(false, s.AutostartEnabled, "off now");
            Directory.Delete(d, true);
        }

        public static void Test_DefaultReload_WritesCommandOnlyWhenTrayRuns()
        {
            string d = TempDir();
            new Config().Save(Path.Combine(d, "config.json"));
            var s = new SettingsService(d, new FakeSetupEnv(), new Autostart(Path.Combine(d, "S"), "x", d));
            Assert.Eq(null, s.AddDevice(17, "Switch"), "tray not running");
            Assert.Eq(false, File.Exists(Path.Combine(d, "cmd.txt")), "no command file");
            StateFile.WriteState(d, true, StateFile.Now());
            Assert.Eq(null, s.AddDevice(18, "PS5"), "tray running");
            Assert.Eq("reload", StateFile.ReadAndDeleteCommand(d), "reload requested");
            Directory.Delete(d, true);
        }

        public static void Test_Detect_WritesAndClearsDirectRecord()
        {
            string d = TempDir(); int r;
            var env = new FakeSetupEnv();
            env.Mons.Add(new SetupMonitor("\\\\?\\DISPLAY#ABC1234#5&1&0&UID1#{x}", "X", true, 0x0F));
            env.Connected = 1;
            var s = Make(d, env, out r);
            File.WriteAllText(Path.Combine(d, "direct-take.txt"), "miss 3");
            DetectResult res = s.Detect(null);
            Assert.Eq(null, res.Error, "ok");
            Config c = Config.Load(Path.Combine(d, "config.json"));
            Assert.Eq("ABC1234", c.MainEdid, "main");
            Assert.Eq((uint)15, c.SelfCode, "own code");
            Assert.Eq((uint)0, c.PeerCode, "peer had the same code: cleared");
            Assert.Eq("none", c.Layout, "one screen");
            Assert.Eq(false, File.Exists(Path.Combine(d, "direct-take.txt")), "record cleared");
            Directory.Delete(d, true);
        }

        public static void Test_Ports_KnownAndUnknown()
        {
            string d = TempDir(); int r;
            var env = new FakeSetupEnv();
            var s = Make(d, env, out r);
            string err;
            Config c = s.Load(out err);
            PortsResult p = s.ReadPorts(c);
            Assert.Eq(false, p.MonitorKnown, "capabilities unreadable");
            Assert.Eq(3, p.Rows.Count, "registered ports only");
            env.Caps = new List<uint> { 15, 16, 17, 18 };
            p = s.ReadPorts(c);
            Assert.Eq(true, p.MonitorKnown, "readable");
            Assert.Eq(4, p.Rows.Count, "all four");
            Directory.Delete(d, true);
        }

        public static void Test_TestPeer_SavesCode()
        {
            string d = TempDir(); int r;
            var env = new FakeSetupEnv();
            var s = Make(d, env, out r);
            Assert.Eq(null, s.ClearPeer(), "clear first");
            PeerTestResult t = s.TestPeer();
            Assert.True(t.Error != null, "no peer");
            string ssh;
            s.SetPeer("", "mac", "other.local", "alice", out ssh);
            t = s.TestPeer();
            Assert.True(t.Error != null && t.Error.Contains("Mac"), "unreachable names the peer: " + t.Error);
            env.Info = new PeerInfo(); env.Info.Code = 15; env.Info.Name = "MacBook";
            t = s.TestPeer();
            Assert.Eq(null, t.Error, "reached");
            Assert.Eq("MacBook", t.Name, "name");
            Assert.Eq(true, t.CodeSaved, "code saved");
            Assert.Eq((uint)15, Config.Load(Path.Combine(d, "config.json")).PeerCode, "in config");
            Directory.Delete(d, true);
        }

        // 对方报的编码是本机的或某台设备的：不存（存了两个口会重名），配置不动
        public static void Test_TestPeer_DoesNotSaveSelfOrDeviceCode()
        {
            string d = TempDir(); int r;
            var env = new FakeSetupEnv();
            var s = Make(d, env, out r);
            env.Info = new PeerInfo(); env.Info.Name = "MacBook";
            string before = File.ReadAllText(Path.Combine(d, "config.json"));
            env.Info.Code = 17;
            PeerTestResult t = s.TestPeer();
            Assert.Eq(null, t.Error, "reached");
            Assert.Eq(false, t.CodeSaved, "device's code not saved");
            env.Info.Code = 16;
            t = s.TestPeer();
            Assert.Eq(false, t.CodeSaved, "own code not saved");
            Assert.Eq(before, File.ReadAllText(Path.Combine(d, "config.json")), "config unchanged");
            Assert.Eq(0, ReloadCounter(), "no reload");
            Directory.Delete(d, true);
        }

        public static void Test_PublicKey_GeneratedWhenMissing()
        {
            string d = TempDir(); int r;
            var env = new FakeSetupEnv(); env.Key = null; env.Generated = "ssh-ed25519 AAAAGEN me@pc";
            var s = Make(d, env, out r);
            Assert.Eq("ssh-ed25519 AAAAGEN me@pc", s.PublicKey(), "generated");
            Assert.True(env.Calls.Contains("keygen"), "keygen ran");
            Directory.Delete(d, true);
        }

        public static void Test_Authorize_AdminElevateAndRefusals()
        {
            string d = TempDir(); int r;
            var env = new FakeSetupEnv();
            var s = Make(d, env, out r);
            string output;
            Assert.True(s.Authorize("not a key", false, out output) != null, "bad key");
            string err = s.Authorize("ssh-ed25519 AAAAPEER mac@book", false, out output);
            Assert.True(err != null && err.Contains("-PeerPubKey \"ssh-ed25519 AAAAPEER mac@book\""), "not admin: prints the command: " + err);
            Assert.Eq(null, s.Authorize("ssh-ed25519 AAAAPEER mac@book", true, out output), "elevated");
            env.AuthRc = 1223;
            Assert.True(s.Authorize("ssh-ed25519 AAAAPEER mac@book", true, out output) != null, "cancelled");
            Directory.Delete(d, true);
        }

        public static void Test_SaveWizard_WritesEverything()
        {
            string d = TempDir(); int r;
            var s = Make(d, new FakeSetupEnv(), out r);
            File.WriteAllText(Path.Combine(d, "direct-take.txt"), "miss 3");
            var c = new Config(); c.MainEdid = "ABC1234"; c.SelfCode = 16; c.Devices.Add(new Device("PS5", 18));
            Assert.Eq(null, s.SaveWizard(c, true), "saved");
            Assert.Eq("PS5", Config.Load(Path.Combine(d, "config.json")).Devices[0].Name, "config written");
            Assert.Eq(true, s.AutostartEnabled, "autostart on");
            Assert.Eq(false, File.Exists(Path.Combine(d, "direct-take.txt")), "record cleared");
            Assert.Eq(1, ReloadCounter(), "reloaded");
            Directory.Delete(d, true);
        }

        public static void Test_SaveWizard_BacksUpUnreadableConfig()
        {
            string d = TempDir(); int r;
            var s = Make(d, new FakeSetupEnv(), out r);
            string cfg = Path.Combine(d, "config.json");
            byte[] broken = new byte[] { 0x7B, 0x20, 0x6E, 0x6F, 0x74, 0x20, 0x6A, 0x73, 0x6F, 0x6E };
            File.WriteAllBytes(cfg, broken);
            File.WriteAllText(cfg + ".bak", "older backup");
            var c = new Config(); c.MainEdid = "ABC1234"; c.SelfCode = 16;
            Assert.Eq(null, s.SaveWizard(c, false), "saved");
            Assert.True(File.ReadAllBytes(cfg + ".bak").Length == broken.Length && File.ReadAllText(cfg + ".bak") == "{ not json", "bak holds the original bytes");
            Assert.Eq("ABC1234", Config.Load(cfg).MainEdid, "config.json is the new one");
            // 读得了的旧配置不留 .bak
            File.Delete(cfg + ".bak");
            Assert.Eq(null, s.SaveWizard(c, false), "saved again");
            Assert.Eq(false, File.Exists(cfg + ".bak"), "readable config: no bak");
            Directory.Delete(d, true);
        }

        public static void Test_Status_FieldsAndProblems()
        {
            string d = TempDir(); int r;
            var s = Make(d, new FakeSetupEnv(), out r);
            Dictionary<string, object> st = s.Status();
            Assert.Eq(true, st["configured"], "configured");
            Assert.Eq("LHC9059", st["mainScreen"], "main");
            Assert.Eq(false, st["trayRunning"], "tray not running");
            var problems = (List<string>)st["problems"];
            Assert.True(problems.Contains(Tr.T("problem.tray-not-running")), "tray problem listed");
            var peer = (Dictionary<string, object>)st["peer"];
            Assert.Eq("MacBook", peer["name"], "peer name");
            Assert.Eq(15, peer["code"], "peer code as number");
            var devices = (List<Dictionary<string, object>>)st["devices"];
            Assert.Eq("Switch", devices[0]["name"], "device");
            Directory.Delete(d, true);
        }
    }
}
