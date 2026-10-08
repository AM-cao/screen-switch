using System;
using System.Collections.Generic;
using System.IO;

namespace ScreenSwitch.Tests
{
    class FakeDdc : IDdc
    {
        public Queue<bool> Reads = new Queue<bool>();
        public bool DefaultRead = true;
        // 读取成功时返回的输入口编码
        public uint Value = 0x10;
        public List<uint> Writes = new List<uint>();
        public bool WriteResult = true;
        public int ReadCount = 0;
        // 写 DDC 时回调，测试用来把写入插进布局调用序列里核对先后
        public Action OnWrite;
        // 读 DDC 时回调（参数为读取结果），用途同上
        public Action<bool> OnRead;
        public bool TryReadInput(out uint value)
        {
            value = Value; ReadCount++;
            bool ok = Reads.Count > 0 ? Reads.Dequeue() : DefaultRead;
            if (OnRead != null) OnRead(ok);
            return ok;
        }
        public bool WriteInput(uint value) { Writes.Add(value); if (OnWrite != null) OnWrite(); return WriteResult; }
        public string Describe() { return "fake"; }
        public void ReadsThen(int count, bool v) { for (int i = 0; i < count; i++) Reads.Enqueue(v); }
    }
    // 有状态的假布局：拿回/让出会改变 Mode，签名随 Mode 与快照变化。
    class FakeLayout : ILayout
    {
        public List<string> Calls = new List<string>();
        public bool Snapshot = true;
        public LayoutMode ModeValue = LayoutMode.Extend;
        public bool ThrowOnTake = false, ThrowOnTakeMain = false, ThrowOnRelease = false, ThrowOnSave = false;
        public string CurSig = "A", SnapSig = "A";
        // Mode 读到这个次数用完之前先回读 Missing（模拟显示器重新同步时的短暂读数），之后回落到 ModeValue。
        public int MissingModeReads = 0;
        // Take() 被调用时回调（在 Calls.Add 之后、抛异常/改 ModeValue 之前），测试用来模拟耗时较久的拿回
        // 并在期间核对状态文件。
        public Action OnTake;
        // Release() 被调用时回调（在 Calls.Add 之后、抛异常/改 ModeValue 之前），测试用来在 Release() 刚
        // 返回后的那次 Mode 读数上安排一次 Missing（不影响调用之前的任何 Mode 读，比如构造时的对账）。
        public Action OnRelease;
        // Mode 被读了多少次（测试用来核对"入口处只读一次"）
        public int ModeReadCount = 0;
        public bool HasSnapshot { get { return Snapshot; } }
        public LayoutMode Mode
        {
            get
            {
                ModeReadCount++;
                if (MissingModeReads > 0) { MissingModeReads--; return LayoutMode.Missing; }
                return ModeValue;
            }
        }
        public void SaveSnapshot()
        {
            Calls.Add("save");
            if (ThrowOnSave) throw new LayoutException("boom save");
            SnapSig = CurSig;
        }
        public void Take(OwnedLayout target)
        {
            if (target == OwnedLayout.MainOnly)
            {
                Calls.Add("take-main");
                if (OnTake != null) OnTake();
                if (ThrowOnTakeMain) throw new LayoutException("boom main");
                ModeValue = LayoutMode.MainOnly;
                return;
            }
            Calls.Add("take");
            if (OnTake != null) OnTake();
            if (ThrowOnTake) throw new LayoutException("boom");
            ModeValue = LayoutMode.Extend;
        }
        public void Release()
        {
            Calls.Add("release");
            if (OnRelease != null) OnRelease();
            if (ThrowOnRelease) throw new LayoutException("boom release");
            ModeValue = LayoutMode.Clone;
        }
        public string CurrentSignature() { return ModeValue == LayoutMode.Extend ? CurSig : null; }
        public string SnapshotSignature() { return Snapshot ? SnapSig : null; }
    }
    class FakeRelay : IRelay
    {
        public int Result = 0;
        public List<string> Requests = new List<string>();
        // 发出请求时回调，测试用来检查此刻的状态文件
        public Action OnRequest;
        public int RequestGivePeer() { Requests.Add("peer"); if (OnRequest != null) OnRequest(); return Result; }
        public int RequestGiveCode(uint code) { Requests.Add("code " + code); if (OnRequest != null) OnRequest(); return Result; }
        // request info：回答与次数
        public PeerInfo Info = null;
        public int InfoRequests = 0;
        public PeerInfo RequestInfo() { InfoRequests++; return Info; }
        // request take（设计文档 5.2）：次数、返回值、调用时回调、是否抛异常
        public int TakeRequests = 0;
        public int TakeResult = 0;
        public Action OnRequestTake;
        public bool ThrowOnTake = false;
        public int RequestTake()
        {
            TakeRequests++;
            if (OnRequestTake != null) OnRequestTake();
            if (ThrowOnTake) throw new InvalidOperationException("boom relay");
            return TakeResult;
        }
    }
    class FakeNotifier : INotifier
    {
        public List<string> Titles = new List<string>();
        public List<string> Texts = new List<string>();
        public void Notify(string title, string text) { Titles.Add(title); Texts.Add(text); }
    }
    class FakeClock : IClock
    {
        public int Slept = 0;
        public long T = 1000;
        public void Sleep(int ms) { Slept += ms; T += ms / 1000; }
        public long Now() { return T; }
    }

    public static class OrchestratorTests
    {
        static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "ss-" + Guid.NewGuid());
            Directory.CreateDirectory(d);
            return d;
        }
        static Orchestrator Make(FakeDdc ddc, FakeLayout layout, FakeRelay relay, FakeNotifier n, FakeClock clock, string dir)
        {
            return Make(ddc, layout, relay, n, clock, dir, TestCfg.Legacy());
        }
        static Orchestrator Make(FakeDdc ddc, FakeLayout layout, FakeRelay relay, FakeNotifier n, FakeClock clock, string dir, Config cfg)
        {
            return new Orchestrator(ddc, layout, relay, n, clock, cfg, new Log(Path.Combine(dir, "t.log")), dir);
        }

        public static void Test_InitialOwnedFromFirstRead()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var o = Make(ddc, new FakeLayout(), new FakeRelay(), new FakeNotifier(), new FakeClock(), d);
            Assert.Eq(false, o.Owned, "not owned");
            bool owned; long ts;
            Assert.True(StateFile.ReadState(d, out owned, out ts), "state written");
            Assert.Eq(false, owned, "state not owned");
            Directory.Delete(d, true);
        }

        public static void Test_Tick_PollsEveryTickWhenNotOwnedAndTakes()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout();
            layout.ModeValue = LayoutMode.Clone; // 启动时已经是让出状态，启动对齐不动作
            var o = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), new FakeClock(), d);
            int fired = 0; o.OwnedChanged += delegate { fired++; };
            ddc.DefaultRead = true;
            o.Tick();
            Assert.Eq(false, o.Owned, "first quick poll: one ok read, debounce not met");
            o.Tick();
            Assert.Eq(true, o.Owned, "second quick poll takes (0.5 s cadence while not owned)");
            Assert.Eq("take", layout.Calls[0], "layout take");
            Assert.Eq(1, fired, "event fired");
            Directory.Delete(d, true);
        }

        public static void Test_TakeBack_RelayOk_PreemptsTakeWithGrace()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone;
            var relay = new FakeRelay(); relay.Result = 0;
            var n = new FakeNotifier();
            var clock = new FakeClock();
            var o = Make(ddc, layout, relay, n, clock, d);
            ddc.DefaultRead = false; // 显示器还没切过来
            o.TakeBack();
            Assert.Eq("peer", relay.Requests[0], "relayed");
            Assert.Eq("take", layout.Calls[0], "layout restored immediately");
            Assert.Eq(true, o.Owned, "owned right away");
            Assert.Eq("已请求 Mac 交还主屏", n.Titles[0], "notice");
            for (int i = 0; i < 12; i++) o.Tick(); // 3 polls at owned cadence, all failing, inside grace
            Assert.Eq(true, o.Owned, "failures ignored during grace");
            Assert.Eq(1, layout.Calls.Count, "no release during grace");
            clock.T += 9; // grace expired, monitor never switched
            for (int i = 0; i < 8; i++) o.Tick();
            Assert.Eq(false, o.Owned, "released after grace when reads still fail");
            Assert.Eq("release", layout.Calls[1], "layout release");
            Directory.Delete(d, true);
        }

        // 对端已接受交回：owned=1 必须在 tracker.Reset(true) 之后立刻写状态文件，不能等 DoTake()
        // （可能因仅主屏的 Missing 等待而较慢）完成才写，否则中继占住 gate 期间对端可能读到超过
        // 10 秒未更新的状态文件（设计文档 5.2）。
        public static void Test_TakeBack_RelayOk_StateFreshBeforeSlowTake()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone;
            var relay = new FakeRelay(); relay.Result = 0;
            var clock = new FakeClock();
            var o = Make(ddc, layout, relay, new FakeNotifier(), clock, d);
            ddc.DefaultRead = false;
            long tsDuringTake = -1;
            bool ownedDuringTake = false;
            layout.OnTake = delegate
            {
                bool owned; long ts;
                Assert.True(StateFile.ReadState(d, out owned, out ts), "state readable during the (slow) take");
                ownedDuringTake = owned;
                tsDuringTake = ts;
                clock.T += 5; // 模拟拿回耗时较久（例如仅主屏等 Missing 消退）
            };
            o.TakeBack();
            Assert.Eq(true, ownedDuringTake, "owned=1 already published before DoTake's delay");
            Assert.Eq(1000L, tsDuringTake, "timestamp is from before the delay, not after");
            Directory.Delete(d, true);
        }

        public static void Test_Tick_ReleasesAfterTwoFails()
        {
            string d = TempDir();
            var ddc = new FakeDdc();
            var layout = new FakeLayout();
            var o = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), new FakeClock(), d);
            Assert.Eq(true, o.Owned, "owned initially");
            ddc.DefaultRead = false;
            for (int i = 0; i < 8; i++) o.Tick();
            Assert.Eq(false, o.Owned, "released");
            Assert.Eq("release", layout.Calls[0], "layout release");
            Directory.Delete(d, true);
        }

        public static void Test_Tick_ExecutesCommandFile()
        {
            string d = TempDir();
            var ddc = new FakeDdc();
            var layout = new FakeLayout();
            var o = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), new FakeClock(), d);
            ddc.ReadsThen(1, false);
            StateFile.WriteCommand(d, "give mac");
            o.Tick();
            Assert.Eq(1, ddc.Writes.Count, "wrote once");
            Assert.Eq((uint)0x0F, ddc.Writes[0], "mac code");
            Assert.Eq("release", layout.Calls[0], "released");
            Assert.Eq(false, o.Owned, "not owned");
            Assert.Eq(false, File.Exists(Path.Combine(d, "cmd.txt")), "cmd consumed");
            Directory.Delete(d, true);
        }

        public static void Test_GiveMac_NotEffective_KeepsLayout()
        {
            string d = TempDir();
            var ddc = new FakeDdc();
            var layout = new FakeLayout();
            var n = new FakeNotifier();
            var clock = new FakeClock();
            var o = Make(ddc, layout, new FakeRelay(), n, clock, d);
            ddc.DefaultRead = true;
            o.GiveToPeer();
            Assert.Eq(1, ddc.Writes.Count, "wrote");
            Assert.Eq(0, layout.Calls.Count, "no layout change");
            Assert.Eq(true, o.Owned, "still owned");
            Assert.True(clock.Slept >= 3000, "waited 3s");
            Assert.Eq("切换未生效", n.Titles[0], "notified");
            Directory.Delete(d, true);
        }

        public static void Test_GiveTo_NotOwned_UsesRelay()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var relay = new FakeRelay();
            var n = new FakeNotifier();
            var o = Make(ddc, new FakeLayout(), relay, n, new FakeClock(), d);
            relay.Result = 0;
            o.GiveToDevice(0x11);
            Assert.Eq("code 17", relay.Requests[0], "relayed");
            Assert.Eq("已请求 Mac 切给 设备 1", n.Titles[0], "ok notice");
            relay.Result = 2;
            o.GiveToDevice(0x11);
            Assert.Eq("请用显示器上的按钮切换输入源", n.Titles[1], "fail notice");
            Assert.Eq(0, ddc.Writes.Count, "no local write");
            Directory.Delete(d, true);
        }

        public static void Test_TakeBack_NotOwned_RelaysWindows()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var relay = new FakeRelay();
            var n = new FakeNotifier();
            var o = Make(ddc, new FakeLayout(), relay, n, new FakeClock(), d);
            relay.Result = 255;
            o.TakeBack();
            Assert.Eq("peer", relay.Requests[0], "relayed give peer");
            Assert.Eq("请用显示器上的按钮切换输入源", n.Titles[0], "fail notice");
            relay.Result = 0;
            o.TakeBack();
            Assert.Eq("已请求 Mac 交还主屏", n.Titles[1], "ok notice");
            Directory.Delete(d, true);
        }

        public static void Test_TakeBack_Owned_EnsuresLayout()
        {
            string d = TempDir();
            var layout = new FakeLayout();
            var n = new FakeNotifier();
            var o = Make(new FakeDdc(), layout, new FakeRelay(), n, new FakeClock(), d);
            o.TakeBack();
            Assert.Eq("take", layout.Calls[0], "ensure take");
            Assert.Eq("主屏已在本机", n.Titles[0], "notice");
            Directory.Delete(d, true);
        }

        public static void Test_Startup_ReconcilesWhenNotOwnedAndExtended()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.Snapshot = true; layout.ModeValue = LayoutMode.Extend;
            var o = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), new FakeClock(), d);
            Assert.Eq(false, o.Owned, "not owned");
            Assert.Eq("release", string.Join(",", layout.Calls.ToArray()), "startup release");
            Directory.Delete(d, true);
        }

        public static void Test_Startup_ReconcilesWhenOwnedButCloned()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(true);
            var layout = new FakeLayout(); layout.Snapshot = true; layout.ModeValue = LayoutMode.Clone;
            var o = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), new FakeClock(), d);
            Assert.Eq(true, o.Owned, "owned");
            Assert.Eq("take", string.Join(",", layout.Calls.ToArray()), "startup take");
            Directory.Delete(d, true);
        }

        public static void Test_Startup_NoActionWhenConsistent()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(true);
            var layout = new FakeLayout(); layout.Snapshot = true; layout.ModeValue = LayoutMode.Extend;
            Make(ddc, layout, new FakeRelay(), new FakeNotifier(), new FakeClock(), d);
            Assert.Eq(0, layout.Calls.Count, "no startup action");
            Directory.Delete(d, true);
        }

        public static void Test_GiveOther_KeepsStateFresh()
        {
            string d = TempDir();
            var ddc = new FakeDdc();
            var layout = new FakeLayout();
            var clock = new FakeClock();
            var o = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), clock, d);
            ddc.ReadsThen(1, false); ddc.ReadsThen(2, true);
            ddc.ReadsThen(1, false); ddc.ReadsThen(2, true);
            o.GiveToDevice(0x11);
            bool owned; long ts;
            Assert.True(StateFile.ReadState(d, out owned, out ts), "state written");
            Assert.True(Math.Abs(clock.T - ts) <= 2, "state fresh: clock=" + clock.T + " state=" + ts);
            Directory.Delete(d, true);
        }

        public static void Test_Tick_OwnedMismatch_RechecksNextTick()
        {
            string d = TempDir();
            var ddc = new FakeDdc();
            var layout = new FakeLayout();
            var cfg = TestCfg.Legacy();
            var o = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), new FakeClock(), d, cfg);
            Assert.Eq(true, o.Owned, "starts owned");
            ddc.DefaultRead = false;
            for (int i = 0; i < 3; i++) o.Tick();
            Assert.Eq(1, ddc.ReadCount, "first 3 ticks don't read");
            o.Tick();
            Assert.Eq(2, ddc.ReadCount, "4th tick reads (owned cadence)");
            Assert.Eq(true, o.Owned, "still owned after one fail");
            o.Tick();
            Assert.Eq(3, ddc.ReadCount, "5th tick rechecks");
            Assert.Eq(false, o.Owned, "released after recheck");
            Assert.Eq("release", layout.Calls[0], "layout release");
            Directory.Delete(d, true);
        }

        public static void Test_Tick_OwnedGlitch_BackToNormalCadence()
        {
            string d = TempDir();
            var ddc = new FakeDdc();
            var layout = new FakeLayout();
            var cfg = TestCfg.Legacy();
            var o = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), new FakeClock(), d, cfg);
            Assert.Eq(true, o.Owned, "starts owned");
            for (int i = 0; i < 3; i++) o.Tick();
            Assert.Eq(1, ddc.ReadCount, "first 3 ticks don't read");
            ddc.Reads.Enqueue(false);
            o.Tick();
            Assert.Eq(2, ddc.ReadCount, "4th tick reads and fails");
            Assert.Eq(true, o.Owned, "still owned after one fail");
            ddc.Reads.Enqueue(true);
            o.Tick();
            Assert.Eq(3, ddc.ReadCount, "5th tick rechecks and succeeds");
            Assert.Eq(true, o.Owned, "still owned after success");
            o.Tick();
            o.Tick();
            Assert.Eq(3, ddc.ReadCount, "6th and 7th ticks don't read (back to cadence)");
            o.Tick();
            Assert.Eq(4, ddc.ReadCount, "8th tick reads (normal cadence tick % 4 == 0)");
            Assert.Eq(true, o.Owned, "still owned");
            Directory.Delete(d, true);
        }

        public static void Test_Tick_RespectsPollMs()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); // first read ok -> starts owned
            var layout = new FakeLayout();
            var cfg = TestCfg.Legacy(); cfg.PollMs = 1000;
            var o = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), new FakeClock(), d, cfg);
            ddc.DefaultRead = true;
            int initial = ddc.ReadCount;
            for (int i = 0; i < 6; i++) o.Tick();
            Assert.Eq(initial + 3, ddc.ReadCount, "6 ticks: 3 reads at ticks 2, 4, 6 (poll divisor = 2)");
            Assert.Eq(true, o.Owned, "still owned");
            Directory.Delete(d, true);
        }

        public static void Test_LayoutFailure_NotifiesButStateUpdates()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.ThrowOnTake = true;
            var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, new FakeClock(), d);
            ddc.DefaultRead = true;
            for (int i = 0; i < 8; i++) o.Tick();
            Assert.Eq(true, o.Owned, "owned despite layout failure");
            Assert.Eq("恢复布局失败", n.Titles[0], "notified");
            Directory.Delete(d, true);
        }

        // ---------------- 拿回后的布局预设（仅主屏）----------------

        const string PREFS_MAIN_ONLY = "{\"ownedLayout\":\"mainOnly\",\"autoSaveLayout\":true}";
        const string PREFS_AUTO_OFF = "{\"ownedLayout\":\"extend\",\"autoSaveLayout\":false}";

        static void WritePrefs(string d, string json) { File.WriteAllText(Path.Combine(d, "prefs.json"), json); }
        static string Seq(FakeLayout l) { return string.Join(",", l.Calls.ToArray()); }
        static int LogLines(string d, string needle)
        {
            string p = Path.Combine(d, "t.log");
            if (!File.Exists(p)) return 0;
            int c = 0;
            foreach (string line in File.ReadAllLines(p)) if (line.Contains(needle)) c++;
            return c;
        }
        static void Ticks(Orchestrator o, int count) { for (int i = 0; i < count; i++) o.Tick(); }

        public static void Test_MainOnly_PassiveTake_UsesTakeMain()
        {
            string d = TempDir();
            WritePrefs(d, PREFS_MAIN_ONLY);
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone;
            var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, new FakeClock(), d);
            Assert.Eq(OwnedLayout.MainOnly, o.OwnedLayout, "preset loaded from prefs.json");
            ddc.DefaultRead = true;
            o.Tick(); o.Tick();
            Assert.Eq(true, o.Owned, "taken");
            Assert.Eq("take-main", Seq(layout), "main-only take");
            Assert.Eq(LayoutMode.MainOnly, layout.ModeValue, "main only");
            Assert.Eq(0, n.Titles.Count, "no notification");
            Directory.Delete(d, true);
        }

        public static void Test_MainOnly_TakeFails_FallsBackToExtend()
        {
            string d = TempDir();
            WritePrefs(d, PREFS_MAIN_ONLY);
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone; layout.ThrowOnTakeMain = true;
            var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, new FakeClock(), d);
            ddc.DefaultRead = true;
            o.Tick(); o.Tick();
            Assert.Eq(true, o.Owned, "taken");
            Assert.Eq("take-main,take", Seq(layout), "fallback to extend");
            Assert.Eq(LayoutMode.Extend, layout.ModeValue, "extended");
            Assert.Eq(1, n.Titles.Count, "one notification");
            Assert.Eq("无法关闭副屏，已按双屏扩展显示", n.Titles[0], "fallback notice");
            Assert.Eq("boom main", n.Texts[0], "body is the exception message");
            Directory.Delete(d, true);
        }

        public static void Test_MainOnly_TakeAndFallbackFail_NotifiesRestoreFailed()
        {
            string d = TempDir();
            WritePrefs(d, PREFS_MAIN_ONLY);
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone; layout.ThrowOnTakeMain = true; layout.ThrowOnTake = true;
            var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, new FakeClock(), d);
            ddc.DefaultRead = true;
            o.Tick(); o.Tick();
            Assert.Eq(true, o.Owned, "owned despite layout failure");
            Assert.Eq("take-main,take", Seq(layout), "tried both");
            Assert.Eq(1, n.Titles.Count, "one notification");
            Assert.Eq("恢复布局失败", n.Titles[0], "restore failed notice");
            Directory.Delete(d, true);
        }

        public static void Test_MainOnly_TakeBack_OwnedAndRelay_UseTakeMain()
        {
            string d = TempDir();
            WritePrefs(d, PREFS_MAIN_ONLY);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.MainOnly;
            var n = new FakeNotifier();
            var o = Make(new FakeDdc(), layout, new FakeRelay(), n, new FakeClock(), d);
            o.TakeBack();
            Assert.Eq("take-main", Seq(layout), "owned: ensure main-only");
            Assert.Eq("主屏已在本机", n.Titles[0], "owned notice");

            string d2 = TempDir();
            WritePrefs(d2, PREFS_MAIN_ONLY);
            var ddc2 = new FakeDdc(); ddc2.Reads.Enqueue(false);
            var layout2 = new FakeLayout(); layout2.ModeValue = LayoutMode.Clone;
            var n2 = new FakeNotifier();
            var o2 = Make(ddc2, layout2, new FakeRelay(), n2, new FakeClock(), d2);
            ddc2.DefaultRead = false;
            o2.TakeBack();
            Assert.Eq("take-main", Seq(layout2), "relay ok: preemptive main-only take");
            Assert.Eq("已请求 Mac 交还主屏", n2.Titles[0], "relay notice");
            Directory.Delete(d, true); Directory.Delete(d2, true);
        }

        public static void Test_MainOnly_GiveMac_RestoresSecondaryBeforeWrite()
        {
            string d = TempDir();
            WritePrefs(d, PREFS_MAIN_ONLY);
            var ddc = new FakeDdc();
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.MainOnly;
            var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, new FakeClock(), d);
            Assert.Eq(0, layout.Calls.Count, "startup: owned main-only is consistent");
            int fired = 0; o.OwnedChanged += delegate { fired++; };
            ddc.OnWrite = delegate { layout.Calls.Add("ddc"); };
            ddc.ReadsThen(1, true);  // 进入克隆后等到能读到再写
            ddc.ReadsThen(1, false); // 写后读失败：已切走
            o.GiveToPeer();
            Assert.Eq("release,ddc", Seq(layout), "clone before the DDC write; already cloned, so no second release");
            Assert.Eq(false, o.Owned, "given away");
            Assert.Eq(1, fired, "event fired");
            bool owned; long ts;
            Assert.True(StateFile.ReadState(d, out owned, out ts), "state written");
            Assert.Eq(false, owned, "state not owned");
            Assert.Eq(0, n.Titles.Count, "no notification");
            Directory.Delete(d, true);
        }

        // 让出前的 Release() 之后，Mode 短暂读到 Missing（显示器重新同步）：不应取消切换。
        public static void Test_MainOnly_GiveMac_MissingBlipAfterRelease_Proceeds()
        {
            string d = TempDir();
            WritePrefs(d, PREFS_MAIN_ONLY);
            var ddc = new FakeDdc();
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.MainOnly;
            var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, new FakeClock(), d);
            // Release() 一返回，紧接着的那次 Mode 读数命中一次 Missing，再往后才回落到真实拓扑（克隆）。
            layout.OnRelease = delegate { layout.MissingModeReads = 1; };
            ddc.OnWrite = delegate { layout.Calls.Add("ddc"); };
            ddc.ReadsThen(1, true);  // 进入克隆后等到能读到再写
            ddc.ReadsThen(1, false); // 写后读失败：已切走
            o.GiveToPeer();
            Assert.Eq("release,ddc", Seq(layout), "transient Missing after release tolerated, no cancel");
            Assert.Eq(false, o.Owned, "given away");
            Assert.Eq(0, n.Titles.Count, "no cancel notice");
            Directory.Delete(d, true);
        }

        // 写 DDC、显示器正在切换输入期间 Mode 短暂读到 Missing：跳过让出的判断必须用 EnsureSecondaryBeforeGive
        // 返回时记下的标记，不能在这时重新读 Mode（否则会多让出一次）。
        public static void Test_MainOnly_GiveMac_MissingModeAfterWrite_SkipDrivenByFlag()
        {
            string d = TempDir();
            WritePrefs(d, PREFS_MAIN_ONLY);
            var ddc = new FakeDdc();
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.MainOnly;
            var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, new FakeClock(), d);
            // DDC 写入的那一刻（对应显示器切输入）拓扑读数变 Missing，直到给出方自己再读也是 Missing。
            ddc.OnWrite = delegate { layout.ModeValue = LayoutMode.Missing; layout.Calls.Add("ddc"); };
            ddc.ReadsThen(1, true);  // 进入克隆后等到能读到再写
            ddc.ReadsThen(1, false); // 写后读失败：已切走
            o.GiveToPeer();
            Assert.Eq("release,ddc", Seq(layout), "skip decision uses the pre-write flag, not a fresh (Missing) Mode read");
            Assert.Eq(false, o.Owned, "given away");
            Assert.Eq(0, n.Titles.Count, "no notification");
            Directory.Delete(d, true);
        }

        public static void Test_MainOnly_GiveMac_WaitsForReadBeforeWrite()
        {
            string d = TempDir();
            WritePrefs(d, PREFS_MAIN_ONLY);
            var ddc = new FakeDdc();
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.MainOnly;
            var clock = new FakeClock();
            var o = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), clock, d);
            ddc.OnRead = delegate(bool ok) { layout.Calls.Add(ok ? "r1" : "r0"); };
            int sleptAtWrite = -1;
            ddc.OnWrite = delegate { sleptAtWrite = clock.Slept; layout.Calls.Add("ddc"); };
            ddc.ReadsThen(2, false); ddc.ReadsThen(1, true); // 进入克隆后显示器重新同步，1.5 秒后才读得到
            ddc.ReadsThen(1, false); // 写后读失败：已切走
            o.GiveToPeer();
            Assert.Eq("release,r0,r0,r1,ddc,r0", Seq(layout), "DDC write only after a successful read");
            Assert.Eq(1500, sleptAtWrite, "polled every 500 ms before the write");
            Assert.Eq(false, o.Owned, "given away");
            Directory.Delete(d, true);
        }

        public static void Test_MainOnly_GiveMac_NeverReadable_WritesAnyway()
        {
            string d = TempDir();
            WritePrefs(d, PREFS_MAIN_ONLY);
            var ddc = new FakeDdc();
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.MainOnly;
            var clock = new FakeClock();
            var o = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), clock, d);
            ddc.DefaultRead = false;
            ddc.OnRead = delegate(bool ok) { layout.Calls.Add(ok ? "r1" : "r0"); };
            int sleptAtWrite = -1;
            ddc.OnWrite = delegate { sleptAtWrite = clock.Slept; layout.Calls.Add("ddc"); };
            o.GiveToPeer();
            Assert.Eq("release,r0,r0,r0,r0,r0,r0,ddc,r0", Seq(layout), "waited at most 3 s, then wrote anyway");
            Assert.Eq(3000, sleptAtWrite, "gave up waiting after 3 s");
            Assert.Eq(1, ddc.Writes.Count, "wrote once");
            Assert.Eq(1, LogLines(d, "writing anyway"), "logged");
            Assert.Eq(false, o.Owned, "given away");
            Directory.Delete(d, true);
        }

        public static void Test_Extend_GiveMac_NoWaitBeforeWrite()
        {
            string d = TempDir();
            var ddc = new FakeDdc();
            var layout = new FakeLayout();
            var o = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), new FakeClock(), d);
            ddc.OnRead = delegate(bool ok) { layout.Calls.Add(ok ? "r1" : "r0"); };
            ddc.OnWrite = delegate { layout.Calls.Add("ddc"); };
            ddc.ReadsThen(1, false);
            o.GiveToPeer();
            Assert.Eq("ddc,r0,release", Seq(layout), "topology untouched before the write: write at once, release after");
            Assert.Eq(false, o.Owned, "given away");
            Directory.Delete(d, true);
        }

        public static void Test_MainOnly_GiveMac_ReleaseFails_ExtendsThenGives()
        {
            string d = TempDir();
            WritePrefs(d, PREFS_MAIN_ONLY);
            var ddc = new FakeDdc();
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.MainOnly; layout.ThrowOnRelease = true;
            var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, new FakeClock(), d);
            ddc.OnWrite = delegate { layout.Calls.Add("ddc"); };
            ddc.ReadsThen(1, true);  // 打开副屏后等到能读到再写
            ddc.ReadsThen(1, false); // 写后读失败：已切走
            o.GiveToPeer();
            Assert.Eq("release,take,ddc,release", Seq(layout), "clone failed -> extend -> write -> release (extended, not cloned)");
            Assert.Eq(false, o.Owned, "given away");
            Assert.Eq(1, ddc.Writes.Count, "wrote once");
            Directory.Delete(d, true);
        }

        public static void Test_MainOnly_GiveMac_SecondaryRestoreFails_Cancels()
        {
            string d = TempDir();
            WritePrefs(d, PREFS_MAIN_ONLY);
            var ddc = new FakeDdc();
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.MainOnly;
            layout.ThrowOnRelease = true; layout.ThrowOnTake = true;
            var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, new FakeClock(), d);
            o.GiveToPeer();
            Assert.Eq(0, ddc.Writes.Count, "no DDC write");
            Assert.Eq("release,take", Seq(layout), "tried clone then extend");
            Assert.Eq(true, o.Owned, "still owned");
            Assert.Eq(1, n.Titles.Count, "one notification");
            Assert.Eq("无法恢复副屏，已取消切换", n.Titles[0], "cancel notice");
            Assert.Eq("副屏没有亮起来，主屏留在本机", n.Texts[0], "cancel body");
            Directory.Delete(d, true);
        }

        public static void Test_MainOnly_GiveMac_NotEffective_RestoresMainOnly()
        {
            string d = TempDir();
            WritePrefs(d, PREFS_MAIN_ONLY);
            var ddc = new FakeDdc();
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.MainOnly;
            var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, new FakeClock(), d);
            ddc.DefaultRead = true;
            o.GiveToPeer();
            Assert.Eq(1, ddc.Writes.Count, "wrote");
            Assert.Eq("release,take-main", Seq(layout), "clone for the attempt, main-only restored");
            Assert.Eq(true, o.Owned, "still owned");
            Assert.Eq("切换未生效", n.Titles[0], "original notice");
            Directory.Delete(d, true);
        }

        public static void Test_MainOnly_GiveMac_WriteFails_RestoresMainOnly()
        {
            string d = TempDir();
            WritePrefs(d, PREFS_MAIN_ONLY);
            var ddc = new FakeDdc(); ddc.WriteResult = false;
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.MainOnly;
            var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, new FakeClock(), d);
            o.GiveToPeer();
            Assert.Eq("release,take-main", Seq(layout), "main-only restored after write failure");
            Assert.Eq("切换未生效", n.Titles[0], "original notice");
            Directory.Delete(d, true);
        }

        public static void Test_MainOnly_GiveDevice_NoSignal()
        {
            string d = TempDir();
            WritePrefs(d, PREFS_MAIN_ONLY);
            var ddc = new FakeDdc();
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.MainOnly;
            var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, new FakeClock(), d);
            ddc.OnWrite = delegate { layout.Calls.Add("ddc"); };
            // 写前等到能读到 -> 写后读失败(切走) -> 观察期读到两次(自动跳回)
            ddc.ReadsThen(1, true); ddc.ReadsThen(1, false); ddc.ReadsThen(2, true);
            o.GiveToDevice(0x11);
            Assert.Eq("release,ddc,take-main", Seq(layout), "secondary restored (clone) before the write, no second release when already cloned");
            Assert.Eq(true, o.Owned, "kept");
            Assert.Eq(LayoutMode.MainOnly, layout.ModeValue, "back to main only");
            Assert.Eq("没有检测到 设备 1 的信号", n.Titles[0], "notified");
            Directory.Delete(d, true);
        }

        public static void Test_Extend_GiveMac_NotEffective_MissingScreen_NoTake()
        {
            string d = TempDir();
            var ddc = new FakeDdc();
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Missing;
            var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, new FakeClock(), d);
            ddc.DefaultRead = true;
            o.GiveToPeer();
            Assert.Eq(1, ddc.Writes.Count, "wrote");
            Assert.Eq(0, layout.Calls.Count, "missing screen: no restore attempt");
            Assert.Eq("切换未生效", n.Titles[0], "original notice");
            Directory.Delete(d, true);
        }

        public static void Test_PassiveRelease_FailsInMainOnly_EnablesSecondary()
        {
            string d = TempDir();
            WritePrefs(d, PREFS_MAIN_ONLY);
            var ddc = new FakeDdc();
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.MainOnly; layout.ThrowOnRelease = true;
            var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, new FakeClock(), d);
            ddc.DefaultRead = false;
            Ticks(o, 8);
            Assert.Eq(false, o.Owned, "released");
            Assert.Eq("release,take", Seq(layout), "secondary enabled after failed release");
            Assert.Eq("让出布局失败", n.Titles[0], "original notice");
            Assert.Eq(1, n.Titles.Count, "one notification");
            Directory.Delete(d, true);
        }

        public static void Test_PassiveRelease_FailsInExtend_NoExtraTake()
        {
            string d = TempDir();
            var ddc = new FakeDdc();
            var layout = new FakeLayout(); layout.ThrowOnRelease = true;
            var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, new FakeClock(), d);
            ddc.DefaultRead = false;
            Ticks(o, 8);
            Assert.Eq(false, o.Owned, "released");
            Assert.Eq("release", Seq(layout), "no take when the secondary is already on");
            Assert.Eq("让出布局失败", n.Titles[0], "original notice");
            Directory.Delete(d, true);
        }

        static string StartupSeq(bool owned, LayoutMode mode, string prefs)
        {
            string d = TempDir();
            if (prefs != null) WritePrefs(d, prefs);
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(owned);
            var layout = new FakeLayout(); layout.ModeValue = mode;
            Make(ddc, layout, new FakeRelay(), new FakeNotifier(), new FakeClock(), d);
            string s = Seq(layout) + "|" + LogLines(d, "startup reconcile: " + (layout.Calls.Count == 0 ? "none" : layout.Calls[0] == "release" ? "release" : "take"));
            Directory.Delete(d, true);
            return s;
        }

        public static void Test_Startup_ReconcileMatrix()
        {
            Assert.Eq("release|1", StartupSeq(false, LayoutMode.MainOnly, null), "not owned + main only -> release (clone enables secondary)");
            Assert.Eq("release|1", StartupSeq(false, LayoutMode.MainOnly, PREFS_MAIN_ONLY), "not owned + main only, main-only preset -> release");
            Assert.Eq("|1", StartupSeq(false, LayoutMode.Clone, PREFS_MAIN_ONLY), "not owned + clone -> none");
            Assert.Eq("take-main|1", StartupSeq(true, LayoutMode.Extend, PREFS_MAIN_ONLY), "owned + extend + main-only preset -> take-main");
            Assert.Eq("take-main|1", StartupSeq(true, LayoutMode.Clone, PREFS_MAIN_ONLY), "owned + clone + main-only preset -> take-main");
            Assert.Eq("take|1", StartupSeq(true, LayoutMode.MainOnly, null), "owned + main only + extend preset -> take");
            Assert.Eq("|1", StartupSeq(true, LayoutMode.MainOnly, PREFS_MAIN_ONLY), "owned + main only + main-only preset -> none");
            Assert.Eq("|1", StartupSeq(true, LayoutMode.Missing, null), "owned + missing -> none");
            Assert.Eq("|1", StartupSeq(true, LayoutMode.Missing, PREFS_MAIN_ONLY), "owned + missing, main-only preset -> none");
        }

        public static void Test_SetOwnedLayout_Owned_AppliesNowAndSaves()
        {
            string d = TempDir();
            var layout = new FakeLayout();
            var n = new FakeNotifier();
            var o = Make(new FakeDdc(), layout, new FakeRelay(), n, new FakeClock(), d);
            Assert.Eq(OwnedLayout.Extend, o.OwnedLayout, "default preset");
            o.SetOwnedLayout(OwnedLayout.MainOnly);
            Assert.Eq(OwnedLayout.MainOnly, o.OwnedLayout, "preset changed");
            Assert.Eq("take-main", Seq(layout), "applied immediately");
            Assert.Eq("{\"ownedLayout\":\"mainOnly\",\"autoSaveLayout\":true}", File.ReadAllText(Path.Combine(d, "prefs.json")), "prefs.json written");
            Assert.Eq(0, n.Titles.Count, "no notification when applied");
            o.SetOwnedLayout(OwnedLayout.Extend);
            Assert.Eq("take-main,take", Seq(layout), "back to extend immediately");
            Assert.Eq(OwnedLayout.Extend, Prefs.Load(Path.Combine(d, "prefs.json")).OwnedLayout, "saved extend");
            Directory.Delete(d, true);
        }

        public static void Test_SetOwnedLayout_NotOwned_OnlyNotifies()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone;
            var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, new FakeClock(), d);
            o.SetOwnedLayout(OwnedLayout.MainOnly);
            Assert.Eq(0, layout.Calls.Count, "layout untouched");
            Assert.Eq("拿回后的布局已更改", n.Titles[0], "notice");
            Assert.Eq("下次拿回主屏时生效", n.Texts[0], "notice body");
            Assert.Eq(OwnedLayout.MainOnly, Prefs.Load(Path.Combine(d, "prefs.json")).OwnedLayout, "saved");
            // 下次启动读回；拿回时按仅主屏
            ddc.Reads.Enqueue(false);
            var o2 = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), new FakeClock(), d);
            Assert.Eq(OwnedLayout.MainOnly, o2.OwnedLayout, "reloaded");
            ddc.DefaultRead = true;
            o2.Tick(); o2.Tick();
            Assert.Eq("take-main", Seq(layout), "next take is main-only");
            Directory.Delete(d, true);
        }

        public static void Test_SetAutoSaveLayout_SavesPrefs()
        {
            string d = TempDir();
            var o = Make(new FakeDdc(), new FakeLayout(), new FakeRelay(), new FakeNotifier(), new FakeClock(), d);
            Assert.Eq(true, o.AutoSaveLayout, "default on");
            o.SetAutoSaveLayout(false);
            Assert.Eq(false, o.AutoSaveLayout, "off");
            Assert.Eq("{\"ownedLayout\":\"extend\",\"autoSaveLayout\":false}", File.ReadAllText(Path.Combine(d, "prefs.json")), "prefs.json written");
            o.SetAutoSaveLayout(true);
            Assert.Eq(true, Prefs.Load(Path.Combine(d, "prefs.json")).AutoSaveLayout, "on again");
            Directory.Delete(d, true);
        }

        public static void Test_PrepareExit_EnablesSecondaryOnlyFromMainOnly()
        {
            string d = TempDir();
            WritePrefs(d, PREFS_MAIN_ONLY);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.MainOnly;
            var o = Make(new FakeDdc(), layout, new FakeRelay(), new FakeNotifier(), new FakeClock(), d);
            o.PrepareExit();
            Assert.Eq("take", Seq(layout), "main only -> extend on exit");
            Assert.Eq(LayoutMode.Extend, layout.ModeValue, "secondary on");

            foreach (LayoutMode m in new LayoutMode[] { LayoutMode.Extend, LayoutMode.Clone, LayoutMode.Missing })
            {
                layout.Calls.Clear();
                layout.ModeValue = m;
                o.PrepareExit();
                Assert.Eq(0, layout.Calls.Count, "no action on exit in " + m);
            }

            layout.ModeValue = LayoutMode.MainOnly; layout.ThrowOnTake = true;
            o.PrepareExit(); // 失败只写日志，不抛
            Assert.True(LogLines(d, "boom") >= 1, "exit failure logged");
            Directory.Delete(d, true);
        }

        // ---------------- 自己拿回后的稳定宽限期 ----------------

        // 每拍 0.5 秒：每两拍把时钟推进 1 秒
        static void TimedTicks(Orchestrator o, FakeClock clock, int count)
        {
            for (int i = 0; i < count; i++) { o.Tick(); if (i % 2 == 1) clock.T++; }
        }

        public static void Test_PassiveTake_SettleGrace_ThenReleasesAsUsual()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone;
            var clock = new FakeClock();
            var o = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), clock, d);
            ddc.DefaultRead = true;
            o.Tick(); o.Tick();
            Assert.Eq(true, o.Owned, "taken");
            ddc.DefaultRead = false; // 拿回改了显示模式，显示器重新同步，读取暂时失败
            int reads = ddc.ReadCount;
            TimedTicks(o, clock, 4); // 拿回后 2 秒
            Assert.True(ddc.ReadCount > reads, "failing reads happened");
            Assert.Eq(true, o.Owned, "still owned 2 s after our own take");
            TimedTicks(o, clock, 4); // 到第 4 秒
            Assert.Eq(true, o.Owned, "still owned up to the end of the settle window");
            Assert.Eq("take", Seq(layout), "no release during the settle window");
            TimedTicks(o, clock, 4);
            Assert.Eq(false, o.Owned, "released once the settle window is over");
            Assert.Eq("take,release", Seq(layout), "release as usual");
            Directory.Delete(d, true);
        }

        public static void Test_SetOwnedLayout_Owned_SettleGrace()
        {
            string d = TempDir();
            var ddc = new FakeDdc();
            var layout = new FakeLayout();
            var clock = new FakeClock();
            var o = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), clock, d);
            Ticks(o, 3); // 下一拍就是常规读取
            o.SetOwnedLayout(OwnedLayout.MainOnly);
            Assert.Eq("take-main", Seq(layout), "applied");
            ddc.DefaultRead = false;
            int reads = ddc.ReadCount;
            TimedTicks(o, clock, 4); // 2 秒
            Assert.True(ddc.ReadCount > reads, "failing reads happened");
            Assert.Eq(true, o.Owned, "not released by failing reads right after our own take");
            Assert.Eq("take-main", Seq(layout), "no release");
            Directory.Delete(d, true);
        }

        // 失败读 3 秒（覆盖常规读取与下一拍复核）仍占有；宽限期过后照常让出
        static void AssertSettles(string name, Orchestrator o, FakeDdc ddc, FakeClock clock, FakeLayout layout)
        {
            int calls = layout.Calls.Count;
            ddc.DefaultRead = false;
            TimedTicks(o, clock, 6);
            Assert.Eq(true, o.Owned, name + ": still owned within the settle window");
            Assert.Eq(calls, layout.Calls.Count, name + ": no release within the settle window");
            TimedTicks(o, clock, 8);
            Assert.Eq(false, o.Owned, name + ": released after the settle window");
        }

        public static void Test_OwnTakes_OtherPaths_SettleGrace()
        {
            // 启动对账拿回
            string d1 = TempDir();
            var ddc1 = new FakeDdc(); ddc1.Reads.Enqueue(true);
            var layout1 = new FakeLayout(); layout1.ModeValue = LayoutMode.Clone;
            var clock1 = new FakeClock();
            var o1 = Make(ddc1, layout1, new FakeRelay(), new FakeNotifier(), clock1, d1);
            Assert.Eq("take", Seq(layout1), "startup reconcile take");
            AssertSettles("startup reconcile", o1, ddc1, clock1, layout1);
            Directory.Delete(d1, true);

            // 占有时按"拿回主屏"
            string d2 = TempDir();
            var ddc2 = new FakeDdc();
            var layout2 = new FakeLayout();
            var clock2 = new FakeClock();
            var o2 = Make(ddc2, layout2, new FakeRelay(), new FakeNotifier(), clock2, d2);
            o2.TakeBack();
            Assert.Eq("take", Seq(layout2), "take-back while owned");
            AssertSettles("take-back while owned", o2, ddc2, clock2, layout2);
            Directory.Delete(d2, true);

            // 对端发来 give windows
            string d3 = TempDir();
            var ddc3 = new FakeDdc();
            var layout3 = new FakeLayout();
            var clock3 = new FakeClock();
            var o3 = Make(ddc3, layout3, new FakeRelay(), new FakeNotifier(), clock3, d3);
            o3.HandleCommand("give windows");
            Assert.Eq("take", Seq(layout3), "give windows while owned");
            AssertSettles("give windows", o3, ddc3, clock3, layout3);
            Directory.Delete(d3, true);

            // 切换未生效，恢复仅主屏
            string d4 = TempDir();
            WritePrefs(d4, PREFS_MAIN_ONLY);
            var ddc4 = new FakeDdc();
            var layout4 = new FakeLayout(); layout4.ModeValue = LayoutMode.MainOnly;
            var clock4 = new FakeClock();
            var o4 = Make(ddc4, layout4, new FakeRelay(), new FakeNotifier(), clock4, d4);
            o4.GiveToPeer();
            Assert.Eq("release,take-main", Seq(layout4), "give not effective, main-only restored");
            AssertSettles("give restore", o4, ddc4, clock4, layout4);
            Directory.Delete(d4, true);
        }

        // ---------------- 拿回主屏的通知按实际生效的布局 ----------------

        static string TakeBackNotices(string prefs, bool throwMain, bool throwTake)
        {
            string d = TempDir();
            if (prefs != null) WritePrefs(d, prefs);
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone;
            layout.ThrowOnTakeMain = throwMain; layout.ThrowOnTake = throwTake;
            var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, new FakeClock(), d);
            ddc.DefaultRead = false;
            o.TakeBack();
            var parts = new List<string>();
            for (int i = 0; i < n.Titles.Count; i++) parts.Add(n.Titles[i] + "|" + n.Texts[i]);
            Directory.Delete(d, true);
            return string.Join(";", parts.ToArray());
        }

        public static void Test_TakeBack_NoticeReflectsAppliedLayout()
        {
            Assert.Eq("已请求 Mac 交还主屏|布局已恢复为双屏", TakeBackNotices(null, false, false), "extend preset");
            Assert.Eq("已请求 Mac 交还主屏|布局已切换为仅主屏", TakeBackNotices(PREFS_MAIN_ONLY, false, false), "main-only applied");
            Assert.Eq("无法关闭副屏，已按双屏扩展显示|boom main;已请求 Mac 交还主屏|布局已恢复为双屏",
                TakeBackNotices(PREFS_MAIN_ONLY, true, false), "main-only fell back to extend: the notice must not claim main-only");
            Assert.Eq("恢复布局失败|boom;已请求 Mac 交还主屏|布局未能恢复",
                TakeBackNotices(null, false, true), "take failed: the notice must not claim a restored layout");
        }

        // ---------------- 中继前刷新状态文件 ----------------

        public static void Test_Relay_StateRefreshedRightBeforeRequest()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone;
            var relay = new FakeRelay(); relay.Result = 2;
            var clock = new FakeClock();
            var o = Make(ddc, layout, relay, new FakeNotifier(), clock, d);
            var seen = new List<long>();
            relay.OnRequest = delegate
            {
                bool owned; long ts;
                Assert.True(StateFile.ReadState(d, out owned, out ts), "state readable during the relay");
                seen.Add(ts);
            };
            clock.T += 7; // 距上次写状态文件已 7 秒，中继还要占用最多约 8.5 秒
            o.TakeBack();
            clock.T += 7;
            o.GiveToDevice(0x11);
            Assert.Eq(2, seen.Count, "two relay requests");
            Assert.Eq(1007L, seen[0], "take-back: state refreshed right before the relay");
            Assert.Eq(1014L, seen[1], "give while not owned: state refreshed right before the relay");
            Directory.Delete(d, true);
        }

        // ---------------- request take（设计文档 5.2、6.3） ----------------

        // 接收端：未占有时收到 pre-take → 立即拿回、owned=1，8 秒宽限期内读失败不让出，期满仍读不到才让出。
        public static void Test_PreTake_NotOwned_TakesWithGrace()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone;
            var relay = new FakeRelay();
            var n = new FakeNotifier();
            var clock = new FakeClock();
            var o = Make(ddc, layout, relay, n, clock, d);
            int fired = 0; o.OwnedChanged += delegate { fired++; };
            ddc.DefaultRead = false; // 显示器还没切过来
            StateFile.WriteCommand(d, "pre-take");
            o.Tick();
            Assert.Eq(false, File.Exists(Path.Combine(d, "cmd.txt")), "cmd consumed");
            Assert.Eq("take", layout.Calls[0], "layout restored immediately");
            Assert.Eq(true, o.Owned, "owned right away");
            Assert.Eq(1, fired, "event fired");
            bool owned; long ts;
            Assert.True(StateFile.ReadState(d, out owned, out ts), "state written");
            Assert.Eq(true, owned, "state owned=1");
            Assert.Eq(0, relay.Requests.Count + relay.TakeRequests, "no relay");
            Assert.Eq(0, n.Titles.Count, "no notice: the switch was started on the Mac");
            for (int i = 0; i < 12; i++) o.Tick(); // 3 polls at owned cadence, all failing, inside grace
            Assert.Eq(true, o.Owned, "failures ignored during grace");
            Assert.Eq(1, layout.Calls.Count, "no release during grace");
            clock.T += 9; // grace expired, the Mac never switched
            for (int i = 0; i < 8; i++) o.Tick();
            Assert.Eq(false, o.Owned, "released after grace when reads still fail");
            Assert.Eq("release", layout.Calls[1], "layout release");
            Directory.Delete(d, true);
        }

        // 显示器按时切过来：宽限期内读数恢复，保持占有，不再拿回一次。
        public static void Test_PreTake_MonitorArrives_StaysOwned()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone;
            var clock = new FakeClock();
            var o = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), clock, d);
            ddc.DefaultRead = false;
            StateFile.WriteCommand(d, "pre-take");
            o.Tick();
            for (int i = 0; i < 4; i++) o.Tick();
            ddc.DefaultRead = true;
            clock.T += 3;
            for (int i = 0; i < 12; i++) o.Tick();
            clock.T += 10;
            for (int i = 0; i < 12; i++) o.Tick();
            Assert.Eq(true, o.Owned, "still owned");
            Assert.Eq(1, layout.Calls.Count, "one take only");
            Directory.Delete(d, true);
        }

        // 已占有时 pre-take 不动布局（主屏已在本机）。
        public static void Test_PreTake_Owned_NoOp()
        {
            string d = TempDir();
            var ddc = new FakeDdc();
            var layout = new FakeLayout();
            var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, new FakeClock(), d);
            int fired = 0; o.OwnedChanged += delegate { fired++; };
            StateFile.WriteCommand(d, "pre-take");
            o.Tick();
            Assert.Eq(true, o.Owned, "still owned");
            Assert.Eq(0, layout.Calls.Count, "no layout change");
            Assert.Eq(0, fired, "no event");
            Assert.Eq(0, n.Titles.Count, "no notice");
            Assert.Eq(false, File.Exists(Path.Combine(d, "cmd.txt")), "cmd consumed");
            Directory.Delete(d, true);
        }

        // pre-take 按"拿回后的布局"拿回：仅主屏预设用 take-main。
        public static void Test_PreTake_MainOnly_UsesTakeMain()
        {
            string d = TempDir();
            WritePrefs(d, PREFS_MAIN_ONLY);
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone;
            var o = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), new FakeClock(), d);
            ddc.DefaultRead = false;
            o.HandleCommand("pre-take");
            Assert.Eq("take-main", layout.Calls[0], "main-only take");
            Assert.Eq(true, o.Owned, "owned");
            Directory.Delete(d, true);
        }

        // owned=1 在拿回之前就发布（与 TakeBack 相同）：拿回较慢时对端读到的仍是新的状态。
        public static void Test_PreTake_StatePublishedBeforeSlowTake()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone;
            var clock = new FakeClock();
            var o = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), clock, d);
            ddc.DefaultRead = false;
            bool ownedDuringTake = false;
            layout.OnTake = delegate
            {
                bool owned; long ts;
                Assert.True(StateFile.ReadState(d, out owned, out ts), "state readable during the take");
                ownedDuringTake = owned;
                clock.T += 5;
            };
            o.HandleCommand("pre-take");
            Assert.Eq(true, ownedDuringTake, "owned=1 published before the take");
            Directory.Delete(d, true);
        }

        // 发送端：切给 Mac 时先 request take，再写 DDC。
        public static void Test_GiveMac_RequestTakeBeforeDdcWrite()
        {
            string d = TempDir();
            var ddc = new FakeDdc();
            var layout = new FakeLayout();
            var relay = new FakeRelay();
            var o = Make(ddc, layout, relay, new FakeNotifier(), new FakeClock(), d);
            var order = new List<string>();
            relay.OnRequestTake = delegate { order.Add("request take"); };
            ddc.OnWrite = delegate { order.Add("ddc write"); };
            ddc.ReadsThen(1, false);
            o.GiveToPeer();
            Assert.Eq(1, relay.TakeRequests, "one request take");
            Assert.Eq(0, relay.Requests.Count, "no request give");
            Assert.Eq("request take,ddc write", string.Join(",", order.ToArray()), "request take first");
            Assert.Eq(false, o.Owned, "given");
            Directory.Delete(d, true);
        }

        // 仅主屏时，request take 在打开副屏之前发出：越早通知，Mac 切布局的时间越能和本机的准备重叠。
        public static void Test_MainOnly_GiveMac_RequestTakeBeforeSecondary()
        {
            string d = TempDir();
            WritePrefs(d, PREFS_MAIN_ONLY);
            var ddc = new FakeDdc();
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.MainOnly;
            var relay = new FakeRelay();
            var o = Make(ddc, layout, relay, new FakeNotifier(), new FakeClock(), d);
            var order = new List<string>();
            relay.OnRequestTake = delegate { order.Add("request take"); };
            layout.OnRelease = delegate { order.Add("release"); };
            ddc.OnWrite = delegate { order.Add("ddc write"); };
            ddc.OnRead = delegate(bool ok) { if (order.Count > 0 && order[order.Count - 1] == "ddc write") ddc.DefaultRead = false; };
            o.GiveToPeer();
            Assert.Eq("request take", order[0], "request take before enabling the secondary");
            Assert.Eq("release", order[1], "then the secondary");
            Assert.True(order.Contains("ddc write"), "then the DDC write");
            Directory.Delete(d, true);
        }

        // request take 的结果只记日志：对端不支持（1）、未运行（3）、连不上（255）、中继抛异常，都照常写 DDC、让出，且不弹通知。
        public static void Test_GiveMac_RequestTakeFailure_NeverBlocksDdcWrite()
        {
            foreach (int rc in new int[] { 1, 3, 255, -1 })
            {
                string d = TempDir();
                var ddc = new FakeDdc();
                var layout = new FakeLayout();
                var relay = new FakeRelay();
                var n = new FakeNotifier();
                if (rc == -1) relay.ThrowOnTake = true; else relay.TakeResult = rc;
                var o = Make(ddc, layout, relay, n, new FakeClock(), d);
                ddc.ReadsThen(1, false);
                o.GiveToPeer();
                Assert.Eq(1, relay.TakeRequests, "request take sent (" + rc + ")");
                Assert.Eq(1, ddc.Writes.Count, "DDC still written (" + rc + ")");
                Assert.Eq((uint)0x0F, ddc.Writes[0], "mac code (" + rc + ")");
                Assert.Eq("release", layout.Calls[0], "released (" + rc + ")");
                Assert.Eq(false, o.Owned, "given (" + rc + ")");
                Assert.Eq(0, n.Titles.Count, "no notice about request take (" + rc + ")");
                Directory.Delete(d, true);
            }
        }

        // 对端发来的 give mac（Mac 按 ⌃⌥1 拿回）也一样先发 request take（与 Mac 的 give_to_code 对称，对方已占有时返回 0 不动）。
        public static void Test_GiveMacCommand_AlsoSendsRequestTake()
        {
            string d = TempDir();
            var ddc = new FakeDdc();
            var relay = new FakeRelay();
            var o = Make(ddc, new FakeLayout(), relay, new FakeNotifier(), new FakeClock(), d);
            ddc.ReadsThen(1, false);
            StateFile.WriteCommand(d, "give mac");
            o.Tick();
            Assert.Eq(1, relay.TakeRequests, "request take sent");
            Assert.Eq(1, ddc.Writes.Count, "wrote");
            Directory.Delete(d, true);
        }

        // 切给 Other、未占有时的切换请求都不发 request take。
        public static void Test_RequestTake_OnlyWhenGivingOwnedToMac()
        {
            string d = TempDir();
            var ddc = new FakeDdc();
            var relay = new FakeRelay();
            var o = Make(ddc, new FakeLayout(), relay, new FakeNotifier(), new FakeClock(), d);
            ddc.DefaultRead = false; // Other 有信号，显示器不跳回
            o.GiveToDevice(0x11);
            Assert.Eq(0, relay.TakeRequests, "no request take for other");
            Assert.Eq(false, o.Owned, "given to other");
            relay.Result = 2;
            o.GiveToPeer();
            Assert.Eq(0, relay.Requests.Count, "give peer while not owned is ignored");
            o.GiveToDevice(0x11);
            o.TakeBack();
            Assert.Eq(0, relay.TakeRequests, "no request take while not owned");
            Assert.Eq("code 17,peer", string.Join(",", relay.Requests.ToArray()), "request give used instead");
            Directory.Delete(d, true);
        }

        // request take 最多占住 gate 约 5 秒：发出前刷新状态文件（与中继 request give 一致）。
        public static void Test_GiveMac_StateRefreshedBeforeRequestTake()
        {
            string d = TempDir();
            var ddc = new FakeDdc();
            var relay = new FakeRelay();
            var clock = new FakeClock();
            var o = Make(ddc, new FakeLayout(), relay, new FakeNotifier(), clock, d);
            long seen = -1;
            relay.OnRequestTake = delegate
            {
                bool owned; long ts;
                Assert.True(StateFile.ReadState(d, out owned, out ts), "state readable");
                seen = ts;
            };
            clock.T += 7;
            ddc.ReadsThen(1, false);
            o.GiveToPeer();
            Assert.Eq(1007L, seen, "state refreshed right before request take");
            Directory.Delete(d, true);
        }

        // ---------------- 布局失败后自愈 ----------------

        // 未占有时副屏必须亮着：拓扑变成仅主屏（例如别的程序关了副屏）→ 约 10 秒内（每 20 拍检查一次）让出，克隆会打开副屏。
        public static void Test_NotOwned_MainOnly_ReleasedWithin10s()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false); ddc.DefaultRead = false;
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone;
            var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, new FakeClock(), d);
            Assert.Eq(0, layout.Calls.Count, "startup: not owned + clone is consistent");
            layout.ModeValue = LayoutMode.MainOnly;
            Ticks(o, 20);
            Assert.Eq("release", Seq(layout), "secondary enabled within 20 ticks (10 s)");
            Assert.Eq(LayoutMode.Clone, layout.ModeValue, "cloned");
            Assert.Eq(1, LogLines(d, "not owned but main-only"), "logged");
            Ticks(o, 100);
            Assert.Eq("release", Seq(layout), "no further action once the secondary is on");
            Assert.Eq(false, o.Owned, "ownership unchanged");
            Assert.Eq(0, n.Titles.Count, "no notification");
            Directory.Delete(d, true);
        }

        // 实机现象：仅主屏时被动让出，UAC 安全桌面期间 SetDisplayConfig 一律被拒（让出与开副屏兜底都失败）。
        // 10 秒后重试，期间不因"未占有 + 仅主屏"提前再试；同一段连续失败只通知一次；恢复后写日志、不再重试。
        public static void Test_PassiveRelease_Fails_RetriedAfter10s_NotifiedOnce()
        {
            string d = TempDir();
            WritePrefs(d, PREFS_MAIN_ONLY);
            var ddc = new FakeDdc();
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.MainOnly;
            layout.ThrowOnRelease = true; layout.ThrowOnTake = true;
            var n = new FakeNotifier();
            var clock = new FakeClock();
            var o = Make(ddc, layout, new FakeRelay(), n, clock, d);
            ddc.DefaultRead = false;
            Ticks(o, 8);
            Assert.Eq(false, o.Owned, "released (ownership follows DDC)");
            Assert.Eq("release,take", Seq(layout), "release failed, enable-secondary fallback failed");
            Assert.Eq(1, n.Titles.Count, "first failure notified");
            Assert.Eq("让出布局失败", n.Titles[0], "failure notice");
            clock.T += 9; Ticks(o, 16); // 含第 20 拍（未占有 + 仅主屏的检查）
            Assert.Eq("release,take", Seq(layout), "no retry before 10 s, not even from the not-owned main-only check");
            clock.T += 1; o.Tick();
            Assert.Eq("release,take,release,take", Seq(layout), "retried after 10 s (still failing)");
            Assert.Eq(1, n.Titles.Count, "retry failure not notified again");
            layout.ThrowOnRelease = false; layout.ThrowOnTake = false; // 提示框被关掉，调用恢复正常
            clock.T += 5; Ticks(o, 4);
            Assert.Eq(4, layout.Calls.Count, "next retry waits 10 s after the last failure");
            clock.T += 5; o.Tick();
            Assert.Eq("release,take,release,take,release", Seq(layout), "retry succeeded");
            Assert.Eq(LayoutMode.Clone, layout.ModeValue, "cloned, secondary on");
            Assert.Eq(1, n.Titles.Count, "only one failure notice for the whole streak");
            Assert.Eq(1, LogLines(d, "layout: recovered"), "recovery logged");
            clock.T += 60; Ticks(o, 100);
            Assert.Eq(5, layout.Calls.Count, "no more retries after success");
            Assert.Eq(false, o.Owned, "still not owned");
            Directory.Delete(d, true);
        }

        // 占有时拿回失败：10 秒后重试 DoTake，成功后不再重试。
        public static void Test_OwnedTake_Fails_RetriedAfter10s()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone; layout.ThrowOnTake = true;
            var n = new FakeNotifier();
            var clock = new FakeClock();
            var o = Make(ddc, layout, new FakeRelay(), n, clock, d);
            ddc.DefaultRead = true;
            o.Tick(); o.Tick();
            Assert.Eq(true, o.Owned, "taken (ownership follows DDC)");
            Assert.Eq("take", Seq(layout), "take failed");
            Assert.Eq("恢复布局失败", n.Titles[0], "failure notice");
            layout.ThrowOnTake = false;
            clock.T += 9; Ticks(o, 8);
            Assert.Eq("take", Seq(layout), "no retry before 10 s");
            clock.T += 1; o.Tick();
            Assert.Eq("take,take", Seq(layout), "retried after 10 s");
            Assert.Eq(LayoutMode.Extend, layout.ModeValue, "extended");
            Assert.Eq(1, n.Titles.Count, "one notice");
            Assert.Eq(1, LogLines(d, "layout: recovered"), "recovery logged");
            clock.T += 60; Ticks(o, 100);
            Assert.Eq(2, layout.Calls.Count, "no more takes after success");
            Assert.Eq(true, o.Owned, "still owned");
            Directory.Delete(d, true);
        }

        // 重试不与即将发生的切换抢：读数与归属不符、下一拍要复核时本拍不重试，复核照常让出。
        public static void Test_Retry_WaitsForPendingRecheck()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone; layout.ThrowOnTake = true;
            var clock = new FakeClock();
            var o = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), clock, d);
            ddc.DefaultRead = true;
            o.Tick(); o.Tick(); // 第 2 拍拿回，布局失败
            Assert.Eq("take", Seq(layout), "take failed");
            layout.ThrowOnTake = false;
            o.Tick(); // 第 3 拍：不到重试时间
            clock.T += 10;
            ddc.DefaultRead = false; // 显示器已切走
            o.Tick(); // 第 4 拍：常规读取失败，下一拍复核
            Assert.Eq("take", Seq(layout), "no retry while a recheck is pending");
            o.Tick(); // 第 5 拍：复核仍失败 -> 让出
            Assert.Eq(false, o.Owned, "released");
            Assert.Eq("take,release", Seq(layout), "released without a take retry in between");
            Directory.Delete(d, true);
        }

        // 没有布局快照时布局操作必然失败，不重试；保存快照后照常重试。
        public static void Test_Retry_NeedsSnapshot()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.Snapshot = false; layout.ModeValue = LayoutMode.Clone; layout.ThrowOnTake = true;
            var clock = new FakeClock();
            var o = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), clock, d);
            ddc.DefaultRead = true;
            o.Tick(); o.Tick();
            Assert.Eq("take", Seq(layout), "take failed");
            Assert.Eq(0, LogLines(d, "retry in"), "no 'retry in 10s' promise without a snapshot");
            Assert.Eq(1, LogLines(d, "retry after one is saved"), "accurate log without a snapshot");
            clock.T += 30; Ticks(o, 40);
            Assert.Eq("take", Seq(layout), "no retry without a snapshot");
            layout.Snapshot = true; layout.ThrowOnTake = false;
            o.Tick();
            Assert.Eq("take,take", Seq(layout), "retried once a snapshot exists");
            Directory.Delete(d, true);
        }

        // 缺屏（Mode 为 Missing：有一块显示器断电/拔掉）时重试必然失败且要走 DisplaySwitch 回退：推迟，不调用布局，
        // 仍按 10 秒节拍再看，只记一次日志；拓扑恢复后照常重试。
        public static void Test_Retry_DeferredWhileMonitorMissing()
        {
            // 占有：拿回失败后副屏断电
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone; layout.ThrowOnTake = true;
            var clock = new FakeClock();
            var o = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), clock, d);
            ddc.DefaultRead = true;
            o.Tick(); o.Tick();
            Assert.Eq("take", Seq(layout), "take failed");
            layout.ModeValue = LayoutMode.Missing;
            clock.T += 10; Ticks(o, 4);
            Assert.Eq("take", Seq(layout), "no layout call on the retry tick while a monitor is missing");
            clock.T += 10; Ticks(o, 4);
            clock.T += 10; Ticks(o, 4);
            Assert.Eq("take", Seq(layout), "still deferred");
            Assert.Eq(1, LogLines(d, "retry deferred"), "deferral logged once, not every 10 s");
            layout.ThrowOnTake = false;
            layout.ModeValue = LayoutMode.Clone; // 显示器回来了
            clock.T += 5; Ticks(o, 4);
            Assert.Eq("take", Seq(layout), "deferral keeps the 10 s cadence");
            clock.T += 5; o.Tick();
            Assert.Eq("take,take", Seq(layout), "retried once the topology is back");
            Assert.Eq(LayoutMode.Extend, layout.ModeValue, "restored");
            Directory.Delete(d, true);

            // 未占有：被动让出失败后主屏断电
            string d2 = TempDir();
            var ddc2 = new FakeDdc();
            var layout2 = new FakeLayout(); layout2.ThrowOnRelease = true;
            var clock2 = new FakeClock();
            var o2 = Make(ddc2, layout2, new FakeRelay(), new FakeNotifier(), clock2, d2);
            ddc2.DefaultRead = false;
            Ticks(o2, 8);
            Assert.Eq(false, o2.Owned, "released");
            Assert.Eq("release", Seq(layout2), "release failed");
            layout2.ModeValue = LayoutMode.Missing;
            clock2.T += 10; Ticks(o2, 4);
            clock2.T += 10; Ticks(o2, 4);
            Assert.Eq("release", Seq(layout2), "no release retry while a monitor is missing");
            layout2.ThrowOnRelease = false;
            layout2.ModeValue = LayoutMode.Extend;
            clock2.T += 10; o2.Tick();
            Assert.Eq("release,release", Seq(layout2), "release retried once the topology is back");
            Assert.Eq(LayoutMode.Clone, layout2.ModeValue, "cloned");
            Directory.Delete(d2, true);
        }

        // 只有重试的失败不再通知；轮询与用户操作（改布局预设、give windows、拿回主屏、切走）在失败期间失败也照常通知。
        public static void Test_FailureNotices_OnlyRetriesSuppressed()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone; layout.ThrowOnTake = true;
            var n = new FakeNotifier();
            var clock = new FakeClock();
            var o = Make(ddc, layout, new FakeRelay(), n, clock, d);
            ddc.DefaultRead = true;
            o.Tick(); o.Tick();
            Assert.Eq("恢复布局失败", string.Join(";", n.Titles.ToArray()), "poll take failure notified");
            clock.T += 10; Ticks(o, 4);
            Assert.Eq("take,take", Seq(layout), "retried");
            Assert.Eq(1, n.Titles.Count, "retry failure not notified");
            o.SetOwnedLayout(OwnedLayout.Extend);
            Assert.Eq(2, n.Titles.Count, "SetOwnedLayout failure notified during the failure run");
            o.HandleCommand("give windows");
            Assert.Eq(3, n.Titles.Count, "give windows failure notified");
            o.TakeBack();
            Assert.Eq("恢复布局失败;恢复布局失败;恢复布局失败;恢复布局失败;主屏已在本机", string.Join(";", n.Titles.ToArray()), "take-back failure notified");
            layout.ModeValue = LayoutMode.Extend; layout.ThrowOnRelease = true;
            ddc.ReadsThen(1, false); // 写后读失败：已切走
            o.GiveToPeer();
            Assert.Eq(false, o.Owned, "given away");
            Assert.Eq("让出布局失败", n.Titles[n.Titles.Count - 1], "give's release failure notified");
            int count = n.Titles.Count;
            ddc.DefaultRead = false;
            clock.T += 10; Ticks(o, 4);
            Assert.True(Seq(layout).EndsWith("release,release"), "release retried: " + Seq(layout));
            Assert.Eq(count, n.Titles.Count, "release retry failure not notified");
            Directory.Delete(d, true);
        }

        // 仅主屏预设下的 DoTake：仅主屏失败但退回扩展成功算成功（不记失败、不重试）；两者都失败才在 10 秒后重试。
        public static void Test_MainOnly_DoTake_FallbackSucceeds_NoRetry_BothFail_Retry()
        {
            string d = TempDir();
            WritePrefs(d, PREFS_MAIN_ONLY);
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone; layout.ThrowOnTakeMain = true;
            var n = new FakeNotifier();
            var clock = new FakeClock();
            var o = Make(ddc, layout, new FakeRelay(), n, clock, d);
            ddc.DefaultRead = true;
            o.Tick(); o.Tick();
            Assert.Eq("take-main,take", Seq(layout), "fell back to extend");
            Assert.Eq("无法关闭副屏，已按双屏扩展显示", n.Titles[0], "fallback notice");
            Assert.Eq(0, LogLines(d, "take failed"), "no failure recorded");
            clock.T += 30; Ticks(o, 60);
            Assert.Eq("take-main,take", Seq(layout), "no retry after a successful fallback");
            Directory.Delete(d, true);

            string d2 = TempDir();
            WritePrefs(d2, PREFS_MAIN_ONLY);
            var ddc2 = new FakeDdc(); ddc2.Reads.Enqueue(false);
            var layout2 = new FakeLayout(); layout2.ModeValue = LayoutMode.Clone; layout2.ThrowOnTakeMain = true; layout2.ThrowOnTake = true;
            var n2 = new FakeNotifier();
            var clock2 = new FakeClock();
            var o2 = Make(ddc2, layout2, new FakeRelay(), n2, clock2, d2);
            ddc2.DefaultRead = true;
            o2.Tick(); o2.Tick();
            Assert.Eq("take-main,take", Seq(layout2), "both failed");
            Assert.Eq("恢复布局失败", n2.Titles[0], "failure notice");
            clock2.T += 9; Ticks(o2, 4);
            Assert.Eq("take-main,take", Seq(layout2), "no retry before 10 s");
            clock2.T += 1; o2.Tick();
            Assert.Eq("take-main,take,take-main,take", Seq(layout2), "retried after 10 s");
            Assert.Eq(1, n2.Titles.Count, "retry failure not notified");
            Directory.Delete(d2, true);
        }

        // 重试可能阻塞数秒：调用布局之前先刷新状态文件（设计文档 5.2）。
        public static void Test_Retry_StateFreshBeforeLayoutCall()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone; layout.ThrowOnTake = true;
            var clock = new FakeClock();
            var o = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), clock, d);
            ddc.DefaultRead = true;
            o.Tick(); o.Tick(); // 第 2 拍拿回失败
            o.Tick(); o.Tick(); // 第 4 拍常规读取，写状态文件（时间戳 1000）
            clock.T += 10;
            long tsDuringRetry = -1;
            layout.OnTake = delegate
            {
                bool owned; long ts;
                Assert.True(StateFile.ReadState(d, out owned, out ts), "state readable during the retry");
                tsDuringRetry = ts;
            };
            o.Tick(); // 第 5 拍不是轮询拍：只有重试
            Assert.Eq("take,take", Seq(layout), "retried");
            Assert.Eq(1010L, tsDuringRetry, "state refreshed right before the retry's layout call");
            Directory.Delete(d, true);
        }

        // 切走被取消、而回退扩展已经动过拓扑且最终读数既不是入口时的仅主屏也不是拿回后的布局（一直 Missing）：
        // 记为布局失败，拿回重试恢复拿回后的布局。读数仍是仅主屏（拓扑没变）则不记、不重试。
        public static void Test_GiveCancelled_UnknownTopology_TakeRetryRestores()
        {
            string d = TempDir();
            WritePrefs(d, PREFS_MAIN_ONLY);
            var ddc = new FakeDdc();
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.MainOnly; layout.ThrowOnRelease = true;
            layout.OnRelease = delegate { layout.ModeValue = LayoutMode.Missing; };
            layout.OnTake = delegate { layout.MissingModeReads = 1000; }; // 扩展被接受，但读数一直是 Missing
            var n = new FakeNotifier();
            var clock = new FakeClock();
            var o = Make(ddc, layout, new FakeRelay(), n, clock, d);
            o.GiveToPeer();
            Assert.Eq("release,take", Seq(layout), "cancelled after the extend fallback");
            Assert.Eq(0, ddc.Writes.Count, "no DDC write");
            Assert.Eq(true, o.Owned, "still owned");
            layout.OnRelease = null; layout.OnTake = null; layout.MissingModeReads = 0; // 拓扑稳定下来（实际是扩展）
            clock.T += 10; Ticks(o, 4);
            Assert.Eq("release,take,take-main", Seq(layout), "take retry restored the owned layout");
            Assert.Eq(LayoutMode.MainOnly, layout.ModeValue, "main only again");
            Assert.Eq(1, n.Titles.Count, "only the cancel notice");
            Directory.Delete(d, true);

            // 仅主屏预设，最终读数仍是仅主屏：拓扑没变，不重试
            string d2 = TempDir();
            WritePrefs(d2, PREFS_MAIN_ONLY);
            var layout2 = new FakeLayout(); layout2.ModeValue = LayoutMode.MainOnly; layout2.ThrowOnRelease = true; layout2.ThrowOnTake = true;
            var clock2 = new FakeClock();
            var o2 = Make(new FakeDdc(), layout2, new FakeRelay(), new FakeNotifier(), clock2, d2);
            o2.GiveToPeer();
            Assert.Eq("release,take", Seq(layout2), "cancelled");
            clock2.T += 30; Ticks(o2, 60);
            Assert.Eq("release,take", Seq(layout2), "main-only preset, still main only: nothing to restore");
            Directory.Delete(d2, true);

            // 双屏扩展预设、用户手动的仅主屏，最终读数仍是仅主屏：拓扑还是用户自己的，不去动它
            string d3 = TempDir();
            var layout3 = new FakeLayout(); // 启动时扩展，与预设一致
            var clock3 = new FakeClock();
            var o3 = Make(new FakeDdc(), layout3, new FakeRelay(), new FakeNotifier(), clock3, d3);
            layout3.ModeValue = LayoutMode.MainOnly; // 用户手动切成仅主屏
            layout3.ThrowOnRelease = true; layout3.ThrowOnTake = true;
            o3.GiveToPeer();
            Assert.Eq("release,take", Seq(layout3), "cancelled");
            clock3.T += 30; Ticks(o3, 60);
            Assert.Eq("release,take", Seq(layout3), "extend preset, user's own main-only unchanged: left alone");
            Directory.Delete(d3, true);
        }

        // 占有时用户手动改拓扑、没有失败记录：不干预（不做周期性对账）。
        public static void Test_Owned_ManualTopologyChange_NoIntervention()
        {
            string[] prefs = { null, null, PREFS_MAIN_ONLY, PREFS_MAIN_ONLY };
            LayoutMode[] start = { LayoutMode.Extend, LayoutMode.Extend, LayoutMode.MainOnly, LayoutMode.MainOnly };
            LayoutMode[] manual = { LayoutMode.Clone, LayoutMode.MainOnly, LayoutMode.Extend, LayoutMode.Clone };
            for (int i = 0; i < manual.Length; i++)
            {
                string d = TempDir();
                if (prefs[i] != null) WritePrefs(d, prefs[i]);
                var ddc = new FakeDdc();
                var layout = new FakeLayout(); layout.ModeValue = start[i];
                var n = new FakeNotifier();
                var clock = new FakeClock();
                var o = Make(ddc, layout, new FakeRelay(), n, clock, d);
                Assert.Eq(0, layout.Calls.Count, "startup consistent " + i);
                layout.ModeValue = manual[i];
                TimedTicks(o, clock, 200); // 100 秒
                Assert.Eq(0, layout.Calls.Count, "manual " + manual[i] + " left alone (" + i + ")");
                Assert.Eq(true, o.Owned, "still owned " + i);
                Assert.Eq(0, n.Titles.Count, "no notification " + i);
                Directory.Delete(d, true);
            }
        }

        // 拿回失败尚未恢复时不自动记住排列：此时的扩展排列不是本程序应用的（与 Mac 的 auto_save_check 一致）。
        public static void Test_AutoSave_SkippedWhileTakeFailing()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone; layout.ThrowOnTake = true;
            var clock = new FakeClock();
            var o = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), clock, d);
            ddc.DefaultRead = true;
            o.Tick(); o.Tick();
            Assert.Eq(true, o.Owned, "taken, layout failed");
            layout.ModeValue = LayoutMode.Extend; layout.CurSig = "B"; // 扩展了，但排列与快照 A 不同
            TimedTicks(o, clock, 80); // 40 秒：自动记住检查 4 次，拿回重试一直失败
            Assert.True(!Seq(layout).Contains("save"), "no auto-save while the take keeps failing: " + Seq(layout));
            Assert.True(Seq(layout).StartsWith("take,take"), "take retried meanwhile: " + Seq(layout));
            Assert.Eq(0, LogLines(d, "arrangement changed"), "no pending arrangement either");
            Directory.Delete(d, true);
        }

        // 失败记录属于当前归属的布局：切走前已在克隆、跳过让出时，拓扑已符合"未占有"，拿回失败的记录随之清除，不再重试。
        public static void Test_GiveMac_AlreadyCloned_ClearsTakeFailure()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone; layout.ThrowOnTake = true;
            var clock = new FakeClock();
            var o = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), clock, d);
            ddc.DefaultRead = true;
            o.Tick(); o.Tick();
            Assert.Eq("take", Seq(layout), "take failed, still cloned");
            ddc.OnWrite = delegate { layout.Calls.Add("ddc"); };
            ddc.ReadsThen(1, false); // 写后读失败：已切走
            o.GiveToPeer();
            Assert.Eq("take,ddc", Seq(layout), "already cloned: release skipped");
            Assert.Eq(false, o.Owned, "given away");
            ddc.DefaultRead = false;
            clock.T += 30; Ticks(o, 40);
            Assert.Eq("take,ddc", Seq(layout), "no retry: the not-owned layout (clone) is in place");
            Directory.Delete(d, true);
        }

        // 切走前开副屏：release() 报错、等待后拓扑读到 Missing → 也要试着扩展；最终仍不是克隆/扩展就取消，不写 DDC。
        static string GiveAfterReleaseThrows(bool extendThrows, bool stillMissingAfterExtend, out int writes, out string notices)
        {
            string d = TempDir();
            WritePrefs(d, PREFS_MAIN_ONLY);
            var ddc = new FakeDdc();
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.MainOnly;
            layout.ThrowOnRelease = true; layout.ThrowOnTake = extendThrows;
            layout.OnRelease = delegate { layout.ModeValue = LayoutMode.Missing; };
            if (stillMissingAfterExtend) layout.OnTake = delegate { layout.MissingModeReads = 1000; };
            var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, new FakeClock(), d);
            ddc.OnWrite = delegate { layout.Calls.Add("ddc"); };
            ddc.ReadsThen(1, true);  // 写前等到能读到
            ddc.ReadsThen(1, false); // 写后读失败：已切走
            o.GiveToPeer();
            writes = ddc.Writes.Count;
            notices = string.Join(";", n.Titles.ToArray());
            string s = Seq(layout) + "|owned=" + o.Owned;
            Directory.Delete(d, true);
            return s;
        }

        public static void Test_MainOnly_GiveMac_ReleaseThrows_MissingAfterWait_TriesExtend()
        {
            int writes; string notices;
            Assert.Eq("release,take|owned=True", GiveAfterReleaseThrows(true, false, out writes, out notices), "extend tried although Missing; extend failed -> cancel");
            Assert.Eq(0, writes, "extend failed: no DDC write");
            Assert.Eq("无法恢复副屏，已取消切换", notices, "extend failed: cancel notice");

            Assert.Eq("release,take|owned=True", GiveAfterReleaseThrows(false, true, out writes, out notices), "extend accepted but still Missing -> cancel");
            Assert.Eq(0, writes, "still Missing: no DDC write");
            Assert.Eq("无法恢复副屏，已取消切换", notices, "still Missing: cancel notice");

            Assert.Eq("release,take,ddc,release|owned=False", GiveAfterReleaseThrows(false, false, out writes, out notices), "extend worked -> give (extended, so release after)");
            Assert.Eq(1, writes, "extend worked: wrote once");
        }

        // release() 正常返回时维持原规则：即使 3 秒后仍读到 Missing 也相信调用已生效、继续切换，不去扩展。
        public static void Test_MainOnly_GiveMac_ReleaseOk_MissingThroughout_Proceeds()
        {
            string d = TempDir();
            WritePrefs(d, PREFS_MAIN_ONLY);
            var ddc = new FakeDdc();
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.MainOnly;
            layout.OnRelease = delegate { layout.MissingModeReads = 1000; };
            var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, new FakeClock(), d);
            ddc.OnWrite = delegate { layout.Calls.Add("ddc"); };
            ddc.ReadsThen(1, true); ddc.ReadsThen(1, false);
            o.GiveToPeer();
            Assert.Eq("release,ddc", Seq(layout), "no extend attempt, no cancel");
            Assert.Eq(false, o.Owned, "given away");
            Assert.Eq(0, n.Titles.Count, "no notice");
            Assert.Eq(1, LogLines(d, "after settling, proceeding"), "logged");
            Directory.Delete(d, true);
        }

        // 切走前的拓扑判断只读一次 Mode（克隆标记与是否需要开副屏出自同一次读数）。
        public static void Test_Give_ReadsModeOnceAtEntry()
        {
            string d = TempDir();
            var ddc = new FakeDdc();
            var layout = new FakeLayout();
            var o = Make(ddc, layout, new FakeRelay(), new FakeNotifier(), new FakeClock(), d);
            int before = layout.ModeReadCount;
            ddc.ReadsThen(1, false);
            o.GiveToPeer();
            Assert.Eq("release", Seq(layout), "extend: write, then release");
            Assert.Eq(1, layout.ModeReadCount - before, "Mode read once for the whole give");
            Directory.Delete(d, true);
        }

        // ---------------- 自动记住屏幕排列 ----------------

        public static void Test_AutoSave_NeedsTwoConsistentChecks()
        {
            string d = TempDir();
            var ddc = new FakeDdc();
            var layout = new FakeLayout(); layout.CurSig = "B"; // 快照是 A
            var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, new FakeClock(), d);
            Ticks(o, 39);
            Assert.Eq(0, layout.Calls.Count, "not saved after the first check");
            Assert.Eq(1, LogLines(d, "layout: arrangement changed, save if unchanged at next check"), "pending logged once");
            o.Tick(); // 第 40 拍：第二次比对一致
            Assert.Eq("save", Seq(layout), "saved at the second consistent check");
            Assert.Eq("B", layout.SnapSig, "snapshot updated");
            Assert.Eq(1, n.Titles.Count, "one notification");
            Assert.Eq("已自动记住新的屏幕排列", n.Titles[0], "auto-save notice");
            Assert.Eq("", n.Texts[0], "empty body");
            Ticks(o, 100);
            Assert.Eq(1, layout.Calls.Count, "no more saves once equal");
            Assert.Eq(1, LogLines(d, "arrangement changed"), "quiet while unchanged");
            Assert.Eq(1, LogLines(d, "auto-saved"), "saved logged once");
            Assert.Eq(true, o.Owned, "polling unaffected");
            Directory.Delete(d, true);
        }

        public static void Test_AutoSave_QuietWhenUnchanged()
        {
            string d = TempDir();
            var layout = new FakeLayout();
            var o = Make(new FakeDdc(), layout, new FakeRelay(), new FakeNotifier(), new FakeClock(), d);
            Ticks(o, 200);
            Assert.Eq(0, layout.Calls.Count, "nothing saved");
            Assert.Eq(0, LogLines(d, "arrangement"), "no arrangement log lines");
            Assert.Eq(0, LogLines(d, "auto-save"), "no auto-save log lines");
            Directory.Delete(d, true);
        }

        public static void Test_AutoSave_SignatureChangesAgain_Restarts()
        {
            string d = TempDir();
            var layout = new FakeLayout(); layout.CurSig = "B";
            var o = Make(new FakeDdc(), layout, new FakeRelay(), new FakeNotifier(), new FakeClock(), d);
            Ticks(o, 20); // 待定 B
            layout.CurSig = "C";
            Ticks(o, 20); // 第 40 拍：C 与待定 B 不同，重新计
            Assert.Eq(0, layout.Calls.Count, "not saved when the arrangement changed again");
            Ticks(o, 19);
            Assert.Eq(0, layout.Calls.Count, "still waiting");
            o.Tick(); // 第 60 拍
            Assert.Eq("save", Seq(layout), "saved after C held for two checks");
            Assert.Eq("C", layout.SnapSig, "snapshot is C");
            Directory.Delete(d, true);
        }

        public static void Test_AutoSave_BackToSaved_ClearsPending()
        {
            string d = TempDir();
            var layout = new FakeLayout(); layout.CurSig = "B";
            var o = Make(new FakeDdc(), layout, new FakeRelay(), new FakeNotifier(), new FakeClock(), d);
            Ticks(o, 20); // 待定 B
            layout.CurSig = "A";
            Ticks(o, 20); // 与快照一致：清空待定
            layout.CurSig = "B";
            Ticks(o, 20); // 第 60 拍：重新记下 B
            Assert.Eq(0, layout.Calls.Count, "pending was cleared, B needs two checks again");
            Ticks(o, 20);
            Assert.Eq("save", Seq(layout), "saved at 80");
            Directory.Delete(d, true);
        }

        public static void Test_AutoSave_NeverInCloneMainOnlyMissingOrNotOwned()
        {
            foreach (LayoutMode m in new LayoutMode[] { LayoutMode.Clone, LayoutMode.MainOnly, LayoutMode.Missing })
            {
                string d = TempDir();
                var layout = new FakeLayout(); layout.CurSig = "B";
                var n = new FakeNotifier();
                var o = Make(new FakeDdc(), layout, new FakeRelay(), n, new FakeClock(), d);
                layout.ModeValue = m; // 占有但拓扑不是双屏扩展
                Ticks(o, 80);
                Assert.Eq(0, layout.Calls.Count, "no save in " + m);
                Assert.Eq(0, n.Titles.Count, "no notice in " + m);
                Directory.Delete(d, true);
            }

            string d2 = TempDir();
            var ddc2 = new FakeDdc(); ddc2.Reads.Enqueue(false); ddc2.DefaultRead = false;
            var layout2 = new FakeLayout(); layout2.ModeValue = LayoutMode.Clone; layout2.CurSig = "B";
            var o2 = Make(ddc2, layout2, new FakeRelay(), new FakeNotifier(), new FakeClock(), d2);
            layout2.ModeValue = LayoutMode.Extend; // 未占有时即使拓扑是扩展也不保存
            Ticks(o2, 80);
            Assert.Eq(false, o2.Owned, "not owned");
            Assert.Eq(0, layout2.Calls.Count, "no save when not owned");
            Directory.Delete(d2, true);

            string d3 = TempDir();
            var layout3 = new FakeLayout(); layout3.Snapshot = false; layout3.CurSig = "B";
            var o3 = Make(new FakeDdc(), layout3, new FakeRelay(), new FakeNotifier(), new FakeClock(), d3);
            Ticks(o3, 80);
            Assert.Eq(0, layout3.Calls.Count, "no auto-save without a snapshot (manual save first)");
            Directory.Delete(d3, true);
        }

        public static void Test_AutoSave_OffInPrefs_NeverSaves()
        {
            string d = TempDir();
            WritePrefs(d, PREFS_AUTO_OFF);
            var layout = new FakeLayout(); layout.CurSig = "B";
            var o = Make(new FakeDdc(), layout, new FakeRelay(), new FakeNotifier(), new FakeClock(), d);
            Assert.Eq(false, o.AutoSaveLayout, "off from prefs.json");
            Ticks(o, 80);
            Assert.Eq(0, layout.Calls.Count, "no save when off");
            Directory.Delete(d, true);
        }

        public static void Test_AutoSave_ToggleOff_ClearsPendingAndStops()
        {
            string d = TempDir();
            var layout = new FakeLayout(); layout.CurSig = "B";
            var o = Make(new FakeDdc(), layout, new FakeRelay(), new FakeNotifier(), new FakeClock(), d);
            Ticks(o, 20); // 待定 B
            o.SetAutoSaveLayout(false);
            o.SetAutoSaveLayout(true); // 两次比对之间关了又开：开关本身清空待定
            Ticks(o, 20); // 第 40 拍：重新记下 B
            Assert.Eq(0, layout.Calls.Count, "pending restarted by the toggle");
            Ticks(o, 20);
            Assert.Eq("save", Seq(layout), "saved at 60");
            layout.CurSig = "C";
            o.SetAutoSaveLayout(false);
            Ticks(o, 80);
            Assert.Eq(1, layout.Calls.Count, "no save while off");
            Directory.Delete(d, true);
        }

        public static void Test_SaveLayout_ClearsPending()
        {
            string d = TempDir();
            var layout = new FakeLayout(); layout.CurSig = "B";
            var o = Make(new FakeDdc(), layout, new FakeRelay(), new FakeNotifier(), new FakeClock(), d);
            Ticks(o, 20); // 待定 B
            o.SaveLayout();
            Assert.Eq("save", Seq(layout), "manual save");
            layout.SnapSig = "A"; // 假设快照随后又与当前不同：待定已清空，需要重新两次比对
            Ticks(o, 20);
            Assert.Eq("save", Seq(layout), "no auto-save at 40: pending was cleared by the manual save");
            Ticks(o, 20);
            Assert.Eq("save,save", Seq(layout), "auto-saved at 60");
            Directory.Delete(d, true);
        }

        public static void Test_AutoSave_SaveFailure_LogsOnly()
        {
            string d = TempDir();
            var ddc = new FakeDdc();
            var layout = new FakeLayout(); layout.CurSig = "B"; layout.ThrowOnSave = true;
            var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, new FakeClock(), d);
            Ticks(o, 40);
            Assert.Eq("save", Seq(layout), "tried to save");
            Assert.Eq(0, n.Titles.Count, "no notification on failure");
            Assert.Eq(1, LogLines(d, "boom save"), "failure logged");
            int reads = ddc.ReadCount;
            Ticks(o, 4);
            Assert.True(ddc.ReadCount > reads, "polling continues");
            Assert.Eq(true, o.Owned, "still owned");
            Directory.Delete(d, true);
        }
    }
}
