<#
.SYNOPSIS
    打包 IslandX 为单文件、免安装运行时的可执行文件。

.DESCRIPTION
    产物是**一个 exe**，目标机器不需要装 .NET。

    默认不压缩。这不是疏忽 —— 压缩把体积从 189MB 降到 78MB，
    代价是**内存从 182MB 涨到 341MB**：压缩的单文件启动时要把程序集解压到内存并常驻，
    不压缩的则能直接内存映射、按需分页。对一个开机就挂着的常驻小工具来说，
    天天多占 160MB 换一次性省下的 110MB 下载量，不划算。

    要小体积就加 -Compress。

.PARAMETER Compress
    压缩单文件。体积 189MB → 78MB，常驻内存 182MB → 341MB。

.PARAMETER Output
    输出目录，默认 dist。

.EXAMPLE
    .\tools\pack.ps1
    .\tools\pack.ps1 -Compress
#>
[CmdletBinding()]
param(
    [switch]$Compress,
    [string]$Output = 'dist'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\IslandX.App\IslandX.App.csproj'

if (-not (Test-Path $project)) { throw "找不到项目：$project" }

$outDir = if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }

# 运行中的实例会锁住 exe，先停掉
Get-Process -Name IslandX -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue

$props = @(
    '--self-contained', 'true'
    '-p:PublishSingleFile=true'
    # WPF 的 5 个原生 DLL（wpfgfx / D3DCompiler / PresentationNative / PenImc / vcruntime）
    # 默认会留在 exe 旁边。要真正的单文件必须开这个，代价是首次启动解压到 %TEMP%\.net
    '-p:IncludeNativeLibrariesForSelfExtract=true'
    "-p:EnableCompressionInSingleFile=$($Compress.IsPresent.ToString().ToLower())"
    '-p:DebugType=none'
    # 不做 PublishTrimmed：SDK 直接拒绝（NETSDK1175，WinForms/WPF 不支持剪裁）。
    # 也不做 PublishReadyToRun：实测体积 +17MB、内存 +120MB、启动反而慢 0.2s，三项全输。
)

Write-Host "打包中（$(if ($Compress) { '压缩，小体积' } else { '不压缩，低内存' })）…" -ForegroundColor Cyan

$sw = [Diagnostics.Stopwatch]::StartNew()
& dotnet publish $project -c Release -r win-x64 -o $outDir --nologo @props
if ($LASTEXITCODE -ne 0) { throw "打包失败（退出码 $LASTEXITCODE）" }

$files = Get-ChildItem $outDir -Recurse -File
$exe = $files | Where-Object Name -eq 'IslandX.exe'

Write-Host ''
Write-Host ("完成，用时 {0:F1}s" -f $sw.Elapsed.TotalSeconds) -ForegroundColor Green
Write-Host ("  产物   : {0}" -f $exe.FullName)
Write-Host ("  体积   : {0:F1} MB" -f ($exe.Length / 1MB))
Write-Host ("  文件数 : {0}{1}" -f $files.Count, $(if ($files.Count -eq 1) { '（真·单文件）' } else { '（应为 1，多出来的是没打进去的依赖）' }))

if ($files.Count -ne 1) {
    Write-Warning "输出不止一个文件，检查 IncludeNativeLibrariesForSelfExtract 是否生效："
    $files | Where-Object Name -ne 'IslandX.exe' | ForEach-Object { Write-Host ("    " + $_.Name) }
}
