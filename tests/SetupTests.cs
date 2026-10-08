using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ScreenSwitch.Tests
{
    // 设置问答（设计文档第 9 节）：假环境加写好的输入，不碰硬件、SSH 和托盘程序
    public static class SetupTests
    {
        const string MainId = "\\\\?\\DISPLAY#LHC9059#5&26941f8e&0&UID4357#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
        const string SecondId = "\\\\?\\DISPLAY#TRG1002#5&26941f8e&0&UID4355#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
        const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExample screen-switch@PC";
        // 两端测试共用的用例行：名字依次是 MacBook、Switch、PS5
        const string InfoLine = "proto=3 os=mac code=15 name=TWFjQm9vaw== devices=17:U3dpdGNo,18:UFM1";

        class FakeEnv : ISetupEnv
        {
            public List<SetupMonitor> Mons = new List<SetupMonitor>();
            public int Connected = 2;
            // null = 读不到 capabilities
            public List<uint> Caps = null;
            public int SshRc = 0;
            public string SshOut = "ssh config updated";
            public string Key = SetupTests.Key;
            public PeerInfo Info = null;
            public TrayRestart Tray = TrayRestart.Restarted;
            public List<string> Calls = new List<string>();
            // 发 request info 时对方的设置
            public string InfoTarget = "";

            public List<SetupMonitor> Monitors() { Calls.Add("monitors"); return Mons; }
            public int ConnectedCount() { return Connected; }
            public List<uint> InputCodes(string mainEdid) { Calls.Add("caps " + mainEdid); return Caps; }
            public int SshClientSetup(string user, string host, out string output)
            {
                Calls.Add("ssh " + user + "@" + host);
                output = SshOut;
                return SshRc;
            }
            public string PublicKey() { return Key; }
            public PeerInfo RequestInfo(Config cfg)
            {
                Calls.Add("info");
                InfoTarget = cfg.PeerOs + " " + cfg.PeerUser + "@" + cfg.PeerHost;
                return Info;
            }
            public TrayRestart RestartTray() { Calls.Add("tray"); return Tray; }

            public int Count(string prefix)
            {
                int n = 0;
                foreach (string c in Calls) if (c.StartsWith(prefix)) n++;
                return n;
            }
        }

        // 一块应答 DDC 的主屏（正显示本机，编码 16）加一块副屏
        static FakeEnv TwoScreens()
        {
            var env = new FakeEnv();
            env.Mons.Add(new SetupMonitor(MainId, "P275MV", true, 0x10));
            env.Mons.Add(new SetupMonitor(SecondId, "T27", false, 0));
            env.Connected = 2;
            return env;
        }

        static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "ss-setup-" + Guid.NewGuid());
            Directory.CreateDirectory(d);
            return d;
        }

        class Result { public int Rc; public string Text; public int Unread; }

        // lines：依次回答每个提问；Unread 是没用到的行数（提问的个数与预期不符时不为 0）
        static Result Run(FakeEnv env, string dir, string[] args, params string[] lines)
        {
            var sb = new StringBuilder();
            foreach (string l in lines) sb.Append(l).Append("\n");
            var reader = new StringReader(sb.ToString());
            var writer = new StringWriter();
            var r = new Result();
            r.Rc = Setup.Run(args, Path.Combine(dir, "config.json"), reader, writer, env);
            r.Text = writer.ToString();
            while (reader.ReadLine() != null) r.Unread++;
            return r;
        }

        static readonly string[] Full = new string[0];
        static readonly string[] DevicesOnly = new string[] { "devices" };

        static Config Load(string dir) { return Config.Load(Path.Combine(dir, "config.json")); }

        // 一份已经在用的配置：对方是 Mac，两个设备，带一些设置问答不问的字段
        static Config Existing()
        {
            var c = new Config();
            c.MainEdid = "LHC9059"; c.Layout = "second"; c.SelfName = "台式机"; c.SelfCode = 16;
            c.PeerName = "MacBook"; c.PeerOs = "mac"; c.PeerHost = "my-mac.local"; c.PeerUser = "alice"; c.PeerCode = 15;
            c.PeerHotkey = "Ctrl+Alt+1"; c.PeerKeyPath = "C:\\keys\\id"; c.PeerTimeoutSec = 4;
            c.Devices.Add(new Device("设备 1", 17));
            c.Devices[0].Hotkey = "Ctrl+Alt+2";
            c.Devices.Add(new Device("设备 2", 18));
            c.DirectTake = false; c.PollMs = 1500; c.Debounce = 3; c.SshExe = "C:\\ssh\\ssh.exe";
            c.ReleaseWidth = 1920; c.ReleaseHeight = 1080;
            c.AfterTakeCommand = "schtasks /run /tn \"A B\""; c.AfterTakeDelayMs = 7;
            return c;
        }

        static void AssertUnrelatedKept(Config c)
        {
            Assert.Eq("Ctrl+Alt+1", c.PeerHotkey, "peer hotkey kept");
            Assert.Eq("C:\\keys\\id", c.PeerKeyPath, "key path kept");
            Assert.Eq(4, c.PeerTimeoutSec, "timeout kept");
            Assert.Eq(false, c.DirectTake, "directTake kept");
            Assert.Eq(1500, c.PollMs, "pollMs kept");
            Assert.Eq(3, c.Debounce, "debounce kept");
            Assert.Eq("C:\\ssh\\ssh.exe", c.SshExe, "sshExe kept");
            Assert.Eq(1920, c.ReleaseWidth, "release width kept");
            Assert.Eq(1080, c.ReleaseHeight, "release height kept");
            Assert.Eq("schtasks /run /tn \"A B\"", c.AfterTakeCommand, "afterTake command kept");
            Assert.Eq(7, c.AfterTakeDelayMs, "afterTake delay kept");
        }

        // ---- 纯函数 ----

        public static void Test_ModelId_InstanceId()
        {
            Assert.Eq("LHC9059", Setup.ModelId(MainId), "model");
            Assert.Eq("LHC9059#5&26941f8e&0&UID4357", Setup.InstanceId(MainId), "model#instance");
            Assert.Eq("TRG1002", Setup.ModelId("MONITOR\\TRG1002\\{4d36e96e-e325-11ce-bfc1-08002be10318}\\0001"), "registry-style id");
            Assert.Eq("", Setup.InstanceId("MONITOR\\TRG1002\\{4d36e96e-e325-11ce-bfc1-08002be10318}\\0001"), "no instance in a registry-style id");
            Assert.Eq("", Setup.ModelId(""), "empty");
            Assert.Eq("", Setup.ModelId(null), "null");
            Assert.Eq("", Setup.ModelId("garbage"), "no separators");
            Assert.Eq("", Setup.InstanceId("\\\\?\\DISPLAY#LHC9059"), "too short");
        }

        public static void Test_MainEdidFor_ModelWhenUnique_InstanceWhenShared()
        {
            var all = new List<string>(new string[] { MainId, SecondId });
            Assert.Eq("LHC9059", Setup.MainEdidFor(MainId, all), "unique model");

            string a = "\\\\?\\DISPLAY#DEL4321#5&aaa&0&UID1#{g}", b = "\\\\?\\DISPLAY#del4321#5&aaa&0&UID2#{g}";
            all = new List<string>(new string[] { a, b });
            Assert.Eq("DEL4321#5&aaa&0&UID1", Setup.MainEdidFor(a, all), "same model twice: model#instance");
            Assert.Eq("del4321#5&aaa&0&UID2", Setup.MainEdidFor(b, all), "case-insensitive model comparison");
            // 结果要能被其余代码当作设备路径的子串匹配到，且只匹配到这一块
            Assert.True(CcdLayout.IsMainDevicePath(a, Setup.MainEdidFor(a, all)), "matches its own path");
            Assert.True(!CcdLayout.IsMainDevicePath(b, Setup.MainEdidFor(a, all)), "does not match the twin");
            // 一个实例是另一个的前缀（UID1 / UID10）：带上后面的 # 才分得开
            string c = "\\\\?\\DISPLAY#DEL4321#5&aaa&0&UID10#{g}";
            all = new List<string>(new string[] { a, c });
            Assert.Eq("DEL4321#5&aaa&0&UID1#", Setup.MainEdidFor(a, all), "prefix of the twin's instance");
            Assert.True(!CcdLayout.IsMainDevicePath(c, Setup.MainEdidFor(a, all)), "does not match UID10");
            Assert.Eq("DEL4321#5&aaa&0&UID10", Setup.MainEdidFor(c, all), "the longer one needs no suffix");
            Assert.Eq("odd-id", Setup.MainEdidFor("odd-id", new List<string>(new string[] { "odd-id" })), "unparseable id used whole");
        }

        public static void Test_InputName_StandardMeanings()
        {
            Assert.Eq("DisplayPort 1", Setup.InputName(15), "15");
            Assert.Eq("DisplayPort 2", Setup.InputName(16), "16");
            Assert.Eq("HDMI 1", Setup.InputName(17), "17");
            Assert.Eq("HDMI 2", Setup.InputName(18), "18");
            Assert.Eq("", Setup.InputName(27), "others: just the number");
            Assert.Eq("", Setup.InputName(0), "0");
        }

        // 给对方执行的命令：Mac 用 screenctl.sh authorize，Windows 用 setup-sshd.ps1 -PeerPubKey
        public static void Test_PeerCommand()
        {
            Assert.Eq("~/screenctl/screenctl.sh authorize \"" + Key + "\"", Setup.PeerCommand("mac", Key), "mac");
            Assert.Eq("powershell -ExecutionPolicy Bypass -File setup-sshd.ps1 -PeerPubKey \"" + Key + "\"", Setup.PeerCommand("windows", Key), "windows");
        }

        // 公钥的注释会被放进双引号里交给 bash / PowerShell：去掉其中可能被解释的字符
        public static void Test_SafeKey_StripsUnsafeCommentCharacters()
        {
            Assert.Eq(Key, Setup.SafeKey(Key + "\r\n"), "plain key unchanged, trimmed");
            Assert.Eq("ssh-ed25519 AAAA+/b= aidb", Setup.SafeKey("ssh-ed25519 AAAA+/b= a$(id)`\"b"), "comment cleaned");
            Assert.Eq("ssh-ed25519 AAAA", Setup.SafeKey("ssh-ed25519   AAAA   $\"`"), "comment dropped when nothing is left");
            Assert.Eq("ssh-ed25519 AAAA me@pc", Setup.SafeKey("ssh-ed25519 AAAA me@pc extra words"), "only the first comment word");
            Assert.Eq(null, Setup.SafeKey("not-a-key"), "one field: not a key");
            Assert.Eq(null, Setup.SafeKey("ssh-ed25519 AA\"AA c"), "bad characters in the key itself");
            Assert.Eq(null, Setup.SafeKey(null), "null");
        }

        // ---- 完整的 setup ----

        // 一块 DDC 主屏 + 副屏 + 能连上的 Mac：生成的配置与手填的一致
        public static void Test_Full_MacPeerReachable_MatchesHandWrittenConfig()
        {
            string d = TempDir();
            FakeEnv env = TwoScreens();
            env.Info = PeerInfo.Parse(InfoLine);
            Result r = Run(env, d, Full,
                "书房 台式机",      // 本机名字
                "",                 // 设置对方：是
                "MacBook", "2", "my-mac.local", "alice",
                "");                // 对方执行完命令后回车
            Assert.Eq(0, r.Rc, "exit 0: " + r.Text);
            Assert.Eq(0, r.Unread, "every answer used");

            var e = new Config();
            e.MainEdid = "LHC9059"; e.Layout = "second"; e.SelfName = "书房 台式机"; e.SelfCode = 16;
            e.PeerName = "MacBook"; e.PeerOs = "mac"; e.PeerHost = "my-mac.local"; e.PeerUser = "alice"; e.PeerCode = 15;
            e.Devices.Add(new Device("Switch", 17));
            e.Devices.Add(new Device("PS5", 18));
            string expected = Path.Combine(d, "expected.json");
            e.Save(expected);
            Assert.Eq(File.ReadAllText(expected), File.ReadAllText(Path.Combine(d, "config.json")), "same file as a hand-written config");

            Assert.Eq(1, env.Count("ssh alice@my-mac.local"), "ssh client side set up for the peer");
            Assert.Eq("mac alice@my-mac.local", env.InfoTarget, "request info sent with the new peer settings");
            Assert.Eq(1, env.Count("tray"), "tray restarted once");
            Assert.Eq(0, env.Count("caps"), "devices came from the peer: capabilities not read");
            Assert.True(r.Text.Contains(Key), "public key printed");
            Assert.True(r.Text.Contains("~/screenctl/screenctl.sh authorize \"" + Key + "\""), "command for a Mac peer: " + r.Text);
            Assert.True(r.Text.Contains("Switch") && r.Text.Contains("PS5"), "adopted devices shown");
            Directory.Delete(d, true);
        }

        // 完整的 setup 写配置时清掉"直接切换从未成功"的记录（设计文档 6.3）；setup devices 不清
        public static void Test_Full_DeletesDirectTakeRecord_DevicesOnlyKeepsIt()
        {
            string d = TempDir();
            string record = Path.Combine(d, "direct-take.txt");
            File.WriteAllText(record, "never");
            Result r = Run(TwoScreens(), d, Full, "", "n", "");
            Assert.Eq(0, r.Rc, "exit 0: " + r.Text);
            Assert.Eq(false, File.Exists(record), "record deleted by the full setup");

            File.WriteAllText(record, "never");
            r = Run(TwoScreens(), d, DevicesOnly, "");
            Assert.Eq(0, r.Rc, "devices exit 0: " + r.Text);
            Assert.Eq(true, File.Exists(record), "setup devices leaves the record alone");
            Directory.Delete(d, true);
        }

        // 停下来时（没有写配置）记录也不动
        public static void Test_Full_Stopped_KeepsDirectTakeRecord()
        {
            string d = TempDir();
            string record = Path.Combine(d, "direct-take.txt");
            File.WriteAllText(record, "never");
            FakeEnv env = TwoScreens();
            env.Connected = 3;
            Assert.Eq(1, Run(env, d, Full).Rc, "stopped");
            Assert.Eq(true, File.Exists(record), "record kept");
            Directory.Delete(d, true);
        }

        public static void Test_Full_PeerSkipped_DevicesEnteredByHand()
        {
            string d = TempDir();
            FakeEnv env = TwoScreens();
            env.Caps = new List<uint>(new uint[] { 15, 16, 17, 18 });
            Result r = Run(env, d, Full,
                "",                 // 名字不改
                "n",                // 不设置对方
                "17", "任天堂 Switch",
                "");                // 结束
            Assert.Eq(0, r.Rc, "exit 0: " + r.Text);
            Assert.Eq(0, r.Unread, "every answer used");
            Config c = Load(d);
            Assert.Eq(null, c.LoadError, "written file parses");
            Assert.Eq("LHC9059", c.MainEdid, "edid");
            Assert.Eq(Environment.MachineName, c.SelfName, "Enter keeps the computer name");
            Assert.Eq((uint)16, c.SelfCode, "self code");
            Assert.Eq(false, c.PeerConfigured, "no peer");
            Assert.Eq(1, c.Devices.Count, "one device");
            Assert.Eq("任天堂 Switch", c.Devices[0].Name, "name with Chinese and a space");
            Assert.Eq((uint)17, c.Devices[0].Code, "code");
            Assert.Eq(0, env.Count("ssh"), "no ssh setup");
            Assert.Eq(0, env.Count("info"), "no request info");
            Assert.Eq(1, env.Count("caps LHC9059"), "capabilities read for the chosen main screen");
            Assert.True(r.Text.Contains("  15  DisplayPort 1"), "15 listed with its meaning: " + r.Text);
            Assert.True(r.Text.Contains("  17  HDMI 1") && r.Text.Contains("  18  HDMI 2"), "HDMI listed");
            Assert.True(!r.Text.Contains("  16  DisplayPort 2"), "this computer's own code not offered");
            Directory.Delete(d, true);
        }

        // 对方连不上：不算错，编码留作未知；Windows 对方的命令是 setup-sshd.ps1
        public static void Test_Full_WindowsPeerUnreachable_CodeStaysUnknown()
        {
            string d = TempDir();
            FakeEnv env = TwoScreens();
            env.Info = null;
            Result r = Run(env, d, Full,
                "",
                "y",
                "", "1", "192.168.1.20", "pc\\bob",   // 名字回车 = 按系统叫 Windows；whoami 的写法也认
                "",
                "");                // 不加设备
            Assert.Eq(0, r.Rc, "exit 0: " + r.Text);
            Assert.Eq(0, r.Unread, "every answer used");
            Config c = Load(d);
            Assert.Eq("Windows", c.PeerName, "default name follows the system");
            Assert.Eq("windows", c.PeerOs, "os");
            Assert.Eq("192.168.1.20", c.PeerHost, "host");
            Assert.Eq("bob", c.PeerUser, "user without the computer prefix");
            Assert.Eq(true, c.PeerConfigured, "peer configured");
            Assert.Eq((uint)0, c.PeerCode, "code unknown");
            Assert.Eq(1, env.Count("ssh bob@192.168.1.20"), "ssh setup");
            Assert.Eq(1, env.Count("info"), "request info tried");
            Assert.Eq(1, env.Count("tray"), "still written and restarted");
            Assert.True(r.Text.Contains("powershell -ExecutionPolicy Bypass -File setup-sshd.ps1 -PeerPubKey \"" + Key + "\""), "command for a Windows peer: " + r.Text);
            Directory.Delete(d, true);
        }

        public static void Test_Full_NoDdcMonitor_Exit1_ConfigNotWritten()
        {
            string d = TempDir();
            var env = new FakeEnv();
            env.Mons.Add(new SetupMonitor(SecondId, "T27", false, 0));
            env.Connected = 1;
            Result r = Run(env, d, Full, "x", "y");
            Assert.Eq(1, r.Rc, "exit 1");
            Assert.Eq(false, File.Exists(Path.Combine(d, "config.json")), "no config written");
            Assert.Eq(0, env.Count("tray"), "tray untouched");
            Assert.Eq(2, r.Unread, "nothing asked");
            Assert.True(r.Text.Contains("DDC/CI"), "explains: " + r.Text);

            // 已有的配置文件原样不动
            string p = Path.Combine(d, "config.json");
            Existing().Save(p);
            byte[] before = File.ReadAllBytes(p);
            Assert.Eq(1, Run(env, d, Full).Rc, "exit 1 with an existing config");
            Assert.Eq(Convert.ToBase64String(before), Convert.ToBase64String(File.ReadAllBytes(p)), "existing config untouched");
            Directory.Delete(d, true);
        }

        // 已设置的主屏此刻没有应答（只认当前输入口的显示器正显示别的设备），而另一块屏应答 DDC：
        // 不能悄悄把另一块屏当成主屏。默认停下；用户确认换主屏才换。
        public static void Test_Full_ConfiguredMainSilent_OtherAnswers_AsksBeforeSwitching()
        {
            string d = TempDir();
            Existing().Save(Path.Combine(d, "config.json"));
            var env = new FakeEnv();
            env.Mons.Add(new SetupMonitor(MainId, "P275MV", false, 0));
            env.Mons.Add(new SetupMonitor(SecondId, "LQ25F165L", true, 7));
            Result r = Run(env, d, Full, "");
            Assert.Eq(1, r.Rc, "Enter = do not switch: stop");
            Assert.True(r.Text.Contains("P275MV") && r.Text.Contains("没有应答"), "says the configured main screen is silent: " + r.Text);
            Assert.Eq("LHC9059", Load(d).MainEdid, "main screen unchanged");
            Assert.Eq((uint)16, Load(d).SelfCode, "own code unchanged");
            Assert.Eq(0, env.Count("tray"), "tray not restarted");

            // 确认换主屏，名字不改，保留对方，两个设备不改名，不再添加
            r = Run(env, d, Full, "y", "", "", "", "", "");
            Assert.Eq(0, r.Rc, "confirmed: goes on: " + r.Text);
            Assert.Eq(0, r.Unread, "every answer used");
            Assert.Eq("TRG1002", Load(d).MainEdid, "main screen switched on request");
            Directory.Delete(d, true);
        }

        public static void Test_Full_ThreeScreens_Stops()
        {
            string d = TempDir();
            FakeEnv env = TwoScreens();
            env.Mons.Add(new SetupMonitor("\\\\?\\DISPLAY#ABC0001#1&1&0&UID1#{g}", "Third", false, 0));
            env.Connected = 3;
            Result r = Run(env, d, Full, "x");
            Assert.Eq(1, r.Rc, "exit 1");
            Assert.Eq(1, r.Unread, "nothing asked");
            Assert.Eq(false, File.Exists(Path.Combine(d, "config.json")), "no config written");
            Assert.Eq(0, env.Count("tray"), "tray untouched");
            Directory.Delete(d, true);
        }

        public static void Test_Full_OnlyMainScreen_LayoutNone()
        {
            string d = TempDir();
            var env = new FakeEnv();
            env.Mons.Add(new SetupMonitor(MainId, "P275MV", true, 0x10));
            env.Connected = 1;
            Result r = Run(env, d, Full, "", "n", "");
            Assert.Eq(0, r.Rc, "exit 0: " + r.Text);
            Assert.Eq("none", Load(d).Layout, "layout none");
            Directory.Delete(d, true);
        }

        // 两块都应答 DDC：列出来让用户选；选错重问
        public static void Test_Full_TwoDdcMonitors_UserChooses()
        {
            string d = TempDir();
            var env = new FakeEnv();
            env.Mons.Add(new SetupMonitor(MainId, "P275MV", true, 0x10));
            env.Mons.Add(new SetupMonitor("\\\\?\\DISPLAY#DEL4321#5&aaa&0&UID1#{g}", "DELL U27", true, 0x0F0F));
            env.Connected = 2;
            Result r = Run(env, d, Full, "3", "x", "", "2", "", "n", "");
            Assert.Eq(0, r.Rc, "exit 0: " + r.Text);
            Assert.Eq(0, r.Unread, "every answer used");
            Config c = Load(d);
            Assert.Eq("DEL4321", c.MainEdid, "the chosen monitor");
            Assert.Eq((uint)15, c.SelfCode, "low 8 bits of the chosen monitor's input value");
            Assert.Eq("second", c.Layout, "two screens");

            // 再运行一次：已配置的主屏是默认选项，回车即可
            r = Run(env, d, Full, "", "", "n", "");
            Assert.Eq(0, r.Rc, "second run: " + r.Text);
            Assert.Eq("DEL4321", Load(d).MainEdid, "Enter keeps the configured main screen");
            Directory.Delete(d, true);
        }

        public static void Test_Full_TwoMonitorsOfTheSameModel_UsesInstanceId()
        {
            string d = TempDir();
            var env = new FakeEnv();
            env.Mons.Add(new SetupMonitor("\\\\?\\DISPLAY#DEL4321#5&aaa&0&UID1#{g}", "DELL U27", true, 0x0F));
            env.Mons.Add(new SetupMonitor("\\\\?\\DISPLAY#DEL4321#5&aaa&0&UID2#{g}", "DELL U27", false, 0));
            env.Connected = 2;
            Result r = Run(env, d, Full, "", "n", "");
            Assert.Eq(0, r.Rc, "exit 0: " + r.Text);
            Assert.Eq("DEL4321#5&aaa&0&UID1", Load(d).MainEdid, "model#instance");
            Directory.Delete(d, true);
        }

        // 已配置的对方可以直接保留：不重做 SSH，仍然问一次 request info；不问的字段、设备的快捷键都留着
        public static void Test_Full_KeepExistingPeer_UnrelatedFieldsSurvive()
        {
            string d = TempDir();
            Config start = Existing();
            start.PeerCode = 0;
            start.Save(Path.Combine(d, "config.json"));
            FakeEnv env = TwoScreens();
            env.Info = PeerInfo.Parse(InfoLine);
            Result r = Run(env, d, Full, "", "");
            Assert.Eq(0, r.Rc, "exit 0: " + r.Text);
            Assert.Eq(0, r.Unread, "every answer used");
            Config c = Load(d);
            Assert.Eq("台式机", c.SelfName, "name kept");
            Assert.Eq("MacBook", c.PeerName, "peer kept");
            Assert.Eq("my-mac.local", c.PeerHost, "host kept");
            Assert.Eq((uint)15, c.PeerCode, "peer code learnt from request info");
            Assert.Eq(0, env.Count("ssh"), "ssh client setup not repeated");
            Assert.Eq(1, env.Count("info"), "request info");
            Assert.Eq(2, c.Devices.Count, "devices adopted from the peer");
            Assert.Eq("Switch", c.Devices[0].Name, "peer's name for 17");
            Assert.Eq("Ctrl+Alt+2", c.Devices[0].Hotkey, "hotkey of the device with the same code kept");
            Assert.Eq("PS5", c.Devices[1].Name, "peer's name for 18");
            AssertUnrelatedKept(c);
            Directory.Delete(d, true);
        }

        // 对方报来的设备列表里去掉本机和对方自己的编码；对方没有设备时改为手工编辑，已有的设备不丢
        public static void Test_Full_PeerDevices_MinusOwnCodes_EmptyFallsBackToManual()
        {
            string d = TempDir();
            Existing().Save(Path.Combine(d, "config.json"));
            FakeEnv env = TwoScreens();
            env.Info = PeerInfo.Parse("proto=3 os=mac code=15 name=TWFjQm9vaw== devices=16:U3dpdGNo,15:UFM1,18:UFM1");
            Result r = Run(env, d, Full, "", "");
            Assert.Eq(0, r.Rc, "exit 0: " + r.Text);
            Config c = Load(d);
            Assert.Eq(1, c.Devices.Count, "16 (self) and 15 (peer) dropped");
            Assert.Eq((uint)18, c.Devices[0].Code, "18 kept");

            Existing().Save(Path.Combine(d, "config.json"));
            env.Info = PeerInfo.Parse("proto=3 os=mac code=15 name=TWFjQm9vaw== devices=");
            r = Run(env, d, Full, "", "", "", "", "");
            Assert.Eq(0, r.Rc, "exit 0: " + r.Text);
            Assert.Eq(0, r.Unread, "two rename questions and the add question");
            Assert.Eq(2, Load(d).Devices.Count, "local devices kept when the peer has none");
            Directory.Delete(d, true);
        }

        public static void Test_Full_ReenterPeer_RemovePeer()
        {
            string d = TempDir();
            Existing().Save(Path.Combine(d, "config.json"));
            FakeEnv env = TwoScreens();
            // n = 重新填写：换成一台 Windows，主机变了所以旧编码作废
            Result r = Run(env, d, Full, "", "n", "书房 PC", "windows", "pc.local", "bob", "", "", "", "");
            Assert.Eq(0, r.Rc, "exit 0: " + r.Text);
            Assert.Eq(0, r.Unread, "every answer used");
            Config c = Load(d);
            Assert.Eq("书房 PC", c.PeerName, "new name");
            Assert.Eq("windows", c.PeerOs, "new os");
            Assert.Eq("pc.local", c.PeerHost, "new host");
            Assert.Eq((uint)0, c.PeerCode, "old code dropped with the old host");
            Assert.Eq("C:\\Users\\bob\\ScreenSwitch\\ScreenSwitch.exe", c.PeerProgramPath, "program follows the new system");

            // - = 不要对方
            r = Run(env, d, Full, "", "-", "", "", "");
            Assert.Eq(0, r.Rc, "exit 0: " + r.Text);
            Assert.Eq(false, Load(d).PeerConfigured, "peer removed");
            Directory.Delete(d, true);
        }

        public static void Test_Full_BadHostAndUser_AskedAgain()
        {
            string d = TempDir();
            FakeEnv env = TwoScreens();
            Result r = Run(env, d, Full, "", "y", "Mac", "5", "mac",
                "", "my mac.local", "a\"b", "m.local",
                "", "al ice", "a@b", "alice",
                "", "");
            Assert.Eq(0, r.Rc, "exit 0: " + r.Text);
            Assert.Eq(0, r.Unread, "every answer used");
            Config c = Load(d);
            Assert.Eq("m.local", c.PeerHost, "host");
            Assert.Eq("alice", c.PeerUser, "user");
            Assert.Eq(1, env.Count("ssh alice@m.local"), "ssh setup only with the accepted values");
            Directory.Delete(d, true);
        }

        // setup-client.ps1 不在程序旁边：打印手动命令，继续
        public static void Test_Full_ClientScriptMissing_PrintsManualCommand()
        {
            string d = TempDir();
            FakeEnv env = TwoScreens();
            env.SshRc = -1; env.Key = null;
            Result r = Run(env, d, Full, "", "y", "", "mac", "m.local", "alice", "");
            Assert.Eq(0, r.Rc, "exit 0: " + r.Text);
            Assert.Eq(0, r.Unread, "no pause when there is no key to hand over");
            Assert.True(r.Text.Contains("powershell -ExecutionPolicy Bypass -File setup-client.ps1 -PeerUser alice -PeerHost m.local"), "manual command: " + r.Text);
            Assert.Eq(true, Load(d).PeerConfigured, "peer still written");
            Directory.Delete(d, true);
        }

        // 输入中途结束：退出码 1，不写配置
        public static void Test_Full_InputEnds_Exit1_ConfigUntouched()
        {
            string d = TempDir();
            FakeEnv env = TwoScreens();
            Result r = Run(env, d, Full, "名字");
            Assert.Eq(1, r.Rc, "exit 1");
            Assert.Eq(false, File.Exists(Path.Combine(d, "config.json")), "no config written");
            Assert.Eq(0, env.Count("tray"), "tray untouched");
            Directory.Delete(d, true);
        }

        public static void Test_BadArguments_Exit1_NothingTouched()
        {
            string d = TempDir();
            FakeEnv env = TwoScreens();
            Assert.Eq(1, Run(env, d, new string[] { "bogus" }).Rc, "unknown sub-command");
            Assert.Eq(1, Run(env, d, new string[] { "devices", "x" }).Rc, "extra argument");
            Assert.Eq(0, env.Calls.Count, "environment not touched");
            Directory.Delete(d, true);
        }

        // 读不了的 config.json：另存为 .bak 后重新生成
        public static void Test_Full_MalformedConfig_BackedUp()
        {
            string d = TempDir();
            string p = Path.Combine(d, "config.json");
            File.WriteAllText(p, "{ broken");
            Result r = Run(TwoScreens(), d, Full, "", "n", "");
            Assert.Eq(0, r.Rc, "exit 0: " + r.Text);
            Assert.Eq("{ broken", File.ReadAllText(p + ".bak"), "old file kept as .bak");
            Assert.Eq(null, Load(d).LoadError, "new file parses");
            Directory.Delete(d, true);
        }

        // ---- setup devices ----

        // 验收：把"设备 1"改名为 Switch；删除、添加、撞码的拒绝
        public static void Test_Devices_RenameDeleteAdd_RejectsBadCodes()
        {
            string d = TempDir();
            Existing().Save(Path.Combine(d, "config.json"));
            FakeEnv env = TwoScreens();
            env.Caps = new List<uint>(new uint[] { 15, 16, 17, 18, 27 });
            Result r = Run(env, d, DevicesOnly,
                "Switch",           // 设备 1 改名
                "-",                // 设备 2 删除
                "17",               // 已经是 Switch
                "16",               // 本机
                "15",               // 对方
                "0", "abc", "256",  // 不是 1–255 的十进制数
                "27", "任天堂 二号 机",
                "18", "",           // 名字回车 = 默认名
                "");
            Assert.Eq(0, r.Rc, "exit 0: " + r.Text);
            Assert.Eq(0, r.Unread, "every answer used");
            Config c = Load(d);
            Assert.Eq(3, c.Devices.Count, "devices");
            Assert.Eq("Switch", c.Devices[0].Name, "renamed");
            Assert.Eq((uint)17, c.Devices[0].Code, "code kept");
            Assert.Eq("Ctrl+Alt+2", c.Devices[0].Hotkey, "hotkey kept");
            Assert.Eq("任天堂 二号 机", c.Devices[1].Name, "name with Chinese and spaces");
            Assert.Eq((uint)27, c.Devices[1].Code, "27");
            Assert.Eq("设备 3", c.Devices[2].Name, "default name");
            Assert.Eq((uint)18, c.Devices[2].Code, "18 added again after the delete");
            Assert.Eq("LHC9059", c.MainEdid, "main screen untouched");
            Assert.Eq("台式机", c.SelfName, "self untouched");
            Assert.Eq((uint)15, c.PeerCode, "peer untouched");
            AssertUnrelatedKept(c);
            Assert.Eq(0, env.Count("monitors"), "devices only: monitors not probed");
            Assert.Eq(0, env.Count("ssh"), "no ssh");
            Assert.Eq(0, env.Count("info"), "no request info");
            Assert.Eq(1, env.Count("tray"), "tray restarted");
            Assert.True(r.Text.Contains("  27"), "27 listed");
            Assert.True(!r.Text.Contains("  15  DisplayPort 1") && !r.Text.Contains("  16  DisplayPort 2"), "self and peer codes not offered: " + r.Text);
            Directory.Delete(d, true);
        }

        // capabilities 读不到：说明后继续手工输入
        public static void Test_Devices_CapabilitiesUnreadable_ManualEntryContinues()
        {
            string d = TempDir();
            Config start = Existing();
            start.Devices.Clear();
            start.Save(Path.Combine(d, "config.json"));
            FakeEnv env = TwoScreens();
            env.Caps = null;
            Result r = Run(env, d, DevicesOnly, " 17 ", " PS5 ", "");
            Assert.Eq(0, r.Rc, "exit 0: " + r.Text);
            Config c = Load(d);
            Assert.Eq(1, c.Devices.Count, "device added");
            Assert.Eq("PS5", c.Devices[0].Name, "trimmed name");
            Assert.Eq((uint)17, c.Devices[0].Code, "trimmed code");
            Directory.Delete(d, true);
        }

        // 还没设置过主屏：先运行完整的 setup
        public static void Test_Devices_NoMainScreenYet_Exit1()
        {
            string d = TempDir();
            FakeEnv env = TwoScreens();
            Result r = Run(env, d, DevicesOnly, "17", "x", "");
            Assert.Eq(1, r.Rc, "exit 1");
            Assert.Eq(false, File.Exists(Path.Combine(d, "config.json")), "no config written");
            Assert.Eq(0, env.Count("tray"), "tray untouched");
            Directory.Delete(d, true);
        }

        public static void Test_Devices_InputEnds_Exit1_ConfigUntouched()
        {
            string d = TempDir();
            string p = Path.Combine(d, "config.json");
            Existing().Save(p);
            byte[] before = File.ReadAllBytes(p);
            FakeEnv env = TwoScreens();
            Result r = Run(env, d, DevicesOnly, "Switch");
            Assert.Eq(1, r.Rc, "exit 1");
            Assert.Eq(Convert.ToBase64String(before), Convert.ToBase64String(File.ReadAllBytes(p)), "config untouched");
            Assert.Eq(0, env.Count("tray"), "tray untouched");
            Directory.Delete(d, true);
        }

        // ---- 重定向的输入、仓库里的默认配置 ----

        // 重定向的输入按 UTF-8 读；不是合法的 UTF-8 时按系统代码页（这里用 936）读
        public static void Test_ConsoleIo_DecodeLine_Utf8ThenFallback()
        {
            Encoding gbk = Encoding.GetEncoding(936);
            Assert.Eq("任天堂 Switch", ConsoleIo.DecodeLine(Encoding.UTF8.GetBytes("任天堂 Switch"), gbk), "utf-8");
            Assert.Eq("任天堂 Switch", ConsoleIo.DecodeLine(gbk.GetBytes("任天堂 Switch"), gbk), "gbk fallback");
            Assert.Eq("abc", ConsoleIo.DecodeLine(Encoding.ASCII.GetBytes("abc"), gbk), "ascii");
            Assert.Eq("", ConsoleIo.DecodeLine(new byte[0], gbk), "empty");
        }

        public static void Test_ConsoleIo_RedirectedReader_Lines()
        {
            byte[] bom = new byte[] { 0xEF, 0xBB, 0xBF };
            byte[] body = Encoding.UTF8.GetBytes("书房 台式机\r\n\r\nlast");
            var ms = new MemoryStream();
            ms.Write(bom, 0, bom.Length); ms.Write(body, 0, body.Length);
            ms.Position = 0;
            TextReader r = ConsoleIo.RedirectedReader(ms, Encoding.GetEncoding(936));
            Assert.Eq("书房 台式机", r.ReadLine(), "first line, BOM skipped, CR removed");
            Assert.Eq("", r.ReadLine(), "empty line");
            Assert.Eq("last", r.ReadLine(), "last line without a newline");
            Assert.Eq(null, r.ReadLine(), "end");
        }

        // config.default.json（install.cmd 首次安装时复制）是 v3 的写法，读出来等于没有配置过
        public static void Test_DefaultConfigFile_IsNeutralV3()
        {
            string p = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..\\config.default.json"));
            Assert.True(File.Exists(p), "config.default.json next to the bin directory: " + p);
            string text = File.ReadAllText(p);
            Assert.True(!text.Contains("\"codes\"") && !text.Contains("\"hotkeys\"") && !text.Contains("\"command\": \"~"), "no legacy keys");
            Config c = Config.Load(p);
            Assert.Eq(null, c.LoadError, "parses");
            Assert.Eq("CHANGE_ME", c.MainEdid, "edid placeholder");
            Assert.Eq("second", c.Layout, "layout");
            Assert.Eq(Environment.MachineName, c.SelfName, "empty self.name = computer name");
            Assert.Eq((uint)0, c.SelfCode, "self code");
            Assert.Eq("mac", c.PeerOs, "peer os");
            Assert.Eq(false, c.PeerConfigured, "no peer");
            Assert.Eq((uint)0, c.PeerCode, "peer code");
            Assert.Eq(0, c.Devices.Count, "no devices");
            Assert.Eq(true, c.DirectTake, "directTake");
            Assert.Eq(0, c.ReleaseWidth, "release width");
            Assert.Eq(0, c.ReleaseHeight, "release height");
            Assert.Eq("", c.AfterTakeCommand, "afterTake kept, empty");
            Assert.Eq(3000, c.AfterTakeDelayMs, "afterTake delay");
        }
    }
}
