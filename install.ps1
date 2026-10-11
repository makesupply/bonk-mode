#Requires -Version 5.1
# Adds Bonk Mode to the Start Menu and starts the hammer. Run once:
#   powershell -ExecutionPolicy Bypass -File .\install.ps1
# Also adds "Bonk Mode - BONK NOW" and "Bonk Mode - Parole" shortcuts, which make
# perfect Stream Deck buttons (System > Open, pick the shortcut).
$ErrorActionPreference = 'Stop'

$ps     = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$script = Join-Path $PSScriptRoot 'bonk-mode.ps1'
$menu   = [Environment]::GetFolderPath('Programs')
$shell  = New-Object -ComObject WScript.Shell

function New-BonkShortcut([string]$Name, [string]$Switch, [string]$Description) {
    $path = Join-Path $menu ($Name + '.lnk')
    $s = $shell.CreateShortcut($path)
    $s.TargetPath       = $ps
    $s.Arguments        = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -STA -File "{0}" {1}' -f $script, $Switch
    $s.WorkingDirectory = $PSScriptRoot
    $s.WindowStyle      = 7
    $s.Description      = $Description
    $s.Save()
    $path
}

New-BonkShortcut 'Bonk Mode' '-Open' 'Bonk Mode - send background apps to RAM jail'
New-BonkShortcut 'Bonk Mode - BONK NOW' '-Bonk' 'Bonk every checked app right now'
New-BonkShortcut 'Bonk Mode - Parole' '-Parole' 'Bring the bonked apps back'

Start-Process $ps -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -STA -File "{0}" -Open' -f $script) -WindowStyle Hidden
Write-Host 'Installed. The hammer is in your tray (check the ^ overflow).'
Write-Host 'Hotkeys: Ctrl+Alt+B bonk, Ctrl+Alt+P parole, Ctrl+Alt+K open. Bonk responsibly.'
