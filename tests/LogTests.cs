using System;
using System.IO;

namespace ScreenSwitch.Tests
{
    public static class LogTests
    {
        public static void Test_Log_AppendsLine()
        {
            string p = Path.Combine(Path.GetTempPath(), "ss-log-" + Guid.NewGuid() + ".log");
            var log = new Log(p);
            log.Info("hello");
            log.Error("bad");
            string[] lines = File.ReadAllLines(p);
            Assert.Eq(2, lines.Length, "two lines");
            Assert.True(lines[0].Contains("[INFO] hello"), "info line");
            Assert.True(lines[1].Contains("[ERROR] bad"), "error line");
            File.Delete(p);
        }

        public static void Test_Log_RotatesOver1MB()
        {
            string p = Path.Combine(Path.GetTempPath(), "ss-log-" + Guid.NewGuid() + ".log");
            File.WriteAllText(p, new string('x', 1024 * 1024 + 10));
            var log = new Log(p);
            log.Info("after");
            Assert.True(File.Exists(p + ".1"), "rotated file exists");
            Assert.True(new FileInfo(p).Length < 1000, "new log is small");
            File.Delete(p); File.Delete(p + ".1");
        }
    }
}
