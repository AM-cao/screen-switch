using System;
using System.IO;

namespace ScreenSwitch.Tests
{
    // 其他测试共用的配置：相当于一份旧的 config.json（编排器的旧测试按它写）——配置了对方（Mac，0x0F）、
    // 两个设备（0x11、0x12），本机编码未知（读取成功即占有），不先直接切。
    static class TestCfg
    {
        public static Config Legacy()
        {
            var c = new Config();
            c.PeerHost = "mac.local"; c.PeerUser = "u"; c.PeerCode = 0x0F;
            c.Devices.Add(new Device("设备 1", 0x11));
            c.Devices.Add(new Device("设备 2", 0x12));
            c.DirectTake = false;
            return c;
        }
    }

    public static class ConfigTests
    {
        static string Temp(string json)
        {
            string p = Path.Combine(Path.GetTempPath(), "ss-cfg-" + Guid.NewGuid() + ".json");
            File.WriteAllText(p, json);
            return p;
        }

        public static void Test_Config_DefaultsWhenMissing()
        {
            var c = Config.Load(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid() + ".json"));
            Assert.Eq("CHANGE_ME", c.MainEdid, "edid");
            Assert.Eq("second", c.Layout, "layout");
            Assert.Eq(false, c.LayoutNone, "layout none");
            Assert.Eq(Environment.MachineName, c.SelfName, "self name = computer name");
            Assert.Eq((uint)0, c.SelfCode, "self code unknown");
            Assert.Eq("Mac", c.PeerName, "peer name");
            Assert.Eq("mac", c.PeerOs, "peer os");
            Assert.Eq((uint)0, c.PeerCode, "peer code unknown");
            Assert.Eq(false, c.PeerConfigured, "no peer");
            Assert.Eq(0, c.Devices.Count, "no devices");
            Assert.Eq(true, c.DirectTake, "direct take on");
            Assert.Eq("", c.PeerHotkey, "no hotkey");
            Assert.Eq(2000, c.PollMs, "poll");
            Assert.Eq(2, c.Debounce, "debounce");
            Assert.Eq(3, c.PeerTimeoutSec, "timeout");
            Assert.Eq("ssh.exe", c.SshExe, "ssh");
            Assert.Eq(0, c.ReleaseWidth, "release 0 = the other screen's own resolution");
            Assert.Eq(0, c.ReleaseHeight, "release height");
        }

        // 设计文档 4.1 的示例
        public static void Test_Config_ReadsV3()
        {
            string p = Temp("{ \"mainEdid\": \"ABC1234\", \"layout\": \"none\","
                + " \"self\": { \"name\": \"台式机\", \"code\": 16 },"
                + " \"peer\": { \"name\": \"MacBook\", \"os\": \"mac\", \"host\": \"my-mac.local\", \"user\": \"alice\", \"code\": 15,"
                + " \"program\": \"/opt/s.sh\", \"hotkey\": \"Ctrl+Alt+1\", \"keyPath\": \"C:\\\\k\", \"timeoutSec\": 4 },"
                + " \"devices\": [ { \"name\": \"Switch\", \"code\": 17, \"hotkey\": \"Ctrl+Alt+2\" }, { \"name\": \"PS5\", \"code\": 18 } ],"
                + " \"directTake\": false, \"pollMs\": 1000, \"debounce\": 3, \"sshExe\": \"s.exe\","
                + " \"release\": { \"width\": 1920, \"height\": 1080 } }");
            var c = Config.Load(p);
            Assert.Eq(null, c.LoadError, "parsed");
            Assert.Eq("ABC1234", c.MainEdid, "edid");
            Assert.Eq("none", c.Layout, "layout");
            Assert.Eq(true, c.LayoutNone, "layout none");
            Assert.Eq("台式机", c.SelfName, "self name");
            Assert.Eq((uint)16, c.SelfCode, "self code");
            Assert.Eq("MacBook", c.PeerName, "peer name");
            Assert.Eq("mac", c.PeerOs, "peer os");
            Assert.Eq("my-mac.local", c.PeerHost, "host");
            Assert.Eq("alice", c.PeerUser, "user");
            Assert.Eq((uint)15, c.PeerCode, "peer code");
            Assert.Eq("/opt/s.sh", c.PeerProgram, "program");
            Assert.Eq("/opt/s.sh", c.PeerProgramPath, "effective program");
            Assert.Eq("Ctrl+Alt+1", c.PeerHotkey, "peer hotkey");
            Assert.Eq("C:\\k", c.PeerKeyPath, "key");
            Assert.Eq(4, c.PeerTimeoutSec, "timeout");
            Assert.Eq(true, c.PeerConfigured, "peer configured");
            Assert.Eq(2, c.Devices.Count, "devices");
            Assert.Eq("Switch", c.Devices[0].Name, "d0 name");
            Assert.Eq((uint)17, c.Devices[0].Code, "d0 code");
            Assert.Eq("Ctrl+Alt+2", c.Devices[0].Hotkey, "d0 hotkey");
            Assert.Eq("PS5", c.Devices[1].Name, "d1 name");
            Assert.Eq("", c.Devices[1].Hotkey, "d1 hotkey default");
            Assert.Eq(false, c.DirectTake, "direct take");
            Assert.Eq(1000, c.PollMs, "poll");
            Assert.Eq(3, c.Debounce, "debounce");
            Assert.Eq("s.exe", c.SshExe, "ssh");
            Assert.Eq(1920, c.ReleaseWidth, "w");
            Assert.Eq(1080, c.ReleaseHeight, "h");
            File.Delete(p);
        }

        // 旧的 config.json 不改也要能用：旧键按设计文档 4.1 的表读取
        public static void Test_Config_ReadsLegacyKeys()
        {
            string p = Temp("{ \"codes\": { \"windows\": 16, \"mac\": 15, \"other\": [17, 18] }, \"mainEdid\": \"LHC9059\", \"pollMs\": 2000, \"debounce\": 2,"
                + " \"peer\": { \"host\": \"m.local\", \"user\": \"u\", \"keyPath\": \"\", \"timeoutSec\": 3, \"command\": \"~/screenctl/screenctl.sh request give {target}\" },"
                + " \"sshExe\": \"ssh.exe\", \"hotkeys\": { \"primary\": \"Ctrl+Alt+1\", \"other\": \"Ctrl+Alt+2\" },"
                + " \"release\": { \"width\": 2560, \"height\": 1440 }, \"afterTake\": { \"command\": \"x\", \"delayMs\": 5 } }");
            var c = Config.Load(p);
            Assert.Eq(null, c.LoadError, "parsed");
            Assert.Eq((uint)16, c.SelfCode, "codes.windows -> self.code");
            Assert.Eq((uint)15, c.PeerCode, "codes.mac -> peer.code");
            Assert.Eq(2, c.Devices.Count, "codes.other -> devices");
            Assert.Eq("设备 1", c.Devices[0].Name, "d0 name");
            Assert.Eq((uint)17, c.Devices[0].Code, "d0 code");
            Assert.Eq("设备 2", c.Devices[1].Name, "d1 name");
            Assert.Eq((uint)18, c.Devices[1].Code, "d1 code");
            Assert.Eq("~/screenctl/screenctl.sh", c.PeerProgram, "peer.command without the suffix -> peer.program");
            Assert.Eq("Mac", c.PeerName, "no peer.name, no peer.os -> Mac");
            Assert.Eq("mac", c.PeerOs, "no peer.os -> mac");
            Assert.Eq("second", c.Layout, "layout default");
            Assert.Eq(true, c.DirectTake, "direct take default");
            Assert.Eq(true, c.PeerConfigured, "peer configured");
            Assert.Eq(2560, c.ReleaseWidth, "w");
            Assert.Eq("x", c.AfterTakeCommand, "afterTake kept");
            Assert.Eq(5, c.AfterTakeDelayMs, "afterTake delay kept");
            File.Delete(p);
        }

        public static void Test_Config_NewKeysWinOverLegacy()
        {
            string p = Temp("{ \"codes\": { \"windows\": 16, \"mac\": 15, \"other\": [17] },"
                + " \"self\": { \"code\": 18 }, \"peer\": { \"code\": 27, \"command\": \"old request give {target}\", \"program\": \"new\" },"
                + " \"devices\": [] }");
            var c = Config.Load(p);
            Assert.Eq((uint)18, c.SelfCode, "self.code wins");
            Assert.Eq((uint)27, c.PeerCode, "peer.code wins");
            Assert.Eq("new", c.PeerProgram, "peer.program wins");
            Assert.Eq(0, c.Devices.Count, "devices (even empty) wins over codes.other");
            File.Delete(p);
        }

        public static void Test_Config_LegacyCommandWithoutSuffix_Ignored()
        {
            string p = Temp("{ \"peer\": { \"user\": \"bob\", \"command\": \"custom {target}\" } }");
            var c = Config.Load(p);
            Assert.Eq("", c.PeerProgram, "no program taken from an unrecognized template");
            Assert.Eq("~/screenctl/screenctl.sh", c.PeerProgramPath, "default by os");
            File.Delete(p);
        }

        public static void Test_Config_PeerNameAndProgramFollowOs()
        {
            string p = Temp("{ \"peer\": { \"os\": \"Windows\", \"host\": \"pc.local\", \"user\": \"bob\" } }");
            var c = Config.Load(p);
            Assert.Eq("windows", c.PeerOs, "os normalized");
            Assert.Eq("Windows", c.PeerName, "name by os");
            Assert.Eq("C:\\Users\\bob\\ScreenSwitch\\ScreenSwitch.exe", c.PeerProgramPath, "program by os");
            File.Delete(p);
            p = Temp("{ \"peer\": { \"os\": \"linux\" } }");
            c = Config.Load(p);
            Assert.Eq("mac", c.PeerOs, "unknown os -> mac");
            Assert.Eq("~/screenctl/screenctl.sh", c.PeerProgramPath, "mac program");
            File.Delete(p);
        }

        public static void Test_Config_PeerConfigured()
        {
            var c = new Config();
            Assert.Eq(false, c.PeerConfigured, "empty");
            c.PeerHost = "h"; Assert.Eq(false, c.PeerConfigured, "no user");
            c.PeerUser = "CHANGE_ME"; Assert.Eq(false, c.PeerConfigured, "placeholder user");
            c.PeerUser = "u"; Assert.Eq(true, c.PeerConfigured, "host + user");
            c.PeerHost = " "; Assert.Eq(false, c.PeerConfigured, "blank host");
        }

        public static void Test_Config_DevicesSkipInvalid_NameDefaults()
        {
            string p = Temp("{ \"devices\": [ { \"name\": \"\", \"code\": 17 }, { \"name\": \"bad\", \"code\": 0 }, { \"name\": \"big\", \"code\": 256 }, { \"code\": 18 }, 5 ],"
                + " \"layout\": \"weird\", \"self\": { \"code\": 999 } }");
            var c = Config.Load(p);
            Assert.Eq(2, c.Devices.Count, "only codes 1-255 kept");
            Assert.Eq("设备 1", c.Devices[0].Name, "empty name defaults");
            Assert.Eq("设备 2", c.Devices[1].Name, "missing name defaults");
            Assert.Eq((uint)18, c.Devices[1].Code, "code");
            Assert.Eq("second", c.Layout, "unknown layout -> second");
            Assert.Eq((uint)0, c.SelfCode, "out-of-range code -> unknown");
            File.Delete(p);
        }

        public static void Test_Config_FindDevice()
        {
            var c = TestCfg.Legacy();
            Assert.Eq("设备 2", c.FindDevice(0x12).Name, "found");
            Assert.Eq(null, c.FindDevice(0x13), "not found");
        }

        // 写出的只有新键；再读回来一致（名字含中文、引号、反斜杠）
        public static void Test_Config_SaveWritesV3_RoundTrips()
        {
            string p = Temp("{ \"codes\": { \"windows\": 16, \"mac\": 15, \"other\": [17] }, \"mainEdid\": \"LHC9059\","
                + " \"peer\": { \"host\": \"m.local\", \"user\": \"u\", \"command\": \"~/screenctl/screenctl.sh request give {target}\" },"
                + " \"hotkeys\": { \"primary\": \"Ctrl+Alt+1\", \"other\": \"Ctrl+Alt+2\" }, \"afterTake\": { \"command\": \"schtasks /run /tn \\\"A B\\\"\", \"delayMs\": 7 } }");
            var c = Config.Load(p);
            c.SelfName = "台式机 \"1\"";
            c.PeerKeyPath = "C:\\keys\\id";
            c.Devices[0].Name = "Switch";
            c.Devices[0].Hotkey = "Ctrl+Alt+2";
            c.Layout = "none";
            c.DirectTake = false;
            c.Save(p);
            string text = File.ReadAllText(p);
            Assert.True(!text.Contains("\"codes\""), "no legacy codes");
            Assert.True(!text.Contains("\"command\": \"~"), "no legacy peer.command");
            Assert.True(!text.Contains("\"hotkeys\""), "no legacy hotkeys");
            Assert.True(text.Contains("台式机"), "non-ASCII written as is");
            byte[] raw = File.ReadAllBytes(p);
            Assert.True(!(raw[0] == 0xEF && raw[1] == 0xBB), "no BOM");
            var r = Config.Load(p);
            Assert.Eq(null, r.LoadError, "saved file parses");
            Assert.Eq("LHC9059", r.MainEdid, "edid");
            Assert.Eq("none", r.Layout, "layout");
            Assert.Eq("台式机 \"1\"", r.SelfName, "self name");
            Assert.Eq((uint)16, r.SelfCode, "self code");
            Assert.Eq("Mac", r.PeerName, "peer name");
            Assert.Eq("m.local", r.PeerHost, "host");
            Assert.Eq("u", r.PeerUser, "user");
            Assert.Eq((uint)15, r.PeerCode, "peer code");
            Assert.Eq("~/screenctl/screenctl.sh", r.PeerProgram, "program");
            Assert.Eq("C:\\keys\\id", r.PeerKeyPath, "key path");
            Assert.Eq(1, r.Devices.Count, "devices");
            Assert.Eq("Switch", r.Devices[0].Name, "device name");
            Assert.Eq((uint)17, r.Devices[0].Code, "device code");
            Assert.Eq("Ctrl+Alt+2", r.Devices[0].Hotkey, "device hotkey");
            Assert.Eq(false, r.DirectTake, "direct take");
            Assert.Eq("schtasks /run /tn \"A B\"", r.AfterTakeCommand, "afterTake command");
            Assert.Eq(7, r.AfterTakeDelayMs, "afterTake delay");
            File.Delete(p);
        }

        public static void Test_Config_MalformedJsonReturnsDefaults()
        {
            string p = Temp("{ \"peer\": { \"host\": \"x\", }");
            var c = Config.Load(p);
            Assert.True(c.LoadError != null, "load error recorded");
            Assert.Eq("", c.PeerHost, "default host");
            Assert.Eq(2000, c.PollMs, "default poll");
            Assert.Eq("CHANGE_ME", c.MainEdid, "default edid");
            File.Delete(p);
        }

        public static void Test_Config_WrongTypeFallsBackToDefault()
        {
            string p = Temp("{ \"pollMs\": \"abc\", \"directTake\": \"maybe\" }");
            var c = Config.Load(p);
            Assert.Eq(2000, c.PollMs, "poll default on bad type");
            Assert.Eq(true, c.DirectTake, "bool default on bad type");
            Assert.Eq(null, c.LoadError, "not a parse failure");
            File.Delete(p);
        }

        public static void Test_Config_PartialJsonKeepsDefaults()
        {
            string p = Temp("{ \"peer\": { \"host\": \"only.local\" } }");
            var c = Config.Load(p);
            Assert.Eq("only.local", c.PeerHost, "host");
            Assert.Eq((uint)0, c.SelfCode, "default code kept");
            Assert.Eq("~/screenctl/screenctl.sh", c.PeerProgramPath, "default program kept");
            File.Delete(p);
        }
    }
}
