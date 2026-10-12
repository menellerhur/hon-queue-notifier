# The game log

Heroes of Newerth: Reborn writes everything it does to a `console.log`. The notifier reads that file and nothing else: no mod, no memory reading, no network inspection. This page describes what the file contains and how the notifier reads it. Observed on game version 0.13.0.0, Windows x64.

## Where it is

The game writes its log into the profile folder of the last mod named on its command line (`-mod`):

| Launch | Log |
|---|---|
| Default | `%USERPROFILE%\Documents\Juvio\Heroes of Newerth\console.log` |
| `-mod "heroes of newerth;extensions"` | `%USERPROFILE%\Documents\Juvio\extensions\console.log` |

Logs with matches in them have been seen in both folders. The notifier follows every `console.log` up to two folders below `Documents\Juvio`, so neither case needs configuring; a specific file can still be chosen in the settings.

The game is installed and started through Juvio, its launcher and platform. Its program is `%LOCALAPPDATA%\Juvio\bin\juvio.exe`, process name `juvio`. The launcher keeps its own logs in `%LOCALAPPDATA%\Juvio\logs\`; the notifier does not use them.

## Format

- **UTF-16 little-endian with a byte order mark** (`FF FE`), two bytes per character. Read as single bytes, every other byte is zero and no text matches: the first versions of the notifier did that and missed every real match. The notifier picks the decoder from the first bytes of the file (`LogText`) and keeps one decoder per file, because a block write can end in the middle of a character. To search the file by hand, decode it first: PowerShell's `Get-Content` reads the byte order mark; elsewhere use `iconv -f UTF-16 -t UTF-8`. A byte-level `grep` finds nothing.
- CRLF line endings, no timestamps.
- **Written in buffered blocks.** The file can end in the middle of a line until the next block arrives.
- **Lines from different threads interleave.** During loading, fragments of different lines are glued together, for example `Loading Material Loading Material '/world/...'`. Patterns must be matched as substrings, never as whole lines.
- One `Engine start up...` header per file, so the game probably starts a new log on each launch. The notifier handles a file that is truncated, replaced or simply appended to.

## A match in the log

| Moment | Text, matched as a substring |
|---|---|
| Match found | `Disconnect - CMatchmakingClient::HandleFoundMatch` |
| Map | `Client loading world "midwars"` |
| Leaves the menu | `Client phase MainMenu -> Loading, 0ms since leaving the menu` |
| Hero pick opens | `Client phase Loading -> Lobby after 2324ms, ...` |
| Game starts | `Client phase Lobby -> InGame after 144368ms, ...` |
| Back in the menu | `Client phase InGame -> MainMenu after ...` |

About 320 KB of loading output follows the match-found line within a few seconds.

How the notifier uses these lines:

- **Trigger:** `CMatchmakingClient::HandleFoundMatch`. If that line were ever missing, `Client loading world "..."` while in the menu and `Client phase MainMenu -> Loading` also count as a match found.
- **Phases** come from the `Client phase A -> B` lines: `MainMenu`, `Loading`, `Lobby`, `InGame`. They drive the popup's later states: game started, match cancelled, match ended.
- **Text already in a file** when the notifier starts sets the phase but never raises an alert. A file that appears later (a new game session) is read from its start.
- **Everything read in one poll** (every 250 ms) is applied first and judged once on the phase it ends in, so a notifier that fell behind never raises a stale alert.

Not in the log: joining or leaving the queue. See [in-queue-research.md](in-queue-research.md).

## Timing

The match-found line reaches the file while the match is popping, not later. In a live test, a PowerShell tail of the file (the one-liner in the README) reported the line about two seconds before the game opened its voice-server connection, which it does as hero pick opens; in the logged matches, hero pick opened 1.4 to 2.3 seconds after the match-found line.

## Checking a log yourself

- `start /wait HonQueueNotifier.exe --replay <console.log>` prints every event and the alert decisions the notifier would have made, without a popup.
- `start /wait HonQueueNotifier.exe --watch` prints the same live while you play.
- The notifier's own log, `%APPDATA%\HonQueueNotifier\HonQueueNotifier.log`, records which `console.log` it follows and every match it saw, with the focus decision, for example `MATCH FOUND: map=midwars, ..., game focused=False, foreground=chrome (pid 1234)`.
