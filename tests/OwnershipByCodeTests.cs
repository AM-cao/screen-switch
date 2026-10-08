using System;
using System.IO;

namespace ScreenSwitch.Tests
{
    // 设计文档 6.1：读 VCP 0x60 成功且低 8 位等于本机编码才算主屏在本机。
    // 这里的假显示器在所有输入口上都应答（读总是成功，值是当前输入口）。
    public static class OwnershipByCodeTests
    {
        static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "ss-" + Guid.NewGuid());
            Directory.CreateDirectory(d);
            return d;
        }

        static Config Cfg()
        {
            var c = TestCfg.Legacy();
            c.SelfCode = 0x10;
            return c;
        }

        static Orchestrator Make(FakeDdc ddc, FakeLayout layout, FakeNotifier n, FakeClock clock, string dir, Config cfg)
        {
            return new Orchestrator(ddc, layout, new FakeRelay(), n, clock, cfg, new Log(Path.Combine(dir, "t.log")), dir);
        }

        public static void Test_Startup_OwnCode_Owned()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Value = 0x10;
            var o = Make(ddc, new FakeLayout(), new FakeNotifier(), new FakeClock(), d, Cfg());
            Assert.Eq(true, o.Owned, "own code read: owned");
            Directory.Delete(d, true);
        }

        public static void Test_Startup_OtherCode_NotOwned()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Value = 0x0F;
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone;
            var o = Make(ddc, layout, new FakeNotifier(), new FakeClock(), d, Cfg());
            Assert.Eq(false, o.Owned, "read succeeds but shows the peer's code: not owned");
            for (int i = 0; i < 8; i++) o.Tick();
            Assert.Eq(false, o.Owned, "successful reads of another code never take");
            Assert.Eq(0, layout.Calls.Count, "no layout change");
            Directory.Delete(d, true);
        }

        public static void Test_OnlyLow8BitsCompared()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Value = 0x0F10;
            var o = Make(ddc, new FakeLayout(), new FakeNotifier(), new FakeClock(), d, Cfg());
            Assert.Eq(true, o.Owned, "0x0F10 & 0xFF == 0x10");
            Directory.Delete(d, true);
        }

        public static void Test_Poll_SwitchedAwayByButton_StillAnswering_Releases()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Value = 0x10;
            var layout = new FakeLayout();
            var o = Make(ddc, layout, new FakeNotifier(), new FakeClock(), d, Cfg());
            Assert.Eq(true, o.Owned, "owned");
            ddc.Value = 0x11; // 用显示器按钮切到了 HDMI，显示器照样应答
            for (int i = 0; i < 8; i++) o.Tick();
            Assert.Eq(false, o.Owned, "released although reads still succeed");
            Assert.Eq("release", string.Join(",", layout.Calls.ToArray()), "layout released");
            ddc.Value = 0x10; // 切回来
            o.Tick(); o.Tick();
            Assert.Eq(true, o.Owned, "taken back when the own code shows again");
            Directory.Delete(d, true);
        }

        // 切给对方：写入后显示器仍然应答，但读到的是对方的编码——算已经切走（原来要等读取失败）
        public static void Test_GivePeer_MonitorKeepsAnswering_CountsAsSwitched()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Value = 0x10;
            var layout = new FakeLayout();
            var n = new FakeNotifier();
            var o = Make(ddc, layout, n, new FakeClock(), d, Cfg());
            ddc.OnWrite = delegate { ddc.Value = ddc.Writes[ddc.Writes.Count - 1]; };
            o.GiveToPeer();
            Assert.Eq((uint)0x0F, ddc.Writes[0], "peer code written");
            Assert.Eq(false, o.Owned, "given away");
            Assert.Eq("release", string.Join(",", layout.Calls.ToArray()), "released");
            Assert.Eq(0, n.Titles.Count, "no 'not effective' notice");
            Directory.Delete(d, true);
        }

        public static void Test_SelfCodeUnknown_AnySuccessfulReadOwns_AndLogsHint()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Value = 0x0F;
            var cfg = Cfg(); cfg.SelfCode = 0;
            var o = Make(ddc, new FakeLayout(), new FakeNotifier(), new FakeClock(), d, cfg);
            Assert.Eq(true, o.Owned, "unknown own code: a successful read owns (old behaviour)");
            Assert.True(File.ReadAllText(Path.Combine(d, "t.log")).Contains("self.code"), "log says to run setup");
            Directory.Delete(d, true);
        }
    }
}
