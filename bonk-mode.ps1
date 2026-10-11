#Requires -Version 5.1
# Bonk Mode - https://github.com/makesupply/bonk-mode
# Copyright 2026 makesupply. Licensed under the Apache License 2.0; see LICENSE and NOTICE.
# Derivative works must keep the NOTICE attribution.
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
      -GameScan           show game folders and any game running right now
      -Radar              list heavy apps that are not on your bonk list yet
      -StartupList        list everything that launches at login and its jail state
    Other:
      -UpdateNow          install the latest release without the tray (no restart)
    Remote control (for Stream Deck, shortcuts, scripts); starts the tray if needed:
      -Bonk / -Parole     bonk or parole right now
      -SetProfile <name>  switch to gaming, streaming, work or battery
#>
param([switch]$ListOnly, [switch]$SelfTest, [switch]$Open, [string]$Preview, [int]$WaitPid, [switch]$GameScan, [string[]]$GameFolder, [switch]$UpdateNow, [switch]$Radar, [switch]$StartupList, [switch]$Bonk, [switch]$Parole, [string]$SetProfile)

$ErrorActionPreference = 'Stop'

# After a self-update the new tray waits for the old one to exit.
if ($WaitPid) { try { [void](Get-Process -Id $WaitPid -ErrorAction Stop).WaitForExit(15000) } catch { } }
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

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
$SettingsFile = Join-Path $AppDir 'settings.json'
$VersionFile  = Join-Path $AppDir 'VERSION'
$RepoUrl    = 'https://github.com/makesupply/bonk-mode'
$RawBase    = 'https://raw.githubusercontent.com/makesupply/bonk-mode/main/'
$RepoZip    = 'https://codeload.github.com/makesupply/bonk-mode/zip/refs/heads/main'
# Files an update may replace. Your bonk-list.json, settings.json and jail state are never touched.
$UpdateFiles = @('bonk-mode.ps1', 'bonk-ui.cs', 'bonk-list.example.json', 'install.ps1', 'README.md', 'CHANGELOG.md', 'VERSION', 'LICENSE', 'NOTICE')
$Version = if (Test-Path -LiteralPath $VersionFile) { (Get-Content -LiteralPath $VersionFile -Raw).Trim() } else { '0.0.0' }

$DefaultSettings = [ordered]@{
    music = 'True'; volume = '60'; sfx = 'True'; shake = 'True'; scanlines = 'True'
    hammer = 'normal'; polite = '4'; autoUpdate = 'True'; gameMode = 'ask'; radar = 'True'; radarMB = '400'; profile = 'gaming'; powerPlan = 'False'; hotkeys = 'True'
}

function Read-Settings {
    $s = @{}
    foreach ($k in $DefaultSettings.Keys) { $s[$k] = $DefaultSettings[$k] }
    if (Test-Path -LiteralPath $SettingsFile) {
        try {
            $j = Get-Content -LiteralPath $SettingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
            foreach ($p in $j.PSObject.Properties) { $s[$p.Name] = [string]$p.Value }
        } catch { }
    }
    $s
}

function Save-Settings {
    $script:Settings | ConvertTo-Json | Set-Content -LiteralPath $SettingsFile -Encoding UTF8
}

$script:Settings = Read-Settings
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
        if ($script:GamePids.ContainsKey($p.Id)) { continue }   # never bonk the game you are playing
        $key = '{0}:{1}' -f $p.Id, $p.ProcessName
        $seen[$key] = $true
        if ($script:PathCache.ContainsKey($key)) { $path = $script:PathCache[$key] }
        else { $path = $null; try { $path = $p.Path } catch { }; $script:PathCache[$key] = $path }
        [void]$out.Add([pscustomobject]@{ Proc = $p; Name = $p.ProcessName; Path = $path; WS = $p.WorkingSet64; CPU = 0.0; IOKB = 0 })
    }
    foreach ($k in @($script:PathCache.Keys)) { if (-not $seen.ContainsKey($k)) { $script:PathCache.Remove($k) } }
    Add-RateSamples $out
    $out
}

# CPU % (of the whole machine) and disk KB/s since the previous snapshot, per process.
# Needs the compiled helper; without it (dry runs) rates simply stay 0.
$script:Samples = @{}
$script:SampleClock = [System.Diagnostics.Stopwatch]::StartNew()
$Cores = [Environment]::ProcessorCount

function Add-RateSamples($Rows) {
    if (-not ('BonkMode.ProcStats' -as [type]) -or -not $Rows.Count) { return }
    $pids = [int[]]@($Rows | ForEach-Object { $_.Proc.Id })
    $cpu = New-Object long[] $pids.Length
    $io = New-Object long[] $pids.Length
    [BonkMode.ProcStats]::SampleMany($pids, $cpu, $io)
    $now = $script:SampleClock.Elapsed.TotalSeconds
    $next = @{}
    for ($i = 0; $i -lt $pids.Length; $i++) {
        if ($cpu[$i] -lt 0) { continue }
        $prev = $script:Samples[$pids[$i]]
        if ($prev) {
            $dt = $now - $prev[0]
            if ($dt -gt 0.5) {
                $Rows[$i].CPU = [math]::Max(0, ($cpu[$i] - $prev[1]) / ($dt * 1e7) / $Cores * 100)
                if ($io[$i] -ge 0 -and $prev[2] -ge 0) { $Rows[$i].IOKB = [int][math]::Max(0, ($io[$i] - $prev[2]) / $dt / 1024) }
                $next[$pids[$i]] = @($now, $cpu[$i], $io[$i])
            } else { $next[$pids[$i]] = $prev }
        } else { $next[$pids[$i]] = @($now, $cpu[$i], $io[$i]) }
    }
    $script:Samples = $next
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
        $bytes = 0; $cpuSum = 0.0; $ioSum = 0
        foreach ($r in $hits) { $bytes += $r.WS; $cpuSum += $r.CPU; $ioSum += $r.IOKB }
        [pscustomobject]@{
            Entry   = $e
            Label   = [string]$e.label
            Enabled = [bool]$e.enabled
            Procs   = @($hits)
            Running = ($hits.Count -gt 0)
            MB      = [math]::Round($bytes / 1MB)
            CPU     = [math]::Round($cpuSum, 1)
            IOKB    = $ioSum
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
# Auto Game Mode: spot games by where they live (Steam libraries, Epic, Xbox,
# your own folders) or by exe name from the "games" block in bonk-list.json.
# ---------------------------------------------------------------------------

# Crash handlers, installers, anti-cheat and launchers inside game folders are not "the game".
$GameExclude = '(?i)crash|report|redist|setup|install|unins|anticheat|battleye|webhelper|prereq|dxsetup|vc_?redist|cefprocess|launcher|updater|helper'
$script:GameRoots = $null
$script:GameRootsAt = [datetime]::MinValue
$script:SeenPids = @{}
$script:GamePids = @{}

function Get-GameRoots {
    $roots = New-Object System.Collections.ArrayList
    $steam = $null
    try { $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath } catch { }
    if (-not $steam) { $steam = Join-Path ${env:ProgramFiles(x86)} 'Steam' }
    $steam = $steam -replace '/', '\'
    $libs = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path -LiteralPath $vdf) {
        foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) { $libs += ($m.Groups[1].Value -replace '\\\\', '\') }
    }
    foreach ($l in ($libs | Select-Object -Unique)) {
        $common = Join-Path $l 'steamapps\common'
        if (Test-Path -LiteralPath $common) { [void]$roots.Add($common.TrimEnd('\') + '\') }
    }
    $known = @((Join-Path $env:ProgramFiles 'Epic Games'), 'C:\XboxGames', (Join-Path $env:ProgramFiles 'EA Games'),
               (Join-Path ${env:ProgramFiles(x86)} 'Ubisoft\Ubisoft Game Launcher\games'), (Join-Path ${env:ProgramFiles(x86)} 'GOG Galaxy\Games'))
    foreach ($d in $known) { if ($d -and (Test-Path -LiteralPath $d)) { [void]$roots.Add($d.TrimEnd('\') + '\') } }
    foreach ($d in @($script:Config.games.folders)) {
        if ($d) { [void]$roots.Add(([Environment]::ExpandEnvironmentVariables($d)).TrimEnd('\') + '\') }
    }
    foreach ($d in @($GameFolder)) { if ($d) { [void]$roots.Add($d.TrimEnd('\') + '\') } }
    $seen = @{}
    foreach ($r in $roots) { if (-not $seen.ContainsKey($r)) { $seen[$r] = $true; $r } }
}

# Returns the names of games that started since the last call. Paths are only read
# for processes never seen before, so a quiet machine costs one Get-Process per tick.
function Find-NewGames {
    if (-not $script:GameRoots -or ((Get-Date) - $script:GameRootsAt).TotalMinutes -gt 10) {
        $script:GameRoots = @(Get-GameRoots); $script:GameRootsAt = Get-Date
    }
    $names = @(@($script:Config.games.names) | Where-Object { $_ })
    $session = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
    $alive = @{}
    $found = New-Object System.Collections.ArrayList
    foreach ($p in Get-Process) {
        $alive[$p.Id] = $true
        if ($script:GamePids.ContainsKey($p.Id) -or $script:SeenPids.ContainsKey($p.Id)) { continue }
        $script:SeenPids[$p.Id] = $true
        if ($p.SessionId -ne $session -or $Protected -contains $p.ProcessName) { continue }
        $isGame = $names -contains $p.ProcessName
        if (-not $isGame -and $p.ProcessName -notmatch $GameExclude) {
            $path = $null
            try { $path = $p.Path } catch { }
            if ($path) { foreach ($r in $script:GameRoots) { if ($path.StartsWith($r, [StringComparison]::OrdinalIgnoreCase)) { $isGame = $true; break } } }
        }
        if ($isGame) { $script:GamePids[$p.Id] = $p.ProcessName; [void]$found.Add($p.ProcessName) }
    }
    foreach ($k in @($script:SeenPids.Keys)) { if (-not $alive.ContainsKey($k)) { $script:SeenPids.Remove($k) } }
    foreach ($k in @($script:GamePids.Keys)) { if (-not $alive.ContainsKey($k)) { $script:GamePids.Remove($k) } }
    @($found)
}

# ---------------------------------------------------------------------------
# Hog Radar: heavy apps that are on nobody's list yet.
# ---------------------------------------------------------------------------

# Never suggested: shared runtimes other apps are built on, and dev/server runtimes
# (closing node or java could take down a local server or a game server).
$RadarIgnore = @('msedgewebview2', 'WebViewHost', 'CefSharp.BrowserSubprocess', 'node', 'java', 'javaw', 'dotnet',
    'vmmem', 'vmmemWSL', 'wsl', 'wslhost', 'com.docker.backend', 'Docker Desktop', 'git', 'ssh', 'MemCompression')

function Get-Suspects {
    $threshold = [int]$script:Settings.radarMB
    $snap = @(Get-Snapshot)
    $covered = @{}
    $spare = @{}
    foreach ($n in @($script:Config.spareForever)) { if ($n) { $spare[$n] = $true } }
    $patterns = New-Object System.Collections.ArrayList
    foreach ($e in @($script:Config.apps)) {
        foreach ($n in @($e.names)) { if ($n) { $covered[$n] = $true } }
        foreach ($p in @($e.paths)) { if ($p) { [void]$patterns.Add([Environment]::ExpandEnvironmentVariables($p)) } }
    }
    $groups = @{}
    foreach ($r in $snap) {
        if ($covered.ContainsKey($r.Name) -or $spare.ContainsKey($r.Name) -or $RadarIgnore -contains $r.Name) { continue }
        if ($r.Path -and ($r.Path.StartsWith($env:windir, [StringComparison]::OrdinalIgnoreCase) -or $r.Path.StartsWith($AppDir, [StringComparison]::OrdinalIgnoreCase))) { continue }
        $hit = $false
        if ($r.Path) { foreach ($pat in $patterns) { if ($r.Path -like $pat) { $hit = $true; break } } }
        if ($hit) { continue }
        if (-not $groups.ContainsKey($r.Name)) { $groups[$r.Name] = New-Object System.Collections.ArrayList }
        [void]$groups[$r.Name].Add($r)
    }
    foreach ($name in $groups.Keys) {
        $rows = $groups[$name]
        $bytes = 0; $cpu = 0.0; foreach ($r in $rows) { $bytes += $r.WS; $cpu += $r.CPU }
        $mb = [math]::Round($bytes / 1MB)
        if ($mb -lt $threshold -and $cpu -lt 10) { continue }   # heavy on RAM, or chewing 10%+ of the CPU
        $path = ($rows | Where-Object { $_.Path } | Select-Object -First 1).Path
        $label = $name
        if ($path) { try { $desc = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileDescription; if ($desc -and $desc.Trim()) { $label = $desc.Trim() } } catch { } }
        [pscustomobject]@{ Name = $name; Label = $label; MB = [int]$mb; Procs = $rows.Count; Path = $path; CPU = [math]::Round($cpu, 1) }
    }
}

function Add-Suspect([string]$Name, [string]$Label) {
    $entry = [pscustomobject]@{ label = $(if ($Label) { $Label } else { $Name }); enabled = $true; names = @($Name) }
    $script:Config.apps = @($script:Config.apps) + $entry
    Save-Config
    Write-Log ('radar: added {0} ({1}) to the list' -f $entry.label, $Name)
    if (Get-Command Add-Stats -ErrorAction SilentlyContinue) { try { Add-Stats 'radar' } catch { } }
}

function Set-SpareForever([string]$Name) {
    if (-not $script:Config.PSObject.Properties['spareForever']) { $script:Config | Add-Member -NotePropertyName spareForever -NotePropertyValue @() }
    $script:Config.spareForever = @(@($script:Config.spareForever) + $Name | Where-Object { $_ } | Select-Object -Unique)
    Save-Config
    Write-Log ('radar: spared {0} forever' -f $Name)
}

# ---------------------------------------------------------------------------
# Profiles: each one remembers its own checkboxes. First visit seeds sensible
# defaults from your current list.
# ---------------------------------------------------------------------------

$ProfileNames = @('gaming', 'streaming', 'work', 'battery')
$ProfileSpare = @{
    streaming = '(?i)discord|obs|spotify|stream ?deck|elgato|streamlabs|voicemod|broadcast|music'
    work      = '(?i)teams|slack|zoom|outlook|onedrive|dropbox|google drive|edge|chrome|firefox|office|acrobat|notion|phone link'
    battery   = '(?i)discord|voice|fxsound|audio|sound'
}
$ProfileSentence = @{
    work    = '(?i)steam|epic|ea app|ubisoft|battle\.net|xbox|razer|icue|g hub|armoury|roblox|overwolf|game'
    battery = '.'
}

function Switch-Profile([string]$Name) {
    if ($ProfileNames -notcontains $Name) { throw "unknown profile: $Name" }
    $current = [string]$script:Settings.profile
    if (-not $script:Config.PSObject.Properties['profiles']) { $script:Config | Add-Member -NotePropertyName profiles -NotePropertyValue ([pscustomobject]@{}) }
    $store = $script:Config.profiles
    $now = @($script:Config.apps | Where-Object { $_.enabled } | ForEach-Object { [string]$_.label })
    if ($store.PSObject.Properties[$current]) { $store.$current = $now } else { $store | Add-Member -NotePropertyName $current -NotePropertyValue $now }
    if ($store.PSObject.Properties[$Name]) {
        $want = @($store.$Name)
        foreach ($e in $script:Config.apps) { $e.enabled = $want -contains [string]$e.label }
    } else {
        foreach ($e in $script:Config.apps) {
            $text = ([string]$e.label) + ' ' + (@($e.names) -join ' ')
            if ($ProfileSpare[$Name] -and $text -match $ProfileSpare[$Name]) { $e.enabled = $false }
            elseif ($ProfileSentence[$Name] -and $text -match $ProfileSentence[$Name]) { $e.enabled = $true }
        }
    }
    $script:Settings.profile = $Name
    Save-Config
    Save-Settings
    Write-Log ('profile: {0} -> {1}' -f $current, $Name)
}

# ---------------------------------------------------------------------------
# Turbo power plan: High performance while bonked, the original plan on parole.
# The original is written to bonk-power.json first so a crash can't strand you.
# ---------------------------------------------------------------------------

$PowerFile = Join-Path $AppDir 'bonk-power.json'

function Get-ActivePlan {
    $ErrorActionPreference = 'Continue'
    $o = (& powercfg /getactivescheme 2>$null) | Out-String
    if ($o -match '([0-9a-fA-F]{8}-[0-9a-fA-F-]{27})') { $Matches[1].ToLower() }
}

function Enable-TurboPower {
    if ($script:Settings.powerPlan -ne 'True' -or $script:Settings.profile -eq 'battery' -or (Test-Path -LiteralPath $PowerFile)) { return }
    $ErrorActionPreference = 'Continue'
    $orig = Get-ActivePlan
    if (-not $orig) { return }
    foreach ($g in '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c', 'e9a42b02-d5df-448d-aa00-03f14749eb61') {   # High performance, Ultimate
        if ($g -eq $orig) { return }
        & powercfg /setactive $g 2>$null | Out-Null
        if ((Get-ActivePlan) -eq $g) {
            @{ original = $orig; turbo = $g } | ConvertTo-Json | Set-Content -LiteralPath $PowerFile -Encoding UTF8
            Write-Log ('power plan: turbo {0} on, was {1}' -f $g, $orig)
            return
        }
    }
    Write-Log 'power plan: this PC has no High performance plan, left it alone'
}

function Restore-PowerPlan {
    if (-not (Test-Path -LiteralPath $PowerFile)) { return }
    $ErrorActionPreference = 'Continue'
    try {
        $s = Get-Content -LiteralPath $PowerFile -Raw | ConvertFrom-Json
        & powercfg /setactive $s.original 2>$null | Out-Null
        Write-Log ('power plan: restored {0}' -f $s.original)
    } catch { }
    Remove-Item -LiteralPath $PowerFile -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
# Startup Jail: stop apps from launching at login using Windows' own
# StartupApproved flags (the same switch Task Manager's Startup tab flips).
# Nothing is deleted; paroling flips the flag back.
# ---------------------------------------------------------------------------

$ApprovedBase = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved'
$StartupSources = @(
    @{ Id = 'hkcu-run';   Label = 'HKCU RuN';        Hive = 'HKCU'; Kind = 'reg';    Path = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run';             Approved = 'Run' },
    @{ Id = 'hklm-run';   Label = 'HKLM RuN';        Hive = 'HKLM'; Kind = 'reg';    Path = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run';             Approved = 'Run' },
    @{ Id = 'hklm-run32'; Label = 'HKLM RuN (32)';   Hive = 'HKLM'; Kind = 'reg';    Path = 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run'; Approved = 'Run32' },
    @{ Id = 'user-folder'; Label = 'STaRTuP FoLDeR'; Hive = 'HKCU'; Kind = 'folder'; Path = [Environment]::GetFolderPath('Startup');                             Approved = 'StartupFolder' },
    @{ Id = 'all-folder'; Label = 'aLL-uSeRS FoLDeR'; Hive = 'HKLM'; Kind = 'folder'; Path = [Environment]::GetFolderPath('CommonStartup');                      Approved = 'StartupFolder' }
)
# Security, audio drivers and Bonk Mode itself can't be jailed. Add your own in bonk-list.json "startupImmune".
$StartupImmune = '(?i)^(SecurityHealth|Windows Defender|RtkAudUService|RTHDVCPL|Bonk Mode|NvBackend|IgfxTray|HotKeysCmds|Persistence)$'

function Get-ExeFromCommand([string]$Command) {
    if (-not $Command) { return $null }
    $cmd = [Environment]::ExpandEnvironmentVariables($Command.Trim())
    if ($cmd.StartsWith('"')) { $end = $cmd.IndexOf('"', 1); if ($end -gt 1) { return $cmd.Substring(1, $end - 1) } }
    $i = $cmd.IndexOf('.exe', [StringComparison]::OrdinalIgnoreCase)
    if ($i -gt 0) { return $cmd.Substring(0, $i + 4) }
    ($cmd -split ' ')[0]
}

function Get-StartupItems {
    $immune = @(@($script:Config.startupImmune) | Where-Object { $_ })
    $running = @{}
    foreach ($p in Get-Process) { $n = $p.ProcessName.ToLower(); if (-not $running.ContainsKey($n)) { $running[$n] = 0 }; $running[$n] += $p.WorkingSet64 }
    $shell = $null
    foreach ($src in $StartupSources) {
        $approvedKey = '{0}:\{1}\{2}' -f $src.Hive, $ApprovedBase, $src.Approved
        $approved = Get-ItemProperty -Path $approvedKey -ErrorAction SilentlyContinue
        $entries = @()
        if ($src.Kind -eq 'reg') {
            $props = Get-ItemProperty -Path $src.Path -ErrorAction SilentlyContinue
            if ($props) { $entries = @($props.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } | ForEach-Object { @{ Name = $_.Name; Command = [string]$_.Value } }) }
        } elseif ($src.Path -and (Test-Path -LiteralPath $src.Path)) {
            foreach ($file in Get-ChildItem -LiteralPath $src.Path -File -ErrorAction SilentlyContinue) {
                if ($file.Name -ieq 'desktop.ini') { continue }
                $cmd = $file.FullName
                if ($file.Extension -ieq '.lnk') {
                    try { if (-not $shell) { $shell = New-Object -ComObject WScript.Shell }; $l = $shell.CreateShortcut($file.FullName); $cmd = ('"{0}" {1}' -f $l.TargetPath, $l.Arguments).Trim() } catch { }
                }
                $entries += @{ Name = $file.Name; Command = $cmd }
            }
        }
        foreach ($e in $entries) {
            $flag = $null
            if ($approved -and $approved.PSObject.Properties[$e.Name]) { $flag = $approved.($e.Name) }
            $enabled = -not ($flag -and $flag.Length -gt 0 -and ($flag[0] % 2) -eq 1)
            $exe = Get-ExeFromCommand $e.Command
            $exeName = if ($exe) { [IO.Path]::GetFileNameWithoutExtension($exe).ToLower() } else { '' }
            $isImmune = ($e.Name -match $StartupImmune) -or ($immune -contains $e.Name) -or
                        ($exe -and $exe.StartsWith($env:windir, [StringComparison]::OrdinalIgnoreCase) -and $e.Name -notmatch '\.(lnk|url)$' -and $exeName -notin @('wscript', 'cscript', 'powershell', 'cmd', 'rundll32'))
            [pscustomobject]@{
                Key      = '{0}|{1}' -f $src.Id, $e.Name
                Name     = $e.Name
                Source   = $src.Label
                Hive     = $src.Hive
                Approved = $approvedKey
                Command  = $e.Command
                Enabled  = $enabled
                Immune   = [bool]$isImmune
                MB       = if ($exeName -and $exeName -notin @('wscript', 'cscript', 'powershell', 'pwsh', 'cmd', 'rundll32') -and $running.ContainsKey($exeName)) { [int][math]::Round($running[$exeName] / 1MB) } else { 0 }
            }
        }
    }
}

function Set-StartupItem([string]$Key, [bool]$Enable) {
    $item = @(Get-StartupItems) | Where-Object { $_.Key -eq $Key } | Select-Object -First 1
    if (-not $item) { throw "startup item not found: $Key" }
    if ($item.Immune) { throw "$($item.Name) is immune" }
    # 12 bytes: 02 = enabled, 03 = disabled + the time it was disabled (FILETIME), exactly like Task Manager.
    $bytes = New-Object byte[] 12
    if ($Enable) { $bytes[0] = 2 } else { $bytes[0] = 3; [BitConverter]::GetBytes([DateTime]::Now.ToFileTime()).CopyTo($bytes, 4) }
    if ($item.Hive -eq 'HKCU') {
        if (-not (Test-Path -LiteralPath $item.Approved)) { New-Item -Path $item.Approved -Force | Out-Null }
        Set-ItemProperty -LiteralPath $item.Approved -Name $item.Name -Value $bytes -Type Binary
    } else {
        # Machine-wide entries need admin: Windows shows its own permission prompt for this one change.
        $hex = ($bytes | ForEach-Object { '0x{0:X2}' -f $_ }) -join ','
        $cmd = "New-Item -Path '{0}' -Force -ErrorAction SilentlyContinue | Out-Null; Set-ItemProperty -LiteralPath '{0}' -Name '{1}' -Value ([byte[]]@({2})) -Type Binary" -f $item.Approved.Replace("'", "''"), $item.Name.Replace("'", "''"), $hex
        $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cmd))
        $p = Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -WindowStyle Hidden -EncodedCommand $enc" -WindowStyle Hidden -PassThru -Wait
        if ($p.ExitCode -ne 0) { throw "admin change was cancelled or failed" }
    }
    if (-not $Enable -and (Get-Command Add-Stats -ErrorAction SilentlyContinue)) { try { Add-Stats 'startup' } catch { } }
    Write-Log ('startup {0}: {1} ({2})' -f $(if ($Enable) { 'paroled' } else { 'jailed' }), $item.Name, $item.Source)
}

# ---------------------------------------------------------------------------
# -ListOnly: report and exit, nothing is closed.
# ---------------------------------------------------------------------------

$script:Config = Read-Config

if ($StartupList) {
    'Startup Jail: apps that launch at login'
    @(Get-StartupItems) | Sort-Object Source, Name | Format-Table @{ n = 'State'; e = { if ($_.Immune) { 'IMMUNE' } elseif ($_.Enabled) { 'starts' } else { 'JAILED' } } }, Name, Source, @{ n = 'RAM now'; e = { if ($_.MB) { '{0} MB' -f $_.MB } else { '' } } } -AutoSize | Out-String -Width 200
    return
}

if ($Radar) {
    'Hog Radar (threshold {0} MB, not on your list, not Windows, not spared):' -f $script:Settings.radarMB
    $s = @(Get-Suspects | Sort-Object MB -Descending)
    if ($s.Count) { $s | Format-Table Label, Name, MB, Procs -AutoSize | Out-String -Width 200 } else { '  No suspects. Your list has it covered.' }
    return
}

if ($GameScan) {
    'Game folders Bonk Mode watches:'
    foreach ($r in Get-GameRoots) { '  ' + $r }
    $now = @(Find-NewGames)
    if ($now.Count) { 'Games running right now: ' + (($now | Select-Object -Unique) -join ', ') }
    else { 'No game running right now.' }
    return
}

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

# --- Lifetime stats and achievements (bonk-stats.json, never published) -----

$StatsFile = Join-Path $AppDir 'bonk-stats.json'
$Achievements = @(
    @{ id = 'first';   name = 'FiRST BLooD';     desc = 'Bonk your first app.';                  test = { param($s) $s.apps -ge 1 } },
    @{ id = 'squad';   name = 'BoNK SQuaD';      desc = 'Bonk 10 apps.';                         test = { param($s) $s.apps -ge 10 } },
    @{ id = 'hammer';  name = 'HaMMeR TiMe';     desc = 'Bonk 100 apps.';                        test = { param($s) $s.apps -ge 100 } },
    @{ id = 'father';  name = 'THe BoNKFaTHeR';  desc = 'Bonk 1,000 apps.';                      test = { param($s) $s.apps -ge 1000 } },
    @{ id = 'gb10';    name = '10 GB CLuB';      desc = 'Free 10 GB of RAM in total.';           test = { param($s) $s.freedMB -ge 10240 } },
    @{ id = 'gb100';   name = 'RaM BaRoN';       desc = 'Free 100 GB of RAM in total.';          test = { param($s) $s.freedMB -ge 102400 } },
    @{ id = 'mega';    name = 'MeGa BoNK';       desc = 'Free 4 GB in a single bonk.';           test = { param($s) $s.bestMB -ge 4096 } },
    @{ id = 'repeat';  name = 'RePeaT oFFeNDeR'; desc = 'Bonk the same app 10 times.';           test = { param($s) @($s.perApp.Values | Where-Object { $_ -ge 10 }).Count -gt 0 } },
    @{ id = 'night';   name = 'NiGHT oWL';       desc = 'Bonk between midnight and 4 AM.';       test = { param($s) [bool]$s.night } },
    @{ id = 'parole';  name = 'PaRoLe BoaRD';    desc = 'Grant parole 50 times.';                test = { param($s) $s.paroles -ge 50 } },
    @{ id = 'gamer';   name = 'GaMe oN';         desc = 'Let Auto Game Mode bonk for you.';      test = { param($s) $s.gameBonks -ge 1 } },
    @{ id = 'boot';    name = 'BooT CaMP';       desc = 'Jail an app from startup.';             test = { param($s) $s.startupJails -ge 1 } },
    @{ id = 'sheriff'; name = 'SHeRiFF';         desc = 'Add a Hog Radar suspect to your list.'; test = { param($s) $s.radarAdds -ge 1 } }
)

function Read-Stats {
    $s = @{ apps = 0; bonks = 0; freedMB = 0; paroles = 0; bestMB = 0; night = $false; gameBonks = 0; startupJails = 0; radarAdds = 0
            since = (Get-Date -Format 'yyyy-MM-dd'); perApp = @{}; unlocked = @(); last = $null }
    if (Test-Path -LiteralPath $StatsFile) {
        try {
            $j = Get-Content -LiteralPath $StatsFile -Raw -Encoding UTF8 | ConvertFrom-Json
            foreach ($p in $j.PSObject.Properties) {
                if ($p.Name -eq 'perApp') { $h = @{}; foreach ($q in $p.Value.PSObject.Properties) { $h[$q.Name] = [int]$q.Value }; $s.perApp = $h }
                elseif ($p.Name -eq 'unlocked') { $s.unlocked = @($p.Value | Where-Object { $_ }) }
                else { $s[$p.Name] = $p.Value }
            }
        } catch { }
    }
    $s
}

function Get-FreeMB { try { [int][math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1024) } catch { 0 } }

function Add-Stats([string]$Kind, $Data) {
    $s = Read-Stats
    switch ($Kind) {
        'bonk' {
            $s.bonks++
            $s.apps += @($Data.Labels).Count
            $s.freedMB += [int]$Data.EstMB
            if ([int]$Data.EstMB -gt [int]$s.bestMB) { $s.bestMB = [int]$Data.EstMB }
            foreach ($l in $Data.Labels) { $s.perApp[$l] = 1 + [int]$s.perApp[$l] }
            if ((Get-Date).Hour -lt 4) { $s.night = $true }
            if ($Data.Game) { $s.gameBonks++ }
            $s.last = @{ before = $Data.Before; after = $Data.After; apps = @($Data.Labels).Count; estMB = [int]$Data.EstMB; when = (Get-Date -Format 'yyyy-MM-dd HH:mm') }
        }
        'parole'  { $s.paroles += [int]$Data }
        'startup' { $s.startupJails++ }
        'radar'   { $s.radarAdds++ }
    }
    $new = @()
    foreach ($a in $Achievements) {
        if ($s.unlocked -notcontains $a.id -and (& $a.test $s)) { $s.unlocked = @($s.unlocked) + $a.id; $new += $a }
    }
    $s | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $StatsFile -Encoding UTF8
    foreach ($a in $new) {
        Write-Log ('achievement unlocked: {0}' -f $a.name)
        if ($script:NotifyIcon) { Show-Balloon ('ACHIEVEMENT UNLOCKED: {0}' -f $a.name) $a.desc }
        if ($script:Ui -and -not $script:Ui.IsDisposed) { $script:Ui.ShowAchievement($a.name) }
    }
    Push-UiStats
}

function Push-UiStats {
    if (-not $script:Ui -or $script:Ui.IsDisposed) { return }
    $s = Read-Stats
    $last = $s.last
    $script:Ui.SetStats(
        [string[]]@('apps', 'freed', 'bonks', 'paroles', 'since', 'lastBefore', 'lastAfter', 'lastApps', 'lastEst', 'lastWhen'),
        [string[]]@(('{0:N0}' -f [int]$s.apps), (Format-Size $s.freedMB), ('{0:N0}' -f [int]$s.bonks), ('{0:N0}' -f [int]$s.paroles), [string]$s.since,
            $(if ($last) { [string]$last.before } else { '' }), $(if ($last) { [string]$last.after } else { '' }),
            $(if ($last) { [string]$last.apps } else { '' }), $(if ($last) { Format-Size $last.estMB } else { '' }), $(if ($last) { [string]$last.when } else { '' })))
    $top = @($s.perApp.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 8)
    $script:Ui.SetShame([string[]]@($top | ForEach-Object { $_.Key }), [int[]]@($top | ForEach-Object { [int]$_.Value }))
    $script:Ui.SetAchievements([string[]]@($Achievements | ForEach-Object { $_.name }), [string[]]@($Achievements | ForEach-Object { $_.desc }),
        [bool[]]@($Achievements | ForEach-Object { $s.unlocked -contains $_.id }))
}

# Both return a status message; -Quiet skips the tray balloon (the window shows it instead).
# $script:Busy stops game mode and timers from starting a second bonk mid-bonk.
$script:Busy = $false

function Invoke-Bonk([switch]$Quiet, [string]$Only) {
    $script:Busy = $true
    try { Invoke-BonkCore -Quiet:$Quiet -Only $Only } finally { $script:Busy = $false }
}

function Invoke-Release([switch]$Quiet, [string]$Only, [string[]]$Labels) {
    $script:Busy = $true
    try { Invoke-ReleaseCore -Quiet:$Quiet -Only $Only -Labels $Labels } finally { $script:Busy = $false }
}

function Invoke-BonkCore([switch]$Quiet, [string]$Only) {
    if ($Only) { $targets = @(Get-Rows | Where-Object { $_.Label -eq $Only -and $_.Running }) }
    else { $targets = @(Get-Rows | Where-Object { $_.Enabled -and $_.Running }) }
    $PoliteWaitMs = 1000 * [int]$script:Settings.polite
    if (-not $targets.Count) {
        $msg = 'Nothing to bonk. Your RAM is living its best life.'
        if (-not $Quiet) { Show-Balloon 'Nothing to bonk' $msg }
        return $msg
    }
    Set-Tip 'Bonk Mode: BONKING...'
    Write-Log ('BONK: ' + (($targets | ForEach-Object { $_.Label }) -join ', '))

    $freeBefore = Get-FreeMB
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
    $freeAfter = Get-FreeMB
    $hitLabels = @($targets | ForEach-Object { $_.Label } | Where-Object { $dodgers -notcontains $_ })
    $hitMB = 0; foreach ($x in $targets) { if ($dodgers -notcontains $x.Label) { $hitMB += $x.MB } }
    if ($hitLabels.Count) {
        try { Add-Stats 'bonk' @{ Labels = $hitLabels; EstMB = $hitMB; Before = $freeBefore; After = $freeAfter; Game = [bool]$script:GameBonking } }
        catch { Write-Log ('stats failed: ' + $_.Exception.Message) }
    }

    $msg = '{0} apps sent to RAM jail. About {1} freed. Go get ''em.' -f ($targets.Count - $dodgers.Count), (Format-Size $mb)
    if ($dodgers.Count) {
        $msg += ' Dodged the bonk (probably need admin): ' + ($dodgers -join ', ')
        Write-Log ('dodged: ' + ($dodgers -join ', '))
    }
    if (@(Read-State).Count) { Enable-TurboPower }
    if (-not $Quiet) { Show-Balloon "BONK! $Hammer" $msg }
    Update-Tip
    $msg
}

function Invoke-ReleaseCore([switch]$Quiet, [string]$Only, [string[]]$Labels) {
    if ($Only) { $Labels = @($Only) }
    $all = @(Read-State)
    $jailed = @(if ($Labels) { $all | Where-Object { $Labels -contains $_.label } } else { $all })
    if (-not $jailed.Count) {
        Write-Log ('RELEASE: nothing matched (asked for: {0}; in jail: {1})' -f ($Labels -join ', '), ((@($all) | ForEach-Object { $_.label }) -join ', '))
        if (-not $Quiet) { Show-Balloon 'Jail is empty' 'Nobody to release.' }
        return 'Jail is empty. Nobody to release.'
    }
    $rows = @(Get-Rows)
    $back = @(); $stay = @()
    $keep = @(if ($Labels) { $all | Where-Object { $Labels -notcontains $_.label } })
    foreach ($j in $jailed) {
        $row = $rows | Where-Object { $_.Label -eq $j.label } | Select-Object -First 1
        if ($row -and $row.Running) { continue }   # already back on its own
        $specs = @(@($j.relaunch) | Where-Object { $_ -and $_.file })
        if (-not $specs.Count) { $stay += $j.label; continue }
        foreach ($s in $specs) {
            try {
                if ($s.args) { Start-Process -FilePath $s.file -ArgumentList $s.args -WindowStyle Minimized }
                else { Start-Process -FilePath $s.file -WindowStyle Minimized }
            } catch { Write-Log ('relaunch failed for {0}: {1}' -f $j.label, $_.Exception.Message) }
        }
        $back += $j.label
    }
    Save-State $keep
    if (-not @(Read-State).Count) { Restore-PowerPlan }
    Write-Log ('RELEASE: ' + ($back -join ', '))
    if ($back.Count) { try { Add-Stats 'parole' $back.Count } catch { Write-Log ('stats failed: ' + $_.Exception.Message) } }
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

function Get-RelaunchSummary($Entry) {
    $prop = $Entry.PSObject.Properties['relaunch']
    if ($prop -and $null -eq $prop.Value) { return 'Nothing to do. Windows or the app brings it back on its own.' }
    if (-not $prop -or $prop.Value -is [string]) { return 'Reopens the same program it was running.' }
    'Reopens ' + ((@($prop.Value) | ForEach-Object { Split-Path -Leaf ([string]$_.file) }) -join ' + ')
}

function Push-UiApps {
    $ui = $script:Ui
    if (-not $ui -or $ui.IsDisposed) { return }
    $rows = @(Get-Rows)
    $jailed = @(Read-State | ForEach-Object { [string]$_.label })
    # Running (biggest first), then jailed, then the sleeping rest.
    $show = @($rows | Where-Object { $_.Running } | Sort-Object MB -Descending) +
            @($rows | Where-Object { -not $_.Running -and $jailed -contains $_.Label }) +
            @($rows | Where-Object { -not $_.Running -and $jailed -notcontains $_.Label })
    $ui.SetApps(
        [string[]]@($show | ForEach-Object { $_.Label }),
        [int[]]@($show | ForEach-Object { [int]$_.MB }),
        [bool[]]@($show | ForEach-Object { $_.Enabled }),
        [bool[]]@($show | ForEach-Object { $_.Running }),
        [bool[]]@($show | ForEach-Object { $jailed -contains $_.Label }),
        [int[]]@($show | ForEach-Object { @($_.Procs).Count }),
        [string[]]@($show | ForEach-Object {
            $n = @(@($_.Entry.names) | Where-Object { $_ }) + @(@($_.Entry.paths) | Where-Object { $_ } | ForEach-Object { "anything in $_" })
            if (@($_.Procs).Count) {
                $live = @($_.Procs | ForEach-Object { $_.Name } | Select-Object -Unique)
                ($live -join ', ') + $(if ($n.Count -gt $live.Count) { '  (watching: ' + ($n -join ', ') + ')' } else { '' })
            } else { $n -join ', ' }
        }),
        [string[]]@($show | ForEach-Object { Get-RelaunchSummary $_.Entry }),
        [int[]]@($show | ForEach-Object { [int]($_.CPU * 10) }),
        [int[]]@($show | ForEach-Object { [int]$_.IOKB }))
}

function Push-UiSettings {
    $ui = $script:Ui
    foreach ($k in $script:Settings.Keys) { $ui.SetSetting($k, [string]$script:Settings[$k]) }
    $ui.SetSetting('autostart', [string](Test-Autostart))
}

function Invoke-UiAction([scriptblock]$Action) {
    $ui = $script:Ui
    try { $msg = [string](& $Action) }
    catch { $msg = 'Misfire: ' + $_.Exception.Message; Write-Log ('ui action failed: ' + $_.Exception.Message) }
    if ($ui -and -not $ui.IsDisposed) {
        Push-UiApps
        $ui.SetStatus($msg)
        $ui.SetBusy($false)
    }
}

function Restart-Tray {
    $argLine = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -STA -File "{0}" -Open -WaitPid {1}' -f $PSCommandPath, $PID
    Start-Process (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList $argLine -WindowStyle Hidden
    if ($script:Ui -and -not $script:Ui.IsDisposed) { $script:Ui.Close() }
    if ($script:NotifyIcon) { $script:NotifyIcon.Visible = $false }
    [System.Windows.Forms.Application]::ExitThread()
}

# Pull the latest release from GitHub. A git clone fast-forwards; a zip install
# downloads the repo zip and copies only $UpdateFiles. Personal files are never touched.
function Invoke-SelfUpdate([switch]$NoRestart) {
    $ui = $script:Ui
    $step = { param($p, $m) if ($ui -and -not $ui.IsDisposed) { $ui.SetUpdateProgress($p, $m) }; Wait-Ui 350 }
    try {
        & $step 8 'Locating BONK.EXE...'
        $git = Get-Command git -ErrorAction SilentlyContinue
        if ($git -and (Test-Path -LiteralPath (Join-Path $AppDir '.git'))) {
            & $step 35 'git pull --ff-only'
            $out = & git -C $AppDir pull --ff-only 2>&1 | Out-String
            if ($LASTEXITCODE -ne 0) { throw ('git pull failed: ' + $out.Trim()) }
            & $step 80 'Fast-forwarded. Patching bytes...'
        } else {
            $work = Join-Path $env:LOCALAPPDATA 'BonkMode\update'
            [void][System.IO.Directory]::CreateDirectory($work)
            $zip = Join-Path $work 'bonk-mode-main.zip'
            & $step 20 'Downloading the crack...'
            $wc = New-Object System.Net.WebClient
            $wc.Headers['User-Agent'] = 'BonkMode'
            $wc.DownloadFileAsync([Uri]$RepoZip, $zip)
            while ($wc.IsBusy) { Wait-Ui 50 }
            & $step 55 'Unpacking...'
            Expand-Archive -LiteralPath $zip -DestinationPath $work -Force
            $src = Join-Path $work 'bonk-mode-main'
            & $step 75 'Patching bytes...'
            foreach ($f in $UpdateFiles) {
                $from = Join-Path $src $f
                if (Test-Path -LiteralPath $from) { Copy-Item -LiteralPath $from -Destination (Join-Path $AppDir $f) -Force }
            }
        }
        $newVersion = (Get-Content -LiteralPath $VersionFile -Raw).Trim()
        if ($NoRestart) {
            Write-Log ('update installed: v{0}' -f $newVersion)
            return ('Updated to v{0}. Restart Bonk Mode to use it.' -f $newVersion)
        }
        & $step 100 'Done. Restarting with the new version...'
        Write-Log ('update installed: v{0}, restarting' -f $newVersion)
        Wait-Ui 600
        Restart-Tray
        'Update installed. Restarting...'
    } catch {
        Write-Log ('update failed: ' + $_.Exception.Message)
        'Update failed: ' + $_.Exception.Message
    }
}

function Test-UpdateQuiet {
    if ($script:Settings.autoUpdate -ne 'True') { return }
    try {
        $wc = New-Object System.Net.WebClient
        $wc.Headers['User-Agent'] = 'BonkMode'
        $remote = $wc.DownloadString($RawBase + 'VERSION?t=' + [DateTime]::UtcNow.Ticks).Trim()
        if ([version]$remote -gt [version]$Version -and $script:NotifiedVersion -ne $remote) {
            $script:NotifiedVersion = $remote
            Show-Balloon "Bonk Mode v$remote is out" 'Double-click the hammer and open UPDATES (key 7) to install it.'
        }
    } catch { }
}

# --- Auto Game Mode loop ----------------------------------------------------

$script:GameActive = $false
$script:GameName = $null
$script:GameGoneSince = $null
$script:PendingDue = $null
$script:AutoBonked = @()
$script:InGameTick = $false

function Update-UiGame {
    if (-not $script:Ui -or $script:Ui.IsDisposed) { return }
    $mode = [string]$script:Settings.gameMode
    $text = if ($mode -eq 'off') { 'oFF' }
            elseif ($script:PendingDue) { 'BoNKiNG iN ' + [math]::Max(0, [math]::Ceiling(($script:PendingDue - (Get-Date)).TotalSeconds)) + '...' }
            elseif ($script:GameActive) { 'PLaYiNG ' + $script:GameName.ToUpper() }
            else { $mode.ToUpper() + ' :: WaTCHiNG' }
    $script:Ui.SetGameStatus($text)
}

function Start-GameBonk {
    $script:PendingDue = $null
    $before = @(Read-State | ForEach-Object { [string]$_.label })
    $script:GameBonking = $true
    try { $msg = Invoke-Bonk -Quiet } finally { $script:GameBonking = $false }
    $after = @(Read-State | ForEach-Object { [string]$_.label })
    $script:AutoBonked = @($after | Where-Object { $before -notcontains $_ })
    Write-Log ('game mode bonk for {0}: {1} [jailed by game mode: {2}]' -f $script:GameName, $msg, ($script:AutoBonked -join ', '))
    Show-Balloon ("Game on: {0}" -f $script:GameName) $msg
    if ($script:Ui -and -not $script:Ui.IsDisposed) { Push-UiApps; $script:Ui.SetStatus('game mode: ' + $msg) }
    Update-UiGame
}

function Stop-GameSession {
    Write-Log ('game ended: {0} [paroling: {1}]' -f $script:GameName, (@($script:AutoBonked) -join ', '))
    $labels = @($script:AutoBonked)
    $script:GameActive = $false; $script:PendingDue = $null; $script:GameGoneSince = $null; $script:AutoBonked = @()
    if ($labels.Count) {
        $msg = Invoke-Release -Quiet -Labels $labels
        Show-Balloon ('GG! {0} closed' -f $script:GameName) $msg
        if ($script:Ui -and -not $script:Ui.IsDisposed) { Push-UiApps; $script:Ui.SetStatus('gg. ' + $msg) }
    }
    Update-UiGame
}

function Invoke-GameTick {
    $mode = [string]$script:Settings.gameMode
    if ($mode -eq 'off' -or $script:Busy -or $script:InGameTick) { return }
    $script:InGameTick = $true
    try {
        $new = @(Find-NewGames)
        if ($new.Count -and -not $script:GameActive) {
            $script:GameActive = $true; $script:GameName = $new[0]; $script:GameGoneSince = $null
            Write-Log ('game detected: {0}' -f $script:GameName)
            if ($script:Ui -and -not $script:Ui.IsDisposed) { $script:Ui.ShowGameBanner($script:GameName) }
            if ($mode -eq 'auto') { Start-GameBonk }
            else {
                $script:PendingDue = (Get-Date).AddSeconds(5)
                Show-Balloon ("Game detected: {0}" -f $script:GameName) 'Bonking your background apps in 5 seconds. Click here to cancel.'
            }
        }
        if ($script:GameActive) {
            if ($script:GamePids.Count -gt 0) { $script:GameGoneSince = $null }
            elseif (-not $script:GameGoneSince) { $script:GameGoneSince = Get-Date }
            elseif (((Get-Date) - $script:GameGoneSince).TotalSeconds -ge 10) { Stop-GameSession }
        }
        Update-UiGame
    } catch { Write-Log ('game tick failed: ' + $_.Exception.Message) }
    finally { $script:InGameTick = $false }
}

function Invoke-CountdownTick {
    if (-not $script:PendingDue) { return }
    Update-UiGame
    if ((Get-Date) -ge $script:PendingDue -and -not $script:Busy) { Start-GameBonk }
}

function Stop-GameCountdown {
    if (-not $script:PendingDue) { return }
    $script:PendingDue = $null
    Write-Log ('game mode bonk cancelled for {0}' -f $script:GameName)
    Show-Balloon 'Bonk cancelled' ("Everything stays open while you play {0}." -f $script:GameName)
    Update-UiGame
}

# --- Hog Radar loop ---------------------------------------------------------

$script:Suspects = @()
$script:NotifiedSuspects = @{}

function Push-UiSuspects {
    if (-not $script:Ui -or $script:Ui.IsDisposed) { return }
    $s = @($script:Suspects | Sort-Object MB -Descending)
    $script:Ui.SetSuspects([string[]]@($s | ForEach-Object { $_.Name }), [string[]]@($s | ForEach-Object { $_.Label }),
        [int[]]@($s | ForEach-Object { $_.MB }), [int[]]@($s | ForEach-Object { $_.Procs }), [int[]]@($s | ForEach-Object { [int]($_.CPU * 10) }))
}

function Invoke-RadarTick {
    if ($script:Settings.radar -ne 'True' -or $script:Busy) { $script:Suspects = @(); Push-UiSuspects; return }
    try {
        $script:Suspects = @(Get-Suspects)
        $fresh = @($script:Suspects | Where-Object { -not $script:NotifiedSuspects.ContainsKey($_.Name) })
        foreach ($s in $fresh) { $script:NotifiedSuspects[$s.Name] = $true }
        if ($fresh.Count -and -not $script:GameActive) {
            $top = $fresh | Sort-Object MB -Descending | Select-Object -First 1
            $more = if ($fresh.Count -gt 1) { ' (+{0} more)' -f ($fresh.Count - 1) } else { '' }
            Show-Balloon ('NEW SUSPECT: {0}{1}' -f $top.Label, $more) ('{0} is eating {1} and is not on your bonk list. Double-click the hammer to sentence or spare it.' -f $top.Label, (Format-Size $top.MB))
        }
        Push-UiSuspects
    } catch { Write-Log ('radar failed: ' + $_.Exception.Message) }
}

function Push-UiStartup {
    if (-not $script:Ui -or $script:Ui.IsDisposed) { return }
    $items = @(Get-StartupItems | Sort-Object @{ e = { $_.Immune } }, @{ e = { -$_.MB } }, Name)
    $script:Ui.SetStartup([string[]]@($items | ForEach-Object { $_.Key }), [string[]]@($items | ForEach-Object { $_.Name }),
        [string[]]@($items | ForEach-Object { $_.Source }), [string[]]@($items | ForEach-Object { [string]$_.Command }),
        [bool[]]@($items | ForEach-Object { $_.Enabled }), [bool[]]@($items | ForEach-Object { $_.Immune }),
        [int[]]@($items | ForEach-Object { $_.MB }), [bool[]]@($items | ForEach-Object { $_.Hive -eq 'HKLM' }))
}

# --- Remote control: hotkeys and -Bonk / -Parole / -SetProfile from other launches ---

$CmdFile = Join-Path $env:LOCALAPPDATA 'BonkMode\cmd.txt'
$script:Hotkeys = $null

function Invoke-TrayCommand([string]$Command) {
    try {
        switch -Regex ($Command) {
            '^bonk$'        { if (-not $script:Busy) { Invoke-Bonk | Out-Null; if ($script:Ui -and -not $script:Ui.IsDisposed) { Push-UiApps } } }
            '^parole$'      { if (-not $script:Busy) { Invoke-Release | Out-Null; if ($script:Ui -and -not $script:Ui.IsDisposed) { Push-UiApps } } }
            '^profile:(.+)$' {
                Switch-Profile $Matches[1]
                Show-Balloon ('Profile: {0}' -f $Matches[1]) 'Your checkboxes switched with it.'
                if ($script:Ui -and -not $script:Ui.IsDisposed) { $script:Ui.SetSetting('profile', $Matches[1]); Push-UiApps }
            }
            default         { Open-BonkWindow }
        }
    } catch { Write-Log ('command {0} failed: {1}' -f $Command, $_.Exception.Message) }
}

function Read-TrayCommand {
    if (-not (Test-Path -LiteralPath $CmdFile)) { return 'open' }
    $cmd = (Get-Content -LiteralPath $CmdFile -Raw).Trim()
    [System.IO.File]::Delete($CmdFile)
    if ($cmd) { $cmd } else { 'open' }
}

function Set-Hotkeys([bool]$On) {
    if ($script:Hotkeys) { $script:Hotkeys.Dispose(); $script:Hotkeys = $null }
    if (-not $On) { return }
    try { Import-BonkUi } catch { Write-Log ('hotkeys unavailable: ' + $_.Exception.Message); return }
    $script:Hotkeys = New-Object BonkMode.HotkeyWindow
    $ok = @()
    foreach ($k in @(@(1, 0x42, 'Ctrl+Alt+B'), @(2, 0x50, 'Ctrl+Alt+P'), @(3, 0x4B, 'Ctrl+Alt+K'))) {
        if ($script:Hotkeys.Register($k[0], 3, $k[1])) { $ok += $k[2] } else { Write-Log ('hotkey {0} is taken by another app' -f $k[2]) }
    }
    $script:Hotkeys.add_Pressed({ param($id) switch ($id) { 1 { Invoke-TrayCommand 'bonk' } 2 { Invoke-TrayCommand 'parole' } 3 { Invoke-TrayCommand 'open' } } })
    Write-Log ('hotkeys: ' + ($ok -join ', '))
}

function New-BonkWindow {
    Import-BonkUi
    $ui = New-Object BonkMode.KeygenForm
    if ($script:NotifyIcon) { $ui.Icon = $script:NotifyIcon.Icon }
    $ui.SetVersion($Version, $RawBase, $RepoUrl)
    $ui.add_ToggleClicked({
        param($label, $on)
        try {
            $entry = @($script:Config.apps) | Where-Object { $_.label -eq $label } | Select-Object -First 1
            if ($entry) { $entry.enabled = [bool]$on; Save-Config }
        } catch { Write-Log ('toggle failed: ' + $_.Exception.Message) }
    })
    $ui.add_BonkClicked({ Invoke-UiAction { Invoke-Bonk -Quiet } })
    $ui.add_ParoleClicked({ Invoke-UiAction { Invoke-Release -Quiet } })
    $ui.add_BonkOneClicked({ param($label) Invoke-UiAction { Invoke-Bonk -Quiet -Only $label }.GetNewClosure() })
    $ui.add_ParoleOneClicked({ param($label) Invoke-UiAction { Invoke-Release -Quiet -Only $label }.GetNewClosure() })
    $ui.add_UpdateClicked({ Invoke-UiAction { Invoke-SelfUpdate } })
    $ui.add_SettingChanged({
        param($key, $value)
        try {
            if ($key -eq 'autostart') { Set-Autostart ($value -eq 'True') }
            elseif ($key -eq 'profile') {
                Switch-Profile $value
                Push-UiApps
                $script:Ui.SetStatus(('profile: {0}. your checkboxes switched with it.' -f $value))
            }
            else { $script:Settings[$key] = $value; Save-Settings }
            if ($key -eq 'powerPlan' -and $value -ne 'True') { Restore-PowerPlan }
            if ($key -eq 'hotkeys') { Set-Hotkeys ($value -eq 'True') }
            if ($key -eq 'gameMode') { if ($value -eq 'off') { $script:PendingDue = $null }; Update-UiGame }
        } catch { Write-Log ('setting failed: ' + $_.Exception.Message) }
    })
    $ui.add_EditClicked({ Start-Process notepad.exe -ArgumentList ('"{0}"' -f $ConfigFile) })
    $ui.add_GameCancelClicked({ Stop-GameCountdown })
    $ui.add_StartupRefresh({ try { Push-UiStartup; $script:Ui.SetStatus('startup list refreshed.') } catch { Write-Log ('startup refresh failed: ' + $_.Exception.Message) } })
    $ui.add_StartupToggle({
        param($key, $enable)
        $msg = try { Set-StartupItem $key $enable; $n = $key.Substring($key.IndexOf('|') + 1); if ($enable) { "$n will launch at login again." } else { "$n is jailed from startup. Faster boot unlocked." } } catch { 'Startup change failed: ' + $_.Exception.Message }
        if ($script:Ui -and -not $script:Ui.IsDisposed) { Push-UiStartup; $script:Ui.SetStatus($msg); $script:Ui.SetBusy($false) }
    })
    $ui.add_SuspectAction({
        param($name, $action)
        try {
            $s = @($script:Suspects) | Where-Object { $_.Name -eq $name } | Select-Object -First 1
            if ($action -eq 'add') { Add-Suspect $name $(if ($s) { $s.Label } else { $name }) }
            else { Set-SpareForever $name }
            $script:Suspects = @($script:Suspects | Where-Object { $_.Name -ne $name })
            Push-UiSuspects
            Push-UiApps
        } catch { Write-Log ('suspect action failed: ' + $_.Exception.Message) }
    })
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
        Push-UiSettings
        Push-UiApps
        Push-UiSuspects
        Push-UiStartup
        Push-UiStats
        Update-UiGame
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

    $gm = New-Object System.Windows.Forms.ToolStripMenuItem ('Auto Game Mode: ' + $script:Settings.gameMode)
    foreach ($m in 'off', 'ask', 'auto') {
        $it = New-Object System.Windows.Forms.ToolStripMenuItem $m
        $it.Checked = ($script:Settings.gameMode -eq $m)
        $it.Tag = 'gm:' + $m
        $it.add_Click({
            param($s, $e)
            $v = $s.Tag.Substring(3)
            $script:Settings.gameMode = $v; Save-Settings
            if ($v -eq 'off') { $script:PendingDue = $null }
            if ($script:Ui -and -not $script:Ui.IsDisposed) { $script:Ui.SetSetting('gameMode', $v) }
            Update-UiGame
        })
        [void]$gm.DropDownItems.Add($it)
    }
    [void]$menu.Items.Add($gm)

    $pm = New-Object System.Windows.Forms.ToolStripMenuItem ('Profile: ' + $script:Settings.profile)
    foreach ($m in $ProfileNames) {
        $it = New-Object System.Windows.Forms.ToolStripMenuItem $m
        $it.Checked = ($script:Settings.profile -eq $m)
        $it.Tag = 'pf:' + $m
        $it.add_Click({
            param($s, $e)
            try {
                $v = $s.Tag.Substring(3)
                Switch-Profile $v
                if ($script:Ui -and -not $script:Ui.IsDisposed) { $script:Ui.SetSetting('profile', $v); Push-UiApps }
                Update-Tip
            } catch { Write-Log ('profile switch failed: ' + $_.Exception.Message) }
        })
        [void]$pm.DropDownItems.Add($it)
    }
    [void]$menu.Items.Add($pm)

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

if ($UpdateNow) {
    Invoke-SelfUpdate -NoRestart
    return
}

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
    $ui = New-BonkWindow
    $script:Ui = $ui
    # Screenshots always show default settings, never this machine's.
    foreach ($k in $DefaultSettings.Keys) { $ui.SetSetting($k, [string]$DefaultSettings[$k]) }
    $ui.SetSetting('autostart', 'False')
    # Demo inmates so screenshots never show anyone's real app list.
    $demo = @(
        @('Google Chrome', 2310, $false, $true, $false, 24, 'chrome', 'Reopens chrome.exe'),
        @('Adobe Creative Cloud', 812, $true, $true, $false, 11, 'Creative Cloud, CoreSync, Adobe Desktop Service', 'Nothing to do. It comes back on its own.'),
        @('Microsoft Teams', 690, $true, $true, $false, 7, 'ms-teams', 'Reopens ms-teams.exe'),
        @('Discord (voice chat)', 540, $false, $true, $false, 6, 'Discord', 'Reopens Update.exe'),
        @('OneDrive', 260, $true, $true, $false, 2, 'OneDrive', 'Reopens OneDrive.exe'),
        @('Microsoft Edge (background)', 410, $true, $true, $false, 9, 'msedge', 'Reopens msedge.exe'),
        @('Spotify (music)', 330, $false, $true, $false, 5, 'Spotify', 'Reopens spotify:'),
        @('Razer Synapse', 220, $false, $true, $false, 4, 'RazerAppEngine', 'Reopens the same program.'),
        @('Windows Widgets / Start feed', 120, $true, $true, $false, 3, 'WidgetBoard, WidgetService', 'Nothing to do. It comes back on its own.'),
        @('Slack', 0, $true, $false, $true, 0, 'slack', 'Reopens slack.exe'),
        @('Zoom', 0, $true, $false, $true, 0, 'Zoom', 'Reopens Zoom.exe'),
        @('Dropbox', 0, $true, $false, $true, 0, 'Dropbox', 'Reopens Dropbox.exe'),
        @('Phone Link', 0, $true, $false, $true, 0, 'PhoneExperienceHost', 'Nothing to do. It comes back on its own.'),
        @('Epic Games Launcher', 0, $false, $false, $false, 0, 'EpicGamesLauncher', 'Reopens the same program.')
    )
    foreach ($round in 1..6) {
        $ui.SetApps([string[]]($demo | ForEach-Object { $_[0] }), [int[]]($demo | ForEach-Object { [int]($_[1] * (0.85 + 0.05 * $round)) }),
            [bool[]]($demo | ForEach-Object { $_[2] }), [bool[]]($demo | ForEach-Object { $_[3] }), [bool[]]($demo | ForEach-Object { $_[4] }),
            [int[]]($demo | ForEach-Object { $_[5] }), [string[]]($demo | ForEach-Object { $_[6] }), [string[]]($demo | ForEach-Object { $_[7] }),
            [int[]]($demo | ForEach-Object { if ($_[3]) { [int](($_[1] * 7 + $round * 13) % 180) } else { 0 } }), [int[]]($demo | ForEach-Object { if ($_[3]) { [int](($_[1] * 3 + $round * 97) % 2400) } else { 0 } }))
    }
    $ui.SetSuspects([string[]]@('GameBoostPro', 'WeatherWidgetPlus'), [string[]]@('GameBoost Pro (totally legit)', 'Weather Widget Plus'), [int[]]@(940, 455), [int[]]@(3, 1), [int[]]@(184, 12))
    $ui.SetStartup([string[]]@('hkcu-run|Steam', 'hkcu-run|Discord', 'hklm-run32|Adobe Creative Cloud', 'user-folder|Spotify.lnk', 'hkcu-run|GameBoostPro', 'hklm-run|SecurityHealth', 'hkcu-run|Teams', 'hkcu-run|OneDrive'),
        [string[]]@('Steam', 'Discord', 'Adobe Creative Cloud', 'Spotify.lnk', 'GameBoostPro', 'SecurityHealth', 'Teams', 'OneDrive'),
        [string[]]@('HKCU RuN', 'HKCU RuN', 'HKLM RuN (32)', 'STaRTuP FoLDeR', 'HKCU RuN', 'HKLM RuN', 'HKCU RuN', 'HKCU RuN'),
        [string[]]@('steam.exe -silent', 'Update.exe --processStart Discord.exe', 'Creative Cloud.exe --showwindow=false', 'Spotify.exe --minimized', 'GameBoostPro.exe /tray', 'SecurityHealthSystray.exe', 'ms-teams.exe msteams:system-initiated', 'OneDrive.exe /background'),
        [bool[]]@($true, $true, $false, $true, $false, $true, $false, $true), [bool[]]@($false, $false, $false, $false, $false, $true, $false, $false),
        [int[]]@(221, 540, 0, 330, 0, 14, 0, 260), [bool[]]@($false, $false, $true, $false, $false, $true, $false, $false))
    $changelog = if (Test-Path -LiteralPath (Join-Path $AppDir 'CHANGELOG.md')) { Get-Content -LiteralPath (Join-Path $AppDir 'CHANGELOG.md') -Raw -Encoding UTF8 } else { '' }
    $ui.PreviewUpdate($Version, $changelog)
    $run = { param($sec) for ($i = 0; $i -lt [int]($sec / 0.03); $i++) { $ui.Advance(0.03) } }
    $shoot = {
        param($name)
        $bmp = New-Object System.Drawing.Bitmap $ui.ClientSize.Width, $ui.ClientSize.Height
        $ui.DrawToBitmap($bmp, (New-Object System.Drawing.Rectangle 0, 0, $bmp.Width, $bmp.Height))
        $file = Join-Path $Preview ('{0}.png' -f $name)
        $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $file
    }
    $ui.SetGameStatus('ASK :: WaTCHiNG')
    & $run 2.6; & $shoot 'home'
    $ui.SetGameStatus('BoNKiNG iN 3...'); $ui.ShowGameBanner('SuperBonkQuest'); & $run 0.5; & $shoot 'gamemode'
    $ui.SetGameStatus('PLaYiNG SUPERBONKQUEST'); & $run 2.5
    $ui.PreviewBonk(); & $run 0.62; & $shoot 'bonk'
    while ($ui.Busy) { $ui.Advance(0.03) }
    & $run 0.35; & $shoot 'jailed'
    & $run 2.5
    $ui.ShowScreen(1); & $run 4.2; & $shoot 'cellblock'
    $ui.ShowScreen(2); & $run 0.5; & $shoot 'dossier'
    $ui.ShowScreen(3); & $run 1.0; & $shoot 'startup'
    $ui.SetStats([string[]]@('apps', 'freed', 'bonks', 'paroles', 'since', 'lastBefore', 'lastAfter', 'lastApps', 'lastEst', 'lastWhen'),
        [string[]]@('137', '48.6 GB', '21', '19', '2026-10-10', '9830', '13420', '6', '3.9 GB', '2026-10-11 21:14'))
    $ui.SetShame([string[]]@('Adobe Creative Cloud', 'Microsoft Teams', 'OneDrive', 'Windows Widgets / Start feed', 'Microsoft Edge (background)', 'Slack'), [int[]]@(21, 19, 17, 12, 9, 4))
    $ui.SetAchievements([string[]]@($Achievements | ForEach-Object { $_.name }), [string[]]@($Achievements | ForEach-Object { $_.desc }),
        [bool[]]@($Achievements | ForEach-Object { @('first', 'squad', 'hammer', 'gb10', 'repeat', 'gamer', 'boot', 'sheriff') -contains $_.id }))
    $ui.ShowScreen(4); & $run 1.2; & $shoot 'stats'
    $ui.ShowScreen(5); & $run 0.5; & $shoot 'settings'
    $ui.ShowScreen(6); & $run 0.5; & $shoot 'updates'
    $ui.SetUpdateProgress(62, 'Patching bytes...'); & $run 0.4; & $shoot 'patching'
    $ui.SetUpdateProgress(-1, '')
    $ui.ShowScreen(7); & $run 1.3; & $shoot 'greetz'
    $ui.Dispose()
    return
}

# Single instance. A second launch (double-clicking the shortcut) asks the
# running tray to open the window instead.
$createdNew = $false
$script:Mutex = New-Object System.Threading.Mutex($true, $MutexName, [ref]$createdNew)
if (-not $createdNew) {
    $cmd = if ($Bonk) { 'bonk' } elseif ($Parole) { 'parole' } elseif ($SetProfile) { 'profile:' + $SetProfile.ToLower() } else { 'open' }
    try { [void][System.IO.Directory]::CreateDirectory((Split-Path $CmdFile)); Set-Content -LiteralPath $CmdFile -Value $cmd -Encoding ASCII } catch { }
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
    try { if ($script:Ui -and -not $script:Ui.IsDisposed -and -not $script:Ui.Busy) { Push-UiApps } } catch { Write-Log ('refresh failed: ' + $_.Exception.Message) }
})

# Quiet update check: 2 minutes after start, then every 6 hours.
$script:UpdTimer = New-Object System.Windows.Forms.Timer
$script:UpdTimer.Interval = 120000
$script:UpdTimer.add_Tick({ $script:UpdTimer.Interval = 6 * 3600 * 1000; Test-UpdateQuiet })
$script:UpdTimer.Start()

# Auto Game Mode: look for new games every 4 seconds; the countdown ticks twice a second.
$script:GameTimer = New-Object System.Windows.Forms.Timer
$script:GameTimer.Interval = 4000
$script:GameTimer.add_Tick({ Invoke-GameTick })
$script:GameTimer.Start()
$script:CountdownTimer = New-Object System.Windows.Forms.Timer
$script:CountdownTimer.Interval = 500
$script:CountdownTimer.add_Tick({ Invoke-CountdownTick })
$script:CountdownTimer.Start()
$script:NotifyIcon.add_BalloonTipClicked({ if ($script:PendingDue) { Stop-GameCountdown } else { Open-BonkWindow } })

# Hog Radar: first sweep after 20 seconds, then every 30.
$script:RadarTimer = New-Object System.Windows.Forms.Timer
$script:RadarTimer.Interval = 20000
$script:RadarTimer.add_Tick({ $script:RadarTimer.Interval = 30000; Invoke-RadarTick })
$script:RadarTimer.Start()

$script:OpenPoll = New-Object System.Windows.Forms.Timer
$script:OpenPoll.Interval = 300
$script:OpenPoll.add_Tick({ if ($script:OpenEvent.WaitOne(0)) { Invoke-TrayCommand (Read-TrayCommand) } })
$script:OpenPoll.Start()

Set-Hotkeys ($script:Settings.hotkeys -eq 'True')
if ($Open) { Open-BonkWindow }
if ($Bonk) { Invoke-TrayCommand 'bonk' } elseif ($Parole) { Invoke-TrayCommand 'parole' } elseif ($SetProfile) { Invoke-TrayCommand ('profile:' + $SetProfile.ToLower()) }

# A crash while bonked must not strand the PC on the turbo power plan.
if (-not @(Read-State).Count) { Restore-PowerPlan }
# The CPU/disk sampler lives in the compiled UI assembly; load it now (cached after first run).
try { Import-BonkUi } catch { Write-Log ('native helper unavailable: ' + $_.Exception.Message) }

Write-Log 'tray started'
try {
    [System.Windows.Forms.Application]::Run((New-Object System.Windows.Forms.ApplicationContext))
} finally {
    $script:Timer.Stop()
    if ($script:NotifyIcon) { $script:NotifyIcon.Dispose() }
    $script:Mutex.ReleaseMutex()
    Write-Log 'tray stopped'
}
