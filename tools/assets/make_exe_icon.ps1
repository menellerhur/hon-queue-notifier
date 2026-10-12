# Makes assets\app-icon.ico, the icon of the exe file in Explorer, from the program's own drawing of
# assets\app-icon.svg (AppIcon.FileIcon: the orange window icon at every size, plus 256 pixels as PNG).
# It loads a built HonQueueNotifier.exe, so build first; after changing the icon, run this and build again.
# The self-test checks that the exe's icon matches the drawing.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\assets\make_exe_icon.ps1 src\HonQueueNotifier\bin\Release\net48\HonQueueNotifier.exe assets\app-icon.ico

param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Out
)
$ErrorActionPreference = 'Stop'

$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path $Exe).Path)
$type = $assembly.GetType('HonQueueNotifier.AppIcon', $true)
$method = $type.GetMethod('FileIcon', [Reflection.BindingFlags]'Public, NonPublic, Static')
[byte[]]$ico = $method.Invoke($null, @())

$path = [IO.Path]::GetFullPath((Join-Path (Get-Location) $Out))
[IO.File]::WriteAllBytes($path, $ico)
Write-Host "Wrote $Out ($($ico.Length) bytes)"
