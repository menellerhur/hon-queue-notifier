# HoN Queue Notifier

Windows tray app that alerts when Heroes of Newerth: Reborn finds a match while the game is not the focused window. It works by tailing the game's own `console.log`. Juvio is the game's launcher and platform (like Steam), not an engine; the game's own process is nonetheless `juvio.exe`.

The aim is the best possible solution, and one that works for every player on a default install.

## Read before changing anything

- `docs/detection-notes.md`: where the game log is, what it contains, how the notifier reads it.
- `docs/in-queue-research.md`: research on knowing you are in queue. Out of scope (see Decisions).
- `README.md`: user-facing behaviour. Keep it in step with the code, its images too: after a visible change to the popup or the settings dialog, regenerate `docs/images/` with `tools/screenshots/` (commands at the top of `render.ps1`).

## Hard constraints

1. **.NET Framework 4.8.** Both projects target `net48`, which is built into every Windows 10 and 11, so players need nothing but the exe (about 0.2 MB). Do not move to modern .NET (8, 10, ...): players would have to install a runtime, or the exe would grow to 60-150 MB. Current C# is fine (`LangVersion latest`), as long as it compiles for `net48`.
2. **Assets are files.** Binary assets (the popup font, the built-in sound) and the icon (an SVG) live in `assets/` and are compiled into the exe as `EmbeddedResource` items in the `.csproj`, read with `AppFiles.Resource`. The exe file's icon, `assets/app-icon.ico`, is generated from the program's own drawing of the SVG and goes in through `ApplicationIcon`. Never paste binary data into the source as base64.
3. **CRLF** line endings for `.cs` and `.cmd`. `.gitattributes` enforces it. A batch file with LF endings misbehaves.
4. **Nothing extra ships.** At run time only .NET Framework 4.8 APIs (`System`, `System.Core`, `System.Drawing`, `System.Windows.Forms`). NuGet packages are allowed only for building and testing (`Microsoft.NETFramework.ReferenceAssemblies`, xUnit, the test SDK), never as something the exe needs.
5. **The core never opens the game process.** No `OpenProcess`, no `Process.MainModule`, no `Process.StartTime` or `HasExited` on the game. `Process.GetProcessesByName`, `.Id` and `.ProcessName` are fine because they read the system process list. Window calls on the game window are limited to: is it foreground, flash, restore and bring to front on a user click, and minimize it when the user presses the minimize hotkey (opt-in, see Decisions).
6. **No input is ever sent to the game.** No auto-accept, no key or mouse simulation. The minimize hotkey is registered with Windows (`RegisterHotKey`) only while the game has focus; Windows hands the key press to this program, nothing is injected and no keyboard hook is used.
7. **Nothing meant to hide the tool** from the game or from anti-cheat software.
8. **Works on a default launch.** Never hard-code a `Documents\Juvio\extensions` path. The log folder depends on the launch arguments.
9. **Never commit** real `console.log` files (other players' names and chat) or a settings file (`HonQueueNotifier.ini` can hold a push topic; the app keeps it in `%APPDATA%\HonQueueNotifier`, outside the repo). Neither belongs in the repo folder, so `.gitignore` does not list them: it holds only what a build, the tools or an editor create there.
10. **Log files only.** No HoN mod and no memory reading, not even as an opt-in layer.
11. **No personal information in the repo.** No real names, usernames, or paths unique to one machine (home folder, checkout folder) in any tracked file, commit message or branch name. Write paths as `%USERPROFILE%\Documents\Juvio\...`, `%LOCALAPPDATA%\Juvio\...`, or relative to `Documents\Juvio`.

## Build, run, test

Layout: `HonQueueNotifier.sln`; the app in `src/HonQueueNotifier/` (`HonQueueNotifier.csproj`); the tests in `tests/HonQueueNotifier.Tests/` (xUnit); binary assets and the icon in `assets/`, and the scripts that rebuild them from their originals in `tools/assets/` (Python; the icon's is PowerShell) (see its README; development only, nothing from them ships); the README's images in `docs/images/`, rendered from the built exe by `tools/screenshots/`; `tools/simulate/simulate-match.ps1`, which plays a scripted match (cancelled, ended, or found while the game is focused) into a stand-in `console.log` under `Documents\Juvio` for the running app; CI and releases in `.github/workflows/build.yml`. Building needs the .NET SDK; Visual Studio and VS Code open the solution directly.

- Build and test: run `build.cmd`. It closes any running `HonQueueNotifier.exe` (by name, also a copy in another folder), runs `dotnet build -c Release`, copies `HonQueueNotifier.exe` into the repository folder, then runs `dotnet test`. Or run `dotnet build` and `dotnet test` yourself.
- Builds are incremental: a file restored from a copy keeps its old timestamp and is not recompiled; touch it, or build with `--no-incremental`.
- The same checks run inside the app: `HonQueueNotifier.exe --selftest [--out <file>]` writes a report ending with `ALL nn CHECKS PASSED`, useful on a player's PC.
- Tray app with an immediate test alert: `HonQueueNotifier.exe --test`
- Replay a saved game log: `start /wait HonQueueNotifier.exe --replay <path> [--chunk <bytes>] [--focused]`
- Live console mode, no popup: `start /wait HonQueueNotifier.exe --watch`
- The app writes its own diagnostics to `HonQueueNotifier.log` in `%APPDATA%\HonQueueNotifier`. Read it first when something did not fire: it records every state change and the focus decision, for example `MATCH FOUND: map=midwars, ..., game focused=False, foreground=chrome (pid 1234)`.

Rules for changes:

- Every behaviour change gets a check in the same commit. Checks live in `src/HonQueueNotifier/Diagnostics/SelfTest.cs`, grouped in sections; each section is one test in `dotnet test` (`tests/HonQueueNotifier.Tests/SelfTestSections.cs`). A new section goes into `SelfTest.Sections`. The decision logic is testable without a UI through `FakeProbe` and `RecordingSink`.
- After changing a check, break the code on purpose once to see the check fail. A check that cannot fail is not a check.
- Do not claim something works on Windows until it has been seen working on Windows. Say what was run and what was not.

## Code map (`src/HonQueueNotifier/`)

| File | Types | Role |
|---|---|---|
| `Program.cs` | `Program` | Entry point and command line; `Version` ("1.0.0", from `<Version>`), shown in the tray tooltip and the settings dialog; `RepoUrl`, linked from the settings dialog. |
| `AppLog.cs` | `AppLog` | The app's own log file. |
| `AppIcon.cs` | `AppIcon` | The program's icon: the phoenix glyph from `assets/app-icon.svg` (`Glyph`, `ParsePath`: M, L, C, Z only), drawn at the exact size (`Draw`, pure, testable); in the tray white or black to suit the taskbar (`Tray`, `LightTaskbar`); for windows the same in orange at every size (`Window`, a multi-image icon); the exe file's icon (`FileIcon`, the same plus 256 px as PNG), saved as `assets/app-icon.ico` by `tools/assets/make_exe_icon.ps1` and put into the exe by `ApplicationIcon`. |
| `AppFiles.cs` | `AppFiles` | Where settings and log live (always `%APPDATA%\HonQueueNotifier`); reads embedded resources; `Open` (a file or link in its default program). |
| `Settings.cs` | `Settings`, `SettingProblem`, `FileStamp` | Defaults, tolerant INI parsing (unknown keys and bad values are reported in the app log), the commented INI template (`ToIni`), `Save`, `Validate`; noticing hand edits of the file. Stored as `HonQueueNotifier.ini`. |
| `Log/LogScanner.cs` | `LogScanner`, `LogEvent` | Appended log text to events. Substring matching with carry-over for text cut mid-line. |
| `Log/MatchTracker.cs` | `MatchTracker`, `GamePhase` | State machine per log file: Menu, MatchFound, Lobby, InGame. Counts matches, with fallback triggers. |
| `Log/LogTailer.cs` | `LogTailer`, `LogText` | Follows one file: open, read what was appended, close. Detects truncation and replacement. `LogText` picks the decoder from the first bytes (the game writes UTF-16LE with a byte order mark). |
| `Log/LogWatcher.cs` | `LogWatcher` | Finds every `console.log` up to two folders below `Documents\Juvio`. |
| `Alerts/AlertEngine.cs` | `AlertEngine`, `Texts`, `IEnvironmentProbe`, `IAlertSink` | Decides what to do and when: focus, idle time, the one (possibly delayed) push per match, sound repeats, the game start (changes the open alert in place), the "over" notice. Pure logic. |
| `Alerts/Core.cs` | `Core` | Glue: watcher plus engine, one `Step` per timer tick (`PollMilliseconds`, 250 ms, fixed). |
| `Alerts/Pusher.cs` | `Pusher`, `PushRequest` | ntfy requests, retry policy. |
| `Alerts/Chime.cs` | `Chime` | Plays the built-in sound (`assets/match-found.wav`, IMA ADPCM) or the user's `.wav` with the Windows `PlaySound` function. Not .NET's `SoundPlayer`: it refuses every WAV that is not plain PCM. |
| `Windows/Native.cs`, `Windows/Win32Probe.cs` | `Native`, `Win32Probe` | P/Invoke and everything Windows-specific about the game window (focus, flash, bring forward, `MinimizeGame`); the game's process name (`GameProcess`, `juvio`, fixed). |
| `Windows/GameHotkey.cs` | `HotkeyText`, `HotkeyState`, `HotkeyWindow` | The minimize hotkey: parsing "Alt+Q", when to register (on, valid, game focused), and a message-only window that receives `WM_HOTKEY`. `TrayApp` registers and releases it each tick and logs both. |
| `Popup/ForgeFont.cs` | `ForgeFont` | The popup's font for the title and the timer (the rest is Segoe UI): `assets/popup-font.ttf` (OFL; overlaps merged, cut to ASCII), loaded with `PrivateFontCollection`, Georgia if that fails. Licence in `docs/licenses/Cinzel-OFL.txt`. |
| `Popup/ForgePainter.cs` | `ForgePainter` | Draws the popup ("Forge") with GDI+ into a premultiplied ARGB bitmap: cached base and glow layers (glow and shadow fade out along a Gaussian curve, `HaloProfile`); the clock is a path with a fixed-width cell per digit; small text in ClearType, spaced word by word (`DrawWords`). Texts (`Lines`), the entry curves (`EntryOpacity`, `EntryOffset`, `EntryBrightness`) and the fire's flicker (`Flame`, value noise joined by Catmull-Rom curves, each ember at its own speed and phase so no two flames turn together; `Heat`, the heat along the outline) are pure functions; `UpdateFire` fills the glow layer pixel by pixel from a precomputed ring (each pixel's distance from the frame and its place on the outline). Testable without a window. |
| `Popup/LayeredSurface.cs` | `LayeredSurface` | A DIB section wrapped in a `Bitmap`, handed to `UpdateLayeredWindow` for per-pixel alpha. |
| `Popup/AlertForm.cs` | `AlertForm` | The popup window: layered, topmost, tool window, shown without taking focus. Fades and slides in over 0.52 s with a short flash. One timer: about 60 frames a second during the entry, 30 while a waiting alert's fire flickers, 4 for a notice (only its clock changes), stopped while hidden. |
| `SettingsForm.cs` | `SettingsForm`, `DialogLabel`, `SheetTabs` | The settings dialog. Standard WinForms controls, `AutoScaleMode.Dpi`, table layouts with fixed spacing (`TextIndent`, `OptionGap`, `SectionGap`), white pages; it only binds controls to `Settings` and shows every setting. Each option's dependent part is greyed out as a whole (`UpdateEnabled`). `DialogLabel` draws its text without WinForms' padding (one left edge), greys out like a check box, and can carry one link (`SetLink`; the ntfy.sh step, which a `LinkLabel` drew in a weight and spacing of its own); `SheetTabs` lets Windows paint the tabs, then swaps its pale grey in the selected tab and around the page for white. |
| `TrayApp.cs` | `TrayApp` | Tray icon (redrawn when the taskbar turns light or dark) and menu (`BuildMenu`), timer, the settings dialog, reloading the settings file after hand edits; implements `IAlertSink`. |
| `Diagnostics/ConsoleModes.cs` | `ConsoleModes` | `--replay` and `--watch`. |
| `Diagnostics/SelfTest.cs`, `Diagnostics/Fakes.cs` | `SelfTest`, `FakeProbe`, `RecordingSink` | All checks, by section; fakes for the engine. |

## How detection works

- Trigger: the substring `CMatchmakingClient::HandleFoundMatch` in newly appended log text.
- Fallback triggers if that line is ever missing: `Client loading world "..."` while in the menu, and `Client phase MainMenu -> Loading`.
- Phases come from `Client phase A -> B` lines (`MainMenu`, `Loading`, `Lobby`, `InGame`).
- The log is **UTF-16 little-endian with a byte order mark** (`FF FE`). Never read it as single bytes: that misses every real match. A block write can also split a two-byte character, which is why each `LogTailer` keeps one `Decoder`. Byte-level `grep` on the real file finds nothing; decode it first.
- The log is written in buffered blocks, can end mid-line, and during loading several threads interleave their output. Never match whole lines.
- Text already in the log when the app starts sets the state but never raises an alert.
- Everything read in one poll is applied first and judged once on the state it ends in, so falling behind cannot produce a stale alert.

## Pitfalls

- The settings dialog's checks need visual styles on and `SetCompatibleTextRenderingDefault(false)`, as in the app; without them WinForms draws different (GDI+) text. The self-test and `tools/screenshots/` both set them.
- On Windows, setting `NumericUpDown.Text` from code commits the value at once. A check that simulates typing must type into the box's inner edit field.
- GDI+ fills overlapping glyph contours as holes under the alternate fill rule. The popup font has its overlaps merged and text paths use the winding rule; keep both.
- Window checks must look only at windows of their own process: searching by name also finds a running copy of the notifier.

## Decisions

- Scope: only a match being found matters. Being in the queue, and a queue timer, are out of scope.
- Phone push: ntfy is the only phone notification system. No Telegram, Discord, Pushover or other services.
- Free only: never a service that requires payment at any point. No own Android or iOS app; use existing apps.
- No own website: mobile notifications go through ntfy and its existing apps. No self-hosted site, web push page or PWA.
- One way for every phone: phone setup works the same on iOS and Android. No platform-specific features such as an Android-only QR deep link.
- Files: settings (`HonQueueNotifier.ini`), the app's log and `--selftest`'s report always live in `%APPDATA%\HonQueueNotifier`, never next to the exe. The settings dialog's General tab shows the folder with "Open folder" and "Open log"; the tray keeps "Open this program's log".
- Version: the tooltip reads "HoN Queue Notifier 1.0.0", and the General tab ends with an "About" section showing "Version: 1.0.0" and "Source code:" with a link to the repository; not in the tray menu.
- Display scaling: not handled per monitor. A popup on a monitor with another scale than the main one may look blurry; that is accepted.
- Tray: no status line in the menu and no status in the tooltip. The last phase found in the log stays "in the menu" after the game is closed, so it would mislead.
- Tray icon: a phoenix with raised wings, adapted from OpenMoji's phoenix emoji (by Vanessa Stoessinger, CC BY-SA 4.0; credited in the README and `docs/licenses/openmoji-phoenix.txt`; the adapted icon is CC BY-SA 4.0 too): one colour, the emoji's outlines cut out so the wings and the S-shaped neck stay apart at 16 px, margin cropped; made by `tools/assets/make_icon.ps1` into `assets/app-icon.svg`. White on a dark taskbar and black on a light one, following a theme change at once; it does not change while an alert is up. Windows show it in orange.
- Minimize hotkey: a key combination that minimizes the game window, for fullscreen, where the game cannot be minimized otherwise. Default Alt+Q (Alt+Tab itself cannot be registered by a program), off by default, only registered while the game has focus so the keys work normally everywhere else. Settings, General tab, with a key-capture box.
- The popup goes on the main display only by default (`Screens=Primary`), and its border is the same width on all four sides.
- The popup never disappears by itself. It leaves on a click or when the game gets focus. A test alert follows the same rule.
- Grey notices: when a match is cancelled or ends, or the game is closed, while the alert is still up, the popup turns into a grey notice ("MATCH CANCELLED", "MATCH ENDED", "GAME WAS CLOSED") instead of vanishing, so a missed game is visible on return.
- Game start: if the game starts while the alert is still up, the same popup turns ember red ("GAME HAS STARTED", its clock now counting from the start of the game). Nothing new happens: no sound, no entry animation, no flash, no push; the sound repeats keep their original window, and a push still pending goes out once, saying the game has started. With no alert up (it was clicked away, or the game was focused when the match was found), the game starting does nothing. Not an option. A match first seen with its game already running (the app fell behind) still gets one full alert.
- Popup look, "Forge": dark stone, bronze trim with corner diamonds, a gold serif title (Cinzel, SIL Open Font License, embedded), an entry that fades and slides in over 0.52 s with a short flash, and a glow that burns like a fire while an alert waits. The glow is one continuous halo around the frame whose heat varies along the edge: a low steady base plus 14 flames as wide, overlapping bumps in that heat, each flickering on its own irregular, never-repeating curve and drifting a little along the edge, stronger along the top; hotter places reach a little further and turn from orange towards yellow. No separate spots, no hard edges, and no shared beat between flames. Ember red once the game has started, pewter grey for notices.
- Sound: on by default. The built-in sound is "Successful accepted reward" by tithuh (Pixabay Content License; `docs/licenses/match-found-sound.txt`), shortened by `tools/assets/make_sound.py`; the user can choose their own `.wav` instead. Never ship the sound as a loose file: the licence forbids distributing it on its own. There is one built-in sound; do not add more.
- Settings dialog: standard Windows look, changes apply at once, hand edits of the file are picked up automatically. First run shows a tray balloon, not the dialog. Apply is greyed out while nothing has been changed.
- All settings in the dialog: every setting in the file is also in the dialog; nobody should have to edit the file. A value not worth showing there is a constant in the code, never a setting only the file has, and there is no hidden "Advanced" part for such values. So the poll interval (250 ms) and the game's process name (`juvio`) are fixed, the popup has no timeout, and a chosen game log is one file. A check fails when a setting is in the file but not in the dialog.
- Settings dialog layout: white tab pages inside a 10 px margin, and the selected tab white too, so tab and page are one sheet (`SheetTabs`); section titles a step larger and semibold, with space above them; each note one short grey line right under its option, lined up with the option's text; more space between options than inside one; options that depend on a check box indented under its text. Headings, labels, check boxes and radio buttons share one left edge. The title bar has the dialog's own colour on Windows 11, unless title bars are set to show the accent colour. No tooltips or info icons: text a user needs stays visible, the rest is cut.
- Numbers in the dialog: an option with several numbers shows them one per line, label, box, unit, with the boxes in one column (`NumberRows`); no number box inside a sentence that wraps onto a second line.
- Greying out: the same rule on every tab. While an option is off, everything that belongs to it is greyed out, its texts included, in the grey Windows uses for a disabled check box; a choice among others keeps its words (it can still be picked), and only its fields are greyed out while another choice is picked. A greyed-out part is not checked for problems. Windows draws a greyed-out check box's or radio button's text a little darker (#555555) than the dialog's greyed-out labels, headings and notes (#6D6D6D); both stay as they are.
- Phone tab: no access token, no Copy and no Generate button (players type a topic they can also type on the phone; the note asks for a name only they would use, since anyone with the same topic gets the alerts). Order: topic, setup steps with the test button, when to send, Advanced (priority, server). Step 3's "Send a test notification" button starts where the words of steps 1 and 2 do, right after "3. ". One push per match.
