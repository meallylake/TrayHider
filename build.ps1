# build.ps1 - compile TrayHider (ASCII only on purpose: Windows PowerShell 5.1 reads
# BOM-less .ps1 files as ANSI, so a Chinese comment here would break parsing).
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir = Join-Path $root 'src'
$distDir = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) { throw 'csc.exe not found (.NET Framework 4.x is required)' }

# --- icon: draw a small "into the tray" glyph, unless one is already provided ---
$icoPath = Join-Path $distDir 'TrayHider.ico'
if (-not (Test-Path $icoPath)) {
    try {
        Add-Type -AssemblyName System.Drawing
        $bmp = New-Object System.Drawing.Bitmap 32, 32
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.Clear([System.Drawing.Color]::Transparent)
        $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 32, 92, 176))
        $g.FillRectangle($bg, 1, 1, 30, 30)
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 3
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawLine($pen, 16, 6, 16, 19)
        $g.DrawLine($pen, 9, 13, 16, 20)
        $g.DrawLine($pen, 23, 13, 16, 20)
        $g.DrawLine($pen, 7, 25, 25, 25)
        $g.Dispose()
        $hicon = $bmp.GetHicon()
        $icon = [System.Drawing.Icon]::FromHandle($hicon)
        $fs = [System.IO.File]::Create($icoPath)
        $icon.Save($fs)
        $fs.Close()
        $bmp.Dispose()
    } catch {
        Write-Warning ("icon generation skipped: " + $_.Exception.Message)
    }
}

$sources = @(Get-ChildItem (Join-Path $srcDir '*.cs') | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw "no .cs files under $srcDir" }

$out = Join-Path $distDir 'TrayHider.exe'
$cscArgs = @(
    '/nologo',
    '/target:winexe',
    '/optimize+',
    '/platform:anycpu',
    '/codepage:65001',
    '/reference:System.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    "/out:$out"
)
if (Test-Path $icoPath) { $cscArgs += "/win32icon:$icoPath" }
$cscArgs += $sources

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "compile failed with exit code $LASTEXITCODE" }

$fi = Get-Item $out
Write-Output ("built : " + $fi.FullName)
Write-Output ("size  : " + $fi.Length + " bytes")
Write-Output ("sha256: " + (Get-FileHash $out -Algorithm SHA256).Hash)
