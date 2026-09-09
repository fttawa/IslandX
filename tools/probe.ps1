<#
.SYNOPSIS
    读一次 IslandX 的调试探针（挂在窗口标题上，需 Debug 构建）。

.DESCRIPTION
    Get-Process 的 MainWindowTitle 读不到它 —— 岛体是无边框工具窗口，
    不进任务栏，系统不认它是"主窗口"。必须 EnumWindows 按 PID 找。

.PARAMETER Field
    只打印某一段（按 | 切分后包含该前缀的那段）。省略则打印整条。
#>
param([string]$Field)

Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Text;
public class IxProbe {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);

    public static string Read(uint targetPid) {
        string found = null;
        EnumWindows((h, l) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != targetPid) return true;
            var sb = new StringBuilder(1024);
            GetWindowTextW(h, sb, sb.Capacity);
            var t = sb.ToString();
            if (t.StartsWith("IslandX|")) { found = t; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

$p = Get-Process IslandX -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { Write-Error 'IslandX 未运行'; exit 1 }

$title = [IxProbe]::Read($p.Id)
if (-not $title) { Write-Error '找不到探针标题（是否为 Release 构建？）'; exit 1 }

if ($Field) {
    ($title -split '\|') | Where-Object { $_ -like "$Field*" }
} else {
    $title
}
