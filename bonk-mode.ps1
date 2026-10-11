#Requires -Version 5.1
<#
    Bonk Mode - a tray hammer that sends background apps to RAM jail before gaming.

    Left- or right-click the hammer:
      BONK 'EM ALL        politely closes every checked app that is running, then
                          force-closes anything that ignored the hint (after 4s)
      Release from jail   reopens what the last bonk closed (Un-bonk)
      app checkboxes      what gets bonked; saved to bonk-list.json

    Never touches the protected list below, whatever bonk-list.json says.

    Run with -ListOnly to print the bonk list and what is running, without
    closing anything.
#>
param([switch]$ListOnly, [switch]$SelfTest)

$ErrorActionPreference = 'Stop'

$AppDir     = $PSScriptRoot
$ConfigFile = Join-Path $AppDir 'bonk-list.json'
$ExampleConfig = Join-Path $AppDir 'bonk-list.example.json'
$StateFile  = Join-Path $AppDir 'bonk-state.json'
$LogFile    = Join-Path $AppDir 'bonk.log'
$RunKey     = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunName    = 'Bonk Mode'
$MutexName  = 'Local\Bonk_Mode_Tray'
$PoliteWaitMs = 4000

# Never bonked: Windows core, the launcher you game from, GPU driver, security,
# this script's own host, and Python runtimes (background agents/scripts).
$Protected = @(
    'explorer', 'dwm', 'csrss', 'winlogon', 'lsass', 'services', 'smss', 'wininit',
    'svchost', 'sihost', 'fontdrvhost', 'ctfmon', 'audiodg', 'RuntimeBroker',
    'ShellExperienceHost', 'StartMenuExperienceHost', 'SearchHost', 'TextInputHost',
    'ApplicationFrameHost', 'conhost', 'ShellHost', 'LockApp', 'dllhost', 'taskhostw',
    'steam', 'steamwebhelper', 'steamservice', 'gameoverlayui64', 'gameoverlayui',
    'GameBar', 'GameBarFTServer', 'GameInputRedistService',
    'nvcontainer', 'NVDisplay.Container', 'NVIDIA Overlay', 'NVIDIA app',
    'MsMpEng', 'SecurityHealthSystray', 'MicrosoftSecurityApp', 'DefenderSessionHelper',
    'powershell', 'pwsh', 'python', 'pythonw', 'WindowsTerminal', 'OpenConsole'
)

$Hammer = [char]::ConvertFromUtf32(0x1F528)

function Write-Log([string]$Message) {
    try { Add-Content -LiteralPath $LogFile -Value ('{0}  {1}' -f (Get-Date -Format 's'), $Message) -Encoding UTF8 } catch { }
}

function Read-Config {
    # First run: your personal list starts as a copy of the example.
    if (-not (Test-Path -LiteralPath $ConfigFile)) { Copy-Item -LiteralPath $ExampleConfig -Destination $ConfigFile }
    $raw = Get-Content -LiteralPath $ConfigFile -Raw -Encoding UTF8
    $cfg = $raw | ConvertFrom-Json
    if (-not $cfg.apps) { throw 'bonk-list.json has no "apps" list' }
    $cfg
}

function Save-Config {
    $script:Config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ConfigFile -Encoding UTF8
}

function Read-State {
    if (-not (Test-Path -LiteralPath $StateFile)) { return @() }
    try { @((Get-Content -LiteralPath $StateFile -Raw -Encoding UTF8 | ConvertFrom-Json).jailed) | Where-Object { $_ } } catch { @() }
}

function Save-State($Jailed) {
    if (-not $Jailed -or @($Jailed).Count -eq 0) {
        Remove-Item -LiteralPath $StateFile -ErrorAction SilentlyContinue
        return
    }
    @{ jailed = @($Jailed) } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $StateFile -Encoding UTF8
}

function Get-Snapshot {
    $session = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
    foreach ($p in Get-Process) {
        if ($p.SessionId -ne $session -or $p.Id -eq $PID) { continue }
        if ($Protected -contains $p.ProcessName) { continue }
        $path = $null
        try { $path = $p.Path } catch { }
        [pscustomobject]@{ Proc = $p; Name = $p.ProcessName; Path = $path; WS = $p.WorkingSet64 }
    }
}

function Select-EntryProcs($Entry, $Snapshot) {
    $names = @($Entry.names) | Where-Object { $_ }
    $paths = @($Entry.paths) | Where-Object { $_ } | ForEach-Object { [Environment]::ExpandEnvironmentVariables($_) }
    @($Snapshot | Where-Object {
        $row = $_
        if ($names -contains $row.Name) { return $true }
        if ($row.Path) { foreach ($pat in $paths) { if ($row.Path -like $pat) { return $true } } }
        $false
    })
}

function Get-Rows {
    $snap = @(Get-Snapshot)
    foreach ($e in @($script:Config.apps)) {
        $procs = @(Select-EntryProcs $e $snap)
        $bytes = ($procs | Measure-Object -Property WS -Sum).Sum
        [pscustomobject]@{
            Entry   = $e
            Label   = [string]$e.label
            Enabled = [bool]$e.enabled
            Procs   = $procs
            Running = ($procs.Count -gt 0)
            MB      = if ($bytes) { [math]::Round($bytes / 1MB) } else { 0 }
        }
    }
}

function Format-Size([double]$MB) {
    if ($MB -ge 1024) { '{0:N1} GB' -f ($MB / 1024) } else { '{0:N0} MB' -f $MB }
}

function Resolve-Relaunch($Entry, $Procs) {
    $prop = $Entry.PSObject.Properties['relaunch']
    if ($prop -and $null -eq $prop.Value) { return @() }
    if (-not $prop -or ($prop.Value -is [string] -and $prop.Value -eq 'auto')) {
        $first = @($Entry.names)[0]
        $main = $Procs | Where-Object { $_.Name -eq $first -and $_.Path } | Select-Object -First 1
        if (-not $main) { $main = $Procs | Where-Object { $_.Path } | Select-Object -First 1 }
        if ($main) { return @([pscustomobject]@{ file = $main.Path; args = '' }) }
        return @()
    }
    @($prop.Value) | ForEach-Object {
        [pscustomobject]@{ file = [Environment]::ExpandEnvironmentVariables([string]$_.file); args = [string]$_.args }
    }
}

# ---------------------------------------------------------------------------
# -ListOnly: report and exit, nothing is closed.
# ---------------------------------------------------------------------------

$script:Config = Read-Config

if ($ListOnly) {
    Get-Rows | Sort-Object @{ e = { -not $_.Enabled } }, @{ e = { -not $_.Running } }, @{ e = { -$_.MB } } |
        Select-Object @{ n = 'Bonk'; e = { if ($_.Enabled) { 'YES' } else { '-' } } },
                      Label,
                      @{ n = 'Running'; e = { if ($_.Running) { 'yes' } else { '' } } },
                      @{ n = 'RAM'; e = { if ($_.Running) { Format-Size $_.MB } else { '' } } },
                      @{ n = 'Procs'; e = { $_.Procs.Count } } |
        Format-Table -AutoSize | Out-String -Width 200
    $jailed = Read-State
    if ($jailed) { 'In RAM jail from last bonk: ' + ((@($jailed) | ForEach-Object { $_.label }) -join ', ') }
    return
}

# ---------------------------------------------------------------------------
# Tray app
# ---------------------------------------------------------------------------

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

function New-HammerIcon {
    $bmp = New-Object System.Drawing.Bitmap 32, 32
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 200, 0))), 0, 0, 31, 31)
    $g.TranslateTransform(16, 17)
    $g.RotateTransform(-35)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(120, 72, 30))), -2.5, -3, 5, 15)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(45, 45, 50))), -10, -11, 20, 9)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(110, 110, 120))), -10, -11, 20, 3)
    $g.Dispose()
    [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
}

function Show-Balloon([string]$Title, [string]$Text, [string]$Kind = 'Info') {
    $script:NotifyIcon.ShowBalloonTip(5000, $Title, $Text, [System.Windows.Forms.ToolTipIcon]::$Kind)
}

function Set-Tip([string]$Text) {
    if ($Text.Length -gt 63) { $Text = $Text.Substring(0, 63) }
    $script:NotifyIcon.Text = $Text
}

function Update-Tip {
    try {
        $rows = @(Get-Rows | Where-Object { $_.Enabled -and $_.Running })
        $mb = ($rows | Measure-Object -Property MB -Sum).Sum
        if ($rows.Count) { Set-Tip ('Bonk Mode: {0} apps ({1}) await RAM jail' -f $rows.Count, (Format-Size $mb)) }
        else { Set-Tip 'Bonk Mode: nothing to bonk. RAM is vibing.' }
    } catch { Set-Tip 'Bonk Mode' }
}

function Wait-Ui([int]$Ms) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $Ms) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 100
    }
}

function Invoke-Bonk {
    $targets = @(Get-Rows | Where-Object { $_.Enabled -and $_.Running })
    if (-not $targets.Count) {
        Show-Balloon 'Nothing to bonk' 'Every app on the list is already closed. Your RAM is living its best life.'
        return
    }
    Set-Tip 'Bonk Mode: BONKING...'
    Write-Log ('BONK: ' + (($targets | ForEach-Object { $_.Label }) -join ', '))

    $jailed = [System.Collections.ArrayList]@(Read-State)
    $all = @()
    $mb = 0
    foreach ($t in $targets) {
        $mb += $t.MB
        $relaunch = @(Resolve-Relaunch $t.Entry @($t.Procs))
        if (-not ($jailed | Where-Object { $_.label -eq $t.Label })) {
            [void]$jailed.Add([pscustomobject]@{ label = $t.Label; relaunch = $relaunch })
        }
        foreach ($row in $t.Procs) {
            $all += [pscustomobject]@{ Label = $t.Label; Proc = $row.Proc }
            # Polite first: apps with a window get the same close a user's X click sends.
            try { if ($row.Proc.MainWindowHandle -ne [IntPtr]::Zero) { [void]$row.Proc.CloseMainWindow() } } catch { }
        }
    }

    Wait-Ui $PoliteWaitMs

    foreach ($x in $all) {
        try { $x.Proc.Refresh(); if (-not $x.Proc.HasExited) { $x.Proc.Kill() } } catch { }
    }
    Wait-Ui 700

    $dodgers = @($all | Where-Object { try { $_.Proc.Refresh(); -not $_.Proc.HasExited } catch { $false } } |
        ForEach-Object { $_.Label } | Select-Object -Unique)
    Save-State $jailed

    $msg = '{0} apps sent to RAM jail. About {1} freed. Go get ''em.' -f ($targets.Count - $dodgers.Count), (Format-Size $mb)
    if ($dodgers.Count) {
        $msg += ' Dodged the bonk (probably need admin): ' + ($dodgers -join ', ')
        Write-Log ('dodged: ' + ($dodgers -join ', '))
    }
    Show-Balloon "BONK! $Hammer" $msg
    Update-Tip
}

function Invoke-Release {
    $jailed = @(Read-State)
    if (-not $jailed.Count) { Show-Balloon 'Jail is empty' 'Nobody to release.'; return }
    $rows = @(Get-Rows)
    $back = @(); $stay = @()
    foreach ($j in $jailed) {
        $row = $rows | Where-Object { $_.Label -eq $j.label } | Select-Object -First 1
        if ($row -and $row.Running) { continue }   # already back on its own
        $specs = @($j.relaunch) | Where-Object { $_ -and $_.file }
        if (-not $specs.Count) { $stay += $j.label; continue }
        foreach ($s in $specs) {
            try {
                if ($s.args) { Start-Process -FilePath $s.file -ArgumentList $s.args -WindowStyle Minimized }
                else { Start-Process -FilePath $s.file -WindowStyle Minimized }
            } catch { Write-Log ('relaunch failed for {0}: {1}' -f $j.label, $_.Exception.Message) }
        }
        $back += $j.label
    }
    Save-State @()
    Write-Log ('RELEASE: ' + ($back -join ', '))
    $msg = if ($back.Count) { 'Parole granted: ' + ($back -join ', ') + '.' } else { 'Everyone was already out.' }
    if ($stay.Count) { $msg += ' Comes back on its own or when you open it: ' + ($stay -join ', ') + '.' }
    Show-Balloon 'Released from RAM jail' $msg
    Wait-Ui 1500
    Update-Tip
}

function Test-Autostart { $null -ne (Get-ItemProperty -Path $RunKey -Name $RunName -ErrorAction SilentlyContinue) }

function Set-Autostart([bool]$On) {
    if ($On) {
        $cmd = '"{0}\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -STA -File "{1}"' -f $env:SystemRoot, $PSCommandPath
        Set-ItemProperty -Path $RunKey -Name $RunName -Value $cmd
    } else {
        Remove-ItemProperty -Path $RunKey -Name $RunName -ErrorAction SilentlyContinue
    }
}

function Update-BonkItem {
    if (-not $script:BonkItem) { return }
    $rows = @($script:Rows | Where-Object { $_.Entry.enabled -and $_.Running })
    $mb = ($rows | Measure-Object -Property MB -Sum).Sum
    if ($rows.Count) { $script:BonkItem.Text = "$Hammer  BONK 'EM ALL  ({0} apps, ~{1})" -f $rows.Count, (Format-Size $mb) }
    else { $script:BonkItem.Text = "$Hammer  BONK 'EM ALL  (nothing running)" }
}

function New-EntryItem($Row) {
    $text = if ($Row.Running) { '{0}   {1}' -f $Row.Label, (Format-Size $Row.MB) } else { $Row.Label }
    $item = New-Object System.Windows.Forms.ToolStripMenuItem $text
    $item.CheckOnClick = $true
    $item.Checked = [bool]$Row.Entry.enabled
    $item.Tag = 'entry:' + $Row.Label
    $item.add_Click({
        param($s, $e)
        try {
            $label = $s.Tag.Substring(6)
            $entry = @($script:Config.apps) | Where-Object { $_.label -eq $label } | Select-Object -First 1
            if ($entry) { $entry.enabled = [bool]$s.Checked; Save-Config; Update-BonkItem }
        } catch { Write-Log ('toggle failed: ' + $_.Exception.Message) }
    })
    $item
}

# Clicking a checkbox keeps the menu open so several can be toggled in a row.
$script:KeepOpen = $false
function Register-KeepOpen($DropDown) {
    $DropDown.add_ItemClicked({
        param($s, $e)
        $script:KeepOpen = ($e.ClickedItem.Tag -is [string]) -and $e.ClickedItem.Tag.StartsWith('entry:')
    })
    $DropDown.add_Closing({
        param($s, $e)
        if ($script:KeepOpen -and $e.CloseReason -eq [System.Windows.Forms.ToolStripDropDownCloseReason]::ItemClicked) { $e.Cancel = $true }
    })
}

function Build-Menu {
    $menu = $script:Menu
    $menu.Items.Clear()
    $script:KeepOpen = $false
    try { $script:Config = Read-Config } catch { Write-Log ('config read failed: ' + $_.Exception.Message) }
    $script:Rows = @(Get-Rows)

    $header = New-Object System.Windows.Forms.ToolStripMenuItem 'Bonk Mode - RAM jail warden'
    $header.Enabled = $false
    [void]$menu.Items.Add($header)

    $script:BonkItem = New-Object System.Windows.Forms.ToolStripMenuItem 'BONK'
    $script:BonkItem.Font = New-Object System.Drawing.Font($menu.Font, [System.Drawing.FontStyle]::Bold)
    $script:BonkItem.add_Click({ try { Invoke-Bonk } catch { Write-Log ('bonk failed: ' + $_.Exception.Message); Show-Balloon 'Bonk misfired' $_.Exception.Message 'Error' } })
    [void]$menu.Items.Add($script:BonkItem)
    Update-BonkItem

    $jailed = @(Read-State)
    $release = New-Object System.Windows.Forms.ToolStripMenuItem ('Release from RAM jail ({0})' -f $jailed.Count)
    $release.Enabled = $jailed.Count -gt 0
    $release.ToolTipText = ($jailed | ForEach-Object { $_.label }) -join ', '
    $release.add_Click({ try { Invoke-Release } catch { Write-Log ('release failed: ' + $_.Exception.Message) } })
    [void]$menu.Items.Add($release)

    [void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
    $running = New-Object System.Windows.Forms.ToolStripMenuItem 'Running now (checked = gets bonked):'
    $running.Enabled = $false
    [void]$menu.Items.Add($running)
    foreach ($r in ($script:Rows | Where-Object { $_.Running } | Sort-Object MB -Descending)) {
        [void]$menu.Items.Add((New-EntryItem $r))
    }

    $idle = New-Object System.Windows.Forms.ToolStripMenuItem 'Not running right now'
    foreach ($r in ($script:Rows | Where-Object { -not $_.Running })) {
        [void]$idle.DropDownItems.Add((New-EntryItem $r))
    }
    Register-KeepOpen $idle.DropDown
    [void]$menu.Items.Add($idle)

    [void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
    $edit = New-Object System.Windows.Forms.ToolStripMenuItem 'Edit bonk list...'
    $edit.add_Click({ Start-Process notepad.exe -ArgumentList ('"{0}"' -f $ConfigFile) })
    [void]$menu.Items.Add($edit)

    $auto = New-Object System.Windows.Forms.ToolStripMenuItem 'Start with Windows'
    $auto.Checked = Test-Autostart
    $auto.add_Click({ try { Set-Autostart (-not (Test-Autostart)) } catch { Write-Log ('autostart failed: ' + $_.Exception.Message) } })
    [void]$menu.Items.Add($auto)

    $quit = New-Object System.Windows.Forms.ToolStripMenuItem 'Exit'
    $quit.add_Click({
        $script:NotifyIcon.Visible = $false
        $script:NotifyIcon.Dispose()
        [System.Windows.Forms.Application]::ExitThread()
    })
    [void]$menu.Items.Add($quit)
}

$script:Menu = New-Object System.Windows.Forms.ContextMenuStrip
$script:Menu.ShowCheckMargin = $true
$script:Menu.ShowImageMargin = $false
$script:Menu.add_Opening({ try { Build-Menu } catch { Write-Log ('menu failed: ' + $_.Exception.Message) } })
Register-KeepOpen $script:Menu

if ($SelfTest) {
    # Build the real menu and print it; no icon, nothing closed.
    Build-Menu
    foreach ($i in $script:Menu.Items) {
        if ($i -is [System.Windows.Forms.ToolStripSeparator]) { '----'; continue }
        '{0} {1}' -f $(if ($i.Checked) { '[x]' } else { '[ ]' }), $i.Text
        foreach ($d in $i.DropDownItems) { '      {0} {1}' -f $(if ($d.Checked) { '[x]' } else { '[ ]' }), $d.Text }
    }
    ''
    'Release plan (what Un-bonk would reopen):'
    foreach ($r in ($script:Rows | Where-Object { $_.Enabled -and $_.Running })) {
        $plan = @(Resolve-Relaunch $r.Entry @($r.Procs))
        '  {0} -> {1}' -f $r.Label, $(if ($plan.Count) { ($plan | ForEach-Object { ('{0} {1}' -f $_.file, $_.args).Trim() }) -join ' + ' } else { '(comes back on its own)' })
    }
    return
}

$createdNew = $false
$script:Mutex = New-Object System.Threading.Mutex($true, $MutexName, [ref]$createdNew)
if (-not $createdNew) {
    [System.Windows.Forms.MessageBox]::Show('Bonk Mode is already in your tray. Look for the hammer.', 'Bonk Mode') | Out-Null
    return
}

$script:NotifyIcon =New-Object System.Windows.Forms.NotifyIcon
$script:NotifyIcon.Icon = New-HammerIcon
$script:NotifyIcon.ContextMenuStrip = $script:Menu
$script:NotifyIcon.Visible = $true
$showMenu = [System.Windows.Forms.NotifyIcon].GetMethod('ShowContextMenu', [System.Reflection.BindingFlags]'Instance,NonPublic')
$script:NotifyIcon.add_MouseUp({
    param($s, $e)
    if ($e.Button -eq [System.Windows.Forms.MouseButtons]::Left) { $showMenu.Invoke($script:NotifyIcon, $null) }
})
Update-Tip

$script:Timer = New-Object System.Windows.Forms.Timer
$script:Timer.Interval = 60000
$script:Timer.add_Tick({ Update-Tip })
$script:Timer.Start()

Write-Log 'tray started'
try {
    [System.Windows.Forms.Application]::Run((New-Object System.Windows.Forms.ApplicationContext))
} finally {
    $script:Timer.Stop()
    if ($script:NotifyIcon) { $script:NotifyIcon.Dispose() }
    $script:Mutex.ReleaseMutex()
    Write-Log 'tray stopped'
}
