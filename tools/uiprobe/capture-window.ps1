param(
    [Parameter(Mandatory)]
    [string] $Title,

    [Parameter(Mandatory)]
    [string] $Out
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class W32Q {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$list = New-Object System.Collections.ArrayList
$cb = [W32Q+EnumProc]{
    param($h, $p2)
    if ([W32Q]::IsWindowVisible($h)) {
        $sb = New-Object System.Text.StringBuilder 512
        [void][W32Q]::GetWindowTextW($h, $sb, 512)
        $t = $sb.ToString()
        if ($t -and $t -like "*$Title*") {
            $r = New-Object W32Q+RECT
            [void][W32Q]::GetWindowRect($h, [ref]$r)
            [void]$list.Add([pscustomobject]@{ H=$h; Title=$t; R=$r })
        }
    }
    return $true
}
[void][W32Q]::EnumWindows($cb, [IntPtr]::Zero)

$w = $list | Select-Object -First 1
if (-not $w) { throw "没找到标题包含 '$Title' 的窗口" }

[void][W32Q]::SetForegroundWindow($w.H)
Start-Sleep -Milliseconds 400

$r = $w.R
$bw = $r.Right - $r.Left
$bh = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap $bw, $bh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[void][W32Q]::PrintWindow($w.H, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "saved $Out  '$($w.Title)'  ($bw x $bh)"
