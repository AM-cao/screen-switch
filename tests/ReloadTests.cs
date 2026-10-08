using System;
using System.IO;

namespace ScreenSwitch.Tests
{
    public static class ReloadTests
    {
        public static void Test_ReloadGate_DefersWhileBusy()
        {
            var g = new ReloadGate();
            Assert.Eq(true, g.Request(false), "idle: reload now");
            Assert.Eq(false, g.OnIdle(), "nothing pending");
            Assert.Eq(false, g.Request(true), "busy: later");
            Assert.Eq(false, g.Request(true), "still busy");
            Assert.Eq(true, g.OnIdle(), "done switching: reload once");
            Assert.Eq(false, g.OnIdle(), "only once");
        }

        public static void Test_ReloadCommand_FiresEvent_NotUnknown()
        {
            string d = Path.Combine(Path.GetTempPath(), "ss-" + Guid.NewGuid());
            Directory.CreateDirectory(d);
            var o = new Orchestrator(new FakeDdc(), new FakeLayout(), new FakeRelay(), new FakeNotifier(), new FakeClock(), TestCfg.Legacy(), new Log(Path.Combine(d, "t.log")), d);
            int fired = 0;
            o.ReloadRequested += delegate { fired++; };
            StateFile.WriteCommand(d, "reload");
            o.Tick();
            Assert.Eq(1, fired, "event fired");
            Assert.True(!File.ReadAllText(Path.Combine(d, "t.log")).Contains("unknown command"), "not logged as unknown");
            Directory.Delete(d, true);
        }

        static Orchestrator Reloaded(FakeDdc ddc, FakeLayout layout, FakeClock clock, string d, OwnershipHandover h)
        {
            return new Orchestrator(ddc, layout, new FakeRelay(), new FakeNotifier(), clock, TestCfg.Legacy(), new Log(Path.Combine(d, "t.log")), d, h);
        }

        // 重新加载接手旧编排器的归属：不读 DDC、不对账（不动布局），照常写状态文件
        public static void Test_Handover_KeepsOwned_NoReadNoLayout()
        {
            string d = Path.Combine(Path.GetTempPath(), "ss-" + Guid.NewGuid());
            Directory.CreateDirectory(d);
            var clock = new FakeClock();
            var oldOrch = new Orchestrator(new FakeDdc(), new FakeLayout(), new FakeRelay(), new FakeNotifier(), clock, TestCfg.Legacy(), new Log(Path.Combine(d, "t.log")), d);
            OwnershipHandover h = oldOrch.Handover();
            Assert.Eq(true, h.Owned, "old one owned");
            var ddc = new FakeDdc(); ddc.DefaultRead = false;
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone; // 启动对账会拿回
            var o = Reloaded(ddc, layout, clock, d, h);
            Assert.Eq(true, o.Owned, "owned kept");
            Assert.Eq(0, ddc.ReadCount, "no DDC read");
            Assert.Eq(0, layout.Calls.Count, "no reconcile");
            bool owned; long ts;
            Assert.True(StateFile.ReadState(d, out owned, out ts), "state written");
            Assert.Eq(true, owned, "state owned");
            Assert.True(File.ReadAllText(Path.Combine(d, "t.log")).Contains("orchestrator reloaded, owned=True"), "logged");

            var notOwned = new OwnershipHandover(); notOwned.Owned = false;
            var ddc2 = new FakeDdc(); var layout2 = new FakeLayout(); // 扩展 + 未占有：启动对账会让出
            o = Reloaded(ddc2, layout2, clock, d, notOwned);
            Assert.Eq(false, o.Owned, "not owned kept although DDC would read ok");
            Assert.Eq(0, ddc2.ReadCount, "no DDC read");
            Assert.Eq(0, layout2.Calls.Count, "no release");
            Directory.Delete(d, true);
        }

        // 宽限期跟着交接：切换刚结束就重新加载，显示器还在重新同步时的失败读数不让出
        public static void Test_Handover_GraceHonoured()
        {
            string d = Path.Combine(Path.GetTempPath(), "ss-" + Guid.NewGuid());
            Directory.CreateDirectory(d);
            var clock = new FakeClock();
            var h = new OwnershipHandover(); h.Owned = true; h.GraceUntil = clock.T + 8;
            var ddc = new FakeDdc(); ddc.DefaultRead = false;
            var layout = new FakeLayout();
            var o = Reloaded(ddc, layout, clock, d, h);
            for (int i = 0; i < 12; i++) o.Tick();
            Assert.Eq(true, o.Owned, "failures ignored during the handed-over grace");
            Assert.Eq(0, layout.Calls.Count, "no release during grace");
            Assert.Eq(h.GraceUntil, o.Handover().GraceUntil, "grace exported again");
            clock.T += 9;
            for (int i = 0; i < 8; i++) o.Tick();
            Assert.Eq(false, o.Owned, "released after grace when reads still fail");
            Assert.Eq("release", layout.Calls[0], "layout release");
            Directory.Delete(d, true);
        }

        // 布局失败的自愈状态跟着交接：拿回失败（例如 UAC 提示框期间）后 10 秒内重新加载，新编排器照常在
        // 失败 10 秒后重试拿回，不必等到下一次切换。
        public static void Test_Handover_KeepsLayoutRetry()
        {
            string d = Path.Combine(Path.GetTempPath(), "ss-" + Guid.NewGuid());
            Directory.CreateDirectory(d);
            var clock = new FakeClock();
            var ddc = new FakeDdc(); ddc.DefaultRead = true;
            var oldLayout = new FakeLayout(); oldLayout.ModeValue = LayoutMode.Clone; oldLayout.ThrowOnTake = true;
            var oldOrch = Reloaded(ddc, oldLayout, clock, d, null); // 启动对账拿回，失败
            Assert.Eq("take", string.Join(",", oldLayout.Calls.ToArray()), "startup take failed");
            clock.T += 3;
            OwnershipHandover h = oldOrch.Handover();
            Assert.Eq(true, h.LayoutFailed, "failure exported");
            Assert.Eq(clock.T - 3, h.LayoutFailAt, "failure time exported");

            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone; // 提示框关掉了，调用恢复正常
            var o = Reloaded(ddc, layout, clock, d, h);
            Assert.Eq(0, layout.Calls.Count, "no reconcile on reload");
            clock.T += 6;
            for (int i = 0; i < 8; i++) o.Tick();
            Assert.Eq(0, layout.Calls.Count, "no retry before 10 s since the failure");
            clock.T += 1;
            o.Tick();
            Assert.Eq("take", string.Join(",", layout.Calls.ToArray()), "new orchestrator retried the take");
            Assert.Eq(LayoutMode.Extend, layout.ModeValue, "extended");
            Assert.Eq(false, o.Handover().LayoutFailed, "recovered");
            clock.T += 60;
            for (int i = 0; i < 100; i++) o.Tick();
            Assert.Eq(1, layout.Calls.Count, "no more retries after success");

            // 推迟中的重试（缺屏）也跟着交接：拓扑恢复时只记一次"恢复"日志
            var h2 = new OwnershipHandover(); h2.Owned = true; h2.LayoutFailed = true; h2.LayoutFailAt = clock.T; h2.RetryDeferred = true;
            Assert.Eq(true, Reloaded(ddc, new FakeLayout(), clock, d, h2).Handover().RetryDeferred, "deferral exported again");
            Directory.Delete(d, true);
        }
    }
}
