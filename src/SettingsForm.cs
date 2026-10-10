using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace ScreenSwitch
{
    // 设置窗口（docs/settings-ui-design.md 第 5 节）：五个标签页，改动立即生效，没有保存/取消。
    public class SettingsForm : Form
    {
        public const string TabPorts = "ports", TabPeer = "peer", TabScreens = "screens", TabHotkeys = "hotkeys", TabGeneral = "general";

        readonly SettingsService svc;
        readonly ITrayHost host;
        readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        // 多行错误也要完整显示：宽度封顶，高度随文字
        readonly Label status = new Label { Dock = DockStyle.Bottom, AutoSize = true, MaximumSize = new Size(600, 0), MinimumSize = new Size(0, 28), Padding = new Padding(16, 6, 16, 6) };
        // "高级"展开与否：整页重画后保持
        bool advancedOpen;
        // 对方电脑的地址正在后台保存（setup-client.ps1 在跑）：这期间再次提交不理会，免得两个脚本同时跑。
        // 在后台线程上清零，所以是 volatile。整页重画后照样有效（字段在窗口上，不在那一页上）。
        volatile bool peerSaving;

        public SettingsForm(SettingsService svc, ITrayHost host)
        {
            this.svc = svc; this.host = host;
            Font = Ui.Font;
            ClientSize = new Size(640, 560);
            MinimumSize = new Size(560, 420);
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            Icon = TrayIcons.Make(true, 32);
            Controls.Add(tabs);
            Controls.Add(status);
            // 先建好窗口句柄：后台读数完成时要 BeginInvoke 回来，此时窗口可能还没显示
            IntPtr handle = Handle;
            Build();
        }

        public void ShowTab(string name)
        {
            foreach (TabPage p in tabs.TabPages) if ((string)p.Tag == name) tabs.SelectedTab = p;
        }

        public string SelectedTabName { get { return tabs.SelectedTab == null ? TabPorts : (string)tabs.SelectedTab.Tag; } }

        public string TabTitle(string name)
        {
            foreach (TabPage p in tabs.TabPages) if ((string)p.Tag == name) return p.Text;
            return null;
        }

        public bool AdvancedOpen { get { return advancedOpen; } set { advancedOpen = value; } }

        // 重新读配置，整个窗口重画（设置写入后、别处改了配置后、换了界面语言后调用）；选中的标签页和"高级"的展开状态不变
        public void Rebuild()
        {
            string current = SelectedTabName;
            Build();
            ShowTab(current);
        }

        void Build()
        {
            Text = Tr.T("settings.title");
            tabs.SuspendLayout();
            foreach (TabPage p in tabs.TabPages) p.Dispose();
            tabs.TabPages.Clear();
            string err;
            Config c = svc.Load(out err);
            if (err != null) Ui.Error(status, err); else Ui.Ok(status, "");
            AddTab(TabPorts, Tr.T("tab.ports"), PortsPage(c));
            AddTab(TabPeer, Tr.T("tab.peer"), PeerPage(c));
            AddTab(TabScreens, Tr.T("tab.screens"), ScreensPage(c));
            AddTab(TabHotkeys, Tr.T("tab.hotkeys"), HotkeysPage(c));
            AddTab(TabGeneral, Tr.T("tab.general"), GeneralPage(c));
            tabs.ResumeLayout();
        }

        void AddTab(string key, string title, Control content)
        {
            var page = new TabPage(title) { Tag = key, AutoScroll = true, UseVisualStyleBackColor = true };
            page.Controls.Add(content);
            tabs.TabPages.Add(page);
        }

        // 只重画一页（别的页、选中的标签都不动）
        void RebuildTab(string key)
        {
            if (IsDisposed) return;
            string err;
            Config c = svc.Load(out err);
            foreach (TabPage p in tabs.TabPages)
            {
                if ((string)p.Tag != key) continue;
                p.SuspendLayout();
                while (p.Controls.Count > 0) { Control old = p.Controls[0]; p.Controls.Remove(old); old.Dispose(); }
                p.Controls.Add(PageFor(key, c));
                p.ResumeLayout();
            }
        }

        Control PageFor(string key, Config c)
        {
            switch (key)
            {
                case TabPorts: return PortsPage(c);
                case TabPeer: return PeerPage(c);
                case TabScreens: return ScreensPage(c);
                case TabHotkeys: return HotkeysPage(c);
                default: return GeneralPage(c);
            }
        }

        // 晚一点再做（等当前控件的事件处理完；事件里不能把触发事件的控件销毁）
        void Later(Action a)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); }
            catch (InvalidOperationException) { }
        }

        // 设置写入后：成功只清掉状态行（改动已生效，页面不动）；失败显示原因，并只把这一页恢复成配置里的样子
        void Done(string err, string tab)
        {
            if (err == null) { Ui.Ok(status, ""); return; }
            Ui.Error(status, err);
            Later(delegate { RebuildTab(tab); Ui.Error(status, err); });
        }

        // 在后台做慢操作（读 capabilities、ssh），做完回到界面线程；owner 已经被销毁（那一页重画过）就不回来
        void Background(Control owner, Action work, Action then)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                Action back = then;
                try { work(); }
                catch (Exception e) { string m = e.Message; back = delegate { Ui.Error(status, m); }; }
                Action guarded = delegate { if (!owner.IsDisposed) back(); };
                // 窗口已经关了就不回来
                try { if (!IsDisposed && IsHandleCreated) BeginInvoke(guarded); }
                catch (InvalidOperationException) { }
            });
        }

        // ---- 输入口 ----

        Control PortsPage(Config c)
        {
            var col = Ui.Column();
            var mainRow = Ui.Row();
            mainRow.Controls.Add(Ui.Line(c.MainEdid == Config.Placeholder ? Tr.T("ports.main-none") : Tr.T("ports.main", c.MainEdid)));
            mainRow.Controls.Add(Ui.Button(Tr.T("ports.detect"), delegate { RunDetect(null); }));
            col.Controls.Add(mainRow);
            var note = Ui.HideWhenEmpty(Ui.Note(Tr.T("ports.reading")));
            col.Controls.Add(note);
            var table = new PortTable();
            col.Controls.Add(table);
            col.Controls.Add(Ui.Button(Tr.T("ports.add"), delegate
            {
                uint code = Ui.AskCode(this, Tr.T("ports.add.prompt"));
                if (code != 0) table.AddPort(code);
            }));
            table.Changed += delegate
            {
                string err = svc.SetPorts(table.GetRows());
                Done(err, TabPorts);
                // 设备可能增删改名：快捷键页的设备行跟着变（不读显示器，只重读配置）
                if (err == null) Later(delegate { RebuildTab(TabHotkeys); });
            };
            PortsResult result = null;
            Background(table, delegate { result = svc.ReadPorts(c); }, delegate
            {
                table.SetRows(result.Rows);
                note.Text = result.MonitorKnown ? "" : Tr.T("ports.unknown");
            });
            return col;
        }

        void RunDetect(string model)
        {
            DetectResult r = null;
            Background(this, delegate { r = svc.Detect(model); }, delegate
            {
                // 几块都符合：让用户点一块，再按它的完整设备路径检测（同型号的两块也分得开）
                if (r.NeedChoice && r.Candidates.Count > 1)
                {
                    string chosen = ChooseMain(r);
                    if (chosen != null) RunDetect(chosen);
                    return;
                }
                if (r.Error != null) { Ui.Error(status, r.Error); return; }
                Ui.Ok(status, "");
                // 主屏或输入口变了：这两页重读（等当前处理完再画）
                Later(delegate { RebuildTab(TabPorts); RebuildTab(TabScreens); RebuildTab(TabHotkeys); });
            });
        }

        // 几块显示器都能控制：让用户选一块，返回它的完整设备路径；取消返回 null
        string ChooseMain(DetectResult r)
        {
            List<SetupMonitor> candidates = r.Candidates;
            var ids = new List<string>();
            foreach (SetupMonitor m in r.Monitors) ids.Add(m.DeviceId);
            using (var f = new Form { Text = Tr.T("app.name"), FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Font = Ui.Font })
            {
                var col = Ui.Column();
                col.Controls.Add(Ui.Line(Tr.T("ports.choose")));
                var list = new ListBox { Width = 360, Height = 90 };
                foreach (SetupMonitor m in candidates) list.Items.Add(Setup.MainEdidFor(m.DeviceId, ids) + "  " + m.Name);
                list.SelectedIndex = 0;
                col.Controls.Add(list);
                var row = Ui.Row();
                var ok = new Button { Text = Tr.T("common.ok"), DialogResult = DialogResult.OK, AutoSize = true };
                var cancel = new Button { Text = Tr.T("wizard.cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
                row.Controls.Add(ok); row.Controls.Add(cancel);
                col.Controls.Add(row);
                f.Controls.Add(col);
                f.AcceptButton = ok; f.CancelButton = cancel;
                if (f.ShowDialog(this) != DialogResult.OK) return null;
                return candidates[list.SelectedIndex].DeviceId;
            }
        }

        // ---- 对方电脑 ----

        Control PeerPage(Config c)
        {
            var col = Ui.Column();
            var panel = new PeerPanel();
            string key = null;
            panel.SetValues(c, "");
            Background(panel, delegate { key = svc.PublicKey(); }, delegate { panel.MyKey = key; });
            panel.FieldsCommitted += delegate
            {
                if (panel.PeerHost.Length == 0 || panel.PeerUser.Length == 0) return;
                // setup-client.ps1 最多跑 60 秒：放到后台，设置窗口与托盘共用界面线程，卡住会停掉托盘的轮询
                // （状态文件不更新，对方判定本机守护进程没在运行）。正在跑时再次提交不理会，免得两个脚本同时跑。
                if (peerSaving) return;
                peerSaving = true;
                string name = panel.PeerName, os = panel.PeerOs, host = panel.PeerHost, user = panel.PeerUser;
                string ssh = null, err = null;
                panel.SshLabel.Text = Tr.T("peer.testing");
                Background(panel, delegate
                {
                    try { err = svc.SetPeer(name, os, host, user, out ssh); }
                    catch (Exception e) { err = e.Message; }
                    finally { peerSaving = false; }
                }, delegate
                {
                    if (err != null) { panel.SshLabel.Text = ""; Ui.Error(status, err); return; }
                    Ui.Ok(status, "");
                    panel.SshLabel.Text = (ssh ?? "").Trim();
                    // 换了地址时对方的输入口清零：输入口页重画，免得下次在那页改动把旧编码写回去
                    Later(delegate { RebuildTab(TabPorts); });
                });
            };
            panel.AuthorizeClicked += delegate
            {
                string theirs = panel.PeerKeyBox.Text, err = null, output;
                Background(panel, delegate { err = svc.Authorize(theirs, true, out output); }, delegate
                {
                    if (err != null) Ui.Error(panel.AuthLabel, err); else Ui.Ok(panel.AuthLabel, Tr.T("peer.authorized"));
                });
            };
            panel.TestClicked += delegate
            {
                Ui.Ok(panel.TestLabel, Tr.T("peer.testing"));
                PeerTestResult r = null;
                Background(panel, delegate { r = svc.TestPeer(); }, delegate
                {
                    ShowTest(panel.TestLabel, r, svc);
                    // 存下了对方的输入口编码：输入口页重画
                    if (r.CodeSaved) Later(delegate { RebuildTab(TabPorts); });
                });
            };
            col.Controls.Add(panel);
            col.Controls.Add(Ui.Note(Tr.T("peer.skip-hint")));
            col.Controls.Add(Ui.Button(Tr.T("peer.clear"), delegate
            {
                string err = svc.ClearPeer();
                Done(err, TabPeer);
                // 对方的地址和输入口都清掉了：这两页重画（快捷键页只列设备和对方，对方的快捷键行不变）
                if (err == null) Later(delegate { RebuildTab(TabPeer); RebuildTab(TabPorts); });
            }));
            return col;
        }

        public static void ShowTest(Label l, PeerTestResult r, SettingsService svc)
        {
            if (r.Error != null) { Ui.Error(l, r.Error); return; }
            if (r.Code == 0) { Ui.Ok(l, Tr.T("peer.reached-nocode", r.Name)); return; }
            string text = Tr.T("peer.reached", r.Name, Ports.Label(r.Code));
            string err;
            Config c = svc.Load(out err);
            if (err == null && c.PeerCode != 0 && c.PeerCode != r.Code) text += "\n" + Tr.T("peer.port-mismatch", Ports.Label(c.PeerCode), Ports.Label(r.Code));
            Ui.Ok(l, text);
        }

        // ---- 屏幕 ----

        Control ScreensPage(Config c)
        {
            var col = Ui.Column();
            var comboRow = Ui.Row();
            DetectResult probe = null;
            var combo = Ui.Line(Tr.T("screens.combo", c.LayoutNone ? Tr.T("screens.main-only") : Tr.T("screens.main-plus", "…")));
            comboRow.Controls.Add(combo);
            comboRow.Controls.Add(Ui.Button(Tr.T("ports.detect"), delegate { RunDetect(c.MainEdid); }));
            col.Controls.Add(comboRow);
            if (!c.LayoutNone)
                Background(combo, delegate { probe = svc.Probe(c.MainEdid); }, delegate
                {
                    combo.Text = Tr.T("screens.combo", probe.Error == null && probe.OtherScreen.Length > 0 ? Tr.T("screens.main-plus", probe.OtherScreen) : Tr.T("screens.main-plus", "?"));
                });
            if (c.LayoutNone) return col;

            col.Controls.Add(Ui.Line(Tr.T("screens.after-take")));
            var extend = new RadioButton { Text = Tr.T("screens.extend"), AutoSize = true, Checked = host.OwnedLayout == OwnedLayout.Extend, Margin = new Padding(12, 2, 0, 2) };
            var mainOnly = new RadioButton { Text = Tr.T("screens.mainonly"), AutoSize = true, Checked = host.OwnedLayout == OwnedLayout.MainOnly, Margin = new Padding(12, 2, 0, 8) };
            extend.CheckedChanged += delegate { if (extend.Checked) host.SetOwnedLayout(OwnedLayout.Extend); };
            mainOnly.CheckedChanged += delegate { if (mainOnly.Checked) host.SetOwnedLayout(OwnedLayout.MainOnly); };
            col.Controls.Add(extend); col.Controls.Add(mainOnly);
            var auto = new CheckBox { Text = Tr.T("screens.autosave"), AutoSize = true, Checked = host.AutoSaveLayout };
            auto.CheckedChanged += delegate { host.SetAutoSaveLayout(auto.Checked); };
            col.Controls.Add(auto);
            col.Controls.Add(Ui.Note(Tr.T("screens.autosave.note")));
            var saved = Ui.Line("");
            col.Controls.Add(Ui.Button(Tr.T("screens.save-now"), delegate
            {
                // 正在切换时要等切换结束（最长约 20 秒）：放到后台，窗口不卡
                string err = null;
                Background(saved, delegate { err = host.SaveLayoutNow(); }, delegate
                {
                    if (err != null) Ui.Error(saved, err); else Ui.Ok(saved, Tr.T("screens.saved"));
                });
            }));
            col.Controls.Add(Ui.Note(Tr.T("screens.save-now.note")));
            col.Controls.Add(saved);
            return col;
        }

        // ---- 快捷键 ----

        Control HotkeysPage(Config c)
        {
            var col = Ui.Column();
            col.Controls.Add(Ui.Note(Tr.T("hotkeys.note")));
            var grid = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            AddHotkeyRow(grid, Tr.T("hotkey.target.peer"), "peer", c.PeerHotkey);
            foreach (Device d in c.Devices) AddHotkeyRow(grid, Tr.T("menu.give-to", d.Name), d.Code.ToString(), d.Hotkey);
            col.Controls.Add(grid);
            return col;
        }

        void AddHotkeyRow(TableLayoutPanel grid, string label, string target, string current)
        {
            int i = grid.RowCount++;
            var box = new HotkeyBox { Text = current ?? "" };
            var problem = Ui.Note("");
            if (!string.IsNullOrEmpty(current) && host.HotkeyProblems.Contains(current.Trim())) Ui.Error(problem, Tr.T("hotkeys.failed"));
            Action commit = delegate
            {
                string err = svc.SetOption("hotkey", new string[] { target, box.Text.Length == 0 ? "none" : box.Text });
                Done(err, TabHotkeys);
                // 写入配置时，托盘程序的进程内重载（SettingsService.Reload）已排进界面线程的消息队列；
                // 这里的重画排在它后面，读到的"没能注册的快捷键"才是新的
                if (err == null) Later(delegate { RebuildTab(TabHotkeys); });
            };
            box.Committed += commit;
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 12, 0) }, 0, i);
            grid.Controls.Add(box, 1, i);
            grid.Controls.Add(Ui.Button(Tr.T("hotkeys.clear"), delegate { box.Text = ""; commit(); }), 2, i);
            grid.Controls.Add(problem, 3, i);
        }

        // ---- 通用 ----

        Control GeneralPage(Config c)
        {
            var col = Ui.Column();
            var auto = new CheckBox { Text = Tr.T("general.autostart"), AutoSize = true, Checked = svc.AutostartEnabled };
            auto.CheckedChanged += delegate
            {
                string err = svc.SetAutostart(auto.Checked);
                if (err != null) Ui.Error(status, err);
            };
            col.Controls.Add(auto);
            col.Controls.Add(LanguageRow(c));
            col.Controls.Add(Ui.Button(Tr.T("general.wizard"), delegate { host.RunWizard(); }));
            col.Controls.Add(Ui.Button(Tr.T("general.log"), delegate { host.OpenLog(); }));
            col.Controls.Add(Ui.Note(Tr.T("general.version", AppInfo.Version)));

            var adv = Ui.Column();
            adv.Padding = new Padding(12, 0, 0, 0);
            adv.Visible = advancedOpen;
            var toggle = Ui.Button(Tr.T("general.advanced") + (advancedOpen ? " ▾" : " ▸"), null);
            toggle.Click += delegate
            {
                advancedOpen = !advancedOpen;
                adv.Visible = advancedOpen;
                toggle.Text = Tr.T("general.advanced") + (advancedOpen ? " ▾" : " ▸");
            };
            col.Controls.Add(toggle);
            var direct = new CheckBox { Text = Tr.T("general.directtake"), AutoSize = true, Checked = c.DirectTake };
            direct.CheckedChanged += delegate { Done(svc.SetOption("direct-take", new string[] { direct.Checked ? "on" : "off" }), TabGeneral); };
            adv.Controls.Add(direct);
            adv.Controls.Add(Ui.Line(Tr.T("general.aftertake")));
            var after = new TextBox { Width = 480, Text = c.AfterTakeCommand };
            Ui.OnCommit(after, delegate { Done(svc.SetOption("after-take", new string[] { after.Text }), TabGeneral); });
            adv.Controls.Add(after);
            adv.Controls.Add(Ui.Button(Tr.T("general.openfolder"), delegate
            {
                try { Process.Start("explorer.exe", "\"" + svc.Dir + "\""); }
                catch (Exception e) { Ui.Error(status, e.Message); }
            }));
            col.Controls.Add(adv);
            return col;
        }

        // 界面语言：跟随系统 / 中文 / English。写入配置后由托盘程序换语言，整个窗口随后重画（不在这个处理里重画）
        Control LanguageRow(Config c)
        {
            string[] values = { "auto", "zh", "en" };
            var row = Ui.Row();
            row.Controls.Add(new Label { Text = Tr.T("general.language"), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 12, 0) });
            var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
            box.Items.AddRange(new object[] { Tr.T("general.language.auto"), Tr.T("general.language.zh"), Tr.T("general.language.en") });
            box.SelectedIndex = Math.Max(0, Array.IndexOf(values, Tr.Setting(c.UiLanguage)));
            box.SelectionChangeCommitted += delegate
            {
                string v = values[Math.Max(0, box.SelectedIndex)];
                string err = svc.SetOption("language", new string[] { v });
                Done(err, TabGeneral);
                if (err == null) host.LanguageChanged(v);
            };
            row.Controls.Add(box);
            return row;
        }
    }
}
