param(
    [ValidateSet('settings', 'about')]
    [string] $Mode = 'settings',

    [ValidateSet('cloudflare', 'oss', 'cos', 'selfhost')]
    [string] $Provider = 'oss',

    [Parameter(Mandatory)]
    [string] $Out
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class W32P {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$probe = 'D:\Software\R2UP\tools\uiprobe\bin\Release\net10.0-windows\uiprobe.exe'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$proj = 'D:\Software\R2UP\tools\uiprobe\uiprobe.csproj'

Stop-Process -Name 'uiprobe' -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

& $dotnet build $proj -c Release --nologo -v q 2>&1 | Select-String -Pattern 'error|错误' | Select-Object -First 5
if ($LASTEXITCODE -ne 0) { throw '探针编译失败' }

$args = if ($Mode -eq 'about') { @('about') } else { @('settings', $Provider) }
$p = Start-Process -FilePath $probe -ArgumentList $args -PassThru
Start-Sleep -Seconds 4

$list = New-Object System.Collections.ArrayList
$cb = [W32P+EnumProc]{
    param($h, $p2)
    $pid2 = 0
    [void][W32P]::GetWindowThreadProcessId($h, [ref]$pid2)
    if ($pid2 -eq $p.Id -and [W32P]::IsWindowVisible($h)) {
        $sb = New-Object System.Text.StringBuilder 512
        [void][W32P]::GetWindowTextW($h, $sb, 512)
        $r = New-Object W32P+RECT
        [void][W32P]::GetWindowRect($h, [ref]$r)
        [void]$list.Add([pscustomobject]@{ H=$h; Title=$sb.ToString(); R=$r })
    }
    return $true
}
[void][W32P]::EnumWindows($cb, [IntPtr]::Zero)

$w = $list | Select-Object -First 1
if (-not $w) { throw '没找到窗口' }

$r = $w.R
$bw = $r.Right - $r.Left
$bh = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap $bw, $bh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[void][W32P]::PrintWindow($w.H, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "saved $Out  '$($w.Title)'  ($bw x $bh)"

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
