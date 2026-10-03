# Create GitHub Releases for every version tag that has no release yet.
#
# ASCII-only on purpose: this machine runs Windows PowerShell 5.1, where a
# non-ASCII script body without a BOM gets mis-decoded and the parser dies on
# here-strings. All Chinese text lives in tools\release-notes\<tag>.md and is
# read as UTF-8 at runtime.
#
# ---------------------------------------------------------------------------
# TWO GHOTCHAS, both learned the hard way. Read before adding a tag.
# ---------------------------------------------------------------------------
#
# 1. THE 'Latest' BADGE. GitHub picks it by published_at among releases that are
#    neither draft nor prerelease. Two traps:
#      - Don't mark a version people should actually use as 'prerelease'. Doing
#        that to v2.0-pro pushed the badge onto v0.1, the newest non-prerelease
#        by creation order. Prerelease means 'not recommended', which is the
#        opposite of what a current release is.
#      - Editing a release (body, asset) resets published_at, and it does not
#        necessarily move forward. v2.0-pro ended up with a published_at
#        EARLIER than its created_at, so an older tag won the badge.
#    GitHub only honours an explicit published_at while the release is a draft.
#    To push a release to the front of the list: PATCH draft=true together with
#    the desired published_at, then PATCH draft=false. Assets survive the round
#    trip, but verify afterwards.
#
# 2. NEVER overwrite a published asset. Superseded binaries are confusing for
#    anyone who already has a SHA256. If a package turns out wrong, ship a new
#    version instead. (v2.0-pro was re-uploaded three times on 2026-10-03 while
#    sorting out the packaging; that is recorded rather than hidden.)
#
# v1.4 already has a published release and is skipped, never touched.

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

# tag -> display name, prerelease flag, optional installer to attach
$tags = [ordered]@{
    'v2.0-pro' = @{ Name = 'ScreenTime v2.0-pro'; Pre = $false; Asset = 'C:\ScreenTime-dev\package\ScreenTime-v2.0-pro-Setup.zip' }
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

    # Skip anything that already has a release: published content is immutable.
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

    try {
        $resp = Invoke-RestMethod -Uri $api -Method Post -Headers $headers -Body ([Text.Encoding]::UTF8.GetBytes($payload)) -Proxy $proxy -TimeoutSec 60 -ErrorAction Stop
        Write-Host '  OK: created'
        $created++

        $asset = $meta.Asset
        if ($asset -and (Test-Path $asset)) {
            $leaf   = Split-Path $asset -Leaf
            $sizeMB = [math]::Round((Get-Item $asset).Length / 1MB, 1)
            $up = "https://uploads.github.com/repos/$repo/releases/$($resp.id)/assets?name=$([Uri]::EscapeDataString($leaf))"
            $ah = @{
                Authorization  = "Bearer $token"
                'User-Agent'   = 'screentime-release'
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
Write-Host ''
Write-Host 'Reminder: after creating releases, confirm the Latest badge:'
Write-Host "  Invoke-RestMethod -Uri '$api/latest' -Headers \$headers -Proxy $proxy | Select tag_name"
Write-Host 'It must point at the newest recommended version, not at whatever was published last.'
