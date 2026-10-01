# 创建 GitHub Release 并上传安装包
#
# 用法：
#   $env:GITHUB_TOKEN = 'github_pat_xxx'
#   pwsh -File tools/create-release.ps1 -Tag v1.2 -Notes tools\release-notes-v1.2.md -Asset package\ScreenTime-v1.2-Setup.zip
#
# 凭据从环境变量读取，绝不写进脚本（GitHub 推送保护会拒绝含 token 的提交）。
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$Notes,
    [Parameter(Mandatory = $true)][string]$Asset,
    [string]$Title = "屏幕使用时间 $Tag",
    [string]$Repo  = 'Snowy0628/screen-time',
    [string]$Token = $env:GITHUB_TOKEN,
    [switch]$Prerelease
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

if ([string]::IsNullOrWhiteSpace($Token)) { Write-Host '缺少 token（GITHUB_TOKEN）'; exit 1 }
if (-not (Test-Path $Notes)) { Write-Host "找不到说明文件：$Notes"; exit 1 }
if (-not (Test-Path $Asset)) { Write-Host "找不到安装包：$Asset"; exit 1 }

$api = 'https://api.github.com'
$hdr = @{
    'Authorization' = "Bearer $Token"
    'Accept'        = 'application/vnd.github+json'
    'User-Agent'    = 'ScreenTime-Release'
}

$body = @{
    tag_name   = $Tag
    name       = $Title
    body       = [System.IO.File]::ReadAllText((Resolve-Path $Notes), [System.Text.Encoding]::UTF8)
    draft      = $false
    prerelease = [bool]$Prerelease
} | ConvertTo-Json

# ---- 已存在就更新，不存在就创建 ----
$existing = $null
try {
    $existing = Invoke-RestMethod -Uri "$api/repos/$Repo/releases/tags/$Tag" -Headers $hdr -TimeoutSec 30
} catch {
    if ([int]$_.Exception.Response.StatusCode -ne 404) { throw }
}

if ($existing) {
    Write-Host "Release $Tag 已存在，更新说明（id=$($existing.id)）"
    $rel = Invoke-RestMethod -Uri "$api/repos/$Repo/releases/$($existing.id)" -Method Patch `
        -Headers $hdr -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) `
        -ContentType 'application/json; charset=utf-8' -TimeoutSec 60
} else {
    Write-Host "创建 Release $Tag"
    $rel = Invoke-RestMethod -Uri "$api/repos/$Repo/releases" -Method Post `
        -Headers $hdr -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) `
        -ContentType 'application/json; charset=utf-8' -TimeoutSec 60
}
Write-Host "  页面 $($rel.html_url)"

# ---- 上传附件 ----
$name = Split-Path $Asset -Leaf
foreach ($a in $rel.assets) {
    if ($a.name -eq $name) {
        Write-Host "  删除同名旧附件 $($a.name)"
        Invoke-RestMethod -Uri "$api/repos/$Repo/releases/assets/$($a.id)" -Method Delete -Headers $hdr -TimeoutSec 30
    }
}

$sizeMB    = [math]::Round((Get-Item $Asset).Length / 1MB, 1)
$localHash = (Get-FileHash $Asset -Algorithm SHA256).Hash
Write-Host "上传 $name（$sizeMB MB）"
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
    --write-out "`n  HTTP=%{http_code}  用时=%{time_total}s  速度=%{speed_upload}B/s`n" `
    -s -S $uploadUrl

# ---- 校验 ----
$rel2 = Invoke-RestMethod -Uri "$api/repos/$Repo/releases/$($rel.id)" -Headers $hdr -TimeoutSec 30
$a2 = $rel2.assets | Where-Object { $_.name -eq $name } | Select-Object -First 1
if (-not $a2) { Write-Host '  ✗ 远端没有找到附件'; exit 2 }

$remoteHash = ($a2.digest -replace '^sha256:', '').ToUpper()
Write-Host "  远端大小：$([math]::Round($a2.size / 1MB, 1)) MB"
Write-Host "  远端 SHA256：$remoteHash"
if ($remoteHash -ne $localHash) { Write-Host '  ✗ 哈希不一致'; exit 2 }
Write-Host '  ✓ 与本地逐字节一致'
Write-Host "  下载地址：$($a2.browser_download_url)"
