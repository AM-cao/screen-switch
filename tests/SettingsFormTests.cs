using System.Collections.Generic;
using System.IO;

namespace ScreenSwitch.Tests
{
    public static class SettingsFormTests
    {
        class FakeHost : ITrayHost
        {
            public List<string> Languages = new List<string>();
            public OwnedLayout OwnedLayout { get { return OwnedLayout.Extend; } }
            public bool AutoSaveLayout { get { return true; } }
            public void SetOwnedLayout(OwnedLayout v) { }
            public void SetAutoSaveLayout(bool on) { }
            public string SaveLayoutNow() { return null; }
            public void RunWizard() { }
            public void OpenLog() { }
            public IList<string> HotkeyProblems { get { return new List<string>(); } }
            public void LanguageChanged(string setting) { Languages.Add(setting); }
        }

        // 换界面语言后整个窗口重画：标题、各页换成新语言，选中的标签页和"高级"的展开状态不变
        public static void Test_Rebuild_NewLanguage_KeepsTabAndAdvanced()
        {
            string d = SettingsServiceTests.TempDir();
            int r;
            var s = SettingsServiceTests.Make(d, new FakeSetupEnv(), out r);
            try
            {
                using (var f = new SettingsForm(s, new FakeHost()))
                {
                    f.ShowTab(SettingsForm.TabGeneral);
                    f.AdvancedOpen = true;
                    Tr.Use("en");
                    f.Rebuild();
                    Assert.Eq(Tr.T("settings.title"), f.Text, "title in the new language");
                    Assert.Eq(SettingsForm.TabGeneral, f.SelectedTabName, "same tab");
                    Assert.Eq(true, f.AdvancedOpen, "advanced still open");
                    Assert.Eq(Tr.T("tab.general"), f.TabTitle(SettingsForm.TabGeneral), "tab titles in the new language");
                }
            }
            finally { Tr.Use("zh"); }
            Directory.Delete(d, true);
        }
    }
}
