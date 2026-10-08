using System;
using System.IO;

namespace ScreenSwitch.Tests
{
    public static class AutostartTests
    {
        public static void Test_EnableDisable_CreatesAndRemovesShortcut()
        {
            string d = Path.Combine(Path.GetTempPath(), "ss-auto-" + Guid.NewGuid());
            Directory.CreateDirectory(d);
            try
            {
                var a = new Autostart(Path.Combine(d, "Startup"), "C:\\x\\ScreenSwitch.exe", "C:\\x");
                Assert.Eq(false, a.Enabled, "off at first");
                a.Enable();
                Assert.Eq(true, a.Enabled, "on");
                Assert.True(File.Exists(Path.Combine(d, "Startup\\ScreenSwitch.lnk")), "shortcut file");
                a.Enable();
                Assert.Eq(true, a.Enabled, "enabling twice is fine");
                a.Disable();
                Assert.Eq(false, a.Enabled, "off again");
                a.Disable();
            }
            finally { Directory.Delete(d, true); }
        }

        public static void Test_ForCurrentUser_PointsAtStartupFolder()
        {
            var a = Autostart.ForCurrentUser("C:\\x");
            Assert.Eq(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "ScreenSwitch.lnk"), a.LinkPath, "same file install.cmd creates");
        }
    }
}
