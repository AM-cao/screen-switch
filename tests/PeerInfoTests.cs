using System;

namespace ScreenSwitch.Tests
{
    // 设计文档 5.4：request info 的输出
    public static class PeerInfoTests
    {
        // 两端测试共用的用例行（设计文档 5.4 的例子）：名字依次是 MacBook、Switch、PS5
        const string Sample = "proto=3 os=mac code=15 name=TWFjQm9vaw== devices=17:U3dpdGNo,18:UFM1";

        public static void Test_Parse_SampleLine()
        {
            PeerInfo i = PeerInfo.Parse(Sample);
            Assert.True(i != null, "parsed");
            Assert.Eq(3, i.Proto, "proto");
            Assert.Eq("mac", i.Os, "os");
            Assert.Eq((uint)15, i.Code, "code");
            Assert.Eq("MacBook", i.Name, "name");
            Assert.Eq(2, i.Devices.Count, "devices");
            Assert.Eq((uint)17, i.Devices[0].Code, "d0 code");
            Assert.Eq("Switch", i.Devices[0].Name, "d0 name");
            Assert.Eq((uint)18, i.Devices[1].Code, "d1 code");
            Assert.Eq("PS5", i.Devices[1].Name, "d1 name");
        }

        public static void Test_Format_SampleLine()
        {
            var c = new Config();
            c.SelfName = "MacBook"; c.SelfCode = 15;
            c.Devices.Add(new Device("Switch", 17));
            c.Devices.Add(new Device("PS5", 18));
            Assert.Eq(Sample.Replace("os=mac", "os=windows"), PeerInfo.Format(c), "same line, os=windows");
        }

        // 名字含中文和空格：只用 Base64 传，整行是 ASCII
        public static void Test_Format_NonAsciiNames_AsciiLine_RoundTrips()
        {
            var c = new Config();
            c.SelfName = "书房 台式机"; c.SelfCode = 16;
            c.Devices.Add(new Device("任天堂 Switch", 17));
            string line = PeerInfo.Format(c);
            foreach (char ch in line) Assert.True(ch >= 0x20 && ch < 0x7F, "ASCII only: " + line);
            Assert.True(!line.Contains("\n") && !line.Contains("\r"), "one line");
            PeerInfo i = PeerInfo.Parse(line);
            Assert.Eq("windows", i.Os, "os");
            Assert.Eq((uint)16, i.Code, "code");
            Assert.Eq("书房 台式机", i.Name, "name");
            Assert.Eq("任天堂 Switch", i.Devices[0].Name, "device name");
        }

        public static void Test_Format_NoDevices_UnknownCode()
        {
            var c = new Config();
            c.SelfName = "pc";
            Assert.Eq("proto=3 os=windows code=0 name=cGM= devices=", PeerInfo.Format(c), "empty devices, code 0");
            PeerInfo i = PeerInfo.Parse(PeerInfo.Format(c));
            Assert.Eq(0, i.Devices.Count, "no devices");
            Assert.Eq((uint)0, i.Code, "unknown code");
        }

        public static void Test_Parse_IgnoresUnknownKeys_AndSurroundingLines()
        {
            PeerInfo i = PeerInfo.Parse("some banner\r\nproto=4 future=x os=windows code=16 name=cGM= devices=17:U3dpdGNo extra\r\n");
            Assert.True(i != null, "parsed");
            Assert.Eq(4, i.Proto, "proto");
            Assert.Eq("windows", i.Os, "os");
            Assert.Eq("pc", i.Name, "name");
            Assert.Eq(1, i.Devices.Count, "devices");
        }

        public static void Test_Parse_Garbage_ReturnsNull()
        {
            Assert.Eq(null, PeerInfo.Parse(null), "null");
            Assert.Eq(null, PeerInfo.Parse(""), "empty");
            Assert.Eq(null, PeerInfo.Parse("rejected"), "old peer's gate answer");
            Assert.Eq(null, PeerInfo.Parse("os=mac code=15"), "no proto");
        }

        public static void Test_Parse_BadFields_Tolerated()
        {
            PeerInfo i = PeerInfo.Parse("proto=3 os=mac code=abc name=!!! devices=17:!!!,x:U3dpdGNo,0:UFM1,18:UFM1,300:UFM1");
            Assert.True(i != null, "parsed");
            Assert.Eq((uint)0, i.Code, "bad code -> unknown");
            Assert.Eq("", i.Name, "bad base64 -> empty");
            Assert.Eq(2, i.Devices.Count, "device with a bad name kept, bad codes dropped");
            Assert.Eq((uint)17, i.Devices[0].Code, "d0 code");
            Assert.Eq("", i.Devices[0].Name, "d0 name empty");
            Assert.Eq("PS5", i.Devices[1].Name, "d1");
        }
    }
}
