using System.Collections.Generic;
using System.IO;

namespace ScreenSwitch.Tests
{
    public static class RelayTests
    {
        static Config Cfg()
        {
            var c = new Config();
            c.PeerHost = "mac.local"; c.PeerUser = "bob"; c.PeerTimeoutSec = 3; c.PeerKeyPath = "";
            return c;
        }

        // 假的 ssh：记下每次的参数，按顺序给出退出码和标准输出
        class FakeSsh
        {
            public List<string> Args = new List<string>();
            public List<int> Timeouts = new List<int>();
            public Queue<int> Codes = new Queue<int>();
            public string Stdout = "";
            public int Run(string args, int timeoutMs, out string stdout, out string stderr)
            {
                Args.Add(args); Timeouts.Add(timeoutMs);
                stdout = Stdout; stderr = "";
                return Codes.Count > 0 ? Codes.Dequeue() : 0;
            }
        }

        static SshRelay Make(Config c, FakeSsh ssh, out string dir)
        {
            dir = Path.Combine(Path.GetTempPath(), "ss-relay-" + System.Guid.NewGuid());
            Directory.CreateDirectory(dir);
            c.PeerHost = "127.0.0.1"; // IPv4 字面量：不做名字解析
            return new SshRelay(c, new Log(Path.Combine(dir, "r.log")), dir, ssh.Run);
        }

        // 远端命令是 <peer.program> request …；没配 program 时按对方的系统推导
        public static void Test_BuildArgs_Basic()
        {
            var c = Cfg();
            Assert.Eq("-o BatchMode=yes -o ConnectTimeout=3 bob@mac.local \"~/screenctl/screenctl.sh request give peer\"",
                SshRelay.BuildArgs(c, "give peer"), "mac peer, default program");
            c.PeerOs = "windows";
            Assert.Eq("-o BatchMode=yes -o ConnectTimeout=3 bob@mac.local \"C:\\Users\\bob\\ScreenSwitch\\ScreenSwitch.exe request give code 17\"",
                SshRelay.BuildArgs(c, "give code 17"), "windows peer, default program");
            c.PeerProgram = "/opt/x/s.sh";
            Assert.Eq("-o BatchMode=yes -o ConnectTimeout=3 bob@mac.local \"/opt/x/s.sh request info\"",
                SshRelay.BuildArgs(c, "info"), "configured program");
        }

        public static void Test_BuildArgs_WithKey()
        {
            var c = new Config();
            c.PeerHost = "h"; c.PeerUser = "u"; c.PeerTimeoutSec = 2; c.PeerKeyPath = "C:\\keys\\id";
            c.PeerProgram = "cmd";
            string a = SshRelay.BuildArgs(c, "give peer");
            Assert.Eq("-o BatchMode=yes -o ConnectTimeout=2 -i \"C:\\keys\\id\" u@h \"cmd request give peer\"", a, "args with key");
        }

        public static void Test_BuildArgs_WithIp()
        {
            var c = Cfg();
            string a = SshRelay.BuildArgs(c, "give peer", "192.168.0.10");
            Assert.Eq("-o BatchMode=yes -o ConnectTimeout=3 -o HostName=192.168.0.10 -o HostKeyAlias=mac.local bob@mac.local \"~/screenctl/screenctl.sh request give peer\"", a, "args with ip");
        }

        public static void Test_BuildArgs_WithIpAndKey()
        {
            var c = new Config();
            c.PeerHost = "h"; c.PeerUser = "u"; c.PeerTimeoutSec = 2; c.PeerKeyPath = "C:\\keys\\id";
            c.PeerProgram = "cmd";
            string a = SshRelay.BuildArgs(c, "take", "10.0.0.5");
            Assert.Eq("-o BatchMode=yes -o ConnectTimeout=2 -o HostName=10.0.0.5 -o HostKeyAlias=h -i \"C:\\keys\\id\" u@h \"cmd request take\"", a, "args with ip and key");
        }

        public static void Test_BuildArgs_NullIp_MatchesTwoArgOverload()
        {
            var c = Cfg();
            Assert.Eq(SshRelay.BuildArgs(c, "give peer"), SshRelay.BuildArgs(c, "give peer", null), "3-arg overload with null ip matches 2-arg overload");
        }

        public static void Test_RunProcess_ReturnsExitCode()
        {
            Assert.Eq(2, SshRelay.RunProcess("cmd.exe", "/c exit 2", 5000), "exit 2");
            Assert.Eq(0, SshRelay.RunProcess("cmd.exe", "/c exit 0", 5000), "exit 0");
        }

        public static void Test_RunProcess_CapturesStderr()
        {
            string err;
            int rc = SshRelay.RunProcess("cmd.exe", "/c echo boom 1>&2 & exit 3", 5000, out err);
            Assert.Eq(3, rc, "exit 3");
            Assert.True(err != null && err.Contains("boom"), "stderr captured: <" + err + ">");
        }

        public static void Test_RunProcess_CapturesStdout()
        {
            string o, err;
            int rc = SshRelay.RunProcess("cmd.exe", "/c echo proto=3 os=mac", 5000, out o, out err);
            Assert.Eq(0, rc, "exit 0");
            Assert.True(o.Contains("proto=3 os=mac"), "stdout captured: <" + o + ">");
        }

        public static void Test_RunProcess_TimeoutReturns255()
        {
            Assert.Eq(255, SshRelay.RunProcess("cmd.exe", "/c ping -n 30 127.0.0.1 >nul", 1000), "timeout");
        }

        public static void Test_RunProcess_MissingExeReturns255()
        {
            Assert.Eq(255, SshRelay.RunProcess("no-such-exe-12345.exe", "", 1000), "missing exe");
        }

        public static void Test_BuildArgs_EscapesQuotes()
        {
            var c = new Config();
            c.PeerHost = "h"; c.PeerUser = "u"; c.PeerTimeoutSec = 1; c.PeerKeyPath = "C:\\k\"x";
            c.PeerProgram = "say \"x\"";
            string a = SshRelay.BuildArgs(c, "give peer");
            Assert.Eq("-o BatchMode=yes -o ConnectTimeout=1 -i \"C:\\k\\\"x\" u@h \"say \\\"x\\\" request give peer\"", a, "escaped quotes");
        }

        // 调用方最多等 ConnectTimeout + 2 秒（设计文档 5.2）：解析对方地址用掉的时间从 ssh 的超时里扣掉，至少留 1 秒。
        public static void Test_TakeSshTimeout_TotalBudget()
        {
            var c = new Config(); c.PeerTimeoutSec = 3;
            Assert.Eq(5000, SshRelay.TakeSshTimeoutMs(c, 0), "no resolve time");
            Assert.Eq(4900, SshRelay.TakeSshTimeoutMs(c, 100), "typical mDNS");
            Assert.Eq(2000, SshRelay.TakeSshTimeoutMs(c, 3000), "resolve timed out, last known address");
            c.PeerTimeoutSec = 1;
            Assert.Eq(1000, SshRelay.TakeSshTimeoutMs(c, 3000), "at least 1 s for ssh");
        }

        public static void Test_RequestTake_SshMissing_Returns255()
        {
            var c = new Config();
            c.SshExe = "no-such-exe.exe"; c.PeerHost = "127.0.0.1";
            string dir = Path.Combine(Path.GetTempPath(), "ss-relay-" + System.Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                int rc = new SshRelay(c, new Log(Path.Combine(dir, "r.log")), dir).RequestTake();
                Assert.Eq(255, rc, "ssh missing");
                Assert.True(File.ReadAllText(Path.Combine(dir, "r.log")).Contains("request take\" -> 255"), "logged with rc");
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void Test_RequestGivePeer_SendsGivePeer()
        {
            var ssh = new FakeSsh(); string dir;
            var relay = Make(Cfg(), ssh, out dir);
            try
            {
                ssh.Codes.Enqueue(2);
                Assert.Eq(2, relay.RequestGivePeer(), "peer's answer passed through");
                Assert.Eq(1, ssh.Args.Count, "one ssh call");
                Assert.True(ssh.Args[0].EndsWith("\"~/screenctl/screenctl.sh request give peer\""), "give peer: " + ssh.Args[0]);
                Assert.Eq(5000, ssh.Timeouts[0], "ConnectTimeout + 2 s");
            }
            finally { Directory.Delete(dir, true); }
        }

        // 对方是旧版本：give peer 被退出码 1 拒绝，用 give windows 重试一次
        public static void Test_RequestGivePeer_Rejected_RetriesOnceAsGiveWindows()
        {
            var ssh = new FakeSsh(); string dir;
            var relay = Make(Cfg(), ssh, out dir);
            try
            {
                ssh.Codes.Enqueue(1); ssh.Codes.Enqueue(0);
                Assert.Eq(0, relay.RequestGivePeer(), "legacy retry accepted");
                Assert.Eq(2, ssh.Args.Count, "two ssh calls");
                Assert.True(ssh.Args[1].EndsWith("\"~/screenctl/screenctl.sh request give windows\""), "retry as give windows: " + ssh.Args[1]);

                ssh.Args.Clear(); ssh.Codes.Enqueue(1); ssh.Codes.Enqueue(1);
                Assert.Eq(1, relay.RequestGivePeer(), "still rejected");
                Assert.Eq(2, ssh.Args.Count, "retried only once");

                foreach (int rc in new int[] { 2, 3, 255 })
                {
                    ssh.Args.Clear(); ssh.Codes.Enqueue(rc);
                    Assert.Eq(rc, relay.RequestGivePeer(), "no retry on " + rc);
                    Assert.Eq(1, ssh.Args.Count, "one call on " + rc);
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void Test_RequestGiveCode_SendsDecimalCode_NoLegacyRetry()
        {
            var ssh = new FakeSsh(); string dir;
            var relay = Make(Cfg(), ssh, out dir);
            try
            {
                ssh.Codes.Enqueue(1);
                Assert.Eq(1, relay.RequestGiveCode(0x11), "unknown device / old peer: 1 passed through");
                Assert.Eq(1, ssh.Args.Count, "no retry for give code");
                Assert.True(ssh.Args[0].EndsWith("\"~/screenctl/screenctl.sh request give code 17\""), "decimal code: " + ssh.Args[0]);
                Assert.Eq(1, relay.RequestGiveCode(0), "0 rejected locally");
                Assert.Eq(1, relay.RequestGiveCode(256), "256 rejected locally");
                Assert.Eq(1, ssh.Args.Count, "no ssh for an out-of-range code");
            }
            finally { Directory.Delete(dir, true); }
        }

        public static void Test_RequestInfo_ParsesAnswer()
        {
            var ssh = new FakeSsh(); string dir;
            var relay = Make(Cfg(), ssh, out dir);
            try
            {
                ssh.Stdout = "proto=3 os=mac code=15 name=TWFjQm9vaw== devices=17:U3dpdGNo,18:UFM1\r\n";
                PeerInfo info = relay.RequestInfo();
                Assert.True(ssh.Args[0].EndsWith("\"~/screenctl/screenctl.sh request info\""), "request info: " + ssh.Args[0]);
                Assert.True(info != null, "parsed");
                Assert.Eq((uint)15, info.Code, "code");
                Assert.Eq("MacBook", info.Name, "name");
                Assert.Eq(2, info.Devices.Count, "devices");
            }
            finally { Directory.Delete(dir, true); }
        }

        // 对方是旧版本（ssh-gate 回 rejected、退出码 1）或连不上：取不到
        public static void Test_RequestInfo_RejectedOrUnreachable_ReturnsNull()
        {
            var ssh = new FakeSsh(); string dir;
            var relay = Make(Cfg(), ssh, out dir);
            try
            {
                ssh.Stdout = "rejected\r\n"; ssh.Codes.Enqueue(1);
                Assert.Eq(null, relay.RequestInfo(), "old peer");
                ssh.Stdout = ""; ssh.Codes.Enqueue(255);
                Assert.Eq(null, relay.RequestInfo(), "unreachable");
                ssh.Stdout = "hello\r\n"; ssh.Codes.Enqueue(0);
                Assert.Eq(null, relay.RequestInfo(), "exit 0 but no info line");
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
