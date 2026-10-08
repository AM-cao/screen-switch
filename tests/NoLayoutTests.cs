using System;
using System.IO;

namespace ScreenSwitch.Tests
{
    // 只有主屏（layout: none，设计文档 7.2）：不操作硬件的 ILayout，拿回、让出只改归属状态。
    public static class NoLayoutTests
    {
        const uint SELF = 0x10, PEER = 0x0F;

        class Rig
        {
            public string Dir;
            public FakeMonitor Mon = new FakeMonitor();
            public PortDdc Ddc;
            public ILayout Layout;
            public FakeRelay Relay = new FakeRelay();
            public FakeNotifier N = new FakeNotifier();
            public FakeClock Clock = new FakeClock();
            public Config Cfg = new Config();
            public Orchestrator O;
            public string LogText() { return File.ReadAllText(Path.Combine(Dir, "t.log")); }
        }

        static Rig Make(uint input)
        {
            var r = new Rig();
            r.Dir = Path.Combine(Path.GetTempPath(), "ss-" + Guid.NewGuid());
            Directory.CreateDirectory(r.Dir);
            r.Mon.Input = input;
            r.Ddc = new PortDdc(r.Mon, SELF);
            r.Cfg.Layout = "none";
            r.Cfg.SelfCode = SELF; r.Cfg.PeerName = "MacBook"; r.Cfg.PeerCode = PEER;
            r.Cfg.PeerHost = "mac.local"; r.Cfg.PeerUser = "u";
            return r;
        }

        static void Start(Rig r)
        {
            var log = new Log(Path.Combine(r.Dir, "t.log"));
            // 与托盘程序一样经 Layouts.Create 选实现；快照路径指向测试目录，用来核对没有人去碰它
            r.Layout = Layouts.Create(r.Cfg, Path.Combine(r.Dir, "layout-owned.bin"), log);
            r.O = new Orchestrator(r.Ddc, r.Layout, r.Relay, r.N, r.Clock, r.Cfg, log, r.Dir);
        }

        static void AssertNoLayoutTrouble(Rig r, string when)
        {
            foreach (string t in r.N.Titles)
                Assert.True(!t.Contains("布局") && !t.Contains("副屏"), when + ": no layout notice, got <" + t + ">");
            Assert.True(!r.LogText().Contains("[ERROR]"), when + ": no errors logged: " + r.LogText());
            Assert.Eq(false, File.Exists(Path.Combine(r.Dir, "layout-owned.bin")), when + ": no snapshot file");
        }

        public static void Test_Create_PicksImplementationByConfig()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ss-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            var log = new Log(Path.Combine(dir, "t.log"));
            var cfg = new Config();
            Assert.True(Layouts.Create(cfg, Path.Combine(dir, "s.bin"), log) is CcdLayout, "layout second: CCD");
            cfg.Layout = "none";
            Assert.True(Layouts.Create(cfg, Path.Combine(dir, "s.bin"), log) is NoLayout, "layout none: no hardware");
            Directory.Delete(dir, true);
        }

        // Mode 在拿回后报告扩展、让出后报告克隆；HasSnapshot 恒为真
        public static void Test_NoLayout_ReportsLastOperation()
        {
            var l = new NoLayout();
            Assert.Eq(true, l.HasSnapshot, "always has a snapshot");
            l.Release();
            Assert.Eq(LayoutMode.Clone, l.Mode, "after release");
            l.Take(OwnedLayout.Extend);
            Assert.Eq(LayoutMode.Extend, l.Mode, "after take");
            l.Release(); l.Take(OwnedLayout.MainOnly);
            Assert.Eq(LayoutMode.Extend, l.Mode, "main-only means nothing with a single screen");
            l.SaveSnapshot();
            Assert.Eq(null, l.CurrentSignature(), "no arrangement to remember");
            Assert.Eq(null, l.SnapshotSignature(), "no snapshot signature");
        }

        public static void Test_StartupReconcile_BothWays()
        {
            Rig r = Make(PEER); Start(r);
            Assert.Eq(false, r.O.Owned, "not owned");
            Assert.Eq(LayoutMode.Clone, r.Layout.Mode, "reconciled to released");
            AssertNoLayoutTrouble(r, "startup not owned");
            Directory.Delete(r.Dir, true);

            r = Make(SELF); Start(r);
            Assert.Eq(true, r.O.Owned, "owned");
            Assert.Eq(LayoutMode.Extend, r.Layout.Mode, "taken");
            AssertNoLayoutTrouble(r, "startup owned");
            Directory.Delete(r.Dir, true);
        }

        // 按显示器按钮切走、切回：轮询跟随，只改归属
        public static void Test_Poll_FollowsReleaseAndTake()
        {
            Rig r = Make(SELF); Start(r);
            int fired = 0; r.O.OwnedChanged += delegate { fired++; };
            r.Mon.Input = PEER;
            for (int i = 0; i < 8; i++) r.O.Tick();
            Assert.Eq(false, r.O.Owned, "released");
            r.Mon.Input = SELF;
            r.O.Tick(); r.O.Tick();
            Assert.Eq(true, r.O.Owned, "taken");
            Assert.Eq(2, fired, "menu refreshed twice");
            r.Clock.T += 60;
            for (int i = 0; i < 60; i++) r.O.Tick(); // 自动记住排列、自愈的节拍都走到
            AssertNoLayoutTrouble(r, "poll");
            Directory.Delete(r.Dir, true);
        }

        public static void Test_GivePeer_ThenTakeBack()
        {
            Rig r = Make(SELF); Start(r);
            r.O.GiveToPeer();
            Assert.Eq(PEER, r.Mon.Input, "switched to the peer");
            Assert.Eq(false, r.O.Owned, "given away");
            Assert.Eq(1, r.Relay.TakeRequests, "peer told to pre-take");
            r.O.TakeBack();
            Assert.Eq(true, r.O.Owned, "pre-taken after the peer accepted");
            Assert.Eq("已请求 MacBook 交还主屏", r.N.Titles[0], "notice");
            Assert.Eq("", r.N.Texts[0], "nothing to say about a layout");
            AssertNoLayoutTrouble(r, "give and take back");
            Directory.Delete(r.Dir, true);
        }

        // 预先拿回（对方的 request take）：只置状态和宽限期
        public static void Test_PreTake_SetsOwnedWithGrace()
        {
            Rig r = Make(PEER); Start(r);
            StateFile.WriteCommand(r.Dir, "pre-take");
            r.O.Tick();
            Assert.Eq(true, r.O.Owned, "owned right away");
            for (int i = 0; i < 12; i++) r.O.Tick();
            Assert.Eq(true, r.O.Owned, "failing reads ignored inside the grace period");
            r.Clock.T += 9;
            for (int i = 0; i < 8; i++) r.O.Tick();
            Assert.Eq(false, r.O.Owned, "given up after the grace period");
            AssertNoLayoutTrouble(r, "pre-take");
            Directory.Delete(r.Dir, true);
        }

        // 偏好里留着"仅主屏"（以前接过副屏）：只有主屏时不起作用，也不出现"仅主屏"的说法
        public static void Test_MainOnlyPreference_Ignored()
        {
            Rig r = Make(PEER);
            File.WriteAllText(Path.Combine(r.Dir, "prefs.json"), "{\"ownedLayout\":\"mainOnly\",\"autoSaveLayout\":true}");
            Start(r);
            r.O.TakeBack();
            Assert.Eq("", r.N.Texts[0], "no layout wording");
            r.O.PrepareExit();
            AssertNoLayoutTrouble(r, "main-only preference");
            Directory.Delete(r.Dir, true);
        }
    }
}
