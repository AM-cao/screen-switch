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
    }
}
