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
    public class TrayApp : ApplicationContext
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
        readonly Icon ownedIcon;
        readonly Icon awayIcon;
        // 已成功注册的快捷键
        readonly List<HotkeyBinding> hotkeys = new List<HotkeyBinding>();
        int busy;

        public TrayApp(string dir)
        {
            this.dir = dir;
            log = new Log(Path.Combine(dir, "screenswitch.log"));
            form = new MessageForm();
            IntPtr h = form.Handle;
            form.OnHotkey = OnHotkey;
            form.OnSessionEnd = OnSessionEnd;

            BuildEngine();

            menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
            menu.RenderMode = ToolStripRenderMode.System;
            ownedIcon = TrayIcons.Make(true, 32);
            awayIcon = TrayIcons.Make(false, 32);
            icon = new NotifyIcon();
            icon.Icon = awayIcon;
            icon.BalloonTipIcon = ToolTipIcon.None;
            icon.ContextMenuStrip = menu;
            icon.Visible = true;
            RebuildMenu();

            if (cfg.LoadError != null) Notify("配置文件有误，已使用默认值", cfg.LoadError);

            RegisterHotkeys();

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 500;
            timer.Tick += delegate { orch.Tick(); };
            timer.Start();

            // 还没设置过（主屏标识还是占位符）：先提示运行设置，别去提保存布局
            if (cfg.MainEdid == Config.Placeholder) Notify("还没有设置", "请运行 ScreenSwitchCli.exe setup");
            else if (!layout.HasSnapshot)
            {
                if (orch.Owned)
                {
                    try { orch.SaveLayout(); Notify("已保存当前双屏布局", "以后拿回主屏时会恢复到这个布局"); }
                    catch (Exception e) { Notify("保存布局失败", e.Message); }
                }
                else Notify("请先在双屏状态下保存布局", "右键托盘图标选择“重新保存当前布局”");
            }
        }

        string ConfigPath { get { return Path.Combine(dir, "config.json"); } }

        // 运行核心：配置、DDC、布局、中继、编排器。重新加载配置时整个重建。
        // 先全部建在局部变量里，建成功才替换字段；中途抛异常时字段保持旧值。
        void BuildEngine()
        {
            Config newCfg = Config.Load(ConfigPath);
            if (newCfg.LoadError != null) log.Error("config: " + newCfg.LoadError);
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
        }

        // 配置改了：空闲时立即重建，正在切换时等切换结束（ReloadGate）
        public void RequestReload()
        {
            if (reloadGate.Request(busy != 0)) ReloadEngine();
        }

        // 重建失败时继续用旧的运行核心：快捷键和定时器在 finally 里恢复
        void ReloadEngine()
        {
            timer.Stop();
            foreach (HotkeyBinding b in hotkeys) UnregisterHotKey(form.Handle, b.Id);
            hotkeys.Clear();
            try
            {
                BuildEngine();
                log.Info("config reloaded");
                if (cfg.LoadError != null) Notify("配置文件有误，已使用默认值", cfg.LoadError);
            }
            catch (Exception e)
            {
                log.Error("reload: " + e);
                Notify("重新加载配置失败", e.Message);
            }
            finally
            {
                RegisterHotkeys();
                RebuildMenu();
                timer.Start();
            }
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
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) { Notify("正在切换中", "请等待当前操作完成"); return; }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    a();
                    if (UiAlive()) form.BeginInvoke(new Action(RebuildMenu));
                }
                catch (Exception e) { log.Error("action: " + e); Notify("操作失败", e.Message); }
                finally
                {
                    Interlocked.Exchange(ref busy, 0);
                    if (UiAlive()) form.BeginInvoke(new Action(delegate { if (Thread.VolatileRead(ref busy) == 0 && reloadGate.OnIdle()) ReloadEngine(); }));
                }
            });
        }

        void RebuildMenu()
        {
            var old = new List<ToolStripItem>();
            foreach (ToolStripItem it in menu.Items) old.Add(it);
            menu.Items.Clear();
            foreach (ToolStripItem it in old) it.Dispose();
            foreach (SwitchEntry entry in SwitchEntries(cfg, orch.Owned))
            {
                SwitchEntry e = entry;
                menu.Items.Add(e.Label, null, delegate { RunSwitch(e); });
            }
            menu.Items.Add(new ToolStripSeparator());
            var status = menu.Items.Add(orch.Owned ? "●  主屏在本机" : "○  主屏不在本机");
            status.Enabled = false;
            icon.Icon = orch.Owned ? ownedIcon : awayIcon;
            menu.Items.Add(new ToolStripSeparator());
            // 只有主屏（layout: none）时没有布局可管，布局相关的项都不显示
            if (!cfg.LayoutNone)
            {
                // 拿回后的布局（二选一，当前选择打勾）与自动记住排列开关
                var ownedTitle = menu.Items.Add("拿回后的布局");
                ownedTitle.Enabled = false;
                var extendItem = new ToolStripMenuItem("双屏扩展", null, delegate { RunBg(delegate { orch.SetOwnedLayout(OwnedLayout.Extend); }); });
                extendItem.Checked = orch.OwnedLayout == OwnedLayout.Extend;
                menu.Items.Add(extendItem);
                var mainOnlyItem = new ToolStripMenuItem("仅主屏（关闭副屏）", null, delegate { RunBg(delegate { orch.SetOwnedLayout(OwnedLayout.MainOnly); }); });
                mainOnlyItem.Checked = orch.OwnedLayout == OwnedLayout.MainOnly;
                menu.Items.Add(mainOnlyItem);
                menu.Items.Add(new ToolStripSeparator());
                var autoSaveItem = new ToolStripMenuItem("自动记住屏幕排列", null, delegate { RunBg(delegate { orch.SetAutoSaveLayout(!orch.AutoSaveLayout); }); });
                autoSaveItem.Checked = orch.AutoSaveLayout;
                menu.Items.Add(autoSaveItem);
                menu.Items.Add("重新保存当前布局", null, delegate
                {
                    RunBg(delegate { orch.SaveLayout(); Notify("已保存当前布局", ""); });
                });
            }
            menu.Items.Add("打开日志", null, delegate
            {
                try { Process.Start(Path.Combine(dir, "screenswitch.log")); }
                catch (Exception e) { Notify("打开日志失败", e.Message); }
            });
            menu.Items.Add("退出", null, delegate { Shutdown(); });
            icon.Text = orch.Owned ? "ScreenSwitch：主屏在本机" : "ScreenSwitch：主屏不在本机";
        }

        public enum SwitchKind { Peer, TakeBack, Device }

        public class SwitchEntry
        {
            public string Label; public SwitchKind Kind; public uint Code;
            public SwitchEntry(string label, SwitchKind kind, uint code) { Label = label; Kind = kind; Code = code; }
        }

        // 菜单最上面的切换项（设计文档 8）：占有时"切给 <对方>"（没有配置对方时不显示），
        // 未占有时"拿回主屏"，然后每个设备一项。
        public static List<SwitchEntry> SwitchEntries(Config cfg, bool owned)
        {
            var list = new List<SwitchEntry>();
            if (!owned) list.Add(new SwitchEntry("拿回主屏", SwitchKind.TakeBack, 0));
            else if (cfg.PeerConfigured) list.Add(new SwitchEntry("切给 " + cfg.PeerName, SwitchKind.Peer, 0));
            foreach (Device d in cfg.Devices) list.Add(new SwitchEntry("切给 " + d.Name, SwitchKind.Device, d.Code));
            return list;
        }

        void RunSwitch(SwitchEntry e)
        {
            if (e.Kind == SwitchKind.Peer) RunBg(delegate { orch.GiveToPeer(); });
            else if (e.Kind == SwitchKind.TakeBack) RunBg(delegate { orch.TakeBack(); });
            else RunBg(delegate { orch.GiveToDevice(e.Code); });
        }

        public class HotkeyBinding
        {
            public int Id; public string Hotkey; public SwitchKind Kind; public uint Code;
            public HotkeyBinding(int id, string hotkey, SwitchKind kind, uint code) { Id = id; Hotkey = hotkey; Kind = kind; Code = code; }
        }

        // 要注册的全局快捷键（设计文档 8）：默认一个也没有；peer.hotkey 与各设备的 hotkey 配了才有。
        // peer.hotkey 随状态在"切给对方"和"拿回"之间切换。
        public static List<HotkeyBinding> HotkeyBindings(Config cfg)
        {
            var list = new List<HotkeyBinding>();
            string peer = (cfg.PeerHotkey ?? "").Trim();
            if (peer.Length > 0) list.Add(new HotkeyBinding(HK_PEER, peer, SwitchKind.Peer, 0));
            for (int i = 0; i < cfg.Devices.Count; i++)
            {
                string hk = (cfg.Devices[i].Hotkey ?? "").Trim();
                if (hk.Length > 0) list.Add(new HotkeyBinding(HK_DEVICE_BASE + i, hk, SwitchKind.Device, cfg.Devices[i].Code));
            }
            return list;
        }

        void OnHotkey(int id)
        {
            foreach (HotkeyBinding b in hotkeys)
            {
                if (b.Id != id) continue;
                uint code = b.Code;
                if (b.Kind == SwitchKind.Device) RunBg(delegate { orch.GiveToDevice(code); });
                else if (!orch.Owned) RunBg(delegate { orch.TakeBack(); });
                else if (cfg.PeerConfigured) RunBg(delegate { orch.GiveToPeer(); });
                return;
            }
        }

        void RegisterHotkeys()
        {
            foreach (HotkeyBinding b in HotkeyBindings(cfg))
            {
                uint mods, vk;
                if (!ParseHotkey(b.Hotkey, out mods, out vk)) { log.Error("hotkey invalid: " + b.Hotkey); continue; }
                if (!RegisterHotKey(form.Handle, b.Id, mods, vk)) { log.Error("hotkey register failed (in use by another program?): " + b.Hotkey); continue; }
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
