using System;
using System.IO;

namespace ScreenSwitch.Tests
{
    // 配对测试（设计文档 12 第 6 条）：两个 Orchestrator 共用一块假显示器，各自用真的 SshRelay，
    // 假的 ssh 把远端命令原样交给另一方的 ssh-gate（Cli.SshGate）。用它验证两台 Windows 互相切换——没有真机可测。
    public static class PairTests
    {
        const uint A_CODE = 0x10, B_CODE = 0x0F, SWITCH = 0x11;

        class Side
        {
            public string Name;
            public string Dir;
            public Config Cfg = new Config();
            public PortDdc Ddc;
            public FakeLayout Layout = new FakeLayout();
            public FakeNotifier N = new FakeNotifier();
            public Orchestrator O;
            public Side Other;
            public FakeClock Clock;
            public int SshCalls;
            public string LogText() { return File.ReadAllText(Path.Combine(Dir, "screenswitch.log")); }

            // 假的 ssh：取出引号里的远端命令，交给对方的 ssh-gate，标准输出就是它打印的内容
            public int Ssh(string args, int timeoutMs, out string stdout, out string stderr)
            {
                SshCalls++;
                stderr = "";
                int q = args.IndexOf(" \"");
                string remote = args.Substring(q + 2, args.Length - q - 3).Replace("\\\"", "\"");
                TextWriter saved = Console.Out;
                var captured = new StringWriter();
                Console.SetOut(captured);
                try { return Cli.SshGate(remote, Other.Dir, Clock.Now()); }
                finally { Console.SetOut(saved); stdout = captured.ToString(); }
            }
        }

        class Pair
        {
            public FakeMonitor Mon = new FakeMonitor();
            public FakeClock Clock = new FakeClock();
            public Side A, B;

            // 两边的托盘程序各走几拍（定时器每 0.5 秒一拍）
            public void Settle()
            {
                for (int i = 0; i < 8; i++) { A.O.Tick(); B.O.Tick(); }
            }

            public void Cleanup()
            {
                Directory.Delete(A.Dir, true);
                Directory.Delete(B.Dir, true);
            }
        }

        static Side MakeSide(Pair p, string name, uint self, string peerName, string peerUser, uint peerCode)
        {
            var s = new Side();
            s.Name = name; s.Clock = p.Clock;
            s.Dir = Path.Combine(Path.GetTempPath(), "ss-pair-" + Guid.NewGuid());
            Directory.CreateDirectory(s.Dir);
            s.Ddc = new PortDdc(p.Mon, self);
            s.Cfg.SelfName = name; s.Cfg.SelfCode = self;
            s.Cfg.PeerName = peerName; s.Cfg.PeerOs = "windows"; s.Cfg.PeerHost = "127.0.0.1"; s.Cfg.PeerUser = peerUser;
            s.Cfg.PeerCode = peerCode;
            s.Cfg.Devices.Add(new Device("Switch", SWITCH));
            return s;
        }

        // 主屏一开始在 A 上。bKnowsACode=false：B 的配置里没有 A 的编码，要用 request info 去问。
        static Pair Make(bool answersEverywhere, bool bKnowsACode)
        {
            var p = new Pair();
            p.Mon.Input = A_CODE; p.Mon.AnswersEverywhere = answersEverywhere;
            // 对方的程序路径按 peer.os / peer.user 推导；一边的用户名带空格
            p.A = MakeSide(p, "书房 台式机", A_CODE, "笔记本", "bob", B_CODE);
            p.B = MakeSide(p, "笔记本", B_CODE, "书房 台式机", "John Smith", bKnowsACode ? A_CODE : 0);
            p.A.Other = p.B; p.B.Other = p.A;
            p.B.Layout.ModeValue = LayoutMode.Clone;
            foreach (Side s in new Side[] { p.A, p.B })
            {
                // ssh-gate 从对方安装目录下的 config.json 读配置
                s.Cfg.Save(Path.Combine(s.Dir, "config.json"));
                var log = new Log(Path.Combine(s.Dir, "screenswitch.log"));
                var relay = new SshRelay(s.Cfg, log, s.Dir, s.Ssh);
                s.O = new Orchestrator(s.Ddc, s.Layout, relay, s.N, p.Clock, s.Cfg, log, s.Dir);
            }
            return p;
        }

        static void AssertOwner(Pair p, Side owner, string when)
        {
            uint code = owner == p.A ? A_CODE : B_CODE;
            Assert.Eq(code, p.Mon.Input, when + ": monitor input");
            Assert.Eq(owner == p.A, p.A.O.Owned, when + ": A owned");
            Assert.Eq(owner == p.B, p.B.O.Owned, when + ": B owned");
            Assert.Eq(owner == p.A ? LayoutMode.Extend : LayoutMode.Clone, p.A.Layout.ModeValue, when + ": A layout");
            Assert.Eq(owner == p.B ? LayoutMode.Extend : LayoutMode.Clone, p.B.Layout.ModeValue, when + ": B layout");
        }

        static string Last(FakeNotifier n) { return n.Titles.Count == 0 ? "" : n.Titles[n.Titles.Count - 1]; }

        // 切给对方 → 对方把它拿回去 → 反方向再来一遍 → 切给设备 → 拿回。显示器只认活跃口。
        public static void Test_TwoWindows_ActiveOnlyMonitor_FullRoundTrip()
        {
            Pair p = Make(false, true);
            AssertOwner(p, p.A, "start");

            p.A.O.GiveToPeer();
            p.Settle();
            AssertOwner(p, p.B, "A gave to B");
            Assert.True(p.B.LogText().Contains("request take: accepted, pre-take written"), "B was told to pre-take");
            Assert.Eq(0, p.A.N.Titles.Count + p.B.N.Titles.Count, "no notices so far");

            // A 把主屏拿回来：直接写入被忽略，B 收到 give peer 后代发
            p.A.O.TakeBack();
            Assert.Eq("已请求 笔记本 交还主屏", Last(p.A.N), "A's notice names the peer");
            p.Settle();
            AssertOwner(p, p.A, "A took back");
            Assert.True(p.B.LogText().Contains("request give peer: accepted"), "B's gate accepted give peer");
            Assert.True(p.B.LogText().Contains("command: give peer"), "B's tray executed give peer");
            Assert.True(p.A.LogText().Contains("request take: already owned"), "A answered B's request take");

            // 反方向：B 拿回（B 的用户名带空格，A 的 ssh-gate 照样认它发来的程序路径）
            p.B.O.TakeBack();
            p.Settle();
            AssertOwner(p, p.B, "B took back");
            Assert.True(p.A.LogText().Contains("request give peer: accepted"), "A's gate accepted give peer");

            // B 占有，切给 Switch（有信号，不跳回）
            p.B.O.GiveToDevice(SWITCH);
            p.Settle();
            Assert.Eq(SWITCH, p.Mon.Input, "monitor on the device");
            Assert.Eq(false, p.A.O.Owned || p.B.O.Owned, "nobody owns");
            Assert.Eq(LayoutMode.Clone, p.B.Layout.ModeValue, "B released");

            // 主屏在设备上、显示器只认活跃口：谁也切不回来，提示按显示器按钮
            int before = p.A.N.Titles.Count;
            p.A.O.TakeBack();
            Assert.Eq("请用显示器上的按钮切换输入源", Last(p.A.N), "hint");
            Assert.Eq(before + 1, p.A.N.Titles.Count, "one notice");
            Assert.True(p.B.LogText().Contains("request give peer: not owned"), "B said not owned");

            // 用按钮切回 A：两边跟随
            p.Mon.Input = A_CODE;
            p.Settle();
            AssertOwner(p, p.A, "button back to A");
            p.Cleanup();
        }

        // 未占有时切给设备：请占有的一方用 give code <n> 代发
        public static void Test_TwoWindows_GiveDeviceThroughPeer()
        {
            Pair p = Make(false, true);
            p.A.O.GiveToPeer();
            p.Settle();
            AssertOwner(p, p.B, "A gave to B");

            p.A.O.GiveToDevice(SWITCH);
            Assert.Eq("已请求 笔记本 切给 Switch", Last(p.A.N), "A's notice");
            p.Settle();
            Assert.Eq(SWITCH, p.Mon.Input, "B switched the monitor to the device");
            Assert.True(p.B.LogText().Contains("request give code 17: accepted"), "B's gate accepted give code");
            Assert.True(p.B.LogText().Contains("command: give code 17"), "B's tray executed it");
            Assert.Eq(false, p.B.O.Owned, "B released");

            // 对方的设备列表里没有这个编码：unknown device（1），提示按按钮
            p.Mon.Input = B_CODE; p.Settle();
            p.A.O.GiveToDevice(0x12);
            Assert.Eq("请用显示器上的按钮切换输入源", Last(p.A.N), "hint after unknown device");
            Assert.True(p.B.LogText().Contains("request give code 18: unknown device"), "B answered unknown device");
            p.Cleanup();
        }

        // 显示器在非活跃口上也应答：拿回、切给设备都能直接切，不靠对方
        public static void Test_TwoWindows_MonitorAnswersEverywhere_DirectSwitches()
        {
            Pair p = Make(true, true);
            p.A.O.GiveToPeer();
            p.Settle();
            AssertOwner(p, p.B, "A gave to B");

            p.B.O.GiveToDevice(SWITCH);
            p.Settle();
            Assert.Eq(SWITCH, p.Mon.Input, "on the device");
            Assert.Eq(false, p.A.O.Owned || p.B.O.Owned, "nobody owns although both still read the monitor");

            // 主屏在设备上，A 直接写自己的编码拿回；B 回答 not owned
            int notices = p.A.N.Titles.Count;
            p.A.O.TakeBack();
            p.Settle();
            AssertOwner(p, p.A, "A took back directly");
            Assert.Eq(notices, p.A.N.Titles.Count, "no notice");

            // B 未占有，直接把主屏切给设备；A 同时也被请求代发
            p.B.O.GiveToDevice(SWITCH);
            p.Settle();
            Assert.Eq(SWITCH, p.Mon.Input, "B switched to the device");
            Assert.Eq(false, p.A.O.Owned, "A released by following the input code");
            Assert.Eq(LayoutMode.Clone, p.A.Layout.ModeValue, "A layout released");
            p.Cleanup();
        }

        // B 不知道 A 的编码：切给对方时先用 request info 问到并写回配置
        public static void Test_TwoWindows_PeerCodeLearnedThroughInfo()
        {
            Pair p = Make(false, false);
            p.A.O.GiveToPeer();
            p.Settle();
            AssertOwner(p, p.B, "A gave to B");

            p.B.O.GiveToPeer();
            p.Settle();
            AssertOwner(p, p.A, "B gave back after learning A's code");
            Assert.Eq(A_CODE, p.B.Cfg.PeerCode, "learned");
            Assert.Eq(A_CODE, Config.Load(Path.Combine(p.B.Dir, "config.json")).PeerCode, "written to B's config.json");
            Assert.Eq(0, p.B.N.Titles.Count, "no notice");
            p.Cleanup();
        }
    }
}
