using System;
using System.Collections.Generic;
using System.IO;

namespace ScreenSwitch.Tests
{
    // 设备列表（设计文档 6.3、8）：每个设备一个菜单项；切给设备只切到它的编码。
    public static class DeviceTests
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
            c.PeerName = "MacBook";
            c.Devices[0].Name = "Switch";
            c.Devices[1].Name = "PS5";
            return c;
        }

        static Orchestrator Make(FakeDdc ddc, FakeLayout layout, FakeRelay relay, FakeNotifier n, string dir)
        {
            return new Orchestrator(ddc, layout, relay, n, new FakeClock(), Cfg(), new Log(Path.Combine(dir, "t.log")), dir);
        }

        // 设备有信号：写它的编码、让出，观察 15 秒没有跳回，结束，不提示
        public static void Test_GiveDevice_Owned_WritesOnlyItsCode()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); var layout = new FakeLayout(); var n = new FakeNotifier(); var relay = new FakeRelay();
            var o = Make(ddc, layout, relay, n, d);
            ddc.DefaultRead = false;
            o.GiveToDevice(0x12);
            Assert.Eq(1, ddc.Writes.Count, "one write");
            Assert.Eq((uint)0x12, ddc.Writes[0], "PS5 code");
            Assert.Eq("release", string.Join(",", layout.Calls.ToArray()), "released");
            Assert.Eq(false, o.Owned, "given away");
            Assert.Eq(0, n.Titles.Count, "no notice");
            Assert.Eq(0, relay.Requests.Count + relay.TakeRequests, "no relay when owned");
            Directory.Delete(d, true);
        }

        // 设备没有信号，主屏自己跳回来：拿回并通知，不再去试别的设备
        public static void Test_GiveDevice_NoSignal_TakesBackAndNamesTheDevice()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); var layout = new FakeLayout(); var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, d);
            ddc.ReadsThen(1, false); // 写入后读不到：已切走
            ddc.ReadsThen(2, true);  // 观察期连续读到两次：跳回来了
            o.GiveToDevice(0x11);
            Assert.Eq(1, ddc.Writes.Count, "only the chosen device's code is written");
            Assert.Eq("release,take", string.Join(",", layout.Calls.ToArray()), "released, then taken back");
            Assert.Eq(true, o.Owned, "kept");
            Assert.Eq("没有检测到 Switch 的信号", n.Titles[0], "notice names the device");
            Assert.Eq("主屏留在本机", n.Texts[0], "notice body");
            Assert.Eq(1, n.Titles.Count, "one notice");
            Directory.Delete(d, true);
        }

        public static void Test_GiveDevice_NoEffect_Notifies()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); var layout = new FakeLayout(); var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, d);
            ddc.DefaultRead = true; // 写入后主屏还在本机
            o.GiveToDevice(0x11);
            Assert.Eq(true, o.Owned, "still owned");
            Assert.Eq(0, layout.Calls.Count, "no layout change");
            Assert.Eq("切换未生效", n.Titles[0], "notice");
            Directory.Delete(d, true);
        }

        public static void Test_GiveDevice_NotOwned_AsksPeerWithCode()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); ddc.Reads.Enqueue(false);
            var layout = new FakeLayout(); layout.ModeValue = LayoutMode.Clone;
            var n = new FakeNotifier(); var relay = new FakeRelay();
            var o = Make(ddc, layout, relay, n, d);
            o.GiveToDevice(0x12);
            Assert.Eq("code 18", relay.Requests[0], "request give code 18");
            Assert.Eq("已请求 MacBook 切给 PS5", n.Titles[0], "notice names peer and device");
            relay.Result = 2;
            o.GiveToDevice(0x12);
            Assert.Eq("请用显示器上的按钮切换输入源", n.Titles[1], "fail notice");
            Assert.Eq(0, ddc.Writes.Count, "no local write");
            Directory.Delete(d, true);
        }

        // 编码不在设备列表里（命令文件里可以出现）：照样切，名字用编码
        public static void Test_GiveDevice_UnknownCode_NamedByCode()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); var layout = new FakeLayout(); var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, d);
            ddc.ReadsThen(1, false); ddc.ReadsThen(2, true);
            o.GiveToDevice(0x1B);
            Assert.Eq((uint)0x1B, ddc.Writes[0], "code written");
            Assert.Eq("没有检测到 0x1B 的信号", n.Titles[0], "named by code");
            Directory.Delete(d, true);
        }

        // 旧命令 give other：只切到第一个设备
        public static void Test_LegacyGiveOther_FirstDeviceOnly()
        {
            string d = TempDir();
            var ddc = new FakeDdc(); var layout = new FakeLayout(); var n = new FakeNotifier();
            var o = Make(ddc, layout, new FakeRelay(), n, d);
            ddc.ReadsThen(1, false); ddc.ReadsThen(2, true);
            o.HandleCommand("give other");
            Assert.Eq(1, ddc.Writes.Count, "one write");
            Assert.Eq((uint)0x11, ddc.Writes[0], "first device");
            Assert.Eq("没有检测到 Switch 的信号", n.Titles[0], "notice");
            Directory.Delete(d, true);
        }
    }
}
