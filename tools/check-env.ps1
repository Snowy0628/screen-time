# M0 环境自检 + NuGet 快速测试（在完全权限下运行）
$ErrorActionPreference = 'Continue'
$ws = 'C:\Users\MR\Desktop\ScreenTime'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'

$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:HTTP_PROXY  = 'http://127.0.0.1:7897'
$env:HTTPS_PROXY = 'http://127.0.0.1:7897'

Write-Output '===== 0. 裸 TCP 连通性（区分“网络不通”与“TLS 被拒”） ====='
foreach ($hp in @(@('api.nuget.org', 443), @('127.0.0.1', 7897), @('www.baidu.com', 443))) {
    $c = New-Object System.Net.Sockets.TcpClient
    try {
        $iar = $c.BeginConnect($hp[0], $hp[1], $null, $null)
        $ok = $iar.AsyncWaitHandle.WaitOne(6000)
        if ($ok -and $c.Connected) { Write-Output ("  TCP OK   {0}:{1}" -f $hp[0], $hp[1]) }
        else { Write-Output ("  TCP FAIL {0}:{1}" -f $hp[0], $hp[1]) }
    } catch {
        Write-Output ("  TCP FAIL {0}:{1} -> {2}" -f $hp[0], $hp[1], $_.Exception.Message)
    } finally { $c.Close() }
}

Write-Output '===== 1. 网络（经由 Clash 7897） ====='
foreach ($u in @('https://api.nuget.org/v3/index.json', 'https://dotnet.microsoft.com/')) {
    try {
        $r = Invoke-WebRequest -Uri $u -TimeoutSec 20 -UseBasicParsing
        Write-Output ("  OK   {0} => HTTP {1}" -f $u, $r.StatusCode)
    } catch {
        Write-Output ("  FAIL {0} => {1}" -f $u, $_.Exception.Message)
    }
}

Write-Output '===== 2. NuGet 用户配置是否可读 ====='
$cfg = Join-Path $env:APPDATA 'NuGet\NuGet.Config'
Write-Output ("  path   : {0}" -f $cfg)
Write-Output ("  exists : {0}" -f (Test-Path $cfg))
try {
    New-Item -ItemType Directory -Force (Split-Path $cfg) -ErrorAction Stop | Out-Null
    Write-Output '  可创建 : 是'
} catch {
    Write-Output ("  可创建 : 否 -> {0}" -f $_.Exception.Message)
}

Write-Output '===== 3. 试还原 Microsoft.Data.Sqlite（含约 7 个传递依赖） ====='
$t = Join-Path $ws '.restoretest'
if (Test-Path $t) { Remove-Item $t -Recurse -Force -ErrorAction SilentlyContinue }
& $dotnet new console -n T -o $t --no-restore 2>&1 | Select-Object -Last 1
& $dotnet add (Join-Path $t 'T.csproj') package Microsoft.Data.Sqlite --version 8.0.10 2>&1 |
    Select-String -Pattern 'error|已添加|PackageReference|警告|Added' | Select-Object -First 10
Write-Output ("  ADD_EXIT = {0}" -f $LASTEXITCODE)

Write-Output '===== 4. 包缓存落地情况 ====='
$pkgRoots = @(
    (Join-Path $env:USERPROFILE '.nuget\packages'),
    (Join-Path $ws '.nugetpackages')
)
foreach ($p in $pkgRoots) {
    if (Test-Path $p) {
        $names = Get-ChildItem $p -Directory -ErrorAction SilentlyContinue | Select-Object -Expand Name
        Write-Output ("  {0} -> {1} 个包: {2}" -f $p, $names.Count, ($names -join ', '))
    } else {
        Write-Output ("  {0} -> 不存在" -f $p)
    }
}

Write-Output '===== 结论 ====='
$net = $false
try { $null = Invoke-WebRequest -Uri 'https://api.nuget.org/v3/index.json' -TimeoutSec 15 -UseBasicParsing; $net = $true } catch {}
Write-Output ("  网络可用   : {0}" -f $net)
Write-Output ("  包缓存可写 : {0}" -f (Test-Path (Join-Path $env:USERPROFILE '.nuget\packages')))
