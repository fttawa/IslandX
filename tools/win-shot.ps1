<#
.SYNOPSIS
    把某个窗口强制置顶（便于截图），可选滚动与点击，然后报出它的屏幕矩形。

.DESCRIPTION
    专为验证设置窗口而写。三件事都是绕开限制的：

      · 置顶用 SetWindowPos(HWND_TOPMOST) 而不是 SetForegroundWindow ——
        后者在后台进程里会被系统的前台锁挡掉（试过，静默失败）。
      · 坐标要先 SetProcessDPIAware，否则 GetWindowRect 拿到的是虚拟化后的值，
        本机 125% 缩放下差 1.25 倍，据此截图会整体偏移。
      · 只碰 user32。落像素交给 ShotBurst —— .NET 10 上 Add-Type 拼不起
        System.Drawing 的依赖链（本项目为此栽过三次）。

.EXAMPLE
    pwsh -File tools/win-shot.ps1 -Title "IslandX 设置" -ScrollDown 4
#>
param(
    [Parameter(Mandatory = $true)][string]$Title,
    [string]$Process = 'IslandX',
    [int]$ScrollDown = 0,
    [int]$ClickX = -1,
    [int]$ClickY = -1
)

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class WinShot {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(int flags, int x, int y, int data, IntPtr extra);

    delegate bool EnumProc(IntPtr h, IntPtr l);

    public static IntPtr Find(uint pid, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid) return true;
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, sb.Capacity);
            if (sb.ToString() == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static string Pin(IntPtr h) {
        // HWND_TOPMOST | 不移动 | 不改尺寸 | 显示
        SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x40);
        RECT r; GetWindowRect(h, out r);
        return r.L + " " + r.T + " " + (r.R - r.L) + " " + (r.B - r.T);
    }

    public static void Wheel(int x, int y, int notches) {
        SetCursorPos(x, y);
        for (var i = 0; i < notches; i++) {
            mouse_event(0x0800, 0, 0, -120, IntPtr.Zero);   // MOUSEEVENTF_WHEEL，负值向下
            System.Threading.Thread.Sleep(60);
        }
    }

    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(150);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);   // LEFTDOWN
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);   // LEFTUP
    }
}
'@

[WinShot]::SetProcessDPIAware() | Out-Null

$proc = Get-Process $Process -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Error "$Process 未运行"; exit 1 }

$hwnd = [WinShot]::Find([uint32]$proc.Id, $Title)
if ($hwnd -eq [IntPtr]::Zero) { Write-Error "找不到标题为「$Title」的窗口"; exit 1 }

$rect = [WinShot]::Pin($hwnd)
$parts = $rect -split ' '

if ($ScrollDown -gt 0) {
    $cx = [int]$parts[0] + [int]$parts[2] / 2
    $cy = [int]$parts[1] + [int]$parts[3] / 2
    [WinShot]::Wheel([int]$cx, [int]$cy, $ScrollDown)
}

if ($ClickX -ge 0 -and $ClickY -ge 0) {
    [WinShot]::Click($ClickX, $ClickY)
}

$rect
