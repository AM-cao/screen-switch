using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ScreenSwitch
{
    public class TrayApp : ApplicationContext, ITrayHost
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        const int WM_HOTKEY = 0x0312;
        const int WM_ENDSESSION = 0x0016;
        // 快捷键编号：对方一个，设备从 HK_DEVICE_BASE 起按设备列表的下标排
        const int HK_PEER = 1, HK_DEVICE_BASE = 100;

        class MessageForm : Form
        {
            public Action<int> OnHotkey;
            public Action OnSessionEnd;
            public MessageForm()
            {
                ShowInTaskbar = false; WindowState = FormWindowState.Minimized; Opacity = 0; FormBorderStyle = FormBorderStyle.None;
            }
            protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_HOTKEY && OnHotkey != null) OnHotkey(m.WParam.ToInt32());
                else if (IsSessionEnd(m.Msg, m.WParam) && OnSessionEnd != null) OnSessionEnd();
                base.WndProc(ref m);
            }
        }

        // 会话确实要结束（注销、关机、重启）：WM_ENDSESSION 且 wParam 非 0。wParam 为 0 表示结束被取消；
        // WM_QUERYENDSESSION 只是询问（可能被别的程序否决），不动作。
        public static bool IsSessionEnd(int msg, IntPtr wParam)
        {
            return msg == WM_ENDSESSION && wParam != IntPtr.Zero;
        }

        class TrayNotifier : INotifier
        {
            readonly TrayApp app;
            public TrayNotifier(TrayApp app) { this.app = app; }
            public void Notify(string title, string text) { app.Notify(title, text); }
        }

        readonly string dir;
        Config cfg;
        readonly Log log;
        Orchestrator orch;
        ILayout layout;
        readonly ReloadGate reloadGate = new ReloadGate();
        readonly NotifyIcon icon;
        readonly ContextMenuStrip menu;
        readonly MessageForm form;
        readonly System.Windows.Forms.Timer timer;
        // 下标 (owned ? 1 : 0) + (problem ? 2 : 0)
        readonly Icon[] icons = new Icon[4];
        SettingsForm settings;
        // 已成功注册的快捷键
        readonly List<HotkeyBinding> hotkeys = new List<HotkeyBinding>();
        // 没能注册的快捷键（写法）
        readonly List<string> hotkeyProblems = new List<string>();
        // 重新加载是不是来自设置窗口之外（命令文件的 reload）：是的话窗口要跟着刷新
        bool externalReload;
        // 重新加载时读不了的 config.json 的错误说明（没替换运行核心）
        string rejectedConfigError;
        int busy;

        public TrayApp(string dir)
        {
            this.dir = dir;
            log = new Log(Path.Combine(dir, "screenswitch.log"));
            form = new MessageForm();
            IntPtr h = form.Handle;
            form.OnHotkey = OnHotkey;
            form.OnSessionEnd = OnSessionEnd;

            BuildEngine(true);

            menu = new ContextMenuStrip();
            menu.RenderMode = ToolStripRenderMode.System;
            for (int i = 0; i < 4; i++) icons[i] = TrayIcons.Make((i & 1) != 0, 32, (i & 2) != 0);
            icon = new NotifyIcon();
            icon.Icon = icons[0];
            icon.BalloonTipIcon = ToolTipIcon.None;
            icon.ContextMenuStrip = menu;
            icon.Visible = true;
            // 先登记快捷键：菜单和图标上的问题提示要用到登记结果
            RegisterHotkeys();
            RebuildMenu();

            if (cfg.LoadError != null) Notify(Tr.T("tray.config-broken"), cfg.LoadError);

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 500;
            timer.Tick += delegate { orch.Tick(); };
            timer.Start();

            // 还没设置过（主屏标识还是占位符）：先提示运行设置，别去提保存布局
            if (cfg.MainEdid == Config.Placeholder) form.BeginInvoke(new Action(OpenWizard));
            else if (!layout.HasSnapshot)
            {
                if (orch.Owned)
                {
                    try { orch.SaveLayout(); Notify(Tr.T("tray.layout-saved"), Tr.T("tray.layout-saved.text")); }
                    catch (Exception e) { Notify(Tr.T("tray.layout-save-failed"), e.Message); }
                }
                else Notify(Tr.T("notify.save-layout-first"), Tr.T("notify.save-layout-first.text"));
            }
        }

        string ConfigPath { get { return Path.Combine(dir, "config.json"); } }

        // 运行核心：配置、DDC、布局、中继、编排器。重新加载配置时整个重建。
        // 先全部建在局部变量里，建成功才替换字段；中途抛异常时字段保持旧值。
        // 返回 false：重新加载时配置文件读不了，没有替换，继续用旧的运行核心。
        // 第一次（启动）没有旧的可用，读不了就用默认值。
        bool BuildEngine(bool initial)
        {
            Config newCfg = Config.Load(ConfigPath);
            if (newCfg.LoadError != null) log.Error("config: " + newCfg.LoadError);
            if (!initial && newCfg.LoadError != null) { rejectedConfigError = newCfg.LoadError; return false; }
            var ddc = new DxvaDdc(newCfg.MainEdid, log);
            ILayout newLayout = Layouts.Create(newCfg, Path.Combine(dir, "layout-owned.bin"), log);
            var relay = new SshRelay(newCfg, log, dir);
            var newOrch = new Orchestrator(ddc, newLayout, relay, new TrayNotifier(this), new RealClock(), newCfg, log, dir);
            newOrch.OwnedChanged += delegate { form.BeginInvoke(new Action(RebuildMenu)); };
            newOrch.ReloadRequested += delegate { form.BeginInvoke(new Action(RequestReload)); };
            var afterTake = new AfterTakeRunner(newCfg.AfterTakeCommand, newCfg.AfterTakeDelayMs, log);
            newOrch.AfterTake = afterTake.Schedule;
            cfg = newCfg;
            layout = newLayout;
            orch = newOrch;
            return true;
        }

        // 命令文件的 reload（命令行改了配置）：重新加载后设置窗口也要刷新
        public void RequestReload()
        {
            externalReload = true;
            ReloadWhenIdle();
        }

        // 配置改了：空闲时立即重建，正在切换时等切换结束（ReloadGate）
        void ReloadWhenIdle()
        {
            if (reloadGate.Request(busy != 0)) ReloadEngine();
        }

        // 重建失败时继续用旧的运行核心：快捷键和定时器在 finally 里恢复
        void ReloadEngine()
        {
            bool refreshSettings = externalReload;
            externalReload = false;
            timer.Stop();
            foreach (HotkeyBinding b in hotkeys) UnregisterHotKey(form.Handle, b.Id);
            hotkeys.Clear();
            try
            {
                if (BuildEngine(false)) log.Info("config reloaded");
                else Notify(Tr.T("notify.config-kept"), rejectedConfigError);
            }
            catch (Exception e)
            {
                log.Error("reload: " + e);
                Notify(Tr.T("tray.reload-failed"), e.Message);
            }
            finally
            {
                RegisterHotkeys();
                RebuildMenu();
                timer.Start();
            }
            // 设置窗口自己触发的重载在事件处理里同步走到这里：它只刷新受影响的页，这里不动。
            // 命令行触发的才整个刷新，并且延后到当前消息处理完。
            if (refreshSettings) RefreshSettingsLater();
        }

        void RefreshSettingsLater()
        {
            SettingsForm s = settings;
            if (s == null || s.IsDisposed || !s.IsHandleCreated) return;
            s.BeginInvoke(new Action(delegate { if (!s.IsDisposed) s.Rebuild(); }));
        }

        bool UiAlive() { return form != null && form.IsHandleCreated && !form.IsDisposed; }

        public void Notify(string title, string text)
        {
            // 托盘图标在构造函数里晚于编排器创建，启动对齐失败时可能更早走到这里。
            if (icon == null || !UiAlive()) { log.Info("notify (no ui): " + title + " | " + text); return; }
            if (form.InvokeRequired) { form.BeginInvoke(new Action<string, string>(Notify), title, text); return; }
            icon.BalloonTipTitle = title;
            icon.BalloonTipText = string.IsNullOrEmpty(text) ? " " : text;
            icon.ShowBalloonTip(4000);
            log.Info("notify: " + title + " | " + text);
        }

        void RunBg(Action a)
        {
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) { Notify(Tr.T("notify.busy"), Tr.T("notify.busy.text")); return; }
            // 开始：切换项马上变灰（RunBg 在界面线程上调用）
            if (UiAlive()) RebuildMenu();
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { a(); }
                catch (Exception e) { log.Error("action: " + e); Notify(Tr.T("notify.failed"), e.Message); }
                finally
                {
                    Interlocked.Exchange(ref busy, 0);
                    // 结束：busy 清零之后再重画菜单，然后才看有没有推迟的重新加载
                    if (UiAlive()) form.BeginInvoke(new Action(delegate
                    {
                        RebuildMenu();
                        if (Thread.VolatileRead(ref busy) == 0 && reloadGate.OnIdle()) ReloadEngine();
                    }));
                }
            });
        }

        List<string> Problems()
        {
            var l = new List<string>();
            if (cfg.LoadError != null) l.Add(Tr.T("problem.config"));
            foreach (string hk in hotkeyProblems) l.Add(Tr.T("problem.hotkey", hk));
            return l;
        }

        void RebuildMenu()
        {
            List<string> problems = Problems();
            TrayMenu.Fill(menu, TrayMenu.Build(cfg, orch.Owned, busy != 0, orch.OwnedLayout, problems), OnMenu);
            icon.Icon = icons[(orch.Owned ? 1 : 0) + (problems.Count > 0 ? 2 : 0)];
            icon.Text = "ScreenSwitch";
        }

        void OnMenu(MenuEntry e)
        {
            switch (e.Kind)
            {
                case MenuKind.Problem: OpenSettings(cfg.LoadError != null ? SettingsForm.TabGeneral : SettingsForm.TabHotkeys); break;
                case MenuKind.TakeBack: RunBg(delegate { orch.TakeBack(); }); break;
                case MenuKind.Peer: RunBg(delegate { orch.GiveToPeer(); }); break;
                case MenuKind.Device: { uint code = e.Code; RunBg(delegate { orch.GiveToDevice(code); }); break; }
                case MenuKind.Extend: RunBg(delegate { orch.SetOwnedLayout(OwnedLayout.Extend); }); break;
                case MenuKind.MainOnly: RunBg(delegate { orch.SetOwnedLayout(OwnedLayout.MainOnly); }); break;
                case MenuKind.Settings: OpenSettings(null); break;
                case MenuKind.Quit: Shutdown(); break;
            }
        }

        // 只开一个设置窗口；已开着就带到前面
        public void OpenSettings(string tab)
        {
            if (settings == null || settings.IsDisposed)
            {
                var svc = new SettingsService(dir, new SetupEnv(dir), Autostart.ForCurrentUser(dir));
                // 设置窗口自己改的配置：进程内重载，窗口自己刷新受影响的页（不走 externalReload）
                svc.Reload = delegate { ReloadWhenIdle(); };
                settings = new SettingsForm(svc, this);
                settings.Show();
            }
            if (tab != null) settings.ShowTab(tab);
            if (settings.WindowState == FormWindowState.Minimized) settings.WindowState = FormWindowState.Normal;
            settings.Activate();
        }

        // ---- ITrayHost ----
        public OwnedLayout OwnedLayout { get { return orch.OwnedLayout; } }
        public bool AutoSaveLayout { get { return orch.AutoSaveLayout; } }
        public void SetOwnedLayout(OwnedLayout v) { RunBg(delegate { orch.SetOwnedLayout(v); }); }
        public void SetAutoSaveLayout(bool on) { RunBg(delegate { orch.SetAutoSaveLayout(on); }); }
        public string SaveLayoutNow()
        {
            try { orch.SaveLayout(); return null; }
            catch (Exception e) { return e.Message; }
        }
        public void RunWizard() { OpenWizard(); }
        public void OpenLog()
        {
            try { Process.Start(Path.Combine(dir, "screenswitch.log")); }
            catch (Exception e) { Notify(Tr.T("notify.log-failed"), e.Message); }
        }
        public IList<string> HotkeyProblems { get { return new List<string>(hotkeyProblems); } }

        WizardForm wizard;

        // 只开一个向导窗口；已开着就带到前面。向导点"完成"后写配置并进程内重载
        public void OpenWizard()
        {
            if (wizard == null || wizard.IsDisposed)
            {
                var svc = new SettingsService(dir, new SetupEnv(dir), Autostart.ForCurrentUser(dir));
                svc.Reload = delegate { ReloadWhenIdle(); };
                wizard = new WizardForm(svc);
                wizard.Show();
            }
            wizard.Activate();
        }

        public class HotkeyBinding
        {
            public int Id; public string Hotkey; public MenuKind Kind; public uint Code;
            public HotkeyBinding(int id, string hotkey, MenuKind kind, uint code) { Id = id; Hotkey = hotkey; Kind = kind; Code = code; }
        }

        // 要注册的全局快捷键（设计文档 8）：默认一个也没有；peer.hotkey 与各设备的 hotkey 配了才有。
        // peer.hotkey 随状态在"切给对方"和"拿回"之间切换。
        public static List<HotkeyBinding> HotkeyBindings(Config cfg)
        {
            var list = new List<HotkeyBinding>();
            string peer = (cfg.PeerHotkey ?? "").Trim();
            if (peer.Length > 0) list.Add(new HotkeyBinding(HK_PEER, peer, MenuKind.Peer, 0));
            for (int i = 0; i < cfg.Devices.Count; i++)
            {
                string hk = (cfg.Devices[i].Hotkey ?? "").Trim();
                if (hk.Length > 0) list.Add(new HotkeyBinding(HK_DEVICE_BASE + i, hk, MenuKind.Device, cfg.Devices[i].Code));
            }
            return list;
        }

        void OnHotkey(int id)
        {
            foreach (HotkeyBinding b in hotkeys)
            {
                if (b.Id != id) continue;
                uint code = b.Code;
                if (b.Kind == MenuKind.Device) RunBg(delegate { orch.GiveToDevice(code); });
                else if (!orch.Owned) RunBg(delegate { orch.TakeBack(); });
                else if (cfg.PeerConfigured) RunBg(delegate { orch.GiveToPeer(); });
                return;
            }
        }

        void RegisterHotkeys()
        {
            hotkeyProblems.Clear();
            foreach (HotkeyBinding b in HotkeyBindings(cfg))
            {
                uint mods, vk;
                if (!ParseHotkey(b.Hotkey, out mods, out vk)) { log.Error("hotkey invalid: " + b.Hotkey); hotkeyProblems.Add(b.Hotkey); continue; }
                if (!RegisterHotKey(form.Handle, b.Id, mods, vk)) { log.Error("hotkey register failed (in use by another program?): " + b.Hotkey); hotkeyProblems.Add(b.Hotkey); continue; }
                hotkeys.Add(b);
                log.Info("hotkey registered: " + b.Hotkey);
            }
        }

        // "Ctrl+Alt+1" -> mods = MOD_ALT(1)|MOD_CONTROL(2)|MOD_SHIFT(4)|MOD_WIN(8)，vk = 虚拟键码
        public static bool ParseHotkey(string text, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0) return false;
                string u = p.ToUpperInvariant();
                if (u == "CTRL" || u == "CONTROL") { mods |= 2; continue; }
                if (u == "ALT") { mods |= 1; continue; }
                if (u == "SHIFT") { mods |= 4; continue; }
                if (u == "WIN") { mods |= 8; continue; }
                if (i != parts.Length - 1) return false;
                if (u.Length == 1 && char.IsLetterOrDigit(u[0])) { vk = (uint)u[0]; return true; }
                if (u.Length >= 2 && u[0] == 'F')
                {
                    int n;
                    if (int.TryParse(u.Substring(1), out n) && n >= 1 && n <= 24) { vk = (uint)(0x70 + n - 1); return true; }
                }
                return false;
            }
            return false;
        }

        // 注销、关机、重启不经过"退出"菜单（Shutdown）：会话结束时同样先把副屏打开。否则仅主屏会留在
        // Windows 的显示配置数据库里，下次开机的登录界面可能只显示在（也许已切给别的设备的）主屏上。
        // 在 WM_ENDSESSION 的处理里同步完成：处理返回后进程随时可能被结束。
        void OnSessionEnd()
        {
            log.Info("session ending: enabling secondary if main-only");
            if (timer != null) timer.Stop();
            try { orch.PrepareExit(); }
            catch (Exception e) { log.Error("session end: prepare exit: " + e.Message); }
        }

        void Shutdown()
        {
            timer.Stop();
            // 仅主屏时先把副屏打开再退出
            try { orch.PrepareExit(); }
            catch (Exception e) { log.Error("prepare exit: " + e.Message); }
            foreach (HotkeyBinding b in hotkeys) UnregisterHotKey(form.Handle, b.Id);
            icon.Visible = false;
            icon.Dispose();
            ExitThread();
        }
    }
}
