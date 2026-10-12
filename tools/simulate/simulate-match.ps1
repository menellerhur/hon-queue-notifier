# Plays a scripted match into a stand-in game log so the running notifier reacts as it would to a real one.
# For the by-hand checks that are hard to wait for in a real game. Development only; nothing here ships.
#
#   powershell -ExecutionPolicy Bypass -File tools\simulate\simulate-match.ps1 -Scenario Cancel
#
#   Cancel   a match is found, hero pick opens, the match is cancelled: the popup turns into the grey
#            "MATCH CANCELLED" notice. Stay out of the game while it runs.
#   End      a match is found, the game starts (the popup turns ember red), the game ends: the grey
#            "MATCH ENDED" notice. Stay out of the game while it runs.
#   Focused  a match is found while the game window has focus: no popup, and the app's log says
#            "game focused=True". Needs the game (juvio.exe) running; click into it during the countdown.
#
# The notifier must be running with no game log chosen in its settings (it then follows every console.log
# under Documents\Juvio). The stand-in log is Documents\Juvio\HonQueueNotifier-simulation\console.log,
# UTF-16LE with a byte order mark like the game's own, and is deleted at the end.
# A phone push goes out as for a real match; the scenarios end within about 7 s of the match being found,
# before the default 8 s delay, so with Mode=smart and the PC in use none is sent.

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Cancel', 'End', 'Focused')]
    [string]$Scenario,
    [int]$Countdown = 10
)

$ErrorActionPreference = 'Stop'

$appDir = Join-Path $env:APPDATA 'HonQueueNotifier'
$appLog = Join-Path $appDir 'HonQueueNotifier.log'
$ini = Join-Path $appDir 'HonQueueNotifier.ini'
$folder = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Juvio\HonQueueNotifier-simulation'
$log = Join-Path $folder 'console.log'

if (-not (Get-Process HonQueueNotifier -ErrorAction SilentlyContinue)) { throw 'HonQueueNotifier.exe is not running. Start it first.' }
if ((Test-Path $ini) -and (Select-String -Path $ini -Pattern '^\s*LogPath\s*=\s*\S' -Quiet)) {
    throw "A game log is chosen in the settings (LogPath in $ini), so the stand-in log would not be read. Clear it first."
}
$gameRunning = [bool](Get-Process juvio -ErrorAction SilentlyContinue)
if ($Scenario -eq 'Focused' -and -not $gameRunning) { throw 'The game (juvio.exe) is not running. Start it and stay in the main menu.' }

function Append([string]$line) {
    $bytes = [Text.Encoding]::Unicode.GetBytes($line + "`r`n")
    $fs = [IO.File]::Open($log, 'Append', 'Write', 'ReadWrite')
    try { $fs.Write($bytes, 0, $bytes.Length) } finally { $fs.Dispose() }
    Write-Host ('  {0:HH:mm:ss}  {1}' -f (Get-Date), $line)
}

function Wait([int]$seconds, [string]$why) {
    Write-Host "  ... $why ($seconds s)"
    Start-Sleep -Seconds $seconds
}

$logLinesBefore = if (Test-Path $appLog) { (Get-Content $appLog).Count } else { 0 }

New-Item -ItemType Directory -Force $folder | Out-Null
[IO.File]::WriteAllText($log, "Engine start up...`r`n", [Text.Encoding]::Unicode)   # writes the FF FE mark
try {
    Write-Host "Stand-in log: $log"
    Wait 7 'until the notifier finds the new log (it looks every 5 s)'

    if ($Scenario -eq 'Focused') {
        Write-Host "Click into the game now. The match is found in $Countdown s."
    } else {
        Write-Host "Stay out of the game (leave this window or another one in front). The match is found in $Countdown s."
    }
    for ($i = $Countdown; $i -gt 0; $i--) { Write-Host -NoNewline "$i "; Start-Sleep -Seconds 1 }
    Write-Host ''

    Append 'Disconnect - CMatchmakingClient::HandleFoundMatch'
    Wait 1 'loading'
    Append 'Client phase MainMenu -> Loading, 0ms since leaving the menu'
    Append 'Client loading world "midwars"'
    Wait 1 'hero pick'
    Append 'Client phase Loading -> Lobby after 2324ms'

    switch ($Scenario) {
        'Cancel' {
            Wait 4 'the popup should be up; the match is cancelled next'
            Append 'Client phase Lobby -> MainMenu after 4000ms'
            Write-Host 'Expected: the popup turns into the grey "MATCH CANCELLED" notice and stays until clicked.'
        }
        'End' {
            Wait 2 'the popup should be up; the game starts next'
            Append 'Client phase Lobby -> InGame after 2000ms'
            Wait 3 'the popup should be ember red, "GAME HAS STARTED"; the game ends next'
            Append 'Client phase InGame -> MainMenu after 3000ms'
            Write-Host 'Expected: the popup turns into the grey "MATCH ENDED" notice and stays until clicked.'
        }
        'Focused' {
            Wait 3 'back to the menu'
            Append 'Client phase Lobby -> MainMenu after 3000ms'
            Write-Host 'Expected: no popup, no sound.'
        }
    }
    Wait 2 'for the notifier to read the last line'
}
finally {
    Remove-Item -Recurse -Force $folder -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "The app's log for this run ($appLog):"
if (Test-Path $appLog) {
    Get-Content $appLog | Select-Object -Skip $logLinesBefore |
        Where-Object { $_ -match 'MATCH FOUND|GAME STARTED|state:|notice|dismiss|CANCELLED|ENDED|simulation' } |
        ForEach-Object { Write-Host "  $_" }
}
