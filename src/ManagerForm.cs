using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace TrayHider
{
    public class ManagerForm : Form
    {
        private readonly TrayContext app;
        private bool allowClose;

        private ListView listWindows;
        private Button btnRefresh, btnHide, btnRestore, btnRestoreAll, btnHideForeground;
        private Label lblWindowsHint;

        private TextBox txtPath, txtArgs;
        private CheckBox chkRemember, chkKeep;
        private Button btnBrowse, btnLaunch;
        private Label lblLaunchHint;

        private ListView listRules;
        private Button btnDeleteRule;
        private CheckBox chkPerWindowIcon, chkKeepDefault, chkHotkeyEnabled, chkAutoStart;
        private Label lblHotkey, lblData, lblStatus;
        private Button btnSave, btnOpenData, btnExit;

        public ManagerForm(TrayContext app)
        {
            this.app = app;
            BuildUi();
            ReloadAll();
        }

        public void AllowClose()
        {
            allowClose = true;
        }

        // ---------------- 界面搭建 ----------------

        private void BuildUi()
        {
            Text = "TrayHider —— 把窗口收进托盘";
            ClientSize = new Size(790, 560);
            MinimumSize = new Size(700, 480);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            Controls.Add(tabs);

            TabPage tabWindows = new TabPage("窗口");
            TabPage tabLaunch = new TabPage("启动到托盘");
            TabPage tabSettings = new TabPage("规则与设置");
            tabs.TabPages.Add(tabWindows);
            tabs.TabPages.Add(tabLaunch);
            tabs.TabPages.Add(tabSettings);

            // ---------- 窗口页 ----------
            listWindows = new ListView();
            listWindows.Location = new Point(12, 12);
            listWindows.Size = new Size(752, 388);
            listWindows.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            listWindows.View = View.Details;
            listWindows.FullRowSelect = true;
            listWindows.MultiSelect = true;
            listWindows.HideSelection = false;
            listWindows.Columns.Add("标题", 380);
            listWindows.Columns.Add("进程", 140);
            listWindows.Columns.Add("PID", 70);
            listWindows.Columns.Add("状态", 150);
            listWindows.DoubleClick += delegate { ToggleSelected(); };
            tabWindows.Controls.Add(listWindows);

            btnRefresh = MakeButton("刷新", 12, 410, 90, tabWindows);
            btnRefresh.Click += delegate { ReloadWindows(); };
            btnHide = MakeButton("收进托盘", 110, 410, 110, tabWindows);
            btnHide.Click += delegate { HideSelected(); };
            btnRestore = MakeButton("放出来", 228, 410, 90, tabWindows);
            btnRestore.Click += delegate { RestoreSelected(); };
            btnRestoreAll = MakeButton("恢复全部", 326, 410, 120, tabWindows);
            btnRestoreAll.Click += delegate { app.RestoreAll(true); ReloadWindows(); };
            btnHideForeground = MakeButton("把当前前台窗口收进来", 454, 410, 190, tabWindows);
            btnHideForeground.Click += delegate { app.HideForeground(); ReloadWindows(); };

            lblWindowsHint = new Label();
            lblWindowsHint.Location = new Point(12, 448);
            lblWindowsHint.Size = new Size(750, 60);
            lblWindowsHint.Text = "「显示中」的行是此刻真实存在的窗口，选中后点「收进托盘」它就消失了（任务栏也一起消失）；"
                + "「已隐藏」的行来自托盘，选中后点「放出来」即可恢复。\r\n双击一行可以来回切换。"
                + "全局快捷键 Ctrl+Alt+H 也能把当前前台窗口直接收进托盘。";
            tabWindows.Controls.Add(lblWindowsHint);

            // ---------- 启动页 ----------
            Label lblP = new Label();
            lblP.Text = "程序：";
            lblP.Location = new Point(12, 24);
            lblP.Size = new Size(56, 22);
            tabLaunch.Controls.Add(lblP);

            txtPath = new TextBox();
            txtPath.Location = new Point(70, 20);
            txtPath.Size = new Size(520, 24);
            tabLaunch.Controls.Add(txtPath);

            btnBrowse = MakeButton("浏览…", 600, 19, 150, tabLaunch);
            btnBrowse.Click += delegate { Browse(); };

            Label lblA = new Label();
            lblA.Text = "参数：";
            lblA.Location = new Point(12, 64);
            lblA.Size = new Size(56, 22);
            tabLaunch.Controls.Add(lblA);

            txtArgs = new TextBox();
            txtArgs.Location = new Point(70, 60);
            txtArgs.Size = new Size(680, 24);
            tabLaunch.Controls.Add(txtArgs);

            chkRemember = new CheckBox();
            chkRemember.Text = "记住它：以后这个程序一出现就自动收进托盘（写入规则）";
            chkRemember.Location = new Point(70, 98);
            chkRemember.Size = new Size(520, 24);
            tabLaunch.Controls.Add(chkRemember);

            chkKeep = new CheckBox();
            chkKeep.Text = "强制保持隐藏（它自己冒出来时再收回去）";
            chkKeep.Location = new Point(70, 126);
            chkKeep.Size = new Size(520, 24);
            chkKeep.Checked = true;
            tabLaunch.Controls.Add(chkKeep);

            btnLaunch = MakeButton("启动并收进托盘", 70, 164, 170, tabLaunch);
            btnLaunch.Click += delegate { Launch(); };

            lblLaunchHint = new Label();
            lblLaunchHint.Location = new Point(70, 208);
            lblLaunchHint.Size = new Size(680, 140);
            lblLaunchHint.Text = "程序会在后台启动，窗口不会出现在桌面和任务栏上 —— 适合那些「只是想让它跑着」的东西："
                + "命令行工具、常驻脚本、只在托盘里待着的小工具。\r\n\r\n"
                + "启动后 TrayHider 会盯着这个进程，它每冒出一个新窗口都会被立刻收走；"
                + "想把它放出来时，双击托盘里它自己的那个图标即可。\r\n\r\n"
                + "注意：如果程序本身要求以管理员身份运行，请让 TrayHider 也以管理员身份启动，否则隐藏会被 Windows 拦下。";
            tabLaunch.Controls.Add(lblLaunchHint);

            // ---------- 设置页 ----------
            listRules = new ListView();
            listRules.Location = new Point(12, 12);
            listRules.Size = new Size(560, 190);
            listRules.View = View.Details;
            listRules.FullRowSelect = true;
            listRules.MultiSelect = true;
            listRules.HideSelection = false;
            listRules.Columns.Add("匹配方式", 200);
            listRules.Columns.Add("启动路径", 340);
            tabSettings.Controls.Add(listRules);

            btnDeleteRule = MakeButton("删除选中规则", 584, 12, 160, tabSettings);
            btnDeleteRule.Click += delegate { DeleteSelectedRules(); };

            chkPerWindowIcon = new CheckBox();
            chkPerWindowIcon.Text = "为每个被隐藏的窗口显示一个独立托盘图标（双击即可放出来）";
            chkPerWindowIcon.Location = new Point(12, 218);
            chkPerWindowIcon.Size = new Size(600, 24);
            tabSettings.Controls.Add(chkPerWindowIcon);

            chkKeepDefault = new CheckBox();
            chkKeepDefault.Text = "默认强制保持隐藏（窗口自己冒出来时再收回去）";
            chkKeepDefault.Location = new Point(12, 246);
            chkKeepDefault.Size = new Size(600, 24);
            tabSettings.Controls.Add(chkKeepDefault);

            chkHotkeyEnabled = new CheckBox();
            chkHotkeyEnabled.Text = "启用全局快捷键";
            chkHotkeyEnabled.Location = new Point(12, 274);
            chkHotkeyEnabled.Size = new Size(600, 24);
            tabSettings.Controls.Add(chkHotkeyEnabled);

            lblHotkey = new Label();
            lblHotkey.Location = new Point(34, 300);
            lblHotkey.Size = new Size(700, 22);
            tabSettings.Controls.Add(lblHotkey);

            chkAutoStart = new CheckBox();
            chkAutoStart.Text = "开机自动启动（登录后悄悄驻留托盘）";
            chkAutoStart.Location = new Point(12, 328);
            chkAutoStart.Size = new Size(600, 24);
            tabSettings.Controls.Add(chkAutoStart);

            btnSave = MakeButton("保存设置", 12, 360, 150, tabSettings);
            btnSave.Click += delegate { SaveSettings(); };

            lblData = new Label();
            lblData.Location = new Point(12, 400);
            lblData.Size = new Size(730, 44);
            tabSettings.Controls.Add(lblData);

            btnOpenData = MakeButton("打开数据目录", 12, 448, 150, tabSettings);
            btnOpenData.Click += delegate
            {
                try { Process.Start("explorer.exe", Store.DataDir); }
                catch { }
            };

            btnExit = MakeButton("退出 TrayHider（恢复所有窗口）", 180, 448, 260, tabSettings);
            btnExit.Click += delegate { ExitApp(); };

            lblStatus = new Label();
            lblStatus.Location = new Point(12, 490);
            lblStatus.Size = new Size(730, 40);
            tabSettings.Controls.Add(lblStatus);
        }

        private Button MakeButton(string text, int x, int y, int w, Control parent)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, 28);
            parent.Controls.Add(b);
            return b;
        }

        // ---------------- 数据刷新 ----------------

        public void ReloadAll()
        {
            ReloadWindows();
            ReloadRules();
            ReloadSettings();
        }

        public void ReloadWindows()
        {
            listWindows.BeginUpdate();
            listWindows.Items.Clear();

            List<IntPtr> hiddenHandles = new List<IntPtr>();
            foreach (HiddenItem it in app.Hider.Items)
            {
                hiddenHandles.Add(it.Hwnd);
                ListViewItem li = new ListViewItem(new string[] {
                    it.DisplayTitle, it.ProcessName, it.Pid.ToString(), "已隐藏（" + it.Source + "）" });
                li.Tag = it;
                li.ForeColor = Color.Gray;
                listWindows.Items.Add(li);
            }

            int me = Process.GetCurrentProcess().Id;
            foreach (WindowInfo w in Native.ListWindows(true, me, false))
            {
                if (hiddenHandles.Contains(w.Handle)) { continue; }
                ListViewItem li = new ListViewItem(new string[] {
                    w.Display, w.ProcessName, w.Pid.ToString(), "显示中" });
                li.Tag = w;
                listWindows.Items.Add(li);
            }
            listWindows.EndUpdate();
        }

        public void ReloadRules()
        {
            listRules.BeginUpdate();
            listRules.Items.Clear();
            foreach (Rule r in app.Rules)
            {
                ListViewItem li = new ListViewItem(new string[] { r.Describe(), r.LaunchPath });
                li.Tag = r;
                listRules.Items.Add(li);
            }
            listRules.EndUpdate();
        }

        public void ReloadSettings()
        {
            AppSettings s = app.Settings;
            chkPerWindowIcon.Checked = s.PerWindowIcon;
            chkKeepDefault.Checked = s.KeepHiddenDefault;
            chkHotkeyEnabled.Checked = s.HotkeyEnabled;
            chkAutoStart.Checked = Store.GetAutoStart();

            if (s.HotkeyEnabled)
            {
                lblHotkey.Text = string.Format("收进托盘：{0}　｜　全部恢复：{1}", s.HotkeyHide, s.HotkeyRestore);
                lblHotkey.ForeColor = SystemColors.ControlText;
            }
            else
            {
                lblHotkey.Text = "快捷键已关闭。";
                lblHotkey.ForeColor = Color.Gray;
            }

            lblData.Text = "数据目录：" + Store.DataDir
                + "\r\n程序文件：" + Application.ExecutablePath
                + "\r\n当前已收进托盘：" + app.Hider.Count + " 个窗口";

            lblStatus.Text = "托盘常驻已就绪。窗口标题、进程名会随任务变化，点「刷新」重新读取。";
        }

        // ---------------- 操作 ----------------

        private void ToggleSelected()
        {
            if (listWindows.SelectedItems.Count == 0) { return; }
            ListViewItem li = listWindows.SelectedItems[0];
            HiddenItem hi = li.Tag as HiddenItem;
            if (hi != null) { app.RestoreItem(hi, true); }
            else
            {
                WindowInfo wi = li.Tag as WindowInfo;
                if (wi != null) { app.HideHandle(wi.Handle, chkKeepDefault.Checked, "管理窗口"); }
            }
            ReloadWindows();
        }

        private void HideSelected()
        {
            int n = 0;
            foreach (ListViewItem li in listWindows.SelectedItems)
            {
                WindowInfo wi = li.Tag as WindowInfo;
                if (wi != null && app.HideHandle(wi.Handle, chkKeepDefault.Checked, "管理窗口") != null) { n++; }
            }
            if (n == 0) { app.Balloon("TrayHider", "请先选中一行「显示中」的窗口。", ToolTipIcon.Info); }
            ReloadWindows();
        }

        private void RestoreSelected()
        {
            foreach (ListViewItem li in listWindows.SelectedItems)
            {
                HiddenItem hi = li.Tag as HiddenItem;
                if (hi != null) { app.RestoreItem(hi, true); }
            }
            ReloadWindows();
        }

        private void Browse()
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Title = "选择要收进托盘运行的程序";
            dlg.Filter = "程序 (*.exe;*.bat;*.cmd;*.lnk)|*.exe;*.bat;*.cmd;*.lnk|所有文件 (*.*)|*.*";
            if (dlg.ShowDialog(this) == DialogResult.OK) { txtPath.Text = dlg.FileName; }
        }

        private void Launch()
        {
            string path = txtPath.Text.Trim();
            if (path.Length == 0)
            {
                MessageBox.Show(this, "先选一个程序。", "TrayHider", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            app.LaunchHidden(path, txtArgs.Text.Trim(), chkKeep.Checked, chkRemember.Checked);
            ReloadRules();
            ReloadWindows();
        }

        private void DeleteSelectedRules()
        {
            List<Rule> doomed = new List<Rule>();
            foreach (ListViewItem li in listRules.SelectedItems)
            {
                Rule r = li.Tag as Rule;
                if (r != null) { doomed.Add(r); }
            }
            foreach (Rule r in doomed) { app.RemoveRule(r); }
            ReloadRules();
        }

        private void SaveSettings()
        {
            AppSettings s = app.Settings;
            s.PerWindowIcon = chkPerWindowIcon.Checked;
            s.KeepHiddenDefault = chkKeepDefault.Checked;
            s.HotkeyEnabled = chkHotkeyEnabled.Checked;
            s.AutoStart = chkAutoStart.Checked;
            app.ApplySettings();
            ReloadSettings();
            lblStatus.Text = "设置已保存（" + DateTime.Now.ToString("HH:mm:ss") + "）。";
        }

        private void ExitApp()
        {
            DialogResult r = MessageBox.Show(this,
                "退出后托盘图标会消失，所有被隐藏的窗口会恢复原样。确定吗？",
                "TrayHider", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) { return; }
            allowClose = true;
            app.ExitApp();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            ReloadWindows();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                app.Balloon("TrayHider 还在后台", "程序继续驻留托盘。要彻底退出，请右键托盘图标 →「退出」。", ToolTipIcon.Info);
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
