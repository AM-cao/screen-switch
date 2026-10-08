using System;
using System.IO;
using System.Threading;

namespace ScreenSwitch.Tests
{
    public static class AfterTakeTests
    {
        static Log TempLog() { return new Log(Path.Combine(Path.GetTempPath(), "ss-after-" + Guid.NewGuid() + ".log")); }

        public static void Test_AfterTake_EmptyCommandNeverRuns()
        {
            int runs = 0;
            var r = new AfterTakeRunner("  ", 0, TempLog(), delegate(string c) { Interlocked.Increment(ref runs); return 0; });
            r.Schedule();
            Thread.Sleep(150);
            Assert.Eq(0, runs, "no command, no run");
        }

        public static void Test_AfterTake_RunsCommandAfterDelay()
        {
            int runs = 0; string got = null;
            var r = new AfterTakeRunner("do it", 50, TempLog(), delegate(string c) { got = c; Interlocked.Increment(ref runs); return 0; });
            r.Schedule();
            Assert.Eq(0, runs, "not run synchronously");
            Thread.Sleep(600);
            Assert.Eq(1, runs, "ran once");
            Assert.Eq("do it", got, "command passed through");
        }

        // 延迟期内的多次拿回合并成一次；之后的拿回再执行一次
        public static void Test_AfterTake_CoalescesWithinDelay()
        {
            int runs = 0;
            var r = new AfterTakeRunner("x", 300, TempLog(), delegate(string c) { Interlocked.Increment(ref runs); return 0; });
            r.Schedule(); Thread.Sleep(50); r.Schedule(); Thread.Sleep(50); r.Schedule();
            Thread.Sleep(900);
            Assert.Eq(1, runs, "three takes within the delay run once");
            r.Schedule();
            Thread.Sleep(900);
            Assert.Eq(2, runs, "a later take runs again");
        }

        public static void Test_AfterTake_RunnerExceptionIsSwallowed()
        {
            var r = new AfterTakeRunner("x", 0, TempLog(), delegate(string c) { throw new InvalidOperationException("boom"); });
            r.Schedule();
            Thread.Sleep(200);
        }

        public static void Test_Config_AfterTakeDefaultsAndJson()
        {
            var def = new Config();
            Assert.Eq("", def.AfterTakeCommand, "default: no command");
            Assert.Eq(3000, def.AfterTakeDelayMs, "default delay");
            string p = Path.Combine(Path.GetTempPath(), "ss-cfg-" + Guid.NewGuid() + ".json");
            File.WriteAllText(p, "{ \"afterTake\": { \"command\": \"schtasks /run /tn X\", \"delayMs\": 1500 } }");
            var c = Config.Load(p);
            Assert.Eq("schtasks /run /tn X", c.AfterTakeCommand, "command");
            Assert.Eq(1500, c.AfterTakeDelayMs, "delay");
            File.Delete(p);
        }

        // 编排器：拿回成功才回调 AfterTake，失败与让出都不回调
        public static void Test_Orchestrator_AfterTakeFiresOnSuccessfulTakeOnly()
        {
            string d = Path.Combine(Path.GetTempPath(), "ss-" + Guid.NewGuid());
            Directory.CreateDirectory(d);
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone;
            var o = new Orchestrator(ddc, layout, new FakeRelay(), new FakeNotifier(), new FakeClock(), TestCfg.Legacy(), new Log(Path.Combine(d, "t.log")), d);
            int fired = 0; o.AfterTake = delegate { fired++; };
            layout.ThrowOnTake = true;
            o.HandleCommand("pre-take");
            Assert.Eq(0, fired, "failed take does not fire");
            Directory.Delete(d, true);

            d = Path.Combine(Path.GetTempPath(), "ss-" + Guid.NewGuid());
            Directory.CreateDirectory(d);
            ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone;
            o = new Orchestrator(ddc, layout, new FakeRelay(), new FakeNotifier(), new FakeClock(), TestCfg.Legacy(), new Log(Path.Combine(d, "t.log")), d);
            fired = 0; o.AfterTake = delegate { fired++; };
            o.HandleCommand("pre-take");
            Assert.Eq("take", layout.Calls[layout.Calls.Count - 1], "took");
            Assert.Eq(1, fired, "successful take fires once");
            Directory.Delete(d, true);
        }
    }
}
