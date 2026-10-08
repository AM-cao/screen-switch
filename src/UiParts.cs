using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenSwitch
{
    // 托盘程序提供给设置窗口和向导的东西（运行中的编排器上的设置、日志、向导入口）
    public interface ITrayHost
    {
        OwnedLayout OwnedLayout { get; }
        bool AutoSaveLayout { get; }
        void SetOwnedLayout(OwnedLayout v);
        void SetAutoSaveLayout(bool on);
        // 返回 null 成功，否则为说明
        string SaveLayoutNow();
        void RunWizard();
        void OpenLog();
        // 没能注册的快捷键（写法）
        IList<string> HotkeyProblems { get; }
    }

    // 界面的小工具：统一字体与间距，所有文字左对齐
    public static class Ui
    {
        public static Font Font { get { return SystemFonts.MessageBoxFont; } }

        public static Label Title(string text)
        {
            var l = new Label { Text = text, AutoSize = true, Font = new Font(Font.FontFamily, Font.Size + 1, FontStyle.Bold), Margin = new Padding(0, 8, 0, 6) };
            return l;
        }

        public static Label Note(string text)
        {
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 8) };
        }

        public static Label Line(string text)
        {
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 6, 0, 4) };
        }

        // 文字为空时不占位置（空标签也会占一行高度）
        public static Label HideWhenEmpty(Label l)
        {
            l.Visible = l.Text.Length > 0;
            l.TextChanged += delegate { l.Visible = l.Text.Length > 0; };
            return l;
        }

        public static FlowLayoutPanel Column()
        {
            return new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Padding = new Padding(16, 12, 16, 12) };
        }

        public static FlowLayoutPanel Row()
        {
            return new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 2, 0, 2) };
        }

        public static Button Button(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 2, 8, 2) };
            b.Click += click;
            return b;
        }

        public static void Error(Label l, string text)
        {
            l.ForeColor = Color.Firebrick;
            l.Text = text ?? "";
        }

        public static void Ok(Label l, string text)
        {
            l.ForeColor = SystemColors.ControlText;
            l.Text = text ?? "";
        }

        // 文字框：回车或失去焦点时提交一次（内容没变不提交）
        public static void OnCommit(TextBox box, Action commit)
        {
            string last = box.Text;
            Action fire = delegate { if (box.Text != last) { last = box.Text; commit(); } };
            box.Leave += delegate { fire(); };
            box.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; fire(); } };
        }

        // 小对话框：输入一个 1-255 的编码；取消返回 0
        public static uint AskCode(IWin32Window owner, string prompt)
        {
            using (var f = new Form { Text = Tr.T("app.name"), FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Font = Font })
            {
                var col = Column();
                col.Controls.Add(Line(prompt));
                var num = new NumericUpDown { Minimum = 1, Maximum = 255, Value = 17, Width = 80 };
                col.Controls.Add(num);
                var row = Row();
                var ok = new Button { Text = Tr.T("common.ok"), DialogResult = DialogResult.OK, AutoSize = true };
                var cancel = new Button { Text = Tr.T("wizard.cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
                row.Controls.Add(ok); row.Controls.Add(cancel);
                col.Controls.Add(row);
                f.Controls.Add(col);
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog(owner) == DialogResult.OK ? (uint)num.Value : 0;
            }
        }
    }

    // 接口表：一口一行——口的名字、接的是什么、设备名（只在选"设备"时显示）
    public class PortTable : UserControl
    {
        readonly TableLayoutPanel grid;
        readonly List<PortRow> rows = new List<PortRow>();
        bool filling;
        public event Action Changed;

        public PortTable()
        {
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            Controls.Add(grid);
        }

        public void SetRows(IList<PortRow> list)
        {
            filling = true;
            grid.SuspendLayout();
            foreach (Control c in grid.Controls) c.Dispose();
            grid.Controls.Clear();
            rows.Clear();
            foreach (PortRow r in list) AddRow(new PortRow(r.Code, r.Use, r.DeviceName, r.FromMonitor));
            grid.ResumeLayout();
            filling = false;
        }

        public List<PortRow> GetRows()
        {
            var l = new List<PortRow>();
            foreach (PortRow r in rows) l.Add(new PortRow(r.Code, r.Use, r.DeviceName, r.FromMonitor));
            return l;
        }

        public void AddPort(uint code)
        {
            if (Ports.Find(rows, code) != null) return;
            AddRow(new PortRow(code, PortUse.None, "", false));
        }

        void AddRow(PortRow r)
        {
            int i = rows.Count;
            rows.Add(r);
            grid.RowCount = rows.Count;
            var label = new Label { Text = Ports.Label(r.Code), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 16, 0) };
            var use = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
            var name = new TextBox { Width = 180, Text = r.DeviceName, Visible = r.Use == PortUse.Device };
            if (r.Use == PortUse.Self)
            {
                use.Items.Add(Tr.T("port.use.self"));
                use.SelectedIndex = 0;
                use.Enabled = false;
            }
            else
            {
                use.Items.AddRange(new object[] { Tr.T("port.use.none"), Tr.T("port.use.peer"), Tr.T("port.use.device") });
                use.SelectedIndex = r.Use == PortUse.Peer ? 1 : r.Use == PortUse.Device ? 2 : 0;
                use.SelectedIndexChanged += delegate
                {
                    r.Use = use.SelectedIndex == 1 ? PortUse.Peer : use.SelectedIndex == 2 ? PortUse.Device : PortUse.None;
                    name.Visible = r.Use == PortUse.Device;
                    // 选了"设备"先等名字填好再提交
                    if (r.Use == PortUse.Device && name.Text.Trim().Length == 0) name.Focus();
                    else Fire();
                };
            }
            Ui.OnCommit(name, delegate { r.DeviceName = name.Text.Trim(); Fire(); });
            grid.Controls.Add(label, 0, i);
            grid.Controls.Add(use, 1, i);
            grid.Controls.Add(name, 2, i);
        }

        void Fire()
        {
            if (!filling && Changed != null) Changed();
        }
    }

    // 对方电脑：名字、系统、地址、用户名；本机公钥；对方公钥与授权；测试连接
    public class PeerPanel : UserControl
    {
        public readonly TextBox NameBox = new TextBox { Width = 220 };
        public readonly ComboBox OsBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        public readonly TextBox HostBox = new TextBox { Width = 220 };
        public readonly TextBox UserBox = new TextBox { Width = 160 };
        public readonly TextBox MyKeyBox = new TextBox { Width = 440, ReadOnly = true };
        public readonly TextBox PeerKeyBox = new TextBox { Width = 440 };
        public readonly Label AuthLabel = Ui.HideWhenEmpty(Ui.Line(""));
        public readonly Label TestLabel = Ui.HideWhenEmpty(Ui.Line(""));
        public readonly Label SshLabel = Ui.HideWhenEmpty(Ui.Note(""));
        public event Action FieldsCommitted;
        public event Action AuthorizeClicked;
        public event Action TestClicked;

        public PeerPanel()
        {
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            OsBox.Items.AddRange(new object[] { "Windows", "Mac" });
            var col = Ui.Column();
            col.Padding = new Padding(0);
            col.Dock = DockStyle.None;
            var form = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            AddField(form, Tr.T("peer.name"), NameBox);
            AddField(form, Tr.T("peer.os"), OsBox);
            AddField(form, Tr.T("peer.host"), HostBox);
            AddField(form, Tr.T("peer.user"), UserBox);
            col.Controls.Add(form);
            col.Controls.Add(SshLabel);
            col.Controls.Add(Ui.Line(Tr.T("peer.mykey")));
            var keyRow = Ui.Row();
            keyRow.Controls.Add(MyKeyBox);
            keyRow.Controls.Add(Ui.Button(Tr.T("peer.copy"), delegate
            {
                if (MyKeyBox.Text.Length == 0) return;
                try { Clipboard.SetText(MyKeyBox.Text); }
                catch (System.Runtime.InteropServices.ExternalException e) { Ui.Error(AuthLabel, e.Message); return; }
                Ui.Ok(AuthLabel, Tr.T("peer.copied"));
            }));
            col.Controls.Add(keyRow);
            col.Controls.Add(Ui.Line(Tr.T("peer.theirkey")));
            var peerRow = Ui.Row();
            peerRow.Controls.Add(PeerKeyBox);
            peerRow.Controls.Add(Ui.Button(Tr.T("peer.authorize"), delegate { if (AuthorizeClicked != null) AuthorizeClicked(); }));
            col.Controls.Add(peerRow);
            col.Controls.Add(Ui.Note(Tr.T("peer.mac-remote-login")));
            col.Controls.Add(AuthLabel);
            col.Controls.Add(Ui.Button(Tr.T("peer.test"), delegate { if (TestClicked != null) TestClicked(); }));
            col.Controls.Add(TestLabel);
            Controls.Add(col);
            Action commit = delegate { if (FieldsCommitted != null) FieldsCommitted(); };
            Ui.OnCommit(NameBox, commit); Ui.OnCommit(HostBox, commit); Ui.OnCommit(UserBox, commit);
            OsBox.SelectionChangeCommitted += delegate { commit(); };
        }

        static void AddField(TableLayoutPanel t, string label, Control c)
        {
            int i = t.RowCount++;
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 12, 0) }, 0, i);
            t.Controls.Add(c, 1, i);
        }

        public void SetValues(Config c, string myKey)
        {
            NameBox.Text = c.PeerConfigured ? c.PeerName : "";
            OsBox.SelectedIndex = c.PeerOs == "windows" ? 0 : 1;
            HostBox.Text = c.PeerHost; UserBox.Text = c.PeerUser;
            MyKeyBox.Text = myKey ?? "";
        }

        public string PeerName { get { return NameBox.Text.Trim(); } }
        public string PeerOs { get { return OsBox.SelectedIndex == 0 ? "windows" : "mac"; } }
        public string PeerHost { get { return HostBox.Text.Trim(); } }
        public string PeerUser { get { return UserBox.Text.Trim(); } }
        public bool Filled { get { return PeerHost.Length > 0 || PeerUser.Length > 0; } }
    }

    // 快捷键输入框：按下组合键即录下（要带修饰键）；Backspace / Delete / Esc 清空
    public class HotkeyBox : TextBox
    {
        [DllImport("user32.dll")] static extern short GetKeyState(int key);
        const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        public event Action Committed;

        public HotkeyBox()
        {
            ReadOnly = true; Width = 160; BackColor = SystemColors.Window; ShortcutsEnabled = false;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.SuppressKeyPress = true; e.Handled = true;
            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete || e.KeyCode == Keys.Escape)
            {
                Text = "";
                if (Committed != null) Committed();
                return;
            }
            string key = KeyName(e.KeyCode);
            bool win = (GetKeyState(VK_LWIN) & 0x8000) != 0 || (GetKeyState(VK_RWIN) & 0x8000) != 0;
            string combo = Combo(e.Control, e.Alt, e.Shift, win, key);
            if (combo == null) return;
            Text = combo;
            if (Committed != null) Committed();
        }

        // 录下的组合；要带 Ctrl、Alt 或 Win（Shift 单独不算），键不支持也不收：返回 null
        public static string Combo(bool ctrl, bool alt, bool shift, bool win, string key)
        {
            if (key == null || !(ctrl || alt || win)) return null;
            return Format(ctrl, alt, shift, win, key);
        }

        public static string KeyName(Keys k)
        {
            if (k >= Keys.A && k <= Keys.Z) return ((char)('A' + (k - Keys.A))).ToString();
            if (k >= Keys.D0 && k <= Keys.D9) return ((char)('0' + (k - Keys.D0))).ToString();
            if (k >= Keys.F1 && k <= Keys.F24) return "F" + (k - Keys.F1 + 1);
            return null;
        }

        public static string Format(bool ctrl, bool alt, bool shift, bool win, string key)
        {
            var parts = new List<string>();
            if (ctrl) parts.Add("Ctrl");
            if (alt) parts.Add("Alt");
            if (shift) parts.Add("Shift");
            if (win) parts.Add("Win");
            parts.Add(key);
            return string.Join("+", parts.ToArray());
        }
    }
}
