<#
.SYNOPSIS
    IslandX 鼠标穿透验证：逐点发 WM_NCHITTEST，断言"只有岛体轮廓与感应区接收点击"。

.DESCRIPTION
    需要 Debug 构建（探针挂在窗口标题上）。

    脚本**先标定坐标空间再测量**，这一步不能省：
    WM_NCHITTEST 的 lParam 是屏幕物理像素，而探针报的 box/hz 是窗口客户区 DIP，
    本机 125% 缩放下两者差 1.25 倍。手推这个换算错过两次，两次都得出
    "命中判定错位"的假结论，然后去改根本没错的产品代码。

    所以这里不推导，而是发两个已知点、用探针回读的 local 坐标反解出换算关系，
    再用一个第三点自检。自检不过就直接判定结果不可信，不出结论。

.EXAMPLE
    pwsh -File tools/hit-probe.ps1
#>

Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Text;
public class IslandProbe {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr SendMessageW(IntPtr h,int m,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }

    public static IntPtr Find(int pid) { IntPtr f=IntPtr.Zero;
        EnumWindows((h,l)=>{ uint p; GetWindowThreadProcessId(h,out p);
            if(p==(uint)pid){ var sb=new StringBuilder(900); GetWindowTextW(h,sb,900);
            if(sb.ToString().StartsWith("IslandX|")){ f=h; return false; } } return true; }, IntPtr.Zero); return f; }
    public static string Title(IntPtr h){ var sb=new StringBuilder(900); GetWindowTextW(h,sb,900); return sb.ToString(); }
    public static long Hit(IntPtr h,int x,int y){ IntPtr lp=(IntPtr)(((y&0xFFFF)<<16)|(x&0xFFFF)); return SendMessageW(h,0x0084,IntPtr.Zero,lp).ToInt64(); }
    public static void Hotkey(IntPtr h,int id){ SendMessageW(h,0x0312,(IntPtr)id,IntPtr.Zero); }
}
'@

$HotkeyExpand = 0xA101
$HotkeyDot    = 0xA102

[IslandProbe]::SetProcessDPIAware() | Out-Null
$proc = Get-Process -Name IslandX -ErrorAction SilentlyContinue
if (-not $proc) { Write-Error "IslandX 未运行"; exit 1 }

$hwnd = [IslandProbe]::Find($proc[0].Id)
if ($hwnd -eq 0) { Write-Error "找不到探针窗口 —— 需要 Debug 构建（Release 下标题只有 'IslandX'）"; exit 1 }

# 鼠标挪开，否则 hover 会把胶囊撑大、与 box 对不上
[IslandProbe]::SetCursorPos(700, 1200) | Out-Null
Start-Sleep -Milliseconds 1000

$wr = New-Object IslandProbe+RECT
[IslandProbe]::GetWindowRect($hwnd, [ref]$wr) | Out-Null

function Get-Local($sx, $sy) {
    [IslandProbe]::Hit($hwnd, $sx, $sy) | Out-Null
    $t = [IslandProbe]::Title($hwnd)
    if ($t -cmatch '\|hit[^@|]*@(-?[\d.]+),(-?[\d.]+)') { @([double]$Matches[1], [double]$Matches[2]) } else { $null }
}

# ---- 标定 ----
$p1 = Get-Local ($wr.L + 100) ($wr.T + 40)
$p2 = Get-Local ($wr.L + 500) ($wr.T + 160)
if (-not $p1 -or -not $p2) { Write-Error "探针未回报 hit 字段"; exit 1 }

$scaleX = (500 - 100) / ($p2[0] - $p1[0])
$scaleY = (160 - 40)  / ($p2[1] - $p1[1])
$offX   = 100 - ($p1[0] * $scaleX)
$offY   = 40  - ($p1[1] * $scaleY)

function To-ScreenX($dip) { [int]($wr.L + $offX + $dip * $scaleX) }
function To-ScreenY($dip) { [int]($wr.T + $offY + $dip * $scaleY) }

$chk = Get-Local (To-ScreenX 200) (To-ScreenY 30)
if ([Math]::Abs($chk[0] - 200) -gt 1 -or [Math]::Abs($chk[1] - 30) -gt 1) {
    Write-Error ("坐标标定自检失败：期望 local=(200,30)，实测 ({0},{1})。不出结论。" -f $chk[0], $chk[1])
    exit 1
}
Write-Host ("坐标标定  屏幕 = 窗口左上 + ({0:F1},{1:F1}) + DIP x {2:F3}   自检通过" -f $offX, $offY, $scaleX)
Write-Host ""

# ---- 扫描 ----
$script:totalPoints = 0
$script:totalBad    = 0

function Invoke-Sweep([string]$label) {
    $t = [IslandProbe]::Title($hwnd)
    $state = if ($t -cmatch '^IslandX\|(\w+)\|') { $Matches[1] } else { '?' }
    if ($t -cmatch '\|box(-?\d+),(-?\d+),(\d+)x(\d+)\|') {
        $bx=[int]$Matches[1]; $by=[int]$Matches[2]; $bw=[int]$Matches[3]; $bh=[int]$Matches[4]
    }
    if ($t -cmatch '\|hz(-?\d+),(-?\d+),(\d+)x(\d+)/(\d)') {
        $hx=[int]$Matches[1]; $hy=[int]$Matches[2]; $hw=[int]$Matches[3]; $hh=[int]$Matches[4]; $hv=$Matches[5]
    }

    $bad = 0; $n = 0; $worst = @()
    foreach ($gy in 0..23) {
        foreach ($gx in 0..69) {
            $cx = $gx * 10; $cy = $gy * 6; $n++
            $v = [IslandProbe]::Hit($hwnd, (To-ScreenX $cx), (To-ScreenY $cy))
            $inBody  = $cx -ge $bx -and $cx -le ($bx+$bw) -and $cy -ge $by -and $cy -le ($by+$bh)
            $inHover = $hv -eq '1' -and $cx -ge $hx -and $cx -le ($hx+$hw) -and $cy -ge $hy -and $cy -le ($hy+$hh)
            if ($v -eq 1 -and -not ($inBody -or $inHover)) {
                $bad++
                if ($worst.Count -lt 6) { $worst += "($cx,$cy)" }
            }
        }
    }
    $mark = if ($bad -eq 0) { 'OK' } else { "误收点如 " + ($worst -join ' ') }
    Write-Host ("  {0,-9} 岛体 {1,3}x{2,-3}  感应区可命中={3}   {4} 点，热区外误收 {5}   {6}" -f `
        $state, $bw, $bh, $hv, $n, $bad, $mark)
    $script:totalPoints += $n
    $script:totalBad    += $bad
}

Write-Host "只有岛体轮廓与感应区应接收点击，其余一律穿透"
Invoke-Sweep '收起'
[IslandProbe]::Hotkey($hwnd, $HotkeyExpand); Start-Sleep -Milliseconds 1600
Invoke-Sweep '展开'
[IslandProbe]::Hotkey($hwnd, $HotkeyExpand); Start-Sleep -Milliseconds 1600
[IslandProbe]::Hotkey($hwnd, $HotkeyDot);    Start-Sleep -Milliseconds 1600
Invoke-Sweep '灵动点'
[IslandProbe]::Hotkey($hwnd, $HotkeyDot);    Start-Sleep -Milliseconds 1600

Write-Host ""
Write-Host ("合计 {0} 点，误收 {1} 处" -f $script:totalPoints, $script:totalBad)
if ($script:totalBad -gt 0) { exit 1 }
