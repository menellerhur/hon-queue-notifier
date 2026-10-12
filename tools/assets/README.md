# Rebuilding the assets

The files in `assets/` are compiled into the exe. These scripts rebuild them from their originals, so they can be changed later (a different sound length, another font weight). Python 3 only; nothing here ships with the program.

```
python -m venv .venv
.venv\Scripts\pip install -r tools\assets\requirements.txt
```

## The built-in sound: `assets/match-found.wav`

```
.venv\Scripts\python tools\assets\make_sound.py <original .mp3> assets\match-found.wav
```

The original is "Successful accepted reward" by tithuh on Pixabay (item 523721): <https://pixabay.com/sound-effects/film-special-effects-successful-accepted-reward-523721/>. It is not in this repository, because the Pixabay licence does not allow distributing it on its own; download it yourself. The numbers that shape the ending (length 2.2 s; from 1.1 s an extra loss of 18.5 / 36 / 66.5 dB below 500 Hz / between 500 and 2000 Hz / above 2000 Hz, most of it in the first few tenths of a second) are at the top of the script. With the same MP3 and those numbers the output is identical, byte for byte, to the file in `assets/`.

## The popup font: `assets/popup-font.ttf`

```
.venv\Scripts\python tools\assets\make_font.py "<Cinzel[wght].ttf>" assets\popup-font.ttf
```

`Cinzel[wght].ttf` comes from [google/fonts](https://github.com/google/fonts/tree/main/ofl/cinzel). The script makes a static weight-700 instance, merges overlapping contours (otherwise the popup draws holes in 4 and 8) and keeps only printable ASCII. The result has the same glyphs, outlines and widths as the file in `assets/`.

## The icon: `assets/app-icon.svg`

```
powershell -NoProfile -STA -ExecutionPolicy Bypass -File tools\assets\make_icon.ps1 tools\assets\openmoji-phoenix.svg assets\app-icon.svg
```

Windows PowerShell, no Python: the script uses WPF (part of .NET Framework 4.8) for the geometry. The original is OpenMoji's phoenix emoji (U+1F426 U+200D U+1F525, by Vanessa Stoessinger, CC BY-SA 4.0), kept unchanged as `tools/assets/openmoji-phoenix.svg` (from [the OpenMoji repository](https://github.com/hfg-gmuend/openmoji), `color/svg/1F426-200D-1F525.svg`). The script draws it in one colour, cuts the emoji's outlines out (3 units wide instead of 2, so the head and neck still show at 16 pixels), leaves a thin gap where one coloured part lies over another, crops the empty margin and writes one path on a 256 x 256 box using only M, L, C and Z, which is all the program's reader understands. The result is under CC BY-SA 4.0 like the original ([docs/licenses/openmoji-phoenix.txt](../../docs/licenses/openmoji-phoenix.txt)).

## The exe file's icon: `assets/app-icon.ico`

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\assets\make_exe_icon.ps1 src\HonQueueNotifier\bin\Release\net48\HonQueueNotifier.exe assets\app-icon.ico
```

The icon Explorer shows for `HonQueueNotifier.exe`, put into the exe by the build (`ApplicationIcon` in the `.csproj`). It is not drawn separately: the script loads a built exe and saves the program's own drawing of `assets/app-icon.svg` (`AppIcon.FileIcon`), the orange window icon at 16 to 64 pixels plus 256 pixels as PNG for Explorer's large views. So after changing `app-icon.svg`: build, run this script, build again. The self-test fails while the exe's icon differs from the drawing.

After changing an asset, run `build.cmd`: the self-test checks that the sound is a WAV Windows can decode, that the font loads without overlapping outlines, and that the icon draws as one phoenix filling its square, its wings and neck apart at tray sizes.
