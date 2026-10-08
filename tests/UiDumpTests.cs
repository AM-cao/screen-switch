using System;
using System.IO;

namespace ScreenSwitch.Tests
{
    public static class UiDumpTests
    {
        public static void Test_WritesNonEmptyPngs()
        {
            string d = Path.Combine(Path.GetTempPath(), "ss-dump-" + Guid.NewGuid());
            try
            {
                Assert.Eq(0, UiDump.Run(d, "zh"), "ok");
                string[] files = Directory.GetFiles(d, "*.png");
                Assert.True(files.Length >= 12, "menu x2 + 5 settings tabs + 5 wizard pages: " + files.Length);
                foreach (string f in files) Assert.True(new FileInfo(f).Length > 2000, "not blank: " + Path.GetFileName(f));
            }
            finally
            {
                Tr.Use("zh");
                if (Directory.Exists(d)) Directory.Delete(d, true);
            }
        }
    }
}
