#Requires -Version 5.1
<#
    Bonk Mode - a tray hammer that sends background apps to RAM jail before gaming.

    Double-click the hammer (or launch with -Open) for the keygen window: live
    squares for every app, click to sentence/spare, BONK 'EM ALL, parole.
    Right-click the hammer for the quick menu:
      BONK 'EM ALL        politely closes every checked app that is running, then
                          force-closes anything that ignored the hint (after 4s)
      Release from jail   reopens what the last bonk closed (Un-bonk)
      app checkboxes      what gets bonked; saved to bonk-list.json

    Never touches the protected list below, whatever bonk-list.json says.

    Dry runs that close nothing:
      -ListOnly           print the bonk list and what is running
      -SelfTest           build the tray menu and print it
      -Preview <dir>      render keygen window frames to PNG files
#>
param([switch]$ListOnly, [switch]$SelfTest, [switch]$Open, [string]$Preview)

$ErrorActionPreference = 'Stop'

$AppDir     = $PSScriptRoot
$ConfigFile = Join-Path $AppDir 'bonk-list.json'
$ExampleConfig = Join-Path $AppDir 'bonk-list.example.json'
$StateFile  = Join-Path $AppDir 'bonk-state.json'
$LogFile    = Join-Path $AppDir 'bonk.log'
$RunKey     = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunName    = 'Bonk Mode'
$MutexName  = 'Local\Bonk_Mode_Tray'
$OpenEventName = 'Local\Bonk_Mode_Open'
$UiSource   = Join-Path $AppDir 'bonk-ui.cs'
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

# Exe paths are slow to read, so they are cached per process for the live window.
$script:PathCache = @{}

function Get-Snapshot {
    $session = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
    $seen = @{}
    $out = New-Object System.Collections.ArrayList
    foreach ($p in Get-Process) {
        if ($p.SessionId -ne $session -or $p.Id -eq $PID) { continue }
        if ($Protected -contains $p.ProcessName) { continue }
        $key = '{0}:{1}' -f $p.Id, $p.ProcessName
        $seen[$key] = $true
        if ($script:PathCache.ContainsKey($key)) { $path = $script:PathCache[$key] }
        else { $path = $null; try { $path = $p.Path } catch { }; $script:PathCache[$key] = $path }
        [void]$out.Add([pscustomobject]@{ Proc = $p; Name = $p.ProcessName; Path = $path; WS = $p.WorkingSet64 })
    }
    foreach ($k in @($script:PathCache.Keys)) { if (-not $seen.ContainsKey($k)) { $script:PathCache.Remove($k) } }
    $out
}

function Get-Rows {
    $snap = @(Get-Snapshot)
    $byName = @{}
    foreach ($row in $snap) {
        if (-not $byName.ContainsKey($row.Name)) { $byName[$row.Name] = New-Object System.Collections.ArrayList }
        [void]$byName[$row.Name].Add($row)
    }
    foreach ($e in @($script:Config.apps)) {
        $hits = New-Object System.Collections.ArrayList
        $ids = @{}
        foreach ($n in @($e.names)) {
            if (-not $n -or -not $byName.ContainsKey($n)) { continue }
            foreach ($r in $byName[$n]) { if (-not $ids.ContainsKey($r.Proc.Id)) { $ids[$r.Proc.Id] = 1; [void]$hits.Add($r) } }
        }
        $paths = @(@($e.paths) | Where-Object { $_ } | ForEach-Object { [Environment]::ExpandEnvironmentVariables($_) })
        if ($paths.Count) {
            foreach ($r in $snap) {
                if (-not $r.Path -or $ids.ContainsKey($r.Proc.Id)) { continue }
                foreach ($pat in $paths) { if ($r.Path -like $pat) { $ids[$r.Proc.Id] = 1; [void]$hits.Add($r); break } }
            }
        }
        $bytes = 0
        foreach ($r in $hits) { $bytes += $r.WS }
        [pscustomobject]@{
            Entry   = $e
            Label   = [string]$e.label
            Enabled = [bool]$e.enabled
            Procs   = @($hits)
            Running = ($hits.Count -gt 0)
            MB      = [math]::Round($bytes / 1MB)
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
        Start-Sleep -Milliseconds 15   # short, so the keygen window keeps animating
    }
}

# Both return a status message; -Quiet skips the tray balloon (the window shows it instead).
function Invoke-Bonk([switch]$Quiet) {
    $targets = @(Get-Rows | Where-Object { $_.Enabled -and $_.Running })
    if (-not $targets.Count) {
        $msg = 'Nothing to bonk. Your RAM is living its best life.'
        if (-not $Quiet) { Show-Balloon 'Nothing to bonk' $msg }
        return $msg
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
    if (-not $Quiet) { Show-Balloon "BONK! $Hammer" $msg }
    Update-Tip
    $msg
}

function Invoke-Release([switch]$Quiet) {
    $jailed = @(Read-State)
    if (-not $jailed.Count) {
        if (-not $Quiet) { Show-Balloon 'Jail is empty' 'Nobody to release.' }
        return 'Jail is empty. Nobody to release.'
    }
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
    if (-not $Quiet) { Show-Balloon 'Released from RAM jail' $msg }
    Wait-Ui 1500
    Update-Tip
    $msg
}

# ---------------------------------------------------------------------------
# Keygen window (bonk-ui.cs, compiled once and cached per source hash)
# ---------------------------------------------------------------------------

$script:Ui = $null

function Import-BonkUi {
    if ('BonkMode.KeygenForm' -as [type]) { return }
    $hash = (Get-FileHash -LiteralPath $UiSource -Algorithm SHA256).Hash.Substring(0, 12)
    $cacheDir = Join-Path $env:LOCALAPPDATA 'BonkMode'
    [void][System.IO.Directory]::CreateDirectory($cacheDir)
    $dll = Join-Path $cacheDir ('bonk-ui-{0}.dll' -f $hash)
    if (-not (Test-Path -LiteralPath $dll)) {
        Add-Type -TypeDefinition (Get-Content -LiteralPath $UiSource -Raw -Encoding UTF8) -ReferencedAssemblies System.Windows.Forms, System.Drawing -OutputAssembly $dll -OutputType Library
    }
    if (-not ('BonkMode.KeygenForm' -as [type])) { Add-Type -Path $dll }
}

function Push-UiRows {
    $ui = $script:Ui
    if (-not $ui -or $ui.IsDisposed) { return }
    $rows = @(Get-Rows)
    $jailed = @(Read-State | ForEach-Object { [string]$_.label })
    $show = @($rows | Where-Object { $_.Running } | Sort-Object MB -Descending) +
            @($rows | Where-Object { -not $_.Running -and $jailed -contains $_.Label })
    $ui.SetRows(
        [string[]]@($show | ForEach-Object { $_.Label }),
        [int[]]@($show | ForEach-Object { [int]$_.MB }),
        [bool[]]@($show | ForEach-Object { $_.Enabled }),
        [bool[]]@($show | ForEach-Object { $_.Running }),
        [bool[]]@($show | ForEach-Object { $jailed -contains $_.Label }))
}

function Invoke-UiAction([scriptblock]$Action) {
    $ui = $script:Ui
    try { $msg = [string](& $Action) }
    catch { $msg = 'Misfire: ' + $_.Exception.Message; Write-Log ('ui action failed: ' + $_.Exception.Message) }
    if ($ui -and -not $ui.IsDisposed) {
        Push-UiRows
        $ui.SetStatus($msg)
        $ui.SetBusy($false)
    }
}

function New-BonkWindow {
    Import-BonkUi
    $ui = New-Object BonkMode.KeygenForm
    if ($script:NotifyIcon) { $ui.Icon = $script:NotifyIcon.Icon }
    $ui.add_ToggleClicked({
        param($label, $on)
        try {
            $entry = @($script:Config.apps) | Where-Object { $_.label -eq $label } | Select-Object -First 1
            if ($entry) { $entry.enabled = [bool]$on; Save-Config }
        } catch { Write-Log ('toggle failed: ' + $_.Exception.Message) }
    })
    $ui.add_BonkClicked({ Invoke-UiAction { Invoke-Bonk -Quiet } })
    $ui.add_ParoleClicked({ Invoke-UiAction { Invoke-Release -Quiet } })
    $ui.add_EditClicked({ Start-Process notepad.exe -ArgumentList ('"{0}"' -f $ConfigFile) })
    $ui
}

function Open-BonkWindow {
    try {
        if ($script:Ui -and -not $script:Ui.IsDisposed) {
            $script:Ui.WindowState = [System.Windows.Forms.FormWindowState]::Normal
            $script:Ui.Activate()
            return
        }
        $script:Config = Read-Config
        $ui = New-BonkWindow
        $ui.add_FormClosed({
            $script:UiTimer.Stop()
            $script:Ui = $null
            [System.GC]::Collect()
        })
        $script:Ui = $ui
        Push-UiRows
        $ui.Show()
        $ui.Activate()
        $script:UiTimer.Start()
    } catch {
        Write-Log ('window failed: ' + $_.Exception.Message)
        Show-Balloon 'Bonk Mode window misfired' $_.Exception.Message 'Error'
    }
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

    $openItem = New-Object System.Windows.Forms.ToolStripMenuItem 'Open Bonk Mode (keygen)'
    $openItem.Font = New-Object System.Drawing.Font($menu.Font, [System.Drawing.FontStyle]::Bold)
    $openItem.add_Click({ Open-BonkWindow })
    [void]$menu.Items.Add($openItem)
    [void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))

    $script:BonkItem = New-Object System.Windows.Forms.ToolStripMenuItem 'BONK'
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

if ($Preview) {
    # Render the keygen window to PNGs: idle, mid-bonk, and the jail banner. Nothing closed, no sound.
    [void][System.IO.Directory]::CreateDirectory($Preview)
    Import-BonkUi
    [BonkMode.KeygenForm]::NoAudio = $true
    $script:Ui = New-BonkWindow
    Push-UiRows
    $shots = @(@{ name = 'idle'; advance = 2.5; bonk = $false }, @{ name = 'bonk'; advance = 0.6; bonk = $true }, @{ name = 'banner'; advance = 0.35; bonk = $false })
    foreach ($s in $shots) {
        if ($s.bonk) { $script:Ui.PreviewBonk() }
        if ($s.name -eq 'banner') { while ($script:Ui.Busy) { $script:Ui.Advance(0.03) } }
        $steps = [int]($s.advance / 0.03)
        for ($i = 0; $i -lt $steps; $i++) { $script:Ui.Advance(0.03) }
        $bmp = New-Object System.Drawing.Bitmap $script:Ui.ClientSize.Width, $script:Ui.ClientSize.Height
        $script:Ui.DrawToBitmap($bmp, (New-Object System.Drawing.Rectangle 0, 0, $bmp.Width, $bmp.Height))
        $file = Join-Path $Preview ('keygen-{0}.png' -f $s.name)
        $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $file
    }
    $script:Ui.Dispose()
    return
}

# Single instance. A second launch (double-clicking the shortcut) asks the
# running tray to open the window instead.
$createdNew = $false
$script:Mutex = New-Object System.Threading.Mutex($true, $MutexName, [ref]$createdNew)
if (-not $createdNew) {
    try { [void][System.Threading.EventWaitHandle]::OpenExisting($OpenEventName).Set() }
    catch { [System.Windows.Forms.MessageBox]::Show('Bonk Mode is already in your tray. Look for the hammer.', 'Bonk Mode') | Out-Null }
    return
}
$script:OpenEvent = New-Object System.Threading.EventWaitHandle($false, [System.Threading.EventResetMode]::AutoReset, $OpenEventName)

$script:NotifyIcon = New-Object System.Windows.Forms.NotifyIcon
$script:NotifyIcon.Icon = New-HammerIcon
$script:NotifyIcon.ContextMenuStrip = $script:Menu
$script:NotifyIcon.Visible = $true
$script:NotifyIcon.add_DoubleClick({ Open-BonkWindow })
Update-Tip

$script:Timer = New-Object System.Windows.Forms.Timer
$script:Timer.Interval = 60000
$script:Timer.add_Tick({ Update-Tip })
$script:Timer.Start()

# Live numbers in the window while it is open.
$script:UiTimer = New-Object System.Windows.Forms.Timer
$script:UiTimer.Interval = 5000
$script:UiTimer.add_Tick({
    try { if ($script:Ui -and -not $script:Ui.IsDisposed -and -not $script:Ui.Busy) { Push-UiRows } } catch { Write-Log ('refresh failed: ' + $_.Exception.Message) }
})

$script:OpenPoll = New-Object System.Windows.Forms.Timer
$script:OpenPoll.Interval = 300
$script:OpenPoll.add_Tick({ if ($script:OpenEvent.WaitOne(0)) { Open-BonkWindow } })
$script:OpenPoll.Start()

if ($Open) { Open-BonkWindow }

Write-Log 'tray started'
try {
    [System.Windows.Forms.Application]::Run((New-Object System.Windows.Forms.ApplicationContext))
} finally {
    $script:Timer.Stop()
    if ($script:NotifyIcon) { $script:NotifyIcon.Dispose() }
    $script:Mutex.ReleaseMutex()
    Write-Log 'tray stopped'
}
