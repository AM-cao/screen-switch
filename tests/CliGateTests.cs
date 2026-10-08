using System;
using System.IO;

namespace ScreenSwitch.Tests
{
    public static class CliGateTests
    {
        static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "ss-" + Guid.NewGuid());
            Directory.CreateDirectory(d);
            TestCfg.Legacy().Save(Path.Combine(d, "config.json"));
            return d;
        }

        // 设计文档 5.2、5.3
        public static void Test_SshGate_AcceptsGivePeerAndCode()
        {
            string d = TempDir();
            StateFile.WriteState(d, true, 1000);
            Assert.Eq(0, Cli.SshGate("C:\\Users\\bob\\ScreenSwitch\\ScreenSwitch.exe request give peer", d, 1001), "peer accepted");
            Assert.Eq("give peer", StateFile.ReadAndDeleteCommand(d), "cmd peer");
            Assert.Eq(0, Cli.SshGate("C:\\x\\ScreenSwitchCli.exe  request give  code 18", d, 1001), "code accepted via cli exe, double spaces");
            Assert.Eq("give code 18", StateFile.ReadAndDeleteCommand(d), "cmd code");
            Assert.Eq(1, Cli.SshGate("C:\\x\\ScreenSwitch.exe request give code 19", d, 1001), "passes the gate, request says unknown device");
            Assert.Eq(false, File.Exists(Path.Combine(d, "cmd.txt")), "unknown device: no cmd");
            StateFile.WriteState(d, false, 1000);
            Assert.Eq(2, Cli.SshGate("C:\\x\\ScreenSwitch.exe request give peer", d, 1001), "not owned passes gate, request says 2");
            Directory.Delete(d, true);
        }

        // 旧写法照常接受：mac → give peer，other → 第一个设备
        public static void Test_SshGate_AcceptsLegacyMacAndOther()
        {
            string d = TempDir();
            StateFile.WriteState(d, true, 1000);
            Assert.Eq(0, Cli.SshGate("C:\\Users\\Administrator\\AppData\\Local\\ScreenSwitch\\ScreenSwitch.exe request give mac", d, 1001), "mac accepted");
            Assert.Eq("give peer", StateFile.ReadAndDeleteCommand(d), "cmd mac");
            Assert.Eq(0, Cli.SshGate("C:\\x\\ScreenSwitchCli.exe  request give other", d, 1001), "other accepted via cli exe, double space");
            Assert.Eq("give code 17", StateFile.ReadAndDeleteCommand(d), "cmd other");
            StateFile.WriteState(d, false, 1000);
            Assert.Eq(2, Cli.SshGate("C:\\x\\ScreenSwitch.exe request give mac", d, 1001), "not owned passes gate, request says 2");
            Directory.Delete(d, true);
        }

        public static void Test_SshGate_AcceptsRequestTake()
        {
            string d = TempDir();
            StateFile.WriteState(d, false, 1000);
            Assert.Eq(0, Cli.SshGate("C:\\Users\\Administrator\\ScreenSwitch\\ScreenSwitch.exe request take", d, 1001), "take accepted");
            Assert.Eq("pre-take", StateFile.ReadAndDeleteCommand(d), "cmd pre-take");
            Assert.Eq(0, Cli.SshGate("C:\\x\\ScreenSwitchCli.exe\trequest  take ", d, 1001), "via cli exe, odd spacing");
            Assert.Eq("pre-take", StateFile.ReadAndDeleteCommand(d), "cmd pre-take again");
            StateFile.WriteState(d, true, 1000);
            Assert.Eq(0, Cli.SshGate("C:\\x\\ScreenSwitch.exe request take", d, 1001), "owned: 0");
            Assert.Eq(false, File.Exists(Path.Combine(d, "cmd.txt")), "owned: no cmd");
            Assert.Eq(3, Cli.SshGate("C:\\x\\ScreenSwitch.exe request take", d, 1020), "stale: 3");
            Directory.Delete(d, true);
        }

        public static void Test_SshGate_AcceptsRequestInfo()
        {
            string d = TempDir();
            Assert.Eq(0, Cli.SshGate("C:\\x\\ScreenSwitch.exe request info", d, 1001), "info accepted with no daemon state");
            Assert.Eq(false, File.Exists(Path.Combine(d, "cmd.txt")), "info writes no cmd");
            Assert.Eq(1, Cli.SshGate("C:\\x\\ScreenSwitch.exe request info x", d, 1001), "extra arg rejected");
            Directory.Delete(d, true);
        }

        // 程序路径里有空格（用户名带空格）或带引号也认
        public static void Test_SshGate_ExePathWithSpacesOrQuotes()
        {
            string d = TempDir();
            StateFile.WriteState(d, true, 1000);
            Assert.Eq(0, Cli.SshGate("C:\\Users\\John Smith\\ScreenSwitch\\ScreenSwitch.exe request give peer", d, 1001), "space in path");
            Assert.Eq("give peer", StateFile.ReadAndDeleteCommand(d), "cmd");
            Assert.Eq(0, Cli.SshGate("\"C:\\Users\\John Smith\\ScreenSwitch\\ScreenSwitchCli.exe\" request give peer", d, 1001), "quoted path");
            Assert.Eq("give peer", StateFile.ReadAndDeleteCommand(d), "cmd quoted");
            Assert.Eq(1, Cli.SshGate("C:\\Users\\John Smith\\other.exe request give peer", d, 1001), "wrong program still rejected");
            Directory.Delete(d, true);
        }

        public static void Test_SshGate_RejectsMalformedTake()
        {
            string d = TempDir();
            StateFile.WriteState(d, false, 1000);
            string[] bad = new string[] {
                "C:\\x\\ScreenSwitch.exe request take now", "C:\\x\\ScreenSwitch.exe request take; id",
                "C:\\x\\ScreenSwitch.exe request take;", "C:\\x\\ScreenSwitch.exe request take mac",
                "C:\\x\\ScreenSwitch.exe take", "C:\\x\\other.exe request take", "request take", "ScreenSwitch.exe request"
            };
            foreach (string b in bad)
            {
                Assert.Eq(1, Cli.SshGate(b, d, 1001), "rejected: " + b);
                Assert.Eq(false, File.Exists(Path.Combine(d, "cmd.txt")), "no cmd written for: " + b);
            }
            Directory.Delete(d, true);
        }

        public static void Test_SshGate_RejectsEverythingElse()
        {
            string d = TempDir();
            StateFile.WriteState(d, true, 1000);
            string[] bad = new string[] {
                null, "", "C:\\x\\ScreenSwitch.exe request give windows", "C:\\x\\ScreenSwitch.exe request give self",
                "C:\\x\\ScreenSwitch.exe status",
                "cmd.exe /c dir", "C:\\x\\ScreenSwitch.exe request give mac; id", "C:\\x\\ScreenSwitch.exe request give mac extra",
                "C:\\x\\ScreenSwitch.exe request give peer extra", "C:\\x\\ScreenSwitch.exe request give peer;",
                "C:\\x\\ScreenSwitch.exe request give code", "C:\\x\\ScreenSwitch.exe request give code 17 18",
                "C:\\x\\ScreenSwitch.exe request give code 0x11", "C:\\x\\ScreenSwitch.exe request give code 0",
                "C:\\x\\ScreenSwitch.exe request give code 256", "C:\\x\\ScreenSwitch.exe request give code 17;id",
                "C:\\x\\ScreenSwitch.exe request give", "C:\\x\\ScreenSwitch.exe request setup", "C:\\x\\ScreenSwitch.exe setup",
                "C:\\x\\other.exe request give mac", "request give mac", "request give peer"
            };
            foreach (string b in bad)
            {
                Assert.Eq(1, Cli.SshGate(b, d, 1001), "rejected: " + (b ?? "<null>"));
                Assert.Eq(false, File.Exists(Path.Combine(d, "cmd.txt")), "no cmd written for: " + (b ?? "<null>"));
            }
            string log = File.ReadAllText(Path.Combine(d, "screenswitch.log"));
            Assert.True(log.Contains("ssh-gate rejected"), "rejections logged");
            Directory.Delete(d, true);
        }
    }
}
