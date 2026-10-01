using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TrayHider
{
    /// <summary>一个顶层窗口的快照。</summary>
    public class WindowInfo
    {
        public IntPtr Handle = IntPtr.Zero;
        public string Title = "";
        public string ClassName = "";
        public int Pid;
        public string ProcessName = "";
        public string ProcessPath = "";
        public bool Visible;
        public bool Iconic;

        public string Display
        {
            get
            {
                if (!string.IsNullOrEmpty(Title)) { return Title; }
                if (!string.IsNullOrEmpty(ClassName)) { return "(" + ClassName + ")"; }
                return "(无标题)";
            }
        }
    }

    /// <summary>user32 里用到的那几个 API。</summary>
    public static class Native
    {
        public const int SW_HIDE = 0;
        public const int SW_SHOWMINIMIZED = 2;
        public const int SW_RESTORE = 9;

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_APPWINDOW = 0x00040000;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const int WM_HOTKEY = 0x0312;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern bool ShowWindowAsync(IntPtr hWnd, int cmdShow);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowLongW(IntPtr hWnd, int index);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public static string TextOf(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) { return ""; }
            StringBuilder sb = new StringBuilder(512);
            GetWindowTextW(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string ClassOf(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) { return ""; }
            StringBuilder sb = new StringBuilder(256);
            GetClassNameW(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static int PidOf(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) { return 0; }
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            return (int)pid;
        }

        public static bool IsToolWindow(IntPtr hWnd)
        {
            return (GetWindowLongW(hWnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0;
        }

        public static bool IsAppWindow(IntPtr hWnd)
        {
            return (GetWindowLongW(hWnd, GWL_EXSTYLE) & WS_EX_APPWINDOW) != 0;
        }

        /// <summary>把 HWND 变成带进程信息的 WindowInfo。</summary>
        public static WindowInfo Describe(IntPtr hWnd)
        {
            WindowInfo info = new WindowInfo();
            info.Handle = hWnd;
            info.Title = TextOf(hWnd);
            info.ClassName = ClassOf(hWnd);
            info.Pid = PidOf(hWnd);
            info.Visible = IsWindowVisible(hWnd);
            info.Iconic = IsIconic(hWnd);
            FillProcess(info);
            return info;
        }

        public static void FillProcess(WindowInfo info)
        {
            if (info.Pid <= 0) { return; }
            try
            {
                Process p = Process.GetProcessById(info.Pid);
                info.ProcessName = p.ProcessName;
                try
                {
                    info.ProcessPath = p.MainModule.FileName;
                }
                catch
                {
                    info.ProcessPath = "";
                }
            }
            catch
            {
                info.ProcessName = "";
            }
        }

        /// <summary>
        /// 枚举顶层窗口。visibleOnly=true 时只返回当前可见的（＝任务栏/桌面上看得见的那些）。
        /// </summary>
        public static List<WindowInfo> ListWindows(bool visibleOnly, int excludePid, bool includeUntitled)
        {
            List<WindowInfo> result = new List<WindowInfo>();
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (visibleOnly && !IsWindowVisible(hWnd)) { return true; }
                string cls = ClassOf(hWnd);
                if (cls == "Shell_TrayWnd" || cls == "Progman" || cls == "WorkerW" ||
                    cls == "Windows.UI.Core.CoreWindow" || cls == "EdgeUiInputTopWndClass")
                {
                    return true;
                }
                if (IsToolWindow(hWnd) && !IsAppWindow(hWnd)) { return true; }
                string title = TextOf(hWnd);
                if (!includeUntitled && title.Length == 0) { return true; }
                int pid = PidOf(hWnd);
                if (excludePid > 0 && pid == excludePid) { return true; }
                WindowInfo info = new WindowInfo();
                info.Handle = hWnd;
                info.Title = title;
                info.ClassName = cls;
                info.Pid = pid;
                info.Visible = IsWindowVisible(hWnd);
                info.Iconic = IsIconic(hWnd);
                FillProcess(info);
                result.Add(info);
                return true;
            }, IntPtr.Zero);
            return result;
        }

        /// <summary>某个进程的所有顶层窗口（含尚未显示的），用于"启动到托盘"。</summary>
        public static List<WindowInfo> WindowsOfProcess(int pid)
        {
            List<WindowInfo> result = new List<WindowInfo>();
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (PidOf(hWnd) != pid) { return true; }
                if (IsToolWindow(hWnd) && !IsAppWindow(hWnd)) { return true; }
                string title = TextOf(hWnd);
                string cls = ClassOf(hWnd);
                // 只认真正的界面窗口：空标题、IME、GDI+/.NET 的内部辅助窗口一律不碰
                if (title.Length == 0) { return true; }
                if (cls == "Default IME" || cls == "MSCTFIME UI" || cls == "IME" ||
                    cls == "GDI+ Hook Window Class" || cls.StartsWith(".NET-BroadcastEventWindow"))
                {
                    return true;
                }
                WindowInfo info = new WindowInfo();
                info.Handle = hWnd;
                info.Title = title;
                info.ClassName = cls;
                info.Pid = pid;
                info.Visible = IsWindowVisible(hWnd);
                info.Iconic = IsIconic(hWnd);
                result.Add(info);
                return true;
            }, IntPtr.Zero);
            return result;
        }
    }
}
