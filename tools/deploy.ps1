# 发布脚本：构建 → 发布 → 部署到本机 → 清除完整性标签
#
# 为什么必须清除完整性标签：
#   在受限环境（低完整性进程）里创建的文件会被自动打上
#   "Mandatory Label\Low Mandatory Level" 标签，并且该标签会沿目录继承。
#   Windows 规定：**从带低完整性标签的文件启动的进程，本身也会被降为低完整性**。
#   低完整性进程被 MIC 禁止向中完整性的 explorer 发窗口消息，
#   于是 Shell_NotifyIcon 返回"拒绝访问"，托盘图标永远无法注册。
#   同时它也写不了 %LOCALAPPDATA%，只能降级到便携模式。
#
# 所以部署后必须显式把 exe 与依赖文件的标签设回 Medium。

param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$DistDir  = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'),
    # 必须放在**没有低完整性标签**的目录。桌面被打了标签，所以不能放桌面。
    # 这个位置与当前已部署的路径保持一致。
    [string]$InstallDir = "C:\ScreenTime"
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host '=== 1. 停止运行中的实例 ===' -ForegroundColor Cyan
Get-Process -Name 'ScreenTime.App' -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
Start-Sleep -Seconds 2

Write-Host '=== 2. 构建 ===' -ForegroundColor Cyan
$env:DOTNET_NOLOGO = '1'
& dotnet build (Join-Path $RepoRoot 'ScreenTime.sln') -c Debug --nologo |
    Select-String -Pattern 'error|已成功' | Select-Object -First 6
if ($LASTEXITCODE -ne 0) { throw "构建失败" }

Write-Host '=== 3. 发布 ===' -ForegroundColor Cyan
# 必须先清 bin/obj/dist 再发布。
# 增量发布会漏文件：曾出现只产出 ScreenTime.App.exe、缺 7 个原生 DLL 的情况
# （e_sqlite3.dll / wpfgfx_cor3.dll 等），打包出去的程序根本起不来。
Remove-Item $DistDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $RepoRoot 'src\ScreenTime.App\bin'),
            (Join-Path $RepoRoot 'src\ScreenTime.App\obj'),
            (Join-Path $RepoRoot 'src\ScreenTime.Core\bin'),
            (Join-Path $RepoRoot 'src\ScreenTime.Core\obj') -Recurse -Force -ErrorAction SilentlyContinue

& dotnet publish (Join-Path $RepoRoot 'src\ScreenTime.App\ScreenTime.App.csproj') `
    -c Release -o $DistDir --nologo |
    Select-String -Pattern 'error' | Select-Object -First 5
if ($LASTEXITCODE -ne 0) { throw "发布失败" }

# 完整性检查：自包含 WPF 应用必须带这些原生库，缺一个都跑不起来
$required = @(
    'ScreenTime.App.exe',
    'e_sqlite3.dll',
    'wpfgfx_cor3.dll',
    'PresentationNative_cor3.dll',
    'D3DCompiler_47_cor3.dll',
    'PenImc_cor3.dll',
    'vcruntime140_cor3.dll'
)
$missing = $required | Where-Object { -not (Test-Path (Join-Path $DistDir $_)) }
if ($missing.Count -gt 0) {
    Write-Host "  ✗ 发布产物缺少文件: $($missing -join ', ')" -ForegroundColor Red
    Write-Host '    这是增量发布留下的坑，请确认已删除 bin/obj/dist 后重试。' -ForegroundColor Yellow
    throw '发布产物不完整'
}
Write-Host "  ✓ 发布产物完整（$((Get-ChildItem $DistDir -File).Count) 个文件）" -ForegroundColor Green

Write-Host "=== 4. 部署到 $InstallDir ===" -ForegroundColor Cyan
if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir | Out-Null }
robocopy $DistDir $InstallDir /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "复制失败 (robocopy $LASTEXITCODE)" }

Write-Host '=== 5. 清除低完整性标签（关键步骤）===' -ForegroundColor Cyan
icacls $InstallDir /setintegritylevel '(OI)(CI)Medium' /T /C | Out-Null

# 复核：确认没有任何文件仍是 Low
$low = @()
Get-ChildItem $InstallDir -Recurse -File | ForEach-Object {
    $o = icacls $_.FullName 2>&1 | Select-String -Pattern 'Low Mandatory'
    if ($o) { $low += $_.Name }
}
if ($low.Count -gt 0) {
    Write-Host "  ✗ 仍有低完整性标签: $($low -join ', ')" -ForegroundColor Red
    throw '完整性标签清除不完整，托盘图标将无法注册'
}
Write-Host '  ✓ 所有文件均为中完整性' -ForegroundColor Green

Write-Host '=== 6. 启动并验证托盘注册 ===' -ForegroundColor Cyan
$exe = Join-Path $InstallDir 'ScreenTime.App.exe'
$statusFile = Join-Path $InstallDir 'status-probe.txt'
Remove-Item $statusFile -Force -ErrorAction SilentlyContinue

Start-Process -FilePath $exe | Out-Null
Start-Sleep -Seconds 15

# 应用在中完整性下会使用 %LOCALAPPDATA%\ScreenTime 作为数据目录
$stdStatus = Join-Path $env:LOCALAPPDATA 'ScreenTime\tray-status.txt'
$portableStatus = Join-Path $InstallDir 'data\tray-status.txt'
$status = if (Test-Path $stdStatus) { $stdStatus } else { $portableStatus }

if (Test-Path $status) {
    Get-Content $status -Encoding UTF8 | Select-String -Pattern '结论|完整性级别|目录来源'
    $conclusion = Get-Content $status -Encoding UTF8 | Select-String -Pattern '结论'
    if ($conclusion -match '注册成功') {
        Write-Host '  ✓ 托盘图标注册成功' -ForegroundColor Green
    } else {
        Write-Host '  ✗ 托盘图标注册失败，见上面结论' -ForegroundColor Red
    }
} else {
    Write-Host "  ? 未找到状态文件: $status" -ForegroundColor Yellow
}

Write-Host ''
Write-Host '部署完成。' -ForegroundColor Cyan
Write-Host "  程序位置: $InstallDir"
Write-Host "  数据目录: $env:LOCALAPPDATA\ScreenTime"
