using System;
using System.IO;

namespace ScreenSwitch.Tests
{
    public static class CliTests
    {
        static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "ss-" + Guid.NewGuid());
            Directory.CreateDirectory(d);
            return d;
        }

        static int Give(string d, long now, params string[] target)
        {
            return Cli.RequestGive(d, TestCfg.Legacy(), target, now);
        }

        public static void Test_Request_NoState_Returns3()
        {
            string d = TempDir();
            Assert.Eq(3, Give(d, 1000, "peer"), "no state");
            Directory.Delete(d, true);
        }

        public static void Test_Request_StaleState_Returns3()
        {
            string d = TempDir();
            StateFile.WriteState(d, true, 1000);
            Assert.Eq(3, Give(d, 1011, "peer"), "stale by 11s");
            Assert.Eq(0, Give(d, 1010, "peer"), "10s is still fresh");
            Directory.Delete(d, true);
        }

        public static void Test_Request_NotOwned_Returns2()
        {
            string d = TempDir();
            StateFile.WriteState(d, false, 1000);
            Assert.Eq(2, Give(d, 1001, "peer"), "not owned");
            Assert.Eq(false, File.Exists(Path.Combine(d, "cmd.txt")), "no cmd written");
            Directory.Delete(d, true);
        }

        // 设计文档 5.2 / 5.1：命令文件内容为 give peer、give code <n>、give self
        public static void Test_Request_Owned_WritesV3Commands()
        {
            string d = TempDir();
            StateFile.WriteState(d, true, 1000);
            Assert.Eq(0, Give(d, 1001, "peer"), "peer accepted");
            Assert.Eq("give peer", StateFile.ReadAndDeleteCommand(d), "cmd peer");
            Assert.Eq(0, Give(d, 1001, "code", "18"), "code accepted");
            Assert.Eq("give code 18", StateFile.ReadAndDeleteCommand(d), "cmd code");
            Assert.Eq(0, Give(d, 1001, "self"), "self accepted");
            Assert.Eq("give self", StateFile.ReadAndDeleteCommand(d), "cmd self");
            Directory.Delete(d, true);
        }

        // 旧写法照常接受：mac → peer，windows → self，other → 设备列表里的第一个设备
        public static void Test_Request_LegacyTargets()
        {
            string d = TempDir();
            StateFile.WriteState(d, true, 1000);
            Assert.Eq(0, Give(d, 1001, "mac"), "mac accepted");
            Assert.Eq("give peer", StateFile.ReadAndDeleteCommand(d), "mac -> peer");
            Assert.Eq(0, Give(d, 1001, "windows"), "windows accepted");
            Assert.Eq("give self", StateFile.ReadAndDeleteCommand(d), "windows -> self");
            Assert.Eq(0, Give(d, 1001, "other"), "other accepted");
            Assert.Eq("give code 17", StateFile.ReadAndDeleteCommand(d), "other -> first device");
            Directory.Delete(d, true);
        }

        public static void Test_Request_UnknownDevice_Returns1()
        {
            string d = TempDir();
            StateFile.WriteState(d, true, 1000);
            Assert.Eq(1, Give(d, 1001, "code", "19"), "code not in the device list");
            Assert.Eq(1, Cli.RequestGive(d, new Config(), new string[] { "other" }, 1001), "legacy other with no devices");
            Assert.Eq(false, File.Exists(Path.Combine(d, "cmd.txt")), "no cmd written");
            Assert.True(File.ReadAllText(Path.Combine(d, "screenswitch.log")).Contains("unknown device"), "answer logged");
            Directory.Delete(d, true);
        }

        public static void Test_Request_BadTarget_Returns1()
        {
            string d = TempDir();
            StateFile.WriteState(d, true, 1000);
            Assert.Eq(1, Give(d, 1001, "tv"), "bad target");
            Assert.Eq(1, Give(d, 1001), "no target");
            Assert.Eq(1, Give(d, 1001, "code"), "code without a number");
            Assert.Eq(1, Give(d, 1001, "code", "0"), "0 out of range");
            Assert.Eq(1, Give(d, 1001, "code", "256"), "256 out of range");
            Assert.Eq(1, Give(d, 1001, "code", "0x11"), "hex not accepted");
            Assert.Eq(1, Give(d, 1001, "code", "+17"), "sign not accepted");
            Assert.Eq(1, Give(d, 1001, "code", "17", "x"), "extra argument");
            Assert.Eq(1, Give(d, 1001, "peer", "x"), "extra argument after peer");
            Assert.Eq(false, File.Exists(Path.Combine(d, "cmd.txt")), "no cmd written");
            Directory.Delete(d, true);
        }

        public static void Test_ParseCode()
        {
            uint v;
            Assert.True(Cli.ParseCode("1", out v) && v == 1, "1");
            Assert.True(Cli.ParseCode("255", out v) && v == 255, "255");
            Assert.True(Cli.ParseCode("017", out v) && v == 17, "leading zero");
            foreach (string bad in new string[] { "", "0", "256", "1000", "-1", "+1", " 1", "1 ", "0x11", "1a", "１７", null })
                Assert.True(!Cli.ParseCode(bad, out v), "rejected: <" + bad + ">");
        }

        public static void Test_RequestTake_NoOrStaleState_Returns3()
        {
            string d = TempDir();
            Assert.Eq(3, Cli.RequestTake(d, 1000), "no state");
            StateFile.WriteState(d, false, 1000);
            Assert.Eq(3, Cli.RequestTake(d, 1011), "stale by 11s");
            Assert.Eq(false, File.Exists(Path.Combine(d, "cmd.txt")), "no cmd written when stale");
            Directory.Delete(d, true);
        }

        public static void Test_RequestTake_Owned_Returns0_NoCommand()
        {
            string d = TempDir();
            StateFile.WriteState(d, true, 1000);
            Assert.Eq(0, Cli.RequestTake(d, 1010), "already owned, 10s is still fresh");
            Assert.Eq(false, File.Exists(Path.Combine(d, "cmd.txt")), "no cmd written");
            Directory.Delete(d, true);
        }

        public static void Test_RequestTake_NotOwned_WritesPreTake()
        {
            string d = TempDir();
            StateFile.WriteState(d, false, 1000);
            Assert.Eq(0, Cli.RequestTake(d, 1001), "accepted");
            Assert.Eq("pre-take", StateFile.ReadAndDeleteCommand(d), "cmd");
            Directory.Delete(d, true);
        }

        // 对端的 request take 走 ssh-gate，托盘程序看不到：回答要记进日志，否则事后只能推断
        public static void Test_RequestTake_LogsAnswer()
        {
            string d = TempDir();
            string log = Path.Combine(d, "screenswitch.log");
            Cli.RequestTake(d, 1000);
            StateFile.WriteState(d, true, 1000);
            Cli.RequestTake(d, 1001);
            StateFile.WriteState(d, false, 1000);
            Cli.RequestTake(d, 1001);
            string text = File.ReadAllText(log);
            Assert.True(text.Contains("request take: daemon not running"), "logs 3");
            Assert.True(text.Contains("request take: already owned"), "logs already owned");
            Assert.True(text.Contains("request take: accepted, pre-take written"), "logs accepted");
            Directory.Delete(d, true);
        }

        public static void Test_RequestGive_LogsAnswer()
        {
            string d = TempDir();
            string log = Path.Combine(d, "screenswitch.log");
            Give(d, 1000, "peer");
            StateFile.WriteState(d, false, 1000);
            Give(d, 1001, "code", "17");
            StateFile.WriteState(d, true, 1000);
            Give(d, 1001, "peer");
            string text = File.ReadAllText(log);
            Assert.True(text.Contains("request give peer: daemon not running"), "logs 3");
            Assert.True(text.Contains("request give code 17: not owned"), "logs 2");
            Assert.True(text.Contains("request give peer: accepted"), "logs accepted");
            Directory.Delete(d, true);
        }

        public static void Test_Run_ParsesRequestTakeArgs()
        {
            string d = TempDir();
            StateFile.WriteState(d, false, StateFile.Now());
            Assert.Eq(0, Cli.Run(new string[] { "request", "take" }, d), "run request take");
            Assert.Eq("pre-take", StateFile.ReadAndDeleteCommand(d), "cmd");
            Assert.Eq(1, Cli.Run(new string[] { "request", "take", "x" }, d), "extra arg");
            Assert.Eq(1, Cli.Run(new string[] { "request", "take", "mac" }, d), "extra target");
            Assert.Eq(1, Cli.Run(new string[] { "request", "takes" }, d), "misspelled");
            Assert.Eq(false, File.Exists(Path.Combine(d, "cmd.txt")), "no cmd written for bad args");
            Directory.Delete(d, true);
        }

        // Run 从 dir 下的 config.json 取设备列表
        public static void Test_Run_ParsesRequestArgs()
        {
            string d = TempDir();
            TestCfg.Legacy().Save(Path.Combine(d, "config.json"));
            StateFile.WriteState(d, true, StateFile.Now());
            Assert.Eq(0, Cli.Run(new string[] { "request", "give", "windows" }, d), "legacy local take");
            Assert.Eq("give self", StateFile.ReadAndDeleteCommand(d), "cmd self");
            Assert.Eq(0, Cli.Run(new string[] { "request", "give", "peer" }, d), "give peer");
            Assert.Eq("give peer", StateFile.ReadAndDeleteCommand(d), "cmd peer");
            Assert.Eq(0, Cli.Run(new string[] { "request", "give", "code", "18" }, d), "give code");
            Assert.Eq("give code 18", StateFile.ReadAndDeleteCommand(d), "cmd code");
            Assert.Eq(1, Cli.Run(new string[] { "request", "give", "code", "19" }, d), "unknown device");
            Assert.Eq(1, Cli.Run(new string[] { "request", "give" }, d), "missing target");
            Assert.Eq(1, Cli.Run(new string[] { "request" }, d), "missing args");
            Assert.Eq(1, Cli.Run(new string[] { "bogus" }, d), "unknown");
            Directory.Delete(d, true);
        }

        // request info：守护进程没在运行也回答，退出码 0
        public static void Test_Run_RequestInfo_AnswersWithoutDaemon()
        {
            string d = TempDir();
            Assert.Eq(0, Cli.Run(new string[] { "request", "info" }, d), "info without config or state");
            Assert.Eq(1, Cli.Run(new string[] { "request", "info", "x" }, d), "extra arg");
            Directory.Delete(d, true);
        }
    }
}
