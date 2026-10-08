using System;
using System.IO;

namespace ScreenSwitch.Tests
{
    public static class PrefsTests
    {
        static string TempFile() { return Path.Combine(Path.GetTempPath(), "ss-prefs-" + Guid.NewGuid() + ".json"); }

        public static void Test_Prefs_DefaultsWhenMissing()
        {
            var p = Prefs.Load(TempFile());
            Assert.Eq(OwnedLayout.Extend, p.OwnedLayout, "default preset");
            Assert.Eq(true, p.AutoSaveLayout, "default auto-save on");
            var q = new Prefs();
            Assert.Eq(OwnedLayout.Extend, q.OwnedLayout, "field default preset");
            Assert.Eq(true, q.AutoSaveLayout, "field default auto-save");
        }

        public static void Test_Prefs_SaveThenLoad()
        {
            string f = TempFile();
            var p = new Prefs(); p.OwnedLayout = OwnedLayout.MainOnly; p.AutoSaveLayout = false;
            p.Save(f);
            Assert.Eq("{\"ownedLayout\":\"mainOnly\",\"autoSaveLayout\":false}", File.ReadAllText(f), "file format");
            Assert.Eq(false, File.Exists(f + ".tmp"), "tmp removed");
            var q = Prefs.Load(f);
            Assert.Eq(OwnedLayout.MainOnly, q.OwnedLayout, "preset read back");
            Assert.Eq(false, q.AutoSaveLayout, "auto-save read back");
            // 覆盖已有文件
            q.OwnedLayout = OwnedLayout.Extend; q.AutoSaveLayout = true;
            q.Save(f);
            Assert.Eq("{\"ownedLayout\":\"extend\",\"autoSaveLayout\":true}", File.ReadAllText(f), "overwritten");
            var r = Prefs.Load(f);
            Assert.Eq(OwnedLayout.Extend, r.OwnedLayout, "extend read back");
            Assert.Eq(true, r.AutoSaveLayout, "on read back");
            File.Delete(f);
        }

        public static void Test_Prefs_CorruptJsonGivesDefaults()
        {
            string f = TempFile();
            foreach (string text in new string[] { "{ \"ownedLayout\": \"mainOnly\", ", "not json", "[1,2]", "\"x\"", "null", "", "\u0000\u0001" })
            {
                File.WriteAllText(f, text);
                var p = Prefs.Load(f);
                Assert.Eq(OwnedLayout.Extend, p.OwnedLayout, "default preset for <" + text + ">");
                Assert.Eq(true, p.AutoSaveLayout, "default auto-save for <" + text + ">");
            }
            File.Delete(f);
        }

        public static void Test_Prefs_UnknownValuesGiveDefaults()
        {
            string f = TempFile();
            File.WriteAllText(f, "{\"ownedLayout\":\"mainonly\",\"autoSaveLayout\":\"no\"}");
            var p = Prefs.Load(f);
            Assert.Eq(OwnedLayout.Extend, p.OwnedLayout, "unknown preset (case differs) -> extend");
            Assert.Eq(true, p.AutoSaveLayout, "non-bool auto-save -> on");

            File.WriteAllText(f, "{\"ownedLayout\":3,\"autoSaveLayout\":0,\"other\":1}");
            p = Prefs.Load(f);
            Assert.Eq(OwnedLayout.Extend, p.OwnedLayout, "number preset -> extend");
            Assert.Eq(true, p.AutoSaveLayout, "number auto-save -> on");

            // 只有一项有效时另一项仍按默认
            File.WriteAllText(f, "{\"ownedLayout\":\"mainOnly\",\"autoSaveLayout\":null}");
            p = Prefs.Load(f);
            Assert.Eq(OwnedLayout.MainOnly, p.OwnedLayout, "valid preset kept");
            Assert.Eq(true, p.AutoSaveLayout, "null auto-save -> on");

            File.WriteAllText(f, "{\"autoSaveLayout\":false}");
            p = Prefs.Load(f);
            Assert.Eq(OwnedLayout.Extend, p.OwnedLayout, "missing preset -> extend");
            Assert.Eq(false, p.AutoSaveLayout, "valid auto-save kept");
            File.Delete(f);
        }
    }
}
