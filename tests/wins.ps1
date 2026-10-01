Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class WinProbe {
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hWnd, StringBuilder text, int count);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  public static List<string> List() {
    var result = new List<string>();
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      if (IsWindowVisible(h)) {
        var sb = new StringBuilder(512); GetWindowTextW(h, sb, 512);
        var cn = new StringBuilder(256); GetClassNameW(h, cn, 256);
        uint pid; GetWindowThreadProcessId(h, out pid);
        result.Add(h.ToInt64() + "|" + pid + "|" + cn + "|" + sb);
      }
      return true;
    }, IntPtr.Zero);
    return result;
  }
  public static string Foreground() {
    IntPtr h = GetForegroundWindow();
    var sb = new StringBuilder(512); GetWindowTextW(h, sb, 512);
    uint pid; GetWindowThreadProcessId(h, out pid);
    return h.ToInt64() + "|" + pid + "|" + sb;
  }
}
"@

function Get-VisibleWindow { [WinProbe]::List() }
function Get-ForegroundWindowInfo { [WinProbe]::Foreground() }
