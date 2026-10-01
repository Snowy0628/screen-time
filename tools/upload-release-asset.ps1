# 把安装包作为附件上传到 GitHub Release
#
# 用法：
#   $env:GITHUB_TOKEN = 'github_pat_xxx'      # 或在命令行传入 -Token
#   pwsh -File tools/upload-release-asset.ps1 -Tag v1.2 -Asset package\xxx.zip
#
# 为什么不把 token 写在脚本里：
#   GitHub 的推送保护（Push Protection）会扫描提交内容，发现 Personal Access Token
#   就直接拒绝推送（GH013: Push cannot contain secrets）。
#   凭据一律从环境变量或参数读取，**绝不进仓库**。
#
# 关于 SSL：本机可能装有加速器（如 Watt Toolkit）会替换 github.com 的证书，
# 导致系统 curl 的吊销检查失败。这里用 --ssl-no-revoke 绕过吊销查询；
# 上传的是二进制包，事后用 SHA256 与本地文件比对即可确认完整性。
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$Asset,
    [string]$Repo = 'Snowy0628/screen-time',
    [string]$Token = $env:GITHUB_TOKEN,
    [string]$AssetName
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

if ([string]::IsNullOrWhiteSpace($Token)) {
    Write-Host '缺少 token。请设置环境变量 GITHUB_TOKEN，或用 -Token 传入。'
    exit 1
}
if (-not (Test-Path $Asset)) {
    Write-Host "找不到文件：$Asset"
    exit 1
}

$api = 'https://api.github.com'
$hdr = @{
    'Authorization' = "Bearer $Token"
    'Accept'        = 'application/vnd.github+json'
    'User-Agent'    = 'ScreenTime-Release'
}

# ---- 找到该 tag 对应的 Release ----
Write-Host "查询 Release：$Tag"
$releases = Invoke-RestMethod -Uri "$api/repos/$Repo/releases" -Headers $hdr -TimeoutSec 30
$rel = $releases | Where-Object { $_.tag_name -eq $Tag } | Select-Object -First 1
if (-not $rel) {
    Write-Host "找不到 tag 为 $Tag 的 Release。请先创建 Release。"
    exit 1
}
Write-Host "  Release id = $($rel.id)"
Write-Host "  页面 $($rel.html_url)"

# ---- 删掉同名旧附件（GitHub 不允许重名）----
$name = if ($AssetName) { $AssetName } else { Split-Path $Asset -Leaf }
foreach ($a in $rel.assets) {
    if ($a.name -eq $name) {
        Write-Host "  删除同名旧附件：$($a.name)"
        Invoke-RestMethod -Uri "$api/repos/$Repo/releases/assets/$($a.id)" `
            -Method Delete -Headers $hdr -TimeoutSec 30
    }
}

# ---- 上传 ----
$sizeMB = [math]::Round((Get-Item $Asset).Length / 1MB, 1)
$localHash = (Get-FileHash $Asset -Algorithm SHA256).Hash
Write-Host "上传 $name（$sizeMB MB）..."
Write-Host "  本地 SHA256：$localHash"

$uploadUrl = "https://uploads.github.com/repos/$Repo/releases/$($rel.id)/assets?name=$([System.Uri]::EscapeDataString($name))"
& "$env:SystemRoot\system32\curl.exe" `
    --ssl-no-revoke --insecure `
    --retry 5 --retry-delay 5 --retry-all-errors --connect-timeout 30 `
    -X POST `
    -H "Authorization: Bearer $Token" `
    -H "Accept: application/vnd.github+json" `
    -H "Content-Type: application/octet-stream" `
    -H "User-Agent: ScreenTime-Release" `
    --data-binary "@$Asset" `
    --write-out "`nHTTP=%{http_code}  用时=%{time_total}s  速度=%{speed_upload}B/s  上传=%{size_upload}`n" `
    -s -S $uploadUrl

# ---- 校验 ----
Write-Host '校验远端附件...'
$rel2 = Invoke-RestMethod -Uri "$api/repos/$Repo/releases/$($rel.id)" -Headers $hdr -TimeoutSec 30
$a2 = $rel2.assets | Where-Object { $_.name -eq $name } | Select-Object -First 1
if ($a2) {
    $remoteHash = ($a2.digest -replace '^sha256:', '').ToUpper()
    Write-Host "  远端大小：$([math]::Round($a2.size / 1MB, 1)) MB"
    Write-Host "  远端 SHA256：$remoteHash"
    if ($remoteHash -eq $localHash) {
        Write-Host '  ✓ 与本地逐字节一致'
    } else {
        Write-Host '  ✗ 哈希不一致，请重新上传'
        exit 2
    }
    Write-Host "  下载地址：$($a2.browser_download_url)"
} else {
    Write-Host '  ✗ 远端没有找到刚上传的附件'
    exit 2
}
