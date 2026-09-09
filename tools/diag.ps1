<#
.SYNOPSIS
    带诊断日志启动 IslandX，用来复现"右键岛体卡死"。

.DESCRIPTION
    日志每写一行就落盘 —— 卡死时进程还活着，带缓冲的话最关键的那几行永远看不到。

    另有一个看门狗线程每 500ms 向 UI 线程投一个空活儿，2 秒内没回话就往日志里
    写"UI 线程无响应"，恢复时再写一条 —— UI 线程卡住时它自己报告不了这件事。

    用法：
        pwsh -File tools\diag.ps1              # Debug 构建（有窗口标题探针）
        pwsh -File tools\diag.ps1 -Release     # Release 构建

    然后**手动右键岛体**，反复几次直到卡住。卡住后（或者觉得够了）：
        pwsh -File tools\diag.ps1 -Show        # 打印日志

    日志在 %APPDATA%\IslandX\diag.log，每次启动会截断重写。
#>
param(
    [switch]$Release,
    [switch]$Show,
    [switch]$Stop
)

$ErrorActionPreference = 'Stop'
$log = Join-Path $env:APPDATA 'IslandX\diag.log'

if ($Show) {
    if (-not (Test-Path $log)) { Write-Error "还没有日志：$log"; exit 1 }
    Get-Content $log -Encoding utf8
    exit 0
}

if ($Stop) {
    Get-Process IslandX -ErrorAction SilentlyContinue | Stop-Process -Force
    Write-Host '已停止 IslandX'
    exit 0
}

$root = Split-Path -Parent $PSScriptRoot
$cfg = if ($Release) { 'Release' } else { 'Debug' }
$exe = Join-Path $root "src\IslandX.App\bin\$cfg\net10.0-windows10.0.19041.0\win-x64\IslandX.exe"

if (-not (Test-Path $exe)) { Write-Error "找不到 $cfg 构建：$exe"; exit 1 }

Get-Process IslandX -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

# 环境变量要设在**被启动的进程**上。Start-Process 会继承当前会话的环境
$env:ISLANDX_DIAG = '1'
Start-Process $exe

Write-Host ''
Write-Host "已启动 $cfg 版，诊断日志已开。" -ForegroundColor Green
Write-Host "日志：$log"
Write-Host ''
Write-Host '现在手动右键岛体，反复几次直到卡住。' -ForegroundColor Yellow
Write-Host '卡住后运行：  pwsh -File tools\diag.ps1 -Show'
Write-Host ''
