#Requires -Version 5.1
# Adds "Bonk Mode" to the Start Menu and starts the hammer. Run once:
#   powershell -ExecutionPolicy Bypass -File .\install.ps1
$ErrorActionPreference = 'Stop'

$ps     = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$script = Join-Path $PSScriptRoot 'bonk-mode.ps1'
$argLine   = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -STA -File "{0}" -Open' -f $script
$lnk    = Join-Path ([Environment]::GetFolderPath('Programs')) 'Bonk Mode.lnk'

$shell = New-Object -ComObject WScript.Shell
$s = $shell.CreateShortcut($lnk)
$s.TargetPath       = $ps
$s.Arguments        = $argLine
$s.WorkingDirectory = $PSScriptRoot
$s.WindowStyle      = 7
$s.Description      = 'Bonk Mode - send background apps to RAM jail'
$s.Save()

Start-Process $ps -ArgumentList $argLine -WindowStyle Hidden
Write-Host "Installed: $lnk"
Write-Host 'The hammer is in your tray (check the ^ overflow). Bonk responsibly.'
