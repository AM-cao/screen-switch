using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace ScreenSwitch
{
    // 向导里不碰界面的部分
    public static class WizardLogic
    {
        // 把"对方电脑"页的输入写进工作中的配置；地址和用户名都空 = 跳过（清掉对方）。不合法返回说明，配置不动。
        public static string ApplyPeer(Config work, string name, string os, string host, string user)
        {
            string h = (host ?? "").Trim(), u0 = (user ?? "").Trim();
            if (h.Length == 0 && u0.Length == 0)
            {
                work.PeerHost = ""; work.PeerUser = ""; work.PeerCode = 0;
                return null;
            }
            if (os != "windows" && os != "mac") return Tr.T("err.peer.os");
            if (!Setup.ValidHost(h)) return Tr.T("err.peer.host");
            string u = Setup.CleanUser(u0);
            if (u == null) return Tr.T("err.peer.user");
            if (!string.Equals(h, work.PeerHost, StringComparison.OrdinalIgnoreCase)) work.PeerCode = 0;
            if (os != work.PeerOs) work.PeerProgram = "";
            work.PeerOs = os; work.PeerHost = h; work.PeerUser = u;
            string n = (name ?? "").Trim();
            work.PeerName = n.Length > 0 ? n : Config.DefaultPeerName(os);
            return null;
        }

        public static string Summary(Config c)
        {
            string none = Tr.T("wizard.summary.none");
            var devs = new List<string>();
            foreach (Device d in c.Devices) devs.Add(d.Name + " " + Ports.Label(d.Code));
            return Tr.T("wizard.summary", c.MainEdid, Ports.Label(c.SelfCode),
                c.LayoutNone ? Tr.T("screens.main-only") : Tr.T("screens.main-plus", "…"),
                c.PeerConfigured ? c.PeerName : none,
                devs.Count == 0 ? none : string.Join("、", devs.ToArray()));
        }
    }

    // 首次启动向导（docs/settings-ui-design.md 第 4 节）：欢迎 → 主屏 → 输入口 → 对方电脑 → 完成。
    // 只改内存里的工作配置；点"完成"才写 config.json。
    public class WizardForm : Form
    {
        readonly SettingsService svc;
        Config work;
        readonly Panel body = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        readonly Button back, next;
        readonly Label error = new Label { AutoSize = true, ForeColor = Color.Firebrick, Margin = new Padding(16, 8, 16, 0) };
        readonly Control[] pages = new Control[5];
        DetectResult detected;
        readonly PortTable portTable = new PortTable();
        readonly PeerPanel peerPanel = new PeerPanel();
        readonly CheckBox autostart = new CheckBox { AutoSize = true, Checked = true };
        readonly Label summary;
        public int PageIndex { get; private set; }
        public event Action Finished;

        public WizardForm(SettingsService svc)
        {
            this.svc = svc;
            string err;
            work = svc.Load(out err);
            if (err != null) work = new Config();
            Text = Tr.T("wizard.title");
            Font = Ui.Font;
            ClientSize = new Size(640, 620);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = TrayIcons.Make(true, 32);
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(12) };
            next = new Button { Text = Tr.T("wizard.next"), AutoSize = true, Padding = new Padding(12, 2, 12, 2) };
            back = new Button { Text = Tr.T("wizard.back"), AutoSize = true, Padding = new Padding(12, 2, 12, 2) };
            next.Click += delegate { Next(); };
            back.Click += delegate { GoTo(PageIndex - 1); };
            bottom.Controls.Add(next); bottom.Controls.Add(back); bottom.Controls.Add(error);
            Controls.Add(body);
            Controls.Add(bottom);
            summary = Ui.Line("");
            autostart.Text = Tr.T("general.autostart");
            pages[0] = Welcome(); pages[1] = MainPage(); pages[2] = PortsPage(); pages[3] = PeerPage(); pages[4] = DonePage();
            // 先建好窗口句柄：后台读数完成时要 BeginInvoke 回来
            IntPtr handle = Handle;
            GoTo(0);
        }

        // 不在窗口里的页面不会随窗口一起释放，这里补上
        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (Control p in pages) if (p != null) p.Dispose();
            base.Dispose(disposing);
        }

        public void GoTo(int page)
        {
            if (page < 0 || page >= pages.Length) return;
            PageIndex = page;
            error.Text = "";
            body.Controls.Clear();
            body.Controls.Add(pages[page]);
            back.Visible = page > 0;
            next.Text = page == pages.Length - 1 ? Tr.T("wizard.finish") : Tr.T("wizard.next");
            if (page == 1) StartDetect(null);
            if (page == 2) LoadPorts();
            if (page == 3) LoadKey();
            if (page == 4) summary.Text = WizardLogic.Summary(work);
        }

        void Next()
        {
            string err = null;
            if (PageIndex == 1)
            {
                if (detected == null || detected.Error != null) err = detected == null ? Tr.T("wizard.main.detecting") : detected.Error;
                else
                {
                    work.MainEdid = detected.MainEdid; work.SelfCode = detected.SelfCode; work.Layout = detected.Layout;
                    if (work.PeerCode == work.SelfCode) work.PeerCode = 0;
                    work.Devices.RemoveAll(delegate(Device d) { return d.Code == work.SelfCode; });
                }
            }
            else if (PageIndex == 2) err = Ports.Apply(work, portTable.GetRows());
            else if (PageIndex == 3) err = WizardLogic.ApplyPeer(work, peerPanel.PeerName, peerPanel.PeerOs, peerPanel.PeerHost, peerPanel.PeerUser);
            else if (PageIndex == 4)
            {
                err = svc.SaveWizard(work, autostart.Checked);
                if (err == null)
                {
                    if (Finished != null) Finished();
                    Close();
                    return;
                }
            }
            if (err != null) { error.Text = err; return; }
            GoTo(PageIndex + 1);
        }

        void Background(Action work2, Action then)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                Action back = then;
                try { work2(); }
                catch (Exception e) { string m = e.Message; back = delegate { error.Text = m; }; }
                try { if (!IsDisposed && IsHandleCreated) BeginInvoke(back); }
                catch (InvalidOperationException) { }
            });
        }

        // ---- 页面 ----

        Control Page(string title, string text)
        {
            var col = Ui.Column();
            col.Controls.Add(Ui.Title(title));
            if (text != null) col.Controls.Add(Ui.Line(text));
            return col;
        }

        Control Welcome()
        {
            var col = Page(Tr.T("wizard.welcome.title"), Tr.T("wizard.welcome.text"));
            col.Controls.Add(Ui.Line(Tr.T("wizard.welcome.before")));
            return col;
        }

        readonly ListBox monitorList = new ListBox { Width = 560, Height = 110 };
        readonly Label mainInfo = Ui.Line("");

        Control MainPage()
        {
            var col = Page(Tr.T("wizard.main.title"), null);
            col.Controls.Add(monitorList);
            col.Controls.Add(mainInfo);
            col.Controls.Add(Ui.Button(Tr.T("ports.detect"), delegate { StartDetect(null); }));
            monitorList.SelectedIndexChanged += delegate
            {
                if (detected == null || detected.Candidates.Count < 2 || monitorList.SelectedIndex < 0) return;
                SetupMonitor m = detected.Monitors[monitorList.SelectedIndex];
                if (m.Ddc) StartDetect(Setup.ModelId(m.DeviceId));
            };
            return col;
        }

        void StartDetect(string model)
        {
            mainInfo.Text = Tr.T("wizard.main.detecting");
            DetectResult r = null;
            Background(delegate { r = svc.Probe(model); }, delegate
            {
                detected = r;
                if (model == null)
                {
                    monitorList.Items.Clear();
                    foreach (SetupMonitor m in r.Monitors)
                        monitorList.Items.Add(Setup.ModelId(m.DeviceId) + "  " + m.Name + "  —  " + (m.Ddc ? Tr.T("wizard.main.ddc-yes") : Tr.T("wizard.main.ddc-no")));
                }
                if (r.Error != null)
                {
                    mainInfo.Text = r.Candidates.Count > 1 ? Tr.T("wizard.main.pick") : r.Error;
                    return;
                }
                mainInfo.Text = Tr.T("wizard.main.self", Ports.Label(r.SelfCode)) + "\n"
                    + (r.Layout == "none" ? Tr.T("wizard.main.only") : r.OtherScreen.Length == 0 ? "" : Tr.T("wizard.main.other", r.OtherScreen));
            });
        }

        Control PortsPage()
        {
            var col = Page(Tr.T("wizard.ports.title"), Tr.T("wizard.ports.text"));
            col.Controls.Add(portTable);
            col.Controls.Add(Ui.Button(Tr.T("ports.add"), delegate
            {
                uint code = Ui.AskCode(this, Tr.T("ports.add.prompt"));
                if (code != 0) portTable.AddPort(code);
            }));
            return col;
        }

        void LoadPorts()
        {
            PortsResult p = null;
            Config snapshot = work;
            Background(delegate { p = svc.ReadPorts(snapshot); }, delegate
            {
                portTable.SetRows(p.Rows);
                if (!p.MonitorKnown) error.Text = Tr.T("ports.unknown");
            });
        }

        Control PeerPage()
        {
            var col = Page(Tr.T("wizard.peer.title"), Tr.T("wizard.peer.text"));
            peerPanel.SetValues(work, "");
            peerPanel.AuthorizeClicked += delegate
            {
                string theirs = peerPanel.PeerKeyBox.Text, err = null, output;
                Background(delegate { err = svc.Authorize(theirs, true, out output); }, delegate
                {
                    if (err != null) Ui.Error(peerPanel.AuthLabel, err); else Ui.Ok(peerPanel.AuthLabel, Tr.T("peer.authorized"));
                });
            };
            peerPanel.TestClicked += delegate
            {
                string err = WizardLogic.ApplyPeer(work, peerPanel.PeerName, peerPanel.PeerOs, peerPanel.PeerHost, peerPanel.PeerUser);
                if (err != null) { Ui.Error(peerPanel.TestLabel, err); return; }
                Ui.Ok(peerPanel.TestLabel, Tr.T("peer.testing"));
                PeerTestResult r = null;
                string sshOut = "";
                Config snapshot = work;
                Background(delegate
                {
                    svc.Env.SshClientSetup(snapshot.PeerUser, snapshot.PeerHost, out sshOut);
                    r = svc.TestPeerWith(snapshot);
                }, delegate
                {
                    peerPanel.SshLabel.Text = (sshOut ?? "").Trim();
                    if (r.Error == null && r.Code != 0 && r.Code != work.SelfCode) work.PeerCode = r.Code;
                    if (r.Error != null) Ui.Error(peerPanel.TestLabel, r.Error);
                    else Ui.Ok(peerPanel.TestLabel, r.Code == 0 ? Tr.T("peer.reached-nocode", r.Name) : Tr.T("peer.reached", r.Name, Ports.Label(r.Code)));
                });
            };
            col.Controls.Add(peerPanel);
            col.Controls.Add(Ui.Note(Tr.T("peer.skip-hint")));
            return col;
        }

        void LoadKey()
        {
            string key = null;
            Background(delegate { key = svc.PublicKey(); }, delegate { peerPanel.MyKeyBox.Text = key ?? ""; });
        }

        Control DonePage()
        {
            var col = Page(Tr.T("wizard.done.title"), Tr.T("wizard.done.text"));
            col.Controls.Add(summary);
            col.Controls.Add(autostart);
            return col;
        }
    }
}
