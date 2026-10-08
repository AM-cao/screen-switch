using System;
using System.Collections.Generic;
using System.IO;

namespace ScreenSwitch.Tests
{
    // 几台设备共用的假显示器。Input 是当前输入口的编码。
    class FakeMonitor
    {
        public uint Input;
        // 在非活跃口上也应答 DDC（读得到当前输入口，写入生效）；否则只认活跃口，非活跃口的读写都失败
        public bool AnswersEverywhere;
        // 非活跃口的写入报告成功，但显示器不理会
        public bool SwallowsInactiveWrites;
    }

    // 接在假显示器某个输入口上的电脑看到的 DDC
    class PortDdc : IDdc
    {
        readonly FakeMonitor mon; readonly uint port;
        public List<uint> Writes = new List<uint>();
        public PortDdc(FakeMonitor mon, uint port) { this.mon = mon; this.port = port; }
        bool Reachable { get { return mon.Input == port || mon.AnswersEverywhere; } }
        public bool TryReadInput(out uint value)
        {
            value = Reachable ? mon.Input : 0;
            return Reachable;
        }
        public bool WriteInput(uint value)
        {
            Writes.Add(value);
            if (Reachable) { mon.Input = value; return true; }
            return mon.SwallowsInactiveWrites;
        }
        public string Describe() { return "port 0x" + port.ToString("X"); }
    }

    // 设计文档 6.3：先直接切，再请对方代发。每条路径：显示器接受 / 忽略直接写入 × 对方接受 / 拒绝 / 未配置。
    public static class DirectTakeTests
    {
        const uint SELF = 0x10, PEER = 0x0F, SWITCH = 0x11;

        class Rig
        {
            public string Dir;
            public FakeMonitor Mon = new FakeMonitor();
            public PortDdc Ddc;
            public FakeLayout Layout = new FakeLayout();
            public FakeRelay Relay = new FakeRelay();
            public FakeNotifier N = new FakeNotifier();
            public FakeClock Clock = new FakeClock();
            public Config Cfg = new Config();
            public Orchestrator O;
        }

        // 主屏此刻在 input 上；peer：是否配置了对方
        static Rig Make(uint input, bool answersEverywhere, bool peer)
        {
            var r = new Rig();
            r.Dir = Path.Combine(Path.GetTempPath(), "ss-" + Guid.NewGuid());
            Directory.CreateDirectory(r.Dir);
            r.Mon.Input = input; r.Mon.AnswersEverywhere = answersEverywhere;
            r.Ddc = new PortDdc(r.Mon, SELF);
            r.Cfg.SelfCode = SELF; r.Cfg.PeerName = "MacBook"; r.Cfg.PeerCode = PEER;
            if (peer) { r.Cfg.PeerHost = "mac.local"; r.Cfg.PeerUser = "u"; }
            r.Cfg.Devices.Add(new Device("Switch", SWITCH));
            if (input != SELF) r.Layout.ModeValue = LayoutMode.Clone;
            return r;
        }

        static void Start(Rig r)
        {
            r.O = new Orchestrator(r.Ddc, r.Layout, r.Relay, r.N, r.Clock, r.Cfg, new Log(Path.Combine(r.Dir, "t.log")), r.Dir);
        }

        static string Seq(FakeLayout l) { return string.Join(",", l.Calls.ToArray()); }

        // ---- 拿回主屏 ----

        public static void Test_TakeBack_MonitorAccepts_NoPeer_TakesQuietly()
        {
            Rig r = Make(PEER, true, false); Start(r);
            Assert.Eq(false, r.O.Owned, "starts not owned");
            r.O.TakeBack();
            Assert.Eq(SELF, r.Ddc.Writes[0], "own code written directly");
            Assert.Eq(SELF, r.Mon.Input, "monitor switched");
            Assert.Eq(true, r.O.Owned, "owned");
            Assert.Eq("take", Seq(r.Layout), "layout restored");
            Assert.Eq(0, r.N.Titles.Count, "no notice");
            Assert.Eq(0, r.Relay.Requests.Count, "no relay without a peer");
            Assert.True(r.Clock.Slept <= 3000, "confirmed within 3 s: " + r.Clock.Slept);
            Directory.Delete(r.Dir, true);
        }

        public static void Test_TakeBack_MonitorAccepts_PeerRejects_TakesQuietly()
        {
            Rig r = Make(PEER, true, true); Start(r);
            r.Relay.Result = 255;
            r.O.TakeBack();
            Assert.Eq("peer", string.Join(",", r.Relay.Requests.ToArray()), "peer asked once");
            Assert.Eq(true, r.O.Owned, "owned through the direct write");
            Assert.Eq("take", Seq(r.Layout), "layout restored");
            Assert.Eq(0, r.N.Titles.Count, "no notice");
            Directory.Delete(r.Dir, true);
        }

        public static void Test_TakeBack_MonitorAccepts_PeerAccepts_PreTakesAndNotifies()
        {
            Rig r = Make(PEER, true, true); Start(r);
            r.O.TakeBack();
            Assert.Eq(SELF, r.Ddc.Writes[0], "own code written first");
            Assert.Eq(true, r.O.Owned, "owned");
            Assert.Eq("take", Seq(r.Layout), "layout restored once");
            Assert.Eq("已请求 MacBook 交还主屏", r.N.Titles[0], "notice");
            Directory.Delete(r.Dir, true);
        }

        public static void Test_TakeBack_MonitorIgnores_NoPeer_HintsButton()
        {
            Rig r = Make(PEER, false, false); Start(r);
            r.O.TakeBack();
            Assert.Eq(1, r.Ddc.Writes.Count, "direct write attempted");
            Assert.Eq(PEER, r.Mon.Input, "monitor did not switch");
            Assert.Eq(false, r.O.Owned, "not owned");
            Assert.Eq(0, r.Layout.Calls.Count, "no layout change");
            Assert.Eq("请用显示器上的按钮切换输入源", r.N.Titles[0], "hint");
            Assert.Eq("没有配置对方电脑", r.N.Texts[0], "reason");
            Assert.Eq(0, r.Clock.Slept, "the write itself failed: no 3 s wait");
            Directory.Delete(r.Dir, true);
        }

        // 直接切换从没成功过、且已经连续 3 次等不到结果：之后不再等（仍然写入），提示不再晚 3 秒。
        // 这份记录存在文件里，重启后仍然有效；删掉文件（重新运行设置）后重新开始等。
        public static void Test_TakeBack_NeverWorked_StopsWaitingAfterThreeMisses()
        {
            Rig r = Make(PEER, false, false); r.Mon.SwallowsInactiveWrites = true; Start(r);
            for (int i = 0; i < 3; i++) r.O.TakeBack();
            Assert.Eq(9000, r.Clock.Slept, "waited 3 s each of the first three times");
            r.O.TakeBack();
            Assert.Eq(9000, r.Clock.Slept, "fourth time: no wait");
            Assert.Eq(4, r.Ddc.Writes.Count, "still written every time");
            Assert.Eq(4, r.N.Titles.Count, "hint every time");

            Start(r); // 重启托盘程序
            r.O.TakeBack();
            Assert.Eq(9000, r.Clock.Slept, "record survives a restart");

            File.Delete(Path.Combine(r.Dir, "direct-take.txt")); // 重新运行设置会清掉这份记录
            Start(r);
            r.O.TakeBack();
            Assert.Eq(12000, r.Clock.Slept, "waits again after the record is cleared");
            Directory.Delete(r.Dir, true);
        }

        // 成功过一次就一直等
        public static void Test_TakeBack_WorkedOnce_KeepsWaiting()
        {
            Rig r = Make(PEER, true, false); Start(r);
            r.O.TakeBack();
            Assert.Eq(true, r.O.Owned, "direct switch worked");
            r.Mon.AnswersEverywhere = false; r.Mon.SwallowsInactiveWrites = true; r.Mon.Input = PEER;
            r.Clock.T += 10; // 过了拿回后的稳定宽限期
            for (int i = 0; i < 8; i++) r.O.Tick();
            Assert.Eq(false, r.O.Owned, "switched away again");
            int before = r.Clock.Slept;
            for (int i = 0; i < 5; i++) r.O.TakeBack();
            Assert.Eq(before + 15000, r.Clock.Slept, "still waits 3 s every time");
            Directory.Delete(r.Dir, true);
        }

        public static void Test_DirectRecord_FileFormat()
        {
            string d = Path.Combine(Path.GetTempPath(), "ss-" + Guid.NewGuid());
            Directory.CreateDirectory(d);
            var rec = new DirectRecord(d);
            Assert.Eq(true, rec.ShouldWait, "no record: wait");
            rec.Missed(); rec.Missed();
            Assert.Eq(true, new DirectRecord(d).ShouldWait, "two misses: still wait");
            rec.Missed();
            Assert.Eq(false, new DirectRecord(d).ShouldWait, "three misses: stop waiting");
            rec.Succeeded();
            Assert.Eq(true, new DirectRecord(d).ShouldWait, "worked once: wait");
            rec.Missed(); rec.Missed(); rec.Missed(); rec.Missed();
            Assert.Eq(true, new DirectRecord(d).ShouldWait, "misses after a success do not count");
            File.WriteAllText(Path.Combine(d, "direct-take.txt"), "garbage");
            Assert.Eq(true, new DirectRecord(d).ShouldWait, "unreadable record: start over");
            Directory.Delete(d, true);
        }

        // 写入报告成功但显示器不理会：等满 3 秒再提示
        public static void Test_TakeBack_MonitorSwallowsWrite_WaitsThenHints()
        {
            Rig r = Make(PEER, false, false); r.Mon.SwallowsInactiveWrites = true; Start(r);
            r.O.TakeBack();
            Assert.Eq(false, r.O.Owned, "not owned");
            Assert.Eq(3000, r.Clock.Slept, "waited 3 s");
            Assert.Eq("请用显示器上的按钮切换输入源", r.N.Titles[0], "hint");
            Directory.Delete(r.Dir, true);
        }

        public static void Test_TakeBack_MonitorIgnores_PeerRejects_HintsWithReason()
        {
            Rig r = Make(SWITCH, false, true); Start(r);
            r.Relay.Result = 2;
            r.O.TakeBack();
            Assert.Eq(false, r.O.Owned, "not owned");
            Assert.Eq("请用显示器上的按钮切换输入源", r.N.Titles[0], "hint");
            Assert.Eq("主屏在其他设备上，或 MacBook 不在线（2）", r.N.Texts[0], "reason names the peer");
            Directory.Delete(r.Dir, true);
        }

        public static void Test_TakeBack_MonitorIgnores_PeerAccepts_SameAsBefore()
        {
            Rig r = Make(PEER, false, true); Start(r);
            r.O.TakeBack();
            Assert.Eq(true, r.O.Owned, "pre-take: owned right away");
            Assert.Eq("take", Seq(r.Layout), "layout restored");
            Assert.Eq("已请求 MacBook 交还主屏", r.N.Titles[0], "notice");
            Directory.Delete(r.Dir, true);
        }

        public static void Test_TakeBack_DirectTakeOff_NoWrite()
        {
            Rig r = Make(PEER, true, true); r.Cfg.DirectTake = false; Start(r);
            r.Relay.Result = 255;
            r.O.TakeBack();
            Assert.Eq(0, r.Ddc.Writes.Count, "no direct write");
            Assert.Eq(0, r.Clock.Slept, "no wait");
            Assert.Eq("请用显示器上的按钮切换输入源", r.N.Titles[0], "hint");
            Directory.Delete(r.Dir, true);
        }

        public static void Test_TakeBack_SelfCodeUnknown_NoWrite()
        {
            Rig r = Make(PEER, false, true); r.Cfg.SelfCode = 0; Start(r);
            r.Relay.Result = 255;
            r.O.TakeBack();
            Assert.Eq(0, r.Ddc.Writes.Count, "nothing to write");
            Assert.Eq("请用显示器上的按钮切换输入源", r.N.Titles[0], "hint");
            Directory.Delete(r.Dir, true);
        }

        // ---- 未占有时切给设备 ----

        public static void Test_GiveDevice_MonitorAccepts_NoPeer_SwitchesQuietly()
        {
            Rig r = Make(PEER, true, false); Start(r);
            r.O.GiveToDevice(SWITCH);
            Assert.Eq(SWITCH, r.Mon.Input, "monitor switched to the device");
            Assert.Eq(0, r.N.Titles.Count, "no notice");
            Assert.Eq(0, r.Relay.Requests.Count, "no relay without a peer");
            Assert.Eq(false, r.O.Owned, "still not owned");
            Directory.Delete(r.Dir, true);
        }

        public static void Test_GiveDevice_MonitorAccepts_PeerRejects_SwitchesQuietly()
        {
            Rig r = Make(PEER, true, true); Start(r);
            r.Relay.Result = 2;
            r.O.GiveToDevice(SWITCH);
            Assert.Eq("code 17", string.Join(",", r.Relay.Requests.ToArray()), "peer asked");
            Assert.Eq(SWITCH, r.Mon.Input, "monitor switched by the direct write");
            Assert.Eq(0, r.N.Titles.Count, "no notice");
            Directory.Delete(r.Dir, true);
        }

        public static void Test_GiveDevice_MonitorAccepts_PeerAccepts_Notifies()
        {
            Rig r = Make(PEER, true, true); Start(r);
            r.O.GiveToDevice(SWITCH);
            Assert.Eq(SWITCH, r.Ddc.Writes[0], "device code written first");
            Assert.Eq("已请求 MacBook 切给 Switch", r.N.Titles[0], "notice");
            Directory.Delete(r.Dir, true);
        }

        public static void Test_GiveDevice_MonitorIgnores_NoPeer_HintsButton()
        {
            Rig r = Make(PEER, false, false); Start(r);
            r.O.GiveToDevice(SWITCH);
            Assert.Eq(PEER, r.Mon.Input, "monitor did not switch");
            Assert.Eq("请用显示器上的按钮切换输入源", r.N.Titles[0], "hint");
            Assert.Eq("没有配置对方电脑", r.N.Texts[0], "reason");
            Directory.Delete(r.Dir, true);
        }

        public static void Test_GiveDevice_MonitorIgnores_PeerRejects_HintsButton()
        {
            Rig r = Make(PEER, false, true); Start(r);
            r.Relay.Result = 3;
            r.O.GiveToDevice(SWITCH);
            Assert.Eq("请用显示器上的按钮切换输入源", r.N.Titles[0], "hint");
            Assert.Eq("主屏在其他设备上，或 MacBook 不在线（3）", r.N.Texts[0], "reason");
            Directory.Delete(r.Dir, true);
        }

        public static void Test_GiveDevice_MonitorIgnores_PeerAccepts_Notifies()
        {
            Rig r = Make(PEER, false, true); Start(r);
            r.O.GiveToDevice(SWITCH);
            Assert.Eq("已请求 MacBook 切给 Switch", r.N.Titles[0], "notice");
            Directory.Delete(r.Dir, true);
        }

        // ---- 切给对方时 peer.code 未知 ----

        public static void Test_GivePeer_CodeUnknown_LearnsFromInfoAndSaves()
        {
            Rig r = Make(SELF, false, true); r.Cfg.PeerCode = 0; Start(r);
            var info = new PeerInfo(); info.Proto = 3; info.Os = "mac"; info.Code = PEER;
            r.Relay.Info = info;
            r.O.GiveToPeer();
            Assert.Eq(1, r.Relay.InfoRequests, "asked the peer");
            Assert.Eq(PEER, r.Cfg.PeerCode, "code learned");
            Assert.Eq(PEER, Config.Load(Path.Combine(r.Dir, "config.json")).PeerCode, "written back to the config file");
            Assert.Eq(PEER, r.Mon.Input, "switched to the peer");
            Assert.Eq(false, r.O.Owned, "given away");
            Assert.Eq(0, r.N.Titles.Count, "no notice");
            Directory.Delete(r.Dir, true);
        }

        public static void Test_GivePeer_CodeUnknown_InfoFails_AsksToRunSetup()
        {
            Rig r = Make(SELF, false, true); r.Cfg.PeerCode = 0; Start(r);
            r.O.GiveToPeer();
            Assert.Eq(1, r.Relay.InfoRequests, "asked the peer");
            Assert.Eq(0, r.Ddc.Writes.Count, "nothing written");
            Assert.Eq(0, r.Relay.TakeRequests, "no request take before the code is known");
            Assert.Eq(true, r.O.Owned, "still owned");
            Assert.Eq("不知道 MacBook 接在哪个输入口", r.N.Titles[0], "notice");
            Assert.Eq("请运行设置", r.N.Texts[0], "notice body");
            Assert.Eq(false, File.Exists(Path.Combine(r.Dir, "config.json")), "config not written");
            Directory.Delete(r.Dir, true);
        }

        public static void Test_GivePeer_CodeKnown_NoInfoRequest()
        {
            Rig r = Make(SELF, false, true); Start(r);
            r.O.GiveToPeer();
            Assert.Eq(0, r.Relay.InfoRequests, "no info request");
            Assert.Eq(PEER, r.Mon.Input, "switched");
            Directory.Delete(r.Dir, true);
        }
    }
}
