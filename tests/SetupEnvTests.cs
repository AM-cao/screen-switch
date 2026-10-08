using System;
using System.Collections.Generic;
using System.IO;

namespace ScreenSwitch.Tests
{
    public static class SetupEnvTests
    {
        // 探测换成计数的假实现：不碰显示器
        class CountingEnv : SetupEnv
        {
            public int Probes;
            public CountingEnv(string dir) : base(dir) { }
            protected override List<SetupMonitor> Probe(out int connected)
            {
                Probes++;
                connected = Probes;
                var l = new List<SetupMonitor>();
                for (int i = 0; i < Probes; i++) l.Add(new SetupMonitor("\\\\?\\DISPLAY#AAA000" + i + "#5&1&0&UID" + i + "#{x}", "", true, 0x10));
                return l;
            }
        }

        // "重新检测"要真的重新探测：每次 Monitors() 都探测；ConnectedCount() 用最近一次的结果，不再探测
        public static void Test_Monitors_ProbesEveryCall_CountFromLatest()
        {
            string d = Path.Combine(Path.GetTempPath(), "ss-env-" + Guid.NewGuid());
            Directory.CreateDirectory(d);
            var env = new CountingEnv(d);
            Assert.Eq(1, env.ConnectedCount(), "first count probes once");
            Assert.Eq(1, env.Probes, "one probe");
            Assert.Eq(2, env.Monitors().Count, "second call probes again");
            Assert.Eq(2, env.ConnectedCount(), "count from the latest probe");
            Assert.Eq(2, env.Probes, "count did not probe again");
            Assert.Eq(3, env.Monitors().Count, "third probe");
            Assert.Eq(3, env.Probes, "every call probes");
            Directory.Delete(d, true);
        }

        // 已有私钥没有公钥：只从私钥导出公钥（-y），绝不在已有私钥上重新生成
        public static void Test_KeygenArgs_NeverOverwritesPrivateKey()
        {
            string a = SetupEnv.KeygenArgs("C:\\u\\.ssh\\id_ed25519", true, "PC");
            Assert.Eq("-y -f \"C:\\u\\.ssh\\id_ed25519\"", a, "derive the public key only");
            a = SetupEnv.KeygenArgs("C:\\u\\.ssh\\id_ed25519", false, "PC");
            Assert.True(a.StartsWith("-t ed25519 -N \"\" -f \"C:\\u\\.ssh\\id_ed25519\""), "generate when there is no private key: " + a);
            Assert.Eq(null, SetupEnv.PubLine("Enter passphrase:\r\n"), "no key in the output");
            Assert.Eq(null, SetupEnv.PubLine(""), "empty output");
            Assert.Eq("ssh-ed25519 AAAAC3Nza", SetupEnv.PubLine("\r\nssh-ed25519 AAAAC3Nza\r\n"), "the key line");
        }
    }
}
