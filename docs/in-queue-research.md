# Knowing that you are in the queue

**Closed.** The notifier reports only that a match was found, and it reads only the game's log files. This page records what was learned about detecting the queue itself (for a reminder such as "in queue for 12:34"), in case the question comes up again. Nothing here is planned.

## Ruled out

**The log.** `console.log` has no line for joining or leaving the queue. The only trace is the in-queue button art being loaded (`/ui/hd_ui/elements/effects/playbutton/inqueue.effect`, `inqueue_button_up.tga`), and only the first time in a session; a second queue in the same session leaves nothing identifiable.

**The game's network connections.** If matchmaking held its own connection while queueing, Windows' connection table would show it without touching the game. It does not. Polling the table every 2 seconds through a whole queue and match showed:

- one encrypted connection on port 443, already open before the queue and still open after the game, with nothing opening or closing at the moment the match was found;
- a voice-server connection on port 7335, opened as hero pick opens and closed when the match ended;
- short-lived web connections in between.

Game traffic is apparently UDP and never appears in the TCP table. The queue leaves no mark there.

## What the game client knows

A public community localisation of the game ships a modified copy of the stock matchmaking UI script, `ui/scripts/fe3/matchmaking.lua`, from game version 0.12.8. It shows that the engine hands the UI layer exactly the events a queue timer needs. The names have not been checked against 0.13.

- Engine triggers the script watches with `interface:RegisterWatch(name, fn)`: `MatchmakingJoinQueue`, `MatchmakingLeaveQueue`, `MatchmakingFoundMatch` (with the match id), `MatchmakingQueueTime` (the timer text on the in-queue button), `MatchmakingConnected`, `MatchmakingDisconnected`, `MatchmakingUsersInQueue`.
- The UI state is `Matchmaking.CurrentState`: `Offline`, `NotInGroup`, `InGroup` or `InQueue`.
- The Play button calls `MatchmakingClient.EnterMatchmakingQueue(region, mode)`; leaving calls `MatchmakingClient.LeaveMatchmakingQueue()`.
- UI scripts print to the log with `println(...)`. The stock `regions.lua` does so: the real log contains `UI: Region is international`.
- Scripts are loaded by `<lua file="..."/>` elements in `.interface` and `.package` files; `matchmaking.lua` is loaded from `ui/fe3/main.interface`.

Community mods add scripts of their own without touching the game's files: a build step copies one stock package from the installed archive, inserts the `<lua file="..."/>` lines, and writes the patched copy into an override archive, `%LOCALAPPDATA%\Juvio\extensions\resources0.jz` (a zip using Zstandard), which the game loads when launched with `-mod "heroes of newerth;extensions"`. The launcher demands an update if it finds extra files in the game folder itself, which is why mods stay out of it.

## Options that were considered

| Option | What it takes | Default launch | Contact with the game | Upkeep |
|---|---|---|---|---|
| A. A UI script prints markers to the log | About ten lines of Lua and a one-line include in an override archive | No, needs the `-mod` launch | An extra archive the game loads | Low, if the override is generated from the installed files |
| B. Read the game's memory | Find the matchmaking state and poll it | Yes | Opens the game process | High: offsets move with patches |
| C. A config switch | A console command or setting in a profile `.cfg` that logs queue changes, if one exists | Yes | A text file in Documents | None |
| D. Ask the developers | One log line on queue join and leave | Yes | None | None |
| E. Manual toggle | A hotkey or tray click meaning "I am queueing" | Yes | None | None, but easy to forget |

C and D cost least. A reuses what is already there: the events exist, printing to the log works, and the notifier already reads the log. B is the only way to give a queue timer to players on a default launch without the developers' help, but a read handle on the game process is exactly what anti-cheat software looks for, and every user of a shared tool would carry that risk. The game's [code of conduct](https://app.juvio.com/faqs/HON/code-of-conduct) lists "third-party software, scripts, macros, or unauthorized modifications" as cheating without saying more, so A and B would both need the developers' blessing first.

## Open questions, should A ever be tried

1. Do the trigger names still exist in the installed version? Extract the UI text files (`ui/**/*.lua`, `*.interface`, `*.package`) from the archive and search for `MatchmakingJoinQueue`. Windows 11's `tar` or 7-Zip 24.01 and later read Zstandard zips; Python needs 3.14.
2. Which stock file is the safest place for the include? `ui/fe3/main.interface` is where `matchmaking.lua` itself is loaded.
3. How quickly does a single `println` reach the file while the game idles in the menu? The log is written in blocks; at a match the loading output pushes everything out, but a lone marker may wait in the buffer.
4. If the notifier starts while you are already queueing, how does it learn when the queue began? A periodic marker carrying the `MatchmakingQueueTime` text would answer it.
5. Does the launcher accept the override archive, as it does for the community mods?
