# Renders the README's images from a built exe: the popup in its three states and the four tabs of
# the settings dialog, at 100 %, without showing anything on screen or taking focus.
#
#   powershell -NoProfile -STA -ExecutionPolicy Bypass -File tools\screenshots\render.ps1
#   python tools\screenshots\make_apng.py docs\images\settings.png docs\images\settings-general.png docs\images\settings-popup.png docs\images\settings-sound.png docs\images\settings-phone.png
#
# Windows PowerShell 5.1 (it runs on .NET Framework 4.8, like the program). Development only.
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\..\HonQueueNotifier.exe'),
    [string]$OutDir = (Join-Path $PSScriptRoot '..\..\docs\images')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class ShotNative {
    public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, int flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
}
'@
[ShotNative]::SetProcessDPIAware() | Out-Null
# as Program.Main does: without the second call WinForms draws text with GDI+, lighter and spaced differently
[System.Windows.Forms.Application]::EnableVisualStyles()
[System.Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)
$asm = [Reflection.Assembly]::LoadFrom((Resolve-Path $Exe))
$all = [Reflection.BindingFlags]'Public,NonPublic,Instance,Static'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

# ---- the popup: drawn by ForgePainter into a transparent bitmap, as the layered window shows it ----
$painterType = $asm.GetType('HonQueueNotifier.ForgePainter')
$stageType = $asm.GetType('HonQueueNotifier.AlertStage')
$noticeType = $asm.GetType('HonQueueNotifier.NoticeReason')
$states = @(
    @{ File = 'popup-found';     Title = 'MATCH FOUND';      Stage = 'Found';   Notice = 'None';      Seconds = 47 },
    @{ File = 'popup-started';   Title = 'GAME HAS STARTED'; Stage = 'Started'; Notice = 'None';      Seconds = 83 },
    @{ File = 'popup-cancelled'; Title = 'MATCH CANCELLED';  Stage = 'Over';    Notice = 'Cancelled'; Seconds = 95 }
)
foreach ($s in $states) {
    $painter = [Activator]::CreateInstance($painterType, $all, $null, @([single]1, [int]580), $null)
    $painterType.GetField('Title').SetValue($painter, $s.Title)
    $painterType.GetField('Stage').SetValue($painter, [Enum]::Parse($stageType, $s.Stage))
    $painterType.GetField('Notice').SetValue($painter, [Enum]::Parse($noticeType, $s.Notice))
    $painterType.GetField('Map').SetValue($painter, 'Mid Wars')
    $size = $painterType.GetField('Size').GetValue($painter)
    $bmp = New-Object System.Drawing.Bitmap $size.Width, $size.Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    # 5 s after it appeared: the entry is over
    $painterType.GetMethod('Draw').Invoke($painter, @($g, [double]5000, [TimeSpan]::FromSeconds($s.Seconds))) | Out-Null
    $g.Dispose()
    $bmp.Save((Join-Path $OutDir "$($s.File).png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $painter.Dispose()
}

# ---- the settings dialog: default settings, shown far off-screen without activation ----
$settings = [Activator]::CreateInstance($asm.GetType('HonQueueNotifier.Settings'), $true)
$icon = $asm.GetType('HonQueueNotifier.AppIcon').GetMethod('Window', $all).Invoke($null, @())
$folder = 'C:\Users\Player\AppData\Roaming\HonQueueNotifier'
$ctor = $asm.GetType('HonQueueNotifier.SettingsForm').GetConstructors($all) | Where-Object { $_.GetParameters().Count -eq 6 }
$form = $ctor.Invoke(@($settings, $icon, $null, $null, "$folder\HonQueueNotifier.ini", "$folder\HonQueueNotifier.log"))
$form.StartPosition = 'Manual'
$form.Location = New-Object System.Drawing.Point -20000, -20000
$hwnd = $form.Handle
[ShotNative]::ShowWindow($hwnd, 4) | Out-Null   # SW_SHOWNOACTIVATE
# draw the title bar as active, as the dialog looks when opened, without taking focus (WM_NCACTIVATE)
[ShotNative]::SendMessage($hwnd, 0x86, [IntPtr]1, [IntPtr]::Zero) | Out-Null
[System.Windows.Forms.Application]::DoEvents()

function Find-Tabs($parent) {
    foreach ($c in $parent.Controls) {
        if ($c -is [System.Windows.Forms.TabControl]) { return $c }
        $found = Find-Tabs $c
        if ($found) { return $found }
    }
}
$tabs = Find-Tabs $form
$names = 'general', 'popup', 'sound', 'phone'
for ($i = 0; $i -lt $names.Count; $i++) {
    $tabs.SelectedIndex = $i
    1..5 | ForEach-Object { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 60 }

    # the whole window, title bar included
    $win = New-Object ShotNative+RECT
    [ShotNative]::GetWindowRect($hwnd, [ref]$win) | Out-Null
    $shot = New-Object System.Drawing.Bitmap ($win.R - $win.L), ($win.B - $win.T), ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($shot)
    $hdc = $g.GetHdc()
    [ShotNative]::PrintWindow($hwnd, $hdc, 2) | Out-Null   # PW_RENDERFULLCONTENT
    $g.ReleaseHdc($hdc)
    $g.Dispose()

    # cut away the invisible resize borders (DWMWA_EXTENDED_FRAME_BOUNDS) and give the image the
    # rounded corners and thin border Windows 11 draws, so it stands out on light and dark pages
    $frame = New-Object ShotNative+RECT
    [ShotNative]::DwmGetWindowAttribute($hwnd, 9, [ref]$frame, 16) | Out-Null
    $w = $frame.R - $frame.L
    $h = $frame.B - $frame.T
    $image = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($image)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'Half'
    $outline = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 16
    $outline.AddArc(0, 0, $d, $d, 180, 90); $outline.AddArc($w - $d, 0, $d, $d, 270, 90)
    $outline.AddArc($w - $d, $h - $d, $d, $d, 0, 90); $outline.AddArc(0, $h - $d, $d, $d, 90, 90)
    $outline.CloseFigure()
    $grey = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(240, 240, 240))
    $g.FillPath($grey, $outline)   # the captured outermost pixels are black; this ring lies under the border
    $texture = New-Object System.Drawing.TextureBrush $shot, (New-Object System.Drawing.Rectangle ($frame.L - $win.L), ($frame.T - $win.T), $w, $h)
    $g.SetClip((New-Object System.Drawing.Rectangle 1, 1, ($w - 2), ($h - 2)))
    $g.FillPath($texture, $outline)
    $g.ResetClip()
    $border = New-Object System.Drawing.Drawing2D.GraphicsPath
    $e = 15
    $border.AddArc(0.5, 0.5, $e, $e, 180, 90); $border.AddArc($w - 0.5 - $e, 0.5, $e, $e, 270, 90)
    $border.AddArc($w - 0.5 - $e, $h - 0.5 - $e, $e, $e, 0, 90); $border.AddArc(0.5, $h - 0.5 - $e, $e, $e, 90, 90)
    $border.CloseFigure()
    $g.PixelOffsetMode = 'None'
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(90, 0, 0, 0)), 1
    $g.DrawPath($pen, $border)
    $g.Dispose(); $texture.Dispose(); $grey.Dispose(); $pen.Dispose(); $shot.Dispose()
    $image.Save((Join-Path $OutDir "settings-$($names[$i]).png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $image.Dispose()
}
$form.Close()
"Images written to $OutDir"
