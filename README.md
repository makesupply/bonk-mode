# 🔨 Bonk Mode

**Send your background apps to RAM jail before you game.**

You boot up a game. Meanwhile, behind your back, nineteen tray apps are syncing files, checking for updates, rendering widgets nobody asked for, and holding a gigabyte of RAM each "just in case." Bonk Mode is a little hammer in your Windows tray. Click it, hit **BONK 'EM ALL**, and they go to jail. When you're done gaming, grant them parole.

```
🔨  BONK 'EM ALL  (14 apps, ~5.8 GB)
    Release from RAM jail (0)
    ───────────────────────────────
    Running now (checked = gets bonked):
 ✔  Microsoft Edge (background)   1.3 GB
 ✔  OneDrive                      210 MB
    Discord (voice chat)          1.0 GB
 ✔  Adobe Creative Cloud          790 MB
    ...
```

## What it does

- **Bonk.** Every checked app gets a polite close first, the same message as clicking its X. Anything still standing 4 seconds later gets bonked for real.
- **Parole.** "Release from RAM jail" reopens what the last bonk closed, quietly and in the background where possible. It remembers the roster even if you restart the tray.
- **Shows you the damage.** The menu lists what's running and how much RAM each app is hogging, sorted biggest first.
- **You pick the inmates.** Checkboxes in the menu decide who gets bonked. The menu stays open so you can tick a bunch at once, and your picks are saved.
- **Diplomatic immunity.** Steam, Explorer, your GPU driver, Windows Security, PowerShell, Python and Windows core processes are never touched, even if you put them on the list. Bonk Mode will not bonk your game launcher out from under you.

## Install

Windows 10/11, built-in PowerShell 5.1. Nothing else to install.

```powershell
git clone https://github.com/makesupply/bonk-mode.git
cd bonk-mode
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

That adds **Bonk Mode** to your Start Menu and starts the hammer. It might hide in the `^` tray overflow, so drag it out where you can reach it. If you want it waiting for you at login, use "Start with Windows" in the menu.

## Make the list yours

The first run copies `bonk-list.example.json` to `bonk-list.json`, which is your personal list (gitignored). Click **Edit bonk list...** in the menu to add apps:

```json
{ "label": "Some Launcher", "enabled": true, "names": ["SomeLauncher", "SomeLauncherHelper"] }
```

| Field | Meaning |
|---|---|
| `names` | Process names without `.exe`. Use Task Manager's Details tab to find them. |
| `paths` | Optional wildcards on the exe path, e.g. `"*\\Corsair\\*"`, to catch every helper process. |
| `enabled` | `true` = gets bonked. |
| `relaunch` | Leave it out to reopen the same exe on parole. Use `null` for apps that come back on their own. Or give a list of `{ "file": ..., "args": ... }`. Environment variables like `%LOCALAPPDATA%` work. |

The starter list only checks the obvious freeloaders: cloud sync, chat apps for work, Adobe's tray army, Widgets and so on. Discord, Spotify, browsers, game launchers and peripheral software (iCUE, G HUB, Synapse) are listed but unchecked. Closing those mid-game can kill your voice chat, music, RGB, or mouse DPI, so that call is yours.

## Dry runs

Neither of these closes anything:

```powershell
.\bonk-mode.ps1 -ListOnly   # table of the list, what's running, and RAM per app
.\bonk-mode.ps1 -SelfTest   # builds the real menu and prints what parole would reopen
```

## Honest notes

- If you have plenty of RAM, the bigger win is usually fewer things waking up the CPU and disk mid-game, not the freed gigabytes themselves. Either way, fewer stutters.
- Some apps run elevated or have a watchdog that respawns them. If something dodges the bonk, the notification tells you who it was.
- "Polite close" means apps like browsers get a normal shutdown and restore their tabs. Anything with unsaved work in a window will still ask you about it, and then get bonked 4 seconds later. Save your stuff first.

## License

MIT. Bonk responsibly.
