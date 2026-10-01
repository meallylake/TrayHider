using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;

namespace TrayHider
{
    /// <summary>一个已经被收进托盘的窗口。</summary>
    public class HiddenItem
    {
        public IntPtr Hwnd = IntPtr.Zero;
        public string Title = "";
        public string ClassName = "";
        public int Pid;
        public string ProcessName = "";
        public string ProcessPath = "";
        public bool WasIconic;
        public bool KeepHidden = true;
        public DateTime HiddenAt = DateTime.Now;
        public string Source = "手动";
        public int FailTicks;

        public string DisplayTitle
        {
            get
            {
                if (!string.IsNullOrEmpty(Title)) { return Title; }
                if (!string.IsNullOrEmpty(ProcessName)) { return "(" + ProcessName + ")"; }
                return "(无标题窗口)";
            }
        }
    }

    /// <summary>
    /// 收进托盘的核心：登记被隐藏的窗口、恢复它们、把偷偷冒出来的窗口再收回去。
    /// 纯逻辑，不含界面；界面通过 Changed 事件刷新托盘图标。
    /// </summary>
    public class Hider
    {
        private readonly List<HiddenItem> items = new List<HiddenItem>();

        /// <summary>隐藏列表发生变化（新增/移除）。</summary>
        public event EventHandler Changed;
        /// <summary>某个窗口被放出来了（参数是它所属进程的 PID）。</summary>
        public event Action<int> Restored;
        /// <summary>某件操作没能完成，附带给用户看的说明。</summary>
        public event Action<string> Failed;

        public IList<HiddenItem> Items { get { return items; } }
        public int Count { get { return items.Count; } }

        public HiddenItem Find(IntPtr hwnd)
        {
            foreach (HiddenItem it in items)
            {
                if (it.Hwnd == hwnd) { return it; }
            }
            return null;
        }

        public bool Contains(IntPtr hwnd)
        {
            return Find(hwnd) != null;
        }

        /// <summary>把窗口收进托盘，返回登记项；失败返回 null。</summary>
        public HiddenItem Hide(IntPtr hwnd, bool keepHidden, string source)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) { return null; }
            HiddenItem exists = Find(hwnd);
            if (exists != null) { return exists; }

            int pid = Native.PidOf(hwnd);
            if (pid <= 0) { return null; }
            if (pid == Process.GetCurrentProcess().Id) { return null; }

            WindowInfo info = Native.Describe(hwnd);
            HiddenItem item = new HiddenItem();
            item.Hwnd = hwnd;
            item.Title = info.Title;
            item.ClassName = info.ClassName;
            item.Pid = pid;
            item.ProcessName = info.ProcessName;
            item.ProcessPath = info.ProcessPath;
            item.WasIconic = info.Iconic;
            item.KeepHidden = keepHidden;
            item.HiddenAt = DateTime.Now;
            item.Source = source;

            Native.ShowWindowAsync(hwnd, Native.SW_HIDE);
            items.Add(item);
            SaveState();
            Store.Log("收进托盘: [{0}] {1} (PID {2}, {3})", source, item.DisplayTitle, item.Pid, item.ClassName);
            RaiseChanged();
            return item;
        }

        /// <summary>把窗口放出来。</summary>
        public void Restore(HiddenItem item, bool activate)
        {
            if (item == null) { return; }
            items.Remove(item);
            if (Native.IsWindow(item.Hwnd))
            {
                Native.ShowWindowAsync(item.Hwnd, item.WasIconic ? Native.SW_SHOWMINIMIZED : Native.SW_RESTORE);
                if (activate)
                {
                    Native.SetForegroundWindow(item.Hwnd);
                }
            }
            Store.Log("恢复窗口: {0} (PID {1})", item.DisplayTitle, item.Pid);
            SaveState();
            RaiseChanged();
            RaiseRestored(item.Pid);
        }

        public int RestoreAll()
        {
            int n = items.Count;
            if (n == 0) { return 0; }
            List<HiddenItem> copy = new List<HiddenItem>(items);
            items.Clear();
            foreach (HiddenItem item in copy)
            {
                if (Native.IsWindow(item.Hwnd))
                {
                    Native.ShowWindowAsync(item.Hwnd, item.WasIconic ? Native.SW_SHOWMINIMIZED : Native.SW_RESTORE);
                }
            }
            Store.Log("恢复全部窗口: {0} 个", n);
            foreach (HiddenItem item in copy) { RaiseRestored(item.Pid); }
            SaveState();
            RaiseChanged();
            return n;
        }

        /// <summary>每秒跑一次：清理已关闭的窗口、刷新标题、把偷偷冒出来的窗口再收回去。</summary>
        public void Tick()
        {
            bool changed = false;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                HiddenItem it = items[i];
                if (!Native.IsWindow(it.Hwnd))
                {
                    items.RemoveAt(i);
                    changed = true;
                    continue;
                }

                string title = Native.TextOf(it.Hwnd);
                if (title != it.Title) { it.Title = title; changed = true; }

                if (Native.IsWindowVisible(it.Hwnd))
                {
                    if (!it.KeepHidden)
                    {
                        // 用户自己把它弄回来了，就不再管它
                        items.RemoveAt(i);
                        changed = true;
                        continue;
                    }
                    it.FailTicks++;
                    if (it.FailTicks > 6)
                    {
                        // 试了 6 秒还是藏不住：多半是权限更高的进程（UIPI 拦住了）
                        items.RemoveAt(i);
                        changed = true;
                        Action<string> fail = Failed;
                        if (fail != null)
                        {
                            fail(string.Format("「{0}」藏不起来，可能它是以管理员身份运行的；请以管理员身份重新启动 TrayHider 再试。", it.DisplayTitle));
                        }
                        continue;
                    }
                    Native.ShowWindowAsync(it.Hwnd, Native.SW_HIDE);
                }
                else
                {
                    it.FailTicks = 0;
                }
            }
            if (changed)
            {
                SaveState();
                RaiseChanged();
            }
        }

        public void SaveState()
        {
            if (items.Count == 0) { Store.ClearState(); return; }
            List<IntPtr> handles = new List<IntPtr>();
            foreach (HiddenItem it in items) { handles.Add(it.Hwnd); }
            Store.SaveState(handles);
        }

        private void RaiseChanged()
        {
            EventHandler h = Changed;
            if (h != null) { h(this, EventArgs.Empty); }
        }

        private void RaiseRestored(int pid)
        {
            Action<int> h = Restored;
            if (h != null) { h(pid); }
        }

        /// <summary>
        /// 上次进程被强杀/崩溃时留下的隐藏窗口，这次启动时先放出来，
        /// 免得用户对着一个"消失的窗口"发懵。
        /// </summary>
        public static int RestoreOrphans()
        {
            List<IntPtr> handles = Store.ReadState();
            if (handles.Count == 0) { Store.ClearState(); return 0; }
            int n = 0;
            foreach (IntPtr h in handles)
            {
                if (!Native.IsWindow(h)) { continue; }
                if (Native.IsWindowVisible(h)) { continue; }
                Native.ShowWindowAsync(h, Native.SW_RESTORE);
                n++;
            }
            Store.ClearState();
            if (n > 0) { Store.Log("恢复了 {0} 个上次遗留的隐藏窗口", n); }
            return n;
        }
    }
}
