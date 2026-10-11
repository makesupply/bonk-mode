# Bonk Mode changelog

## v2.6.2 - Store Apps Know The Way Home
- Fixed: paroling a Microsoft Store app added by Hog Radar (for example ChatGPT) failed with "Access is denied", because Windows doesn't allow launching Store apps straight from their install folder. Bonk Mode now recognizes them and tells you they come back when you open them.

## v2.6.1 - Where's The Volume Knob
- Speaker button in the top-right corner, next to minimize: one click turns the music on or off on any screen. It pulses while the tunes play and shows an X when muted. (The music button had been squeezed off the Home screen by the profile switcher.)

## v2.6.0 - Solitary Confinement
- Some apps restart themselves after a bonk (watchdogs, updaters, helper services). Mark them SOLO and they get shut straight back down within 3 seconds, every time, with every escape attempt counted.
- If you open a jailed app yourself (Start menu, taskbar, desktop), Bonk Mode sees Explorer started it and paroles it instead of fighting you.
- Apps that break out without SOLO raise a JAILBREAK notice once, suggesting solitary.
- Solitary cells in the cell block are pitch dark with one flickering bulb; the inmate sulks in the corner. Each plate shows its escape attempts. SOLO toggles on every cell and in the dossier.
- A siren and an ESCAPE FOILED! banner when a breakout is caught.
- Two new achievements: THE WARDEN and THE HOLE (foil 10 escapes). 15 in total.
- Setting: Solitary confinement on/off.

## v2.5.0 - Hotkeys, Stream Deck and Stats
- Global hotkeys that work inside games: Ctrl+Alt+B bonks, Ctrl+Alt+P paroles, Ctrl+Alt+K opens the keygen. Toggle them in Settings.
- Remote control: bonk-mode.ps1 -Bonk, -Parole and -SetProfile <name> act on the running tray (and start it if needed). The installer adds BONK NOW and Parole shortcuts to the Start Menu, which make perfect Stream Deck buttons.
- New STATS screen (key 5): apps bonked, RAM freed, bonk sessions and paroles, a Hall of Shame of repeat offenders, and a before/after report of your free RAM for the last bonk.
- 13 achievements to unlock, from FIRST BLOOD to THE BONKFATHER. Unlocking one pops a banner and a fanfare.
- Stats live in bonk-stats.json on your PC and survive updates.

## v2.4.0 - Profiles, CPU and Disk
- Profiles: gaming, streaming, work and battery. Each remembers its own checkboxes. The first visit seeds sensible defaults: streaming spares Discord, OBS and Spotify; work spares Teams, Slack, Zoom and your browsers and sentences game launchers; battery sentences nearly everything. Switch from the purple PROFILE button on Home, Settings, or the tray menu.
- Every card now shows CPU (as a share of your whole machine) and disk activity. Anything burning more than 8% of the CPU gets a flashing CPU HOG badge.
- Dossiers show CPU and disk, and the live chart draws CPU (orange) on top of RAM (green).
- Hog Radar now also flags apps chewing 10% or more of the CPU, even when their RAM is small.
- Turbo power plan (opt-in): Windows switches to High performance while you're bonked and back to your own plan on parole. The original plan is saved first, so a crash can't strand you; it's restored the next time the tray starts.

## v2.3.0 - Startup Jail
- New STARTUP screen (key 4): every app that launches when you log in, from both Run keys, the 32-bit Run key and both Startup folders, with how heavy it is right now.
- Click STARTS to jail an app from startup, click JAILED to parole it back. It uses Windows' own StartupApproved switch, the same one Task Manager's Startup tab uses, so nothing is ever deleted and Task Manager shows the same state.
- Machine-wide (HKLM) entries ask Windows for admin permission for that one change.
- Windows Security, audio drivers and Bonk Mode itself are immune. Add your own immune entries with startupImmune in bonk-list.json.
- Seven screens now; the cell block tab is called JAIL.
- New: -StartupList prints everything that launches at login and its jail state.

## v2.2.0 - Hog Radar
- Every 30 seconds Bonk Mode sweeps for heavy apps that are not on your bonk list. Anything over the threshold (400 MB by default) becomes a NEW SUSPECT.
- Suspects get their own gold section at the top of Home with a radar sweep. [+ LIST] adds one to your bonk list in a click, [SPARE] ignores it forever.
- One notification per new suspect, never during a game.
- Never suggests Windows itself, Bonk Mode, shared runtimes like Edge WebView2, or dev and server runtimes like node, java and Docker.
- Settings: Hog radar on/off and the radar threshold (200, 400, 800 or 1500 MB).
- New: -Radar lists current suspects without changing anything.

## v2.1.0 - Auto Game Mode
- Start a game and Bonk Mode notices. It counts down 5 seconds (click the notification or CANCEL to stop it), then bonks your list. Quit the game and everything it bonked gets paroled 10 seconds later.
- Watches Steam libraries (all of them, read from Steam's own library list), Epic, Xbox, EA, Ubisoft and GOG folders, plus any folders or exe names you add to the new games block in bonk-list.json.
- The game you're playing is never bonked, even if it is on your list.
- Auto game mode setting: off, ask (default) or auto. Also in the tray menu.
- GAME ON! banner and a live game-mode line in the release info box.
- Settings screen scrolls now. More settings are coming.
- New: -GameScan shows the watched folders and any game running now. -UpdateNow installs the latest release from the command line.
- Fixed: parole now reopens apps that have a single relaunch command, and single-app parole works again.

## v2.0.1 - Credit Where Credit Is Due
- Relicensed from MIT to Apache License 2.0 with a NOTICE file. Forks and derivatives must keep the attribution: "Based on Bonk Mode by makesupply".
- License headers in the source files, and credit on the Greetz screen and in the scroller.
- The updater now also ships the NOTICE file.

## v2.0.0 - The Cell Block Update
- Six screens in one keygen: Home, Cell Block, Dossiers, Settings, Updates, Greetz. Keys 1-6, glitch-wipe transitions.
- Home groups every app under GONNA GET BONKED / SPARED / IN JAIL with big readable names, so you know exactly what the hammer hits.
- Targets get pulsing crosshairs and sweat nervously. As they should.
- Cell Block: every jailed app is a striped inmate pacing its cell and yelling things. Parole them one at a time and watch them run.
- Dossiers: rap sheet for every app on your list, with mugshot, process aliases, parole plan and a live RAM chart. Bonk or parole a single app from here.
- Settings inside the app: music, volume, sound FX, screen shake, scanlines, hammer speed, polite warning, start with Windows, update checks.
- Live updater: checks GitHub, shows this changelog, installs with one click, and restarts itself. Your bonk list and settings survive.
- A warden hammer patrols the cell block. A mascot hammer with googly eyes practices swings next to the logo.
- Greetz screen with a plasma background and a spinning cube, because it is the law.

## v1.1.0 - The Keygen
- Double-click the tray hammer to open a 1999-style keygen window.
- Wobbling dithered ASCII logo, copper bars, starfield, CRT scanlines, typing NFO box.
- Hammer animation with screen shake, comic BONK bursts, flying RAM bits and prison bars.
- Original chiptune and sound effects synthesized live. No audio files.

## v1.0.0 - First Bonk
- Tray hammer with BONK 'EM ALL and Release from RAM jail.
- Polite close first, force close after a few seconds.
- Kill list in bonk-list.json with checkboxes in the tray menu.
- Diplomatic immunity for Steam, Explorer, the GPU driver and Windows core.
