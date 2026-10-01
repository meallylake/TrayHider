$ErrorActionPreference = 'Continue'
$root    = 'H:\ds-road\tray-hider'
$exe     = Join-Path $root 'dist\TrayHider.exe'
$dataDir = Join-Path $root 'tests\data'
$report  = Join-Path $root 'tests\ui-report.txt'
$logPath = Join-Path $dataDir 'trayhider.log'

Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class UiProbe {
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr l);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder t, int c);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder t, int c);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
  public static string Text(IntPtr h) { var sb = new StringBuilder(512); GetWindowTextW(h, sb, 512); return sb.ToString(); }
  public static string Cls(IntPtr h) { var sb = new StringBuilder(256); GetClassNameW(h, sb, 256); return sb.ToString(); }
  public static IntPtr FindMain(int pid, string titlePart) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      uint p; GetWindowThreadProcessId(h, out p);
      if ((int)p == pid && IsWindowVisible(h) && Text(h).IndexOf(titlePart) >= 0) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
  public static IntPtr FindChildByClass(IntPtr parent, string part) {
    IntPtr found = IntPtr.Zero;
    EnumChildWindows(parent, delegate(IntPtr h, IntPtr l) {
      if (Cls(h).IndexOf(part) >= 0) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
  // 只发不带指针的鼠标消息：跨进程 SendMessage 不会封送指针，
  // 用 TCM_GETITEMRECT 这类"指针参数"消息会把目标进程写崩（实测踩过）。
  public static void ClickAt(IntPtr h, int x, int y) {
    IntPtr lp = (IntPtr)((y << 16) | (x & 0xFFFF));
    SendMessage(h, 0x0201, (IntPtr)1, lp);      // WM_LBUTTONDOWN
    SendMessage(h, 0x0202, IntPtr.Zero, lp);    // WM_LBUTTONUP
  }
  public static List<string> Children(IntPtr parent) {
    var list = new List<string>();
    if (parent == IntPtr.Zero) { return list; }
    EnumChildWindows(parent, delegate(IntPtr h, IntPtr l) { list.Add(Cls(h) + "|" + Text(h)); return true; }, IntPtr.Zero);
    return list;
  }
}
"@

$lines = New-Object System.Collections.Generic.List[string]
function Say([string]$t) { [void]$lines.Add([string]$t); Write-Output $t }
function Check([bool]$ok, [string]$t) { if ($ok) { Say ("PASS  " + $t) } else { Say ("FAIL  " + $t) } }

Get-Process -Name TrayHider -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
# 每次测试前清空日志：下面的"无异常"断言只看本次运行
Remove-Item $logPath -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $dataDir 'queue\*.cmd') -Force -ErrorAction SilentlyContinue

Say '===== manager window smoke test ====='
Start-Process -FilePath $exe -ArgumentList ('--manager --data-dir "' + $dataDir + '"')
Start-Sleep -Seconds 6

$procs = @(Get-Process -Name TrayHider -ErrorAction SilentlyContinue)
Check ($procs.Count -ge 1) 'resident process is running'
$resident = $procs | Sort-Object StartTime | Select-Object -First 1
$hwnd = [UiProbe]::FindMain($resident.Id, 'TrayHider')
Say ("       resident pid " + $resident.Id + ", manager hwnd " + $hwnd)
Check ($hwnd -ne [IntPtr]::Zero) 'manager window is visible'

$kids = [UiProbe]::Children($hwnd)
Say ("       child controls: " + $kids.Count)
$kids | Sort-Object -Unique | ForEach-Object { Say ("         " + $_) }

$tabs    = @($kids | Where-Object { $_ -match 'SysTabControl32' })
$lists   = @($kids | Where-Object { $_ -match 'SysListView32' })
$headers = @($kids | Where-Object { $_ -match 'SysHeader32' })
$buttons = @($kids | Where-Object { $_ -match 'Button' })
Check ($tabs.Count -ge 1)    ('tab control present (' + $tabs.Count + ')')
Check ($lists.Count -ge 1)   ('window list present (' + $lists.Count + ')')
Check ($headers.Count -ge 1) ('list column headers present (' + $headers.Count + ')')
Check ($buttons.Count -ge 5) ('tab-1 action buttons present (' + $buttons.Count + ')')

# WinForms 只在标签页第一次可见时才创建它的子控件，
# 所以这里真的把三个标签页都点一遍，验证另外两页也能正常构建。
$tabHwnd = [UiProbe]::FindChildByClass($hwnd, 'SysTabControl32')
Check ($tabHwnd -ne [IntPtr]::Zero) 'located the tab control handle'
$before = ([UiProbe]::Children($hwnd)).Count
foreach ($y in @(8, 12, 16)) {
    for ($x = 4; $x -le 340; $x += 8) { [UiProbe]::ClickAt($tabHwnd, $x, $y) }
}
Start-Sleep -Milliseconds 1500
$kidsAll = [UiProbe]::Children($hwnd)
$lvAll = @($kidsAll | Where-Object { $_ -match 'SysListView32' }).Count
$btAll = @($kidsAll | Where-Object { $_ -match 'Button' }).Count
Say ("       swept the tab strip: controls " + $before + " -> " + $kidsAll.Count + ", listviews " + $lvAll + ", buttons " + $btAll)
Check ($kidsAll.Count -gt $before) ('clicking the tab strip built the other tab pages (controls ' + $before + ' -> ' + $kidsAll.Count + ')')
Check ($lvAll -ge 2) ('both list views exist after visiting all tabs (' + $lvAll + ')')
Check ($btAll -ge 9) ('every button exists after visiting all tabs (' + $btAll + ')')

$alive = [bool](Get-Process -Id $resident.Id -ErrorAction SilentlyContinue)
Check $alive 'resident still alive after opening UI (no crash)'

$logTail = if (Test-Path $logPath) { (Get-Content $logPath -Raw) } else { '' }
Check (-not ($logTail -match '打开管理窗口失败')) 'no "failed to open manager window" in log'
Check (-not ($logTail -match '界面线程异常|未处理异常|常驻实例异常')) 'no unhandled exception logged'

Say ''
Say '===== closing the manager window should just hide it (tray keeps running) ====='
# WM_CLOSE = the X button
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Closer {
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
}
"@
[void][Closer]::SendMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
Start-Sleep -Seconds 2
Check ([bool](Get-Process -Id $resident.Id -ErrorAction SilentlyContinue)) 'after WM_CLOSE the tray process is still alive'
Check (([UiProbe]::FindMain($resident.Id, 'TrayHider')) -eq [IntPtr]::Zero) 'manager window is now hidden'

Say ''
Say '===== quit ====='
Start-Process -FilePath $exe -ArgumentList ('--quit --data-dir "' + $dataDir + '"') -Wait -WindowStyle Hidden
Start-Sleep -Seconds 2
Check ((@(Get-Process -Name TrayHider -ErrorAction SilentlyContinue)).Count -eq 0) 'resident exited on --quit'

Say ''
Say '===== log tail ====='
$tail = if (Test-Path $logPath) { Get-Content $logPath -Raw } else { '(no log)' }
Say $tail

$lines | Set-Content -LiteralPath $report -Encoding UTF8
