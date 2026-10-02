# Long-running health monitor for ScreenTime.App
#
# Purpose: verify that "app silently disappears a while after being minimized to
# tray" is really fixed. Root cause was SHQueryUserNotificationState throwing
# AccessViolationException (uncatchable in .NET, kills the process instantly);
# it has been replaced with a window-geometry check. That API was more likely to
# fault under load / fullscreen, so playing a game is the key test window.
#
# Every 20 s it records: process alive?, heartbeat lag, CPU, memory, crash count;
# each round it also scans the Windows event log for new .NET Runtime /
# Application Error events mentioning ScreenTime.
#
# Duration: default 10 minutes. The original crash happened at ~90 s, so a few
# minutes of clean running is already meaningful evidence - no need to watch for
# hours. Override with -Minutes if a longer soak is ever wanted.
#
# NOTE: this file is deliberately ASCII-only. PowerShell parses .ps1 using the
# console code page; a UTF-8 BOM or re-encoded non-ASCII text makes the parser
# fail before the script even runs (hit that twice already).
param(
    [int]$Minutes = 10
)

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$ws      = 'C:\ScreenTime-dev'
$hb      = "$env:LOCALAPPDATA\ScreenTime\heartbeat.txt"
$rawLog  = Join-Path $ws 'assets\_longmonitor.tsv'
$textLog = Join-Path $ws 'assets\_longmonitor.txt'
$started = Get-Date

# Only count crash events after monitoring starts, so the crashes recorded
# before the fix are not mistaken for new ones.
$marker = $started

Remove-Item $rawLog, $textLog -Force -ErrorAction SilentlyContinue
"time`telapsed_s`tstate`tpid`thb_lag_s`tcpu_s`tmem_mb`tcrash_events" | Set-Content $rawLog -Encoding UTF8

function Write-Line($msg) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $msg
    $line | Tee-Object -FilePath $textLog -Append
}

Write-Line "monitor started (every 20 s, up to $Minutes minutes)"
Write-Line "counting crash events newer than $($marker.ToString('HH:mm:ss'))"

$alive0   = Get-Process -Name 'ScreenTime.App' -ErrorAction SilentlyContinue
$firstPid = if ($alive0) { $alive0[0].Id } else { 0 }
Write-Line "initial pid = $firstPid"

$lastPid   = $firstPid
$crashSeen = 0
$rounds    = [int]($Minutes * 60 / 20)

for ($i = 1; $i -le $rounds; $i++) {
    Start-Sleep -Seconds 20

    $p     = Get-Process -Name 'ScreenTime.App' -ErrorAction SilentlyContinue
    $alive = [bool]$p
    $pid0  = if ($alive) { $p[0].Id } else { 0 }

    $hbAge = -1
    if (Test-Path $hb) {
        try {
            $first = ((Get-Content $hb -Raw) -split "`n")[0]
            $hbAge = [int]([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - [long]$first)
        } catch { $hbAge = -1 }
    }

    $cpu = if ($alive) { [math]::Round($p[0].TotalProcessorTime.TotalSeconds, 1) } else { 0 }
    $mem = if ($alive) { [math]::Round($p[0].WorkingSet64 / 1MB, 1) } else { 0 }

    $newCrash = 0
    try {
        $ev = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $marker } -MaxEvents 60 -ErrorAction SilentlyContinue |
              Where-Object { $_.Message -match 'ScreenTime' }
        if ($ev) { $newCrash = @($ev).Count }
    } catch { }

    $elapsed = [int]((Get-Date) - $started).TotalSeconds

    $state = if ($alive) { 'alive' } else { 'gone' }
    $row = "{0}`t{1}`t{2}`t{3}`t{4}`t{5}`t{6}`t{7}" -f `
        (Get-Date -Format 'HH:mm:ss'), $elapsed, $state, $pid0, $hbAge, $cpu, $mem, $newCrash
    Add-Content $rawLog -Encoding UTF8 -Value $row

    if (-not $alive) {
        Write-Line "!!! process gone after $([math]::Round($elapsed/60,1)) min"
        if ($newCrash -gt 0) { Write-Line "    $newCrash crash event(s) in event log" }
        break
    }

    if ($pid0 -ne $lastPid) {
        Write-Line "note: pid changed $lastPid -> $pid0 (app restarted)"
        $lastPid = $pid0
    }

    if ($newCrash -gt 0 -and $crashSeen -eq 0) {
        $crashSeen = $newCrash
        Write-Line "!!! $newCrash ScreenTime crash event(s) detected"
    }

    # Heartbeat lag over 30 s while alive means the tick loop is stuck.
    if ($hbAge -gt 30) {
        Write-Line "warning: heartbeat lag ${hbAge}s (tick loop may be stuck)"
    }

    # One summary line every 5 minutes keeps the text log readable.
    if ($i % 15 -eq 0) {
        Write-Line "alive $([math]::Round($elapsed/60,1)) min  hb_lag ${hbAge}s  mem ${mem}MB  cpu ${cpu}s"
    }
}

Write-Line "monitor finished"
