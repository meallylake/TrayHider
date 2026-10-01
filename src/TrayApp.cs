using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TrayHider
{
    public enum CliMode
    {
        Default, Resident, List, HideForeground, HideMatch, RestoreMatch, RestoreAll, Launch, Manager, Quit, Help
    }

    public class CliOptions
    {
        public CliMode Mode = CliMode.Default;
        public string Arg = "";
        public string LaunchArgs = "";
        public bool Keep = true;
        public string OutFile = "";
        public string DataDir = "";

        private static string Next(string[] args, ref int i)
        {
            if (i + 1 < args.Length) { i++; return args[i]; }
            return "";
        }

        public static CliOptions Parse(string[] args)
        {
            CliOptions o = new CliOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                switch (a)
                {
                    case "--resident": o.Mode = CliMode.Resident; break;
                    case "--list": o.Mode = CliMode.List; break;
                    case "--hide-foreground": o.Mode = CliMode.HideForeground; break;
                    case "--hide": o.Mode = CliMode.HideMatch; o.Arg = Next(args, ref i); break;
                    case "--restore": o.Mode = CliMode.RestoreMatch; o.Arg = Next(args, ref i); break;
                    case "--restore-all": o.Mode = CliMode.RestoreAll; break;
                    case "--launch": o.Mode = CliMode.Launch; o.Arg = Next(args, ref i); break;
                    case "--args": o.LaunchArgs = Next(args, ref i); break;
                    case "--manager": o.Mode = CliMode.Manager; break;
                    case "--quit": o.Mode = CliMode.Quit; break;
                    case "--keep": o.Keep = true; break;
                    case "--no-keep": o.Keep = false; break;
                    case "--out": o.OutFile = Next(args, ref i); break;
                    case "--data-dir": o.DataDir = Next(args, ref i); break;
                    case "--help":
                    case "-h":
                    case "/?": o.Mode = CliMode.Help; break;
                }
            }
            return o;
        }

        public string ToCommand()
        {
            switch (Mode)
            {
                case CliMode.HideForeground:
                    return "hide-foreground\t" + Native.GetForegroundWindow().ToInt64() + "\t" + (Keep ? "1" : "0");
                case CliMode.HideMatch:
                    return "hide-match\t" + Arg + "\t" + (Keep ? "1" : "0");
                case CliMode.RestoreMatch:
                    return "restore-match\t" + Arg;
                case CliMode.RestoreAll:
                    return "restore-all";
                case CliMode.Launch:
                    return "launch\t" + Arg + "\t" + (LaunchArgs == null ? "" : LaunchArgs) + "\t" + (Keep ? "1" : "0") + "\t0";
                case CliMode.Quit:
                    return "quit";
                default:
                    return "manager";
            }
        }
    }

    internal class PendingLaunch
    {
        public int Pid;
        public string Name = "";
        public bool Keep = true;
        public DateTime Until = DateTime.Now.AddSeconds(60);
        /// <summary>已经处理过的窗口：用户手动放出来的就不再收回去。</summary>
        public List<IntPtr> Handled = new List<IntPtr>();
    }

    /// <summary>只用来收 WM_HOTKEY 的隐形窗口（句柄存在，但从不显示、不进任务栏）。</summary>
    public class HotkeyWindow : Form
    {
        public event Action<int> Hotkey;

        public HotkeyWindow()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(1, 1);
            Location = new Point(-4000, -4000);
            // 关键：只把句柄建出来，绝不 Show()。
            // 也不要自己指定 CreateParams.ClassName —— 用系统已有的类名（如 "static"）
            // 会污染 WinForms 的窗口类注册表，之后创建管理窗口时会抛 "类已存在"。
            IntPtr unused = Handle;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                Action<int> h = Hotkey;
                if (h != null)
                {
                    try { h(m.WParam.ToInt32()); }
                    catch (Exception ex) { Store.Log("快捷键处理失败: {0}", ex.Message); }
                }
            }
            base.WndProc(ref m);
        }

        public void UnregisterAll()
        {
            try
            {
                Native.UnregisterHotKey(Handle, 1);
                Native.UnregisterHotKey(Handle, 2);
            }
            catch { }
        }
    }

    /// <summary>常驻实例：托盘图标 + 全局快捷键 + 命令轮询。</summary>
    public class TrayContext : ApplicationContext
    {
        private readonly Hider hider = new Hider();
        private AppSettings settings;
        private List<Rule> rules;
        private NotifyIcon mainIcon;
        private HotkeyWindow hotkeyWindow;
        private ManagerForm manager;
        private readonly Dictionary<IntPtr, NotifyIcon> windowIcons = new Dictionary<IntPtr, NotifyIcon>();
        private readonly Dictionary<string, Icon> iconCache = new Dictionary<string, Icon>();
        private readonly List<PendingLaunch> pending = new List<PendingLaunch>();
        private Timer queueTimer, tickTimer, ruleTimer, launchTimer;
        private bool exiting;

        public Hider Hider { get { return hider; } }
        public AppSettings Settings { get { return settings; } }
        public List<Rule> Rules { get { return rules; } }

        public TrayContext()
        {
            settings = Store.LoadSettings();
            rules = Store.LoadRules();

            hider.Changed += delegate { SyncTray(); };
            hider.Failed += delegate(string msg) { Balloon("没能收进托盘", msg, ToolTipIcon.Warning); };
            // 用户把一个"启动到托盘"的程序放出来了，就不再盯着它
            hider.Restored += delegate(int pid)
            {
                pending.RemoveAll(delegate(PendingLaunch p) { return p.Pid == pid; });
            };

            int restored = Hider.RestoreOrphans();

            mainIcon = new NotifyIcon();
            mainIcon.Icon = AppIcon();
            mainIcon.Text = "TrayHider";
            mainIcon.Visible = true;
            mainIcon.DoubleClick += delegate { ShowManager(); };

            hotkeyWindow = new HotkeyWindow();
            hotkeyWindow.Hotkey += OnHotkey;
            RegisterHotkeys();

            queueTimer = new Timer();
            queueTimer.Interval = 250;
            queueTimer.Tick += delegate { ProcessQueue(); };
            queueTimer.Start();

            tickTimer = new Timer();
            tickTimer.Interval = 1000;
            tickTimer.Tick += delegate { hider.Tick(); };
            tickTimer.Start();

            ruleTimer = new Timer();
            ruleTimer.Interval = 2000;
            ruleTimer.Tick += delegate { ApplyRules(); };
            ruleTimer.Start();

            launchTimer = new Timer();
            launchTimer.Interval = 300;
            launchTimer.Tick += delegate { CheckPendingLaunches(); };
            launchTimer.Start();

            SyncTray();
            ApplyRules();

            if (restored > 0)
            {
                Balloon("TrayHider", string.Format("已把上次异常退出时留下的 {0} 个隐藏窗口放了出来。", restored), ToolTipIcon.Info);
            }
            Store.Log("===== TrayHider 启动：数据目录 {0}，规则 {1} 条 =====", Store.DataDir, rules.Count);
        }

        // ---------------- 图标 ----------------

        private Icon AppIcon()
        {
            try
            {
                Icon ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ico != null) { return ico; }
            }
            catch { }
            return SystemIcons.Application;
        }

        private Icon IconFor(HiddenItem item)
        {
            string path = item.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                if (iconCache.ContainsKey(path)) { return iconCache[path]; }
                try
                {
                    Icon ico = Icon.ExtractAssociatedIcon(path);
                    if (ico != null)
                    {
                        iconCache[path] = ico;
                        return ico;
                    }
                }
                catch { }
            }
            return SystemIcons.Application;
        }

        private static string Ellipsis(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) { return "(无标题)"; }
            if (s.Length <= max) { return s; }
            return s.Substring(0, max - 1) + "…";
        }

        // ---------------- 托盘同步 ----------------

        private void SyncTray()
        {
            if (settings.PerWindowIcon)
            {
                foreach (HiddenItem it in new List<HiddenItem>(hider.Items))
                {
                    if (!windowIcons.ContainsKey(it.Hwnd)) { AddWindowIcon(it); }
                    else { UpdateWindowIcon(it); }
                }
                List<IntPtr> dead = new List<IntPtr>();
                foreach (KeyValuePair<IntPtr, NotifyIcon> kv in windowIcons)
                {
                    if (hider.Find(kv.Key) == null) { dead.Add(kv.Key); }
                }
                foreach (IntPtr h in dead)
                {
                    NotifyIcon ni = windowIcons[h];
                    windowIcons.Remove(h);
                    try { ni.Visible = false; ni.Dispose(); } catch { }
                }
            }
            else if (windowIcons.Count > 0)
            {
                foreach (NotifyIcon ni in windowIcons.Values)
                {
                    try { ni.Visible = false; ni.Dispose(); } catch { }
                }
                windowIcons.Clear();
            }
            RebuildMenu();
        }

        private void AddWindowIcon(HiddenItem item)
        {
            try
            {
                NotifyIcon ni = new NotifyIcon();
                ni.Icon = IconFor(item);
                ni.Text = Ellipsis(item.DisplayTitle, 60);
                ni.Visible = true;
                ni.DoubleClick += delegate { RestoreItem(item, true); };

                ContextMenuStrip m = new ContextMenuStrip();
                ToolStripMenuItem r = new ToolStripMenuItem("把这个窗口放出来");
                r.Click += delegate { RestoreItem(item, true); };
                m.Items.Add(r);
                ToolStripMenuItem k = new ToolStripMenuItem("结束该进程");
                k.Click += delegate { KillItemProcess(item); };
                m.Items.Add(k);
                m.Items.Add(new ToolStripSeparator());
                ToolStripMenuItem ra = new ToolStripMenuItem("恢复全部窗口");
                ra.Click += delegate { RestoreAll(true); };
                m.Items.Add(ra);
                ni.ContextMenuStrip = m;

                windowIcons[item.Hwnd] = ni;
            }
            catch (Exception ex) { Store.Log("添加托盘图标失败: {0}", ex.Message); }
        }

        private void UpdateWindowIcon(HiddenItem item)
        {
            try
            {
                NotifyIcon ni = windowIcons[item.Hwnd];
                ni.Text = Ellipsis(item.DisplayTitle, 60);
            }
            catch { }
        }

        private void RebuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            ToolStripMenuItem open = new ToolStripMenuItem("打开管理窗口");
            open.Click += delegate { ShowManager(); };
            menu.Items.Add(open);
            menu.Items.Add(new ToolStripSeparator());

            if (hider.Count == 0)
            {
                ToolStripMenuItem none = new ToolStripMenuItem("(当前没有隐藏的窗口)");
                none.Enabled = false;
                menu.Items.Add(none);
            }
            else
            {
                ToolStripMenuItem header = new ToolStripMenuItem(string.Format("已收进托盘：{0} 个窗口", hider.Count));
                header.Enabled = false;
                menu.Items.Add(header);
                foreach (HiddenItem it in hider.Items)
                {
                    HiddenItem captured = it;
                    ToolStripMenuItem mi = new ToolStripMenuItem("放出来：" + Ellipsis(it.DisplayTitle, 45));
                    mi.Click += delegate { RestoreItem(captured, true); };
                    menu.Items.Add(mi);
                }
                ToolStripMenuItem all = new ToolStripMenuItem("恢复全部窗口");
                all.Click += delegate { RestoreAll(true); };
                menu.Items.Add(all);
            }

            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem hideFg = new ToolStripMenuItem("把当前前台窗口收进托盘");
            hideFg.Click += delegate { HideForeground(); };
            menu.Items.Add(hideFg);

            ToolStripMenuItem hk = new ToolStripMenuItem(string.Format("快捷键：{0}｜全部恢复 {1}", settings.HotkeyHide, settings.HotkeyRestore));
            hk.Enabled = false;
            menu.Items.Add(hk);

            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem quit = new ToolStripMenuItem(string.Format("退出（并恢复全部 {0} 个窗口）", hider.Count));
            quit.Click += delegate { ExitApp(); };
            menu.Items.Add(quit);

            mainIcon.ContextMenuStrip = menu;
            mainIcon.Text = hider.Count == 0
                ? "TrayHider · 空闲"
                : string.Format("TrayHider · 已收进托盘 {0} 个窗口", hider.Count);
        }

        public void Balloon(string title, string text, ToolTipIcon icon)
        {
            try
            {
                if (mainIcon == null) { return; }
                mainIcon.BalloonTipTitle = Ellipsis(title, 60);
                mainIcon.BalloonTipText = Ellipsis(text, 250);
                mainIcon.BalloonTipIcon = icon;
                mainIcon.ShowBalloonTip(4000);
            }
            catch { }
        }

        // ---------------- 收 / 放 ----------------

        public HiddenItem HideHandle(IntPtr hwnd, bool keep, string source)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd))
            {
                Balloon("TrayHider", "没有找到可用的窗口。", ToolTipIcon.Warning);
                return null;
            }
            if (Native.PidOf(hwnd) == Process.GetCurrentProcess().Id)
            {
                Balloon("TrayHider", "那是本程序自己的窗口，已忽略。", ToolTipIcon.Info);
                return null;
            }
            HiddenItem item = hider.Hide(hwnd, keep, source);
            if (item != null)
            {
                Balloon("已收进托盘", item.DisplayTitle + "\r\n双击托盘里它自己的图标就能放出来。", ToolTipIcon.Info);
            }
            return item;
        }

        public void RestoreItem(HiddenItem item, bool activate)
        {
            hider.Restore(item, activate);
        }

        public void RestoreAll(bool announce)
        {
            int n = hider.RestoreAll();
            if (announce)
            {
                Balloon("TrayHider", n == 0 ? "当前没有隐藏的窗口。" : string.Format("已放出 {0} 个窗口。", n), ToolTipIcon.Info);
            }
        }

        public void HideForeground()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero)
            {
                Balloon("TrayHider", "没有取到前台窗口。", ToolTipIcon.Warning);
                return;
            }
            HideHandle(fg, settings.KeepHiddenDefault, "前台窗口");
        }

        private void KillItemProcess(HiddenItem item)
        {
            DialogResult r = MessageBox.Show(
                string.Format("确定要结束 {0}（PID {1}）吗？\r\n没保存的数据会丢失。", item.DisplayTitle, item.Pid),
                "TrayHider", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) { return; }
            try
            {
                Process p = Process.GetProcessById(item.Pid);
                p.Kill();
                Store.Log("结束了进程 {0} (PID {1})", item.ProcessName, item.Pid);
            }
            catch (Exception ex)
            {
                Balloon("结束进程失败", ex.Message, ToolTipIcon.Warning);
            }
            hider.Tick();
        }

        // ---------------- 启动到托盘 ----------------

        public bool LaunchHidden(string path, string args, bool keep, bool addRule)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Balloon("启动失败", "找不到文件：" + path, ToolTipIcon.Warning);
                return false;
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(path, args == null ? "" : args);
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) { psi.WorkingDirectory = dir; }

                Process p = Process.Start(psi);
                if (p == null)
                {
                    Balloon("启动失败", "系统没有返回进程句柄。", ToolTipIcon.Warning);
                    return false;
                }
                PendingLaunch pl = new PendingLaunch();
                pl.Pid = p.Id;
                pl.Name = Path.GetFileName(path);
                pl.Keep = keep;
                pl.Until = DateTime.Now.AddSeconds(60);
                pending.Add(pl);

                if (addRule)
                {
                    Rule r = new Rule();
                    r.Kind = "exe";
                    r.Value = Path.GetFileName(path);
                    r.LaunchPath = path;
                    r.LaunchArgs = args == null ? "" : args;
                    r.Keep = keep;
                    AddRule(r);
                }
                Store.Log("启动并收进托盘: {0} (PID {1})", path, p.Id);
                Balloon("已启动并收进托盘", pl.Name + "\r\n它的窗口不会出现在任务栏上。", ToolTipIcon.Info);
                return true;
            }
            catch (Exception ex)
            {
                Balloon("启动失败", ex.Message, ToolTipIcon.Warning);
                Store.Log("启动失败 {0}: {1}", path, ex.Message);
                return false;
            }
        }

        private void CheckPendingLaunches()
        {
            if (pending.Count == 0) { return; }
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                PendingLaunch pl = pending[i];
                bool alive = true;
                try { Process.GetProcessById(pl.Pid); }
                catch { alive = false; }
                if (!alive || DateTime.Now > pl.Until)
                {
                    pending.RemoveAt(i);
                    continue;
                }
                foreach (WindowInfo w in Native.WindowsOfProcess(pl.Pid))
                {
                    if (pl.Handled.Contains(w.Handle)) { continue; }
                    if (hider.Contains(w.Handle)) { pl.Handled.Add(w.Handle); continue; }
                    if (hider.Hide(w.Handle, pl.Keep, "启动：" + pl.Name) != null) { pl.Handled.Add(w.Handle); }
                }
            }
        }

        // ---------------- 规则 ----------------

        public void AddRule(Rule r)
        {
            rules.Add(r);
            Store.SaveRules(rules);
            Store.Log("新增规则: {0}", r.Describe());
        }

        public void RemoveRule(Rule r)
        {
            rules.Remove(r);
            Store.SaveRules(rules);
        }

        private void ApplyRules()
        {
            if (rules.Count == 0) { return; }
            int me = Process.GetCurrentProcess().Id;
            foreach (WindowInfo w in Native.ListWindows(true, me, false))
            {
                if (hider.Contains(w.Handle)) { continue; }
                foreach (Rule r in rules)
                {
                    if (r.Matches(w))
                    {
                        hider.Hide(w.Handle, r.Keep, "规则：" + r.Value);
                        break;
                    }
                }
            }
        }

        // ---------------- 快捷键 ----------------

        private void RegisterHotkeys()
        {
            if (!settings.HotkeyEnabled) { return; }
            uint mods, key;
            if (TryParseHotkey(settings.HotkeyHide, out mods, out key))
            {
                if (!Native.RegisterHotKey(hotkeyWindow.Handle, 1, mods, key))
                {
                    Store.Log("快捷键 {0} 注册失败（可能被别的程序占用）", settings.HotkeyHide);
                }
            }
            if (TryParseHotkey(settings.HotkeyRestore, out mods, out key))
            {
                if (!Native.RegisterHotKey(hotkeyWindow.Handle, 2, mods, key))
                {
                    Store.Log("快捷键 {0} 注册失败（可能被别的程序占用）", settings.HotkeyRestore);
                }
            }
        }

        public static bool TryParseHotkey(string text, out uint modifiers, out uint key)
        {
            modifiers = 0;
            key = 0;
            if (string.IsNullOrEmpty(text)) { return false; }
            string[] parts = text.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string t = parts[i].Trim();
                if (t.Length == 0) { continue; }
                string low = t.ToLowerInvariant();
                if (low == "ctrl" || low == "control") { modifiers |= Native.MOD_CONTROL; continue; }
                if (low == "alt") { modifiers |= Native.MOD_ALT; continue; }
                if (low == "shift") { modifiers |= Native.MOD_SHIFT; continue; }
                if (low == "win") { modifiers |= Native.MOD_WIN; continue; }
                if (low.Length == 1)
                {
                    key = (uint)char.ToUpperInvariant(low[0]);
                }
                else if (low[0] == 'f')
                {
                    int n;
                    if (int.TryParse(low.Substring(1), out n) && n >= 1 && n <= 24) { key = (uint)(0x70 + n - 1); }
                }
            }
            return modifiers != 0 && key != 0;
        }

        private void OnHotkey(int id)
        {
            if (id == 1) { HideForeground(); }
            else if (id == 2) { RestoreAll(true); }
        }

        // ---------------- 命令队列 ----------------

        private void ProcessQueue()
        {
            List<string> cmds = Store.TakeCommands();
            foreach (string raw in cmds)
            {
                try { HandleCommand(raw); }
                catch (Exception ex) { Store.Log("执行命令失败 [{0}]: {1}", raw, ex.Message); }
            }
        }

        private static bool Flag(string[] parts, int index, bool fallback)
        {
            if (parts.Length <= index) { return fallback; }
            return parts[index].Trim() != "0";
        }

        private void HandleCommand(string raw)
        {
            string[] p = raw.Split('\t');
            if (p.Length == 0) { return; }
            switch (p[0])
            {
                case "hide-foreground":
                    {
                        IntPtr h = IntPtr.Zero;
                        if (p.Length > 1)
                        {
                            long v;
                            if (long.TryParse(p[1].Trim(), out v)) { h = new IntPtr(v); }
                        }
                        if (h == IntPtr.Zero || !Native.IsWindow(h)) { h = Native.GetForegroundWindow(); }
                        HideHandle(h, Flag(p, 2, settings.KeepHiddenDefault), "命令行");
                        break;
                    }
                case "hide-match":
                    MatchAndHide(p.Length > 1 ? p[1] : "", Flag(p, 2, settings.KeepHiddenDefault));
                    break;
                case "restore-match":
                    MatchAndRestore(p.Length > 1 ? p[1] : "");
                    break;
                case "restore-all":
                    RestoreAll(true);
                    break;
                case "launch":
                    LaunchHidden(p.Length > 1 ? p[1] : "", p.Length > 2 ? p[2] : "",
                        Flag(p, 3, true), Flag(p, 4, false));
                    break;
                case "manager":
                    ShowManager();
                    break;
                case "quit":
                    ExitApp();
                    break;
            }
        }

        private void MatchAndHide(string value, bool keep)
        {
            if (string.IsNullOrEmpty(value)) { return; }
            int me = Process.GetCurrentProcess().Id;
            int pid;
            bool byPid = int.TryParse(value, out pid);
            int n = 0;
            foreach (WindowInfo w in Native.ListWindows(true, me, !byPid))
            {
                bool hit = byPid ? (w.Pid == pid) : (w.Title.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hit && hider.Hide(w.Handle, keep, "命令行") != null) { n++; }
            }
            Balloon("TrayHider", n == 0 ? "没有找到匹配的窗口。" : string.Format("已收进托盘 {0} 个窗口。", n),
                n == 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }

        private void MatchAndRestore(string value)
        {
            if (string.IsNullOrEmpty(value)) { return; }
            int pid;
            bool byPid = int.TryParse(value, out pid);
            List<HiddenItem> hits = new List<HiddenItem>();
            foreach (HiddenItem it in hider.Items)
            {
                bool hit = byPid
                    ? (it.Pid == pid)
                    : (it.DisplayTitle.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hit) { hits.Add(it); }
            }
            foreach (HiddenItem it in hits) { hider.Restore(it, true); }
            Balloon("TrayHider", string.Format("放出了 {0} 个窗口。", hits.Count), ToolTipIcon.Info);
        }

        // ---------------- 管理窗口 / 退出 ----------------

        public void ShowManager()
        {
            try
            {
                if (manager == null || manager.IsDisposed) { manager = new ManagerForm(this); }
                if (!manager.Visible) { manager.Show(); }
                if (manager.WindowState == FormWindowState.Minimized) { manager.WindowState = FormWindowState.Normal; }
                manager.Activate();
                manager.BringToFront();
                manager.ReloadAll();
            }
            catch (Exception ex) { Store.Log("打开管理窗口失败: {0}", ex.Message); }
        }

        public void ApplySettings()
        {
            Store.SaveSettings(settings);
            Store.SetAutoStart(settings.AutoStart);
            if (hotkeyWindow != null)
            {
                hotkeyWindow.UnregisterAll();
                RegisterHotkeys();
            }
            SyncTray();
        }

        public void ExitApp()
        {
            if (exiting) { return; }
            exiting = true;
            Store.Log("退出 TrayHider，恢复 {0} 个窗口", hider.Count);
            hider.RestoreAll();

            foreach (NotifyIcon ni in windowIcons.Values)
            {
                try { ni.Visible = false; ni.Dispose(); } catch { }
            }
            windowIcons.Clear();

            if (mainIcon != null)
            {
                try { mainIcon.Visible = false; mainIcon.Dispose(); } catch { }
                mainIcon = null;
            }
            if (hotkeyWindow != null)
            {
                hotkeyWindow.UnregisterAll();
                try { hotkeyWindow.Dispose(); } catch { }
                hotkeyWindow = null;
            }
            if (queueTimer != null) { queueTimer.Stop(); }
            if (tickTimer != null) { tickTimer.Stop(); }
            if (ruleTimer != null) { ruleTimer.Stop(); }
            if (launchTimer != null) { launchTimer.Stop(); }

            if (manager != null && !manager.IsDisposed)
            {
                manager.AllowClose();
                manager.Close();
            }
            ExitThread();
        }
    }

    internal static class CliClient
    {
        public const string MutexName = "Local\\TrayHider.SingleInstance";

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        private static void AttachConsoleIfAny()
        {
            try
            {
                if (AttachConsole(-1))
                {
                    StreamWriter w = new StreamWriter(Console.OpenStandardOutput());
                    w.AutoFlush = true;
                    Console.SetOut(w);
                }
            }
            catch { }
        }

        public static bool ResidentRunning()
        {
            System.Threading.Mutex m = null;
            try
            {
                return System.Threading.Mutex.TryOpenExisting(MutexName, out m);
            }
            catch { return false; }
            finally { if (m != null) { m.Close(); } }
        }

        public static void EnsureResident()
        {
            if (ResidentRunning()) { return; }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath,
                    "--resident --data-dir \"" + Store.DataDir + "\"");
                psi.UseShellExecute = true;
                // 绝不能设 WindowStyle = Hidden：本程序是 GUI 子系统，本来就没有控制台窗口；
                // 而 SW_HIDE 会传给常驻实例的第一个窗口，管理窗口将永远显示不出来（实测踩过）。
                Process.Start(psi);
            }
            catch (Exception ex) { Store.Log("拉起常驻实例失败: {0}", ex.Message); }
            for (int i = 0; i < 40; i++)
            {
                if (ResidentRunning()) { return; }
                System.Threading.Thread.Sleep(100);
            }
            Store.Log("等待常驻实例超时");
        }

        public static void Send(CliOptions opt)
        {
            EnsureResident();
            Store.Enqueue(opt.ToCommand());
            AttachConsoleIfAny();
            Console.WriteLine("TrayHider: 已把命令交给托盘实例执行。");
        }

        public static void List(CliOptions opt)
        {
            List<WindowInfo> wins = Native.ListWindows(true, Process.GetCurrentProcess().Id, false);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("PID      进程                  标题");
            sb.AppendLine("-------  --------------------  ----------------------------------------");
            foreach (WindowInfo w in wins)
            {
                string name = w.ProcessName == null ? "" : w.ProcessName;
                if (name.Length > 20) { name = name.Substring(0, 20); }
                sb.AppendLine(string.Format("{0,-8} {1,-20}  {2}", w.Pid, name, w.Title));
            }
            string text = sb.ToString();
            if (!string.IsNullOrEmpty(opt.OutFile))
            {
                try { File.WriteAllText(opt.OutFile, text, new UTF8Encoding(true)); }
                catch (Exception ex) { Store.Log("写出列表失败: {0}", ex.Message); }
            }
            else
            {
                AttachConsoleIfAny();
                Console.WriteLine(text);
            }
        }

        public static void PrintHelp()
        {
            AttachConsoleIfAny();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("TrayHider —— 把任何窗口收进系统托盘");
            sb.AppendLine();
            sb.AppendLine("  TrayHider.exe                     打开管理窗口（并确保托盘常驻）");
            sb.AppendLine("  TrayHider.exe --hide-foreground   把当前前台窗口收进托盘");
            sb.AppendLine("  TrayHider.exe --hide <标题|PID>   把匹配的窗口收进托盘");
            sb.AppendLine("  TrayHider.exe --restore <标题|PID>");
            sb.AppendLine("  TrayHider.exe --restore-all       放出全部被隐藏的窗口");
            sb.AppendLine("  TrayHider.exe --launch <exe> [--args \"...\"] [--keep]");
            sb.AppendLine("  TrayHider.exe --list [--out 文件]  列出当前可见窗口");
            sb.AppendLine("  TrayHider.exe --quit              退出托盘常驻（恢复所有窗口）");
            sb.AppendLine("  --data-dir <目录>                 指定数据目录（默认 %APPDATA%\\TrayHider）");
            sb.AppendLine();
            sb.AppendLine("常驻模式快捷方式：--resident");
            Console.WriteLine(sb.ToString());
        }
    }

    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            CliOptions opt = CliOptions.Parse(args);
            Store.Init(opt.DataDir);

            switch (opt.Mode)
            {
                case CliMode.Help:
                    CliClient.PrintHelp();
                    return;
                case CliMode.List:
                    CliClient.List(opt);
                    return;
                case CliMode.Resident:
                    RunResident();
                    return;
                default:
                    CliClient.Send(opt);
                    return;
            }
        }

        private static void RunResident()
        {
            bool created;
            System.Threading.Mutex mutex = new System.Threading.Mutex(true, CliClient.MutexName, out created);
            if (!created)
            {
                Store.Log("已有实例在运行，忽略本次 --resident");
                mutex.Close();
                return;
            }

            TrayContext ctx = null;
            try
            {
                ctx = new TrayContext();

                Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
                {
                    Store.Log("界面线程异常: {0}", e.Exception);
                    if (ctx != null) { ctx.Hider.RestoreAll(); }
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Store.Log("未处理异常: {0}", e.ExceptionObject);
                    if (ctx != null) { ctx.Hider.RestoreAll(); }
                };
                AppDomain.CurrentDomain.ProcessExit += delegate
                {
                    // 无论怎么退出，都别把用户的窗口留在"消失"状态
                    if (ctx != null) { ctx.Hider.RestoreAll(); }
                };

                Application.Run(ctx);
            }
            catch (Exception ex)
            {
                Store.Log("常驻实例异常退出: {0}", ex);
                if (ctx != null) { ctx.Hider.RestoreAll(); }
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch { }
                mutex.Close();
            }
        }
    }
}
