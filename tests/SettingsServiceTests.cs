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
    }
}
