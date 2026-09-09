<#
.SYNOPSIS
    延迟若干毫秒后在屏幕坐标点一下（默认左键，-Button Right 打右键）。

.DESCRIPTION
    配合 ShotBurst --burst 用：连拍是同步阻塞的，点击必须从另一个进程发出，
    而且要落在连拍窗口的中段 —— 太早拍不到起始帧，太晚拍不到收尾。
    延迟放在这里而不是 shell 里，是因为 shell 的 sleep 精度只有几十毫秒，
    而被拍的动画总长只有 240ms。
#>
param(
    [Parameter(Mandatory = $true)][int]$X,
    [Parameter(Mandatory = $true)][int]$Y,
    [int]$DelayMs = 150,
    [ValidateSet('Left', 'Right')][string]$Button = 'Left'
)

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class Clicker {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(int flags, int x, int y, int data, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@

[Clicker]::SetProcessDPIAware() | Out-Null
[Clicker]::SetCursorPos($X, $Y) | Out-Null

Start-Sleep -Milliseconds $DelayMs

if ($Button -eq 'Right') {
    [Clicker]::mouse_event(0x0008, 0, 0, 0, [IntPtr]::Zero)   # RIGHTDOWN
    [Clicker]::mouse_event(0x0010, 0, 0, 0, [IntPtr]::Zero)   # RIGHTUP
} else {
    [Clicker]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)   # LEFTDOWN
    [Clicker]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)   # LEFTUP
}
