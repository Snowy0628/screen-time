# Create GitHub Releases for every version tag that does not have one yet.
#
# ASCII-only on purpose: this machine runs Windows PowerShell 5.1, where a
# non-ASCII script body without a BOM gets mis-decoded and the parser dies on
# here-strings. All Chinese text lives in tools\release-notes\<tag>.md and is
# read as UTF-8 at runtime.
#
# v1.4 already has a published release and is skipped, never overwritten.

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$token = $env:GH_TOKEN
if (-not $token) { throw 'GH_TOKEN is not set' }

$repo  = 'Snowy0628/screen-time'
$api   = "https://api.github.com/repos/$repo/releases"
$proxy = 'http://127.0.0.1:7897'
$notesDir = Join-Path $PSScriptRoot 'release-notes'

$headers = @{
    Authorization  = "Bearer $token"
    'User-Agent'   = 'screentime-release'
    Accept         = 'application/vnd.github+json'
    'Content-Type' = 'application/json; charset=utf-8'
}

# tag -> display name, prerelease flag, optional asset path
$tags = [ordered]@{
    'v2.0-pro' = @{ Name = "$([char]0x5C4F)$([char]0x5E55)$([char]0x4F7F)$([char]0x7528)$([char]0x65F6)$([char]0x95F4) v2.0-pro"; Pre = $true;  Asset = 'C:\ScreenTime-dev\package\ScreenTime-v2.0-pro-Setup.zip' }
    'v2.0'     = @{ Name = 'ScreenTime v2.0';     Pre = $true;  Asset = $null }
    'v1.7'     = @{ Name = 'ScreenTime v1.7';     Pre = $false; Asset = $null }
    'v1.6'     = @{ Name = 'ScreenTime v1.6';     Pre = $false; Asset = $null }
    'v1.5'     = @{ Name = 'ScreenTime v1.5';     Pre = $false; Asset = $null }
    'v1.3'     = @{ Name = 'ScreenTime v1.3';     Pre = $false; Asset = $null }
    'v1.2'     = @{ Name = 'ScreenTime v1.2';     Pre = $false; Asset = $null }
    'v1.0'     = @{ Name = 'ScreenTime v1.0';     Pre = $false; Asset = $null }
    'v0.1'     = @{ Name = 'ScreenTime v0.1';     Pre = $false; Asset = $null }
}

$created = 0; $skipped = 0; $failed = 0

foreach ($tag in $tags.Keys) {
    $meta = $tags[$tag]
    Write-Host ''
    Write-Host "--- $tag ---"

    # Skip anything that already has a release. Published releases are immutable.
    $exists = $false
    try {
        $null = Invoke-RestMethod -Uri "$api/tags/$tag" -Headers $headers -Proxy $proxy -TimeoutSec 25 -ErrorAction Stop
        $exists = $true
    } catch { }

    if ($exists) {
        Write-Host '  SKIP: release already exists'
        $skipped++
        continue
    }

    $notesFile = Join-Path $notesDir "$tag.md"
    if (Test-Path $notesFile) {
        $body = [IO.File]::ReadAllText($notesFile, [Text.Encoding]::UTF8)
    } else {
        $body = "Release $tag"
    }

    $payload = @{
        tag_name   = $tag
        name       = $meta.Name
        body       = $body
        draft      = $false
        prerelease = [bool]$meta.Pre
    } | ConvertTo-Json -Depth 3 -Compress

    $bytes = [Text.Encoding]::UTF8.GetBytes($payload)

    try {
        $resp = Invoke-RestMethod -Uri $api -Method Post -Headers $headers -Body $bytes -Proxy $proxy -TimeoutSec 60 -ErrorAction Stop
        Write-Host "  OK: created"
        $created++

        $asset = $meta.Asset
        if ($asset -and (Test-Path $asset)) {
            $leaf  = Split-Path $asset -Leaf
            $sizeMB = [math]::Round((Get-Item $asset).Length / 1MB, 1)
            $up = "https://uploads.github.com/repos/$repo/releases/$($resp.id)/assets?name=$([Uri]::EscapeDataString($leaf))"
            $ah = @{
                Authorization = "Bearer $token"
                'User-Agent'  = 'screentime-release'
                'Content-Type' = 'application/zip'
            }
            Write-Host "  uploading $leaf ($sizeMB MB) ..."
            $ar = Invoke-RestMethod -Uri $up -Method Post -Headers $ah -InFile $asset -Proxy $proxy -TimeoutSec 3600 -ErrorAction Stop
            Write-Host "  OK: asset $($ar.name) $([math]::Round($ar.size/1MB,1)) MB"
            Write-Host "  SHA256: $((Get-FileHash $asset -Algorithm SHA256).Hash)"
        }
    } catch {
        $msg = $_.Exception.Message
        if ($msg.Length -gt 160) { $msg = $msg.Substring(0, 160) }
        Write-Host "  FAIL: $msg"
        $failed++
    }
}

Write-Host ''
Write-Host "created=$created skipped=$skipped failed=$failed"
