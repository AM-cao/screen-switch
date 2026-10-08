using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace ScreenSwitch
{
    // 把菜单、设置窗口每一页、向导每一页画成 PNG，供不安装就审阅界面（docs/settings-ui-design.md 第 12 节）。
    // 用示例数据，不碰硬件、SSH 和用户的配置；窗口放在屏幕外。
    public static class UiDump
    {
        class DemoEnv : ISetupEnv
        {
            public List<SetupMonitor> Monitors()
            {
                return new List<SetupMonitor> {
                    new SetupMonitor("\\\\?\\DISPLAY#ABC1234#5&1&0&UID1#{x}", "Main Monitor", true, 16),
                    new SetupMonitor("\\\\?\\DISPLAY#XYZ0001#5&1&0&UID2#{x}", "Side Monitor", false, 0) };
            }
            public int ConnectedCount() { return 2; }
            public List<uint> InputCodes(string mainEdid) { return new List<uint> { 15, 16, 17, 18 }; }
            public int SshClientSetup(string user, string host, out string output) { output = ""; return 0; }
            public string PublicKey() { return "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExampleExampleExample screen-switch@PC"; }
            public PeerInfo RequestInfo(Config cfg) { return null; }
            public TrayRestart RestartTray() { return TrayRestart.Missing; }
            public string GenerateKey() { return PublicKey(); }
            public int AuthorizePeerKey(string key, bool elevate, out string output) { output = ""; return 2; }
            public bool IsAdmin() { return false; }
        }

        class DemoHost : ITrayHost
        {
            public OwnedLayout OwnedLayout { get { return OwnedLayout.Extend; } }
            public bool AutoSaveLayout { get { return true; } }
            public void SetOwnedLayout(OwnedLayout v) { }
            public void SetAutoSaveLayout(bool on) { }
            public string SaveLayoutNow() { return null; }
            public void RunWizard() { }
            public void OpenLog() { }
            public IList<string> HotkeyProblems { get { return new List<string>(); } }
        }

        static Config Demo()
        {
            var c = new Config();
            c.MainEdid = "ABC1234"; c.SelfName = "PC"; c.SelfCode = 16;
            c.PeerName = "MacBook"; c.PeerOs = "mac"; c.PeerHost = "my-mac.local"; c.PeerUser = "alice"; c.PeerCode = 15;
            c.Devices.Add(new Device("Switch", 17));
            return c;
        }

        public static int Run(string outDir, string lang)
        {
            Tr.Use(lang == "en" ? "en" : "zh");
            Directory.CreateDirectory(outDir);
            string work = Path.Combine(Path.GetTempPath(), "ss-ui-dump-" + Guid.NewGuid());
            Directory.CreateDirectory(work);
            try
            {
                Demo().Save(Path.Combine(work, "config.json"));
                var svc = new SettingsService(work, new DemoEnv(), new Autostart(Path.Combine(work, "Startup"), "x", work));
                svc.Reload = null;
                Config c = Demo();
                DumpMenu(TrayMenu.Build(c, false, false, OwnedLayout.Extend, null), Path.Combine(outDir, "menu-away.png"));
                DumpMenu(TrayMenu.Build(c, true, false, OwnedLayout.MainOnly, null), Path.Combine(outDir, "menu-owned.png"));
                using (var f = new SettingsForm(svc, new DemoHost()))
                {
                    Prepare(f);
                    string[] tabs = { SettingsForm.TabPorts, SettingsForm.TabPeer, SettingsForm.TabScreens, SettingsForm.TabHotkeys, SettingsForm.TabGeneral };
                    for (int i = 0; i < tabs.Length; i++)
                    {
                        f.ShowTab(tabs[i]);
                        Settle();
                        Save(f, Path.Combine(outDir, "settings-" + (i + 1) + "-" + tabs[i] + ".png"));
                    }
                }
                using (var w = new WizardForm(svc))
                {
                    Prepare(w);
                    for (int i = 0; i < 5; i++)
                    {
                        w.GoTo(i);
                        Settle();
                        Save(w, Path.Combine(outDir, "wizard-" + (i + 1) + ".png"));
                    }
                }
                return 0;
            }
            finally { try { Directory.Delete(work, true); } catch (IOException) { } }
        }

        static void Prepare(Form f)
        {
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-32000, -32000);
            f.ShowInTaskbar = false;
            f.Show();
            Settle();
        }

        // 让后台读数（示例环境是即时的）回到界面线程并画完
        static void Settle()
        {
            for (int i = 0; i < 20; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
        }

        static void Save(Control c, string path)
        {
            using (var bmp = new Bitmap(c.Width, c.Height))
            {
                c.DrawToBitmap(bmp, new Rectangle(0, 0, c.Width, c.Height));
                bmp.Save(path, ImageFormat.Png);
            }
        }

        // 菜单放进一个无边框窗口里当普通控件画（ContextMenuStrip 平时是顶层弹出窗口，不在屏幕上时画不出来）
        static void DumpMenu(List<MenuEntry> entries, string path)
        {
            using (var menu = new ContextMenuStrip())
            using (var f = new Form { FormBorderStyle = FormBorderStyle.None, BackColor = SystemColors.Menu })
            {
                TrayMenu.Fill(menu, entries, delegate { });
                menu.AutoClose = false;
                menu.TopLevel = false;
                menu.Dock = DockStyle.None;
                menu.Location = new Point(0, 0);
                f.Controls.Add(menu);
                menu.Visible = true;
                menu.PerformLayout();
                f.ClientSize = menu.Size;
                Prepare(f);
                Save(f, path);
            }
        }
    }
}
