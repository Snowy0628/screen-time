# Create a GitHub Release and upload the installer as an asset.
#
# Usage:
#   $env:GITHUB_TOKEN = 'github_pat_xxx'
#   pwsh -File tools/create-release.ps1 -Tag v1.4 -Notes tools\release-notes-v1.4.md -Asset package\ScreenTime-v1.4-Setup.zip
#
# The token is read from the environment and never written into the repo
# (GitHub Push Protection rejects any commit containing a PAT).
#
# This script is deliberately **ASCII-only**. PowerShell reads .ps1 files using
# the console code page; a UTF-8 BOM or double-encoded Chinese text makes the
# parser fail before the script even runs. All Chinese text lives in the
# release-notes markdown file, which is read with an explicit encoding.
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$Notes,
    [Parameter(Mandatory = $true)][string]$Asset,
    [string]$Title,
    [string]$Repo  = 'Snowy0628/screen-time',
    [string]$Token = $env:GITHUB_TOKEN,
    [switch]$Prerelease
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

if ([string]::IsNullOrWhiteSpace($Title)) { $Title = "ScreenTime $Tag" }
if ([string]::IsNullOrWhiteSpace($Token)) { Write-Host 'ERROR: missing token (set GITHUB_TOKEN)'; exit 1 }
if (-not (Test-Path $Notes)) { Write-Host "ERROR: notes file not found: $Notes"; exit 1 }
if (-not (Test-Path $Asset)) { Write-Host "ERROR: asset not found: $Asset"; exit 1 }

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

# Update the release if the tag already has one, otherwise create it.
$existing = $null
try {
    $existing = Invoke-RestMethod -Uri "$api/repos/$Repo/releases/tags/$Tag" -Headers $hdr -TimeoutSec 30
} catch {
    if ([int]$_.Exception.Response.StatusCode -ne 404) { throw }
}

if ($existing) {
    Write-Host "Release $Tag exists, updating notes (id=$($existing.id))"
    $rel = Invoke-RestMethod -Uri "$api/repos/$Repo/releases/$($existing.id)" -Method Patch `
        -Headers $hdr -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) `
        -ContentType 'application/json; charset=utf-8' -TimeoutSec 60
} else {
    Write-Host "Creating release $Tag"
    $rel = Invoke-RestMethod -Uri "$api/repos/$Repo/releases" -Method Post `
        -Headers $hdr -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) `
        -ContentType 'application/json; charset=utf-8' -TimeoutSec 60
}
Write-Host "  page: $($rel.html_url)"

# Remove a same-named asset first (GitHub rejects duplicates on one release).
#
# NOTE: this only ever touches the SAME release (same tag) and the SAME file
# name - it is re-uploading that one asset. It never walks other releases.
#
# POLICY (do not change without asking the user):
#   Past releases and their installer assets are kept on GitHub permanently.
#   Never delete an older release or an older tag when shipping a new version -
#   the user explicitly asked to retain historical installers, and deleted
#   release assets are NOT recoverable (they live only on GitHub's servers).
#   Old packages are also no longer kept locally, so a deleted asset is gone
#   for good.
$name = Split-Path $Asset -Leaf
foreach ($a in $rel.assets) {
    if ($a.name -eq $name) {
        Write-Host "  removing old asset $($a.name)"
        Invoke-RestMethod -Uri "$api/repos/$Repo/releases/assets/$($a.id)" -Method Delete -Headers $hdr -TimeoutSec 30
    }
}

$sizeMB    = [math]::Round((Get-Item $Asset).Length / 1MB, 1)
$localHash = (Get-FileHash $Asset -Algorithm SHA256).Hash
Write-Host "Uploading $name ($sizeMB MB)"
Write-Host "  local SHA256: $localHash"

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
    --write-out "`n  HTTP=%{http_code}  time=%{time_total}s  speed=%{speed_upload}B/s`n" `
    -s -S $uploadUrl

# Verify what actually landed on the server.
$rel2 = Invoke-RestMethod -Uri "$api/repos/$Repo/releases/$($rel.id)" -Headers $hdr -TimeoutSec 30
$a2 = $rel2.assets | Where-Object { $_.name -eq $name } | Select-Object -First 1
if (-not $a2) { Write-Host '  ERROR: asset not found remotely'; exit 2 }

$remoteHash = ($a2.digest -replace '^sha256:', '').ToUpper()
Write-Host "  remote size  : $([math]::Round($a2.size / 1MB, 1)) MB"
Write-Host "  remote SHA256: $remoteHash"
if ($remoteHash -ne $localHash) { Write-Host '  ERROR: hash mismatch'; exit 2 }
Write-Host '  OK: byte-for-byte identical to local'
Write-Host "  download: $($a2.browser_download_url)"
