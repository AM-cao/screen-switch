using System;
using System.IO;

namespace ScreenSwitch.Tests
{
    // 命令文件的内容（设计文档 5.1）：give peer、give code <n>、give self、pre-take；旧内容照常认。
    public static class CommandFileTests
    {
        static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "ss-" + Guid.NewGuid());
            Directory.CreateDirectory(d);
            return d;
        }

        static Orchestrator Make(FakeDdc ddc, FakeLayout layout, FakeRelay relay, string dir)
        {
            return new Orchestrator(ddc, layout, relay, new FakeNotifier(), new FakeClock(), TestCfg.Legacy(), new Log(Path.Combine(dir, "t.log")), dir);
        }

        static void GivePeerCase(string command)
        {
            string d = TempDir();
            var ddc = new FakeDdc(); var layout = new FakeLayout(); var relay = new FakeRelay();
            var o = Make(ddc, layout, relay, d);
            ddc.ReadsThen(1, false);
            StateFile.WriteCommand(d, command);
            o.Tick();
            Assert.Eq(1, relay.TakeRequests, command + ": request take sent to the peer first");
            Assert.Eq(1, ddc.Writes.Count, command + ": one write");
            Assert.Eq((uint)0x0F, ddc.Writes[0], command + ": peer code");
            Assert.Eq(false, o.Owned, command + ": given away");
            Directory.Delete(d, true);
        }

        public static void Test_GivePeer_SwitchesToPeerCode() { GivePeerCase("give peer"); }
        public static void Test_LegacyGiveMac_SameAsGivePeer() { GivePeerCase("give mac"); }

        // give code <n>：只写这一个编码，不去试别的设备
        public static void Test_GiveCode_WritesOnlyThatCode()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); var layout = new FakeLayout(); var relay = new FakeRelay();
            var o = Make(ddc, layout, relay, d);
            ddc.DefaultRead = false; // 设备有信号，显示器不跳回
            StateFile.WriteCommand(d, "give code 18");
            o.Tick();
            Assert.Eq(1, ddc.Writes.Count, "one write");
            Assert.Eq((uint)0x12, ddc.Writes[0], "code 18");
            Assert.Eq(0, relay.TakeRequests, "no request take for a device");
            Assert.Eq(false, o.Owned, "given away");
            Assert.Eq("release", string.Join(",", layout.Calls.ToArray()), "released once");
            Directory.Delete(d, true);
        }

        // 旧内容 give other：切到设备列表里的第一个设备
        public static void Test_LegacyGiveOther_StartsWithFirstDevice()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); var layout = new FakeLayout();
            var o = Make(ddc, layout, new FakeRelay(), d);
            ddc.DefaultRead = false;
            StateFile.WriteCommand(d, "give other");
            o.Tick();
            Assert.Eq((uint)0x11, ddc.Writes[0], "first device");
            Assert.Eq(false, o.Owned, "given away");
            Directory.Delete(d, true);
        }

        static void GiveSelfCase(string command)
        {
            string d = TempDir();
            var ddc = new FakeDdc(); var layout = new FakeLayout();
            var o = Make(ddc, layout, new FakeRelay(), d);
            o.HandleCommand(command);
            Assert.Eq("take", string.Join(",", layout.Calls.ToArray()), command + ": owned, layout re-applied");
            Assert.Eq(0, ddc.Writes.Count, command + ": no DDC write");
            Directory.Delete(d, true);

            d = TempDir();
            ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone;
            o = Make(ddc, layout, new FakeRelay(), d);
            o.HandleCommand(command);
            Assert.Eq(0, layout.Calls.Count, command + ": not owned, ignored");
            Directory.Delete(d, true);
        }

        public static void Test_GiveSelf_ReappliesLayoutWhenOwned() { GiveSelfCase("give self"); }
        public static void Test_LegacyGiveWindows_SameAsGiveSelf() { GiveSelfCase("give windows"); }

        public static void Test_MalformedCommands_LoggedNotExecuted()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); var layout = new FakeLayout(); var relay = new FakeRelay();
            var o = Make(ddc, layout, relay, d);
            foreach (string bad in new string[] { "give code", "give code 0", "give code 256", "give code 0x11", "give code 17 18", "give tv", "give", "" })
                o.HandleCommand(bad);
            Assert.Eq(0, ddc.Writes.Count, "no DDC write");
            Assert.Eq(0, layout.Calls.Count, "no layout change");
            Assert.Eq(0, relay.Requests.Count + relay.TakeRequests, "no relay");
            Assert.Eq(true, o.Owned, "still owned");
            Assert.True(File.ReadAllText(Path.Combine(d, "t.log")).Contains("unknown command: give code 0x11"), "logged");
            Directory.Delete(d, true);
        }
    }
}
