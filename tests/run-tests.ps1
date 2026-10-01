$ErrorActionPreference = 'Continue'
$root    = 'H:\ds-road\tray-hider'
$exe     = Join-Path $root 'dist\TrayHider.exe'
$csc     = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$tests   = Join-Path $root 'tests'
$target  = Join-Path $tests 'Target.exe'
$dataDir = Join-Path $tests 'data'
$report  = Join-Path $tests 'report.txt'
$logPath = Join-Path $dataDir 'trayhider.log'
$stateFile = Join-Path $dataDir 'hidden.state'

. (Join-Path $tests 'wins.ps1')

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class KeySend {
  [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hWnd, int cmd);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
  public static bool ForceForeground(IntPtr hWnd) {
      if (hWnd == IntPtr.Zero) { return false; }
      IntPtr fg = GetForegroundWindow();
      uint fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero);
      uint myThread = GetCurrentThreadId();
      bool attached = false;
      try {
          if (fgThread != myThread) { attached = AttachThreadInput(myThread, fgThread, true); }
          ShowWindowAsync(hWnd, 9);
          BringWindowToTop(hWnd);
          SetForegroundWindow(hWnd);
      } finally {
          if (attached) { AttachThreadInput(myThread, fgThread, false); }
      }
      return GetForegroundWindow() == hWnd;
  }
}
"@

$reportLines = New-Object System.Collections.Generic.List[string]
function Say([string]$t) { [void]$reportLines.Add([string]$t); Write-Output $t }
function Check([bool]$ok, [string]$t) { if ($ok) { Say ("PASS  " + $t) } else { Say ("FAIL  " + $t) } }

# rows are "hwnd|pid|class|title"
function VisRows { @(Get-VisibleWindow) }
function Field([string]$row, [int]$i) { ($row -split '\|', 4)[$i] }
function VisTitles { @(Get-VisibleWindow | ForEach-Object { ($_ -split '\|', 4)[3] }) }
function VisPids { @(Get-VisibleWindow | ForEach-Object { [int](($_ -split '\|', 4)[1]) }) }
function TitleVisible([string]$t) { (VisTitles) -contains $t }
function PidVisible([int]$p) { (VisPids) -contains $p }
function HwndOfTitle([string]$t) {
    foreach ($row in Get-VisibleWindow) { if ((($row -split '\|', 4)[3]) -eq $t) { return [IntPtr][long](($row -split '\|', 4)[0]) } }
    return [IntPtr]::Zero
}

function ResidentProc { @(Get-Process -Name TrayHider -ErrorAction SilentlyContinue) }

function Start-Resident {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = '--resident --data-dir "' + $dataDir + '"'
    $psi.UseShellExecute = $true
    $psi.WindowStyle = 'Hidden'
    $p = [System.Diagnostics.Process]::Start($psi)
    Start-Sleep -Seconds 3
    return $p
}

function Invoke-TrayHider([string]$argLine) {
    Start-Process -FilePath $exe -ArgumentList ($argLine + ' --data-dir "' + $dataDir + '"') -Wait -WindowStyle Hidden
    Start-Sleep -Seconds 2
}

function KillAllTargets {
    Get-Process -Name Target -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- setup
if (-not (Test-Path $target)) {
    & $csc /nologo /target:winexe /out:$target /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll (Join-Path $tests 'target.cs') | Out-Null
}
Say ("TrayHider.exe  : " + (Get-Item $exe).Length + " bytes")
Say ("Target.exe     : " + (Test-Path $target))
Say ("data dir       : " + $dataDir)
Get-ChildItem $dataDir -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

Say ''
Say '===== 阶段 1：对照实验 —— 直接启动 Target.exe 窗口可见 ====='
KillAllTargets
$t1 = Start-Process $target -PassThru
Start-Sleep -Seconds 3
Check (TitleVisible 'TrayHiderTestTarget') 'Target.exe 普通启动时窗口可见（对照组成立）'

Say ''
Say '===== 阶段 2：启动 TrayHider 常驻（托盘） ====='
$winBefore = VisRows
$resident = Start-Resident
$rp = ResidentProc
Check ($rp.Count -ge 1) 'TrayHider 常驻进程已运行'
$newWindows = @(Compare-Object -ReferenceObject $winBefore -DifferenceObject (VisRows))
Check ($newWindows.Count -eq 0) ("TrayHider 启动没有带出任何新窗口（控制台/管理窗口都没有）：新增 " + $newWindows.Count + " 个")
Say ("       resident pid: " + (($rp | ForEach-Object { $_.Id }) -join ','))

Say ''
Say '===== 阶段 3：把正在运行的窗口收进托盘 / 再放出来 ====='
Check (TitleVisible 'TrayHiderTestTarget') '收之前：窗口可见'
Invoke-TrayHider '--hide TrayHiderTestTarget'
Check (-not (TitleVisible 'TrayHiderTestTarget')) '--hide 之后：窗口从可见窗口列表消失'
Check ($null -ne (Get-Process -Id $t1.Id -ErrorAction SilentlyContinue)) '进程仍然活着（只是藏起来，没被杀掉）'
Invoke-TrayHider '--restore-all'
Check (TitleVisible 'TrayHiderTestTarget') '--restore-all 之后：窗口回来了'

Say ''
Say '===== 阶段 4：--list 列出可见窗口 ====='
$listFile = Join-Path $tests 'list.txt'
Invoke-TrayHider ('--list --out "' + $listFile + '"')
$listText = if (Test-Path $listFile) { Get-Content $listFile -Raw } else { '' }
Check ($listText -match 'TrayHiderTestTarget') '--list 输出了当前可见窗口（含测试窗口）'

Say ''
Say '===== 阶段 5：启动到托盘（启动瞬间就不出现在桌面上） ====='
Invoke-TrayHider ('--launch "' + $target + '" --args TrayHiderLaunchTest')
Start-Sleep -Seconds 2
$launched = @(Get-Process -Name Target -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $t1.Id })
Check ($launched.Count -ge 1) '程序已经被启动起来了'
Check (-not (TitleVisible 'TrayHiderLaunchTest')) '启动出来的窗口从未出现在可见窗口列表里'
if ($launched.Count -ge 1) { Check (-not (PidVisible $launched[0].Id)) '该进程确实没有可见窗口（不是启动失败）' }
Invoke-TrayHider '--restore-all'
Check (TitleVisible 'TrayHiderLaunchTest') '恢复后：这个窗口也能被放出来'

Say ''
Say '===== 阶段 6：全局快捷键 Ctrl+Alt+H ====='
$hwnd = HwndOfTitle 'TrayHiderTestTarget'
if ($hwnd -ne [IntPtr]::Zero) {
    Say ("       ForceForeground: " + [KeySend]::ForceForeground($hwnd))
    Start-Sleep -Milliseconds 800
}
$fg = Get-ForegroundWindowInfo
$fgPid = [int](($fg -split '\|', 3)[1])
Say ("       前台窗口: " + $fg + "   测试窗口 pid: " + $t1.Id)
if ($fgPid -eq $t1.Id) {
    [void][KeySend]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
    [void][KeySend]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
    [void][KeySend]::keybd_event(0x48, 0, 0, [UIntPtr]::Zero)
    [void][KeySend]::keybd_event(0x48, 0, 2, [UIntPtr]::Zero)
    [void][KeySend]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    [void][KeySend]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Seconds 3
    Check (-not (TitleVisible 'TrayHiderTestTarget')) 'Ctrl+Alt+H 把前台窗口收进了托盘'
    Invoke-TrayHider '--restore-all'
    Check (TitleVisible 'TrayHiderTestTarget') '再恢复：正常'
} else {
    Say 'SKIP  测试窗口没能成为前台窗口，跳过按键测试（绝不对真实窗口按键）'
}
Check (-not ((Get-Content $logPath -Raw -ErrorAction SilentlyContinue) -match '注册失败')) '两个全局快捷键都注册成功了'

Say ''
Say '===== 阶段 7：进程被强杀后，下次启动自动把窗口放回来 ====='
Invoke-TrayHider '--hide TrayHiderTestTarget'
Check (-not (TitleVisible 'TrayHiderTestTarget')) '再次把窗口收进托盘'
foreach ($p in (ResidentProc)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 2
Check ((ResidentProc).Count -eq 0) 'TrayHider 常驻进程已被强杀（模拟崩溃）'
Check (-not (TitleVisible 'TrayHiderTestTarget')) '此刻窗口仍是隐藏的（崩溃本来就会这样）'
Check (Test-Path $stateFile) '崩溃前留下了 hidden.state 记录'
$resident2 = Start-Resident
Start-Sleep -Seconds 2
Check (TitleVisible 'TrayHiderTestTarget') '重新启动 TrayHider 后自动把遗留窗口放了出来'
Check (-not (Test-Path $stateFile)) '遗留记录已清除'

Say ''
Say '===== 阶段 8：退出时恢复所有窗口 ====='
Invoke-TrayHider '--hide TrayHiderTestTarget'
Invoke-TrayHider '--quit'
Start-Sleep -Seconds 2
Check ((ResidentProc).Count -eq 0) '--quit 之后常驻进程已退出'
Check (TitleVisible 'TrayHiderTestTarget') '--quit 之前隐藏的窗口被恢复'

Say ''
Say '===== 清理 ====='
KillAllTargets
Start-Sleep -Seconds 1
Say ("剩余 Target 进程   : " + (@(Get-Process -Name Target -ErrorAction SilentlyContinue)).Count)
Say ("剩余 TrayHider 进程: " + (@(ResidentProc)).Count)

Say ''
Say '===== trayhider.log 全文 ====='
if (Test-Path $logPath) { Say (Get-Content $logPath -Raw) } else { Say '(无日志)' }

$reportLines | Set-Content -LiteralPath $report -Encoding UTF8
