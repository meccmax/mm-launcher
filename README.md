# MM Launcher

Server browser and launcher for **Misfit Mercenaries** DayZ servers.
Built with C# / .NET 10 and Avalonia.

## Milestone 1 (this version)

- **Finds MM servers automatically.** Scans ports 2000–3000 on `game.misfitmercenaries.com` and `servers.misfitmercenaries.com`. New servers appear after a Rescan; nobody edits a list.
- **Live status, queried straight from each server** (no middleman service): name, map, players, queue, in-game time, ping, 1PP/3PP, required DLC, version and password flag.
- **Required mods for each server**, read from the server's own status reply. Each mod shows its real Steam Workshop title, size and last update.
- **Checks this PC's Steam install** and marks each mod as Installed, Update, Missing, or Removed (deleted from the Workshop).
- Auto-refreshes every 30 seconds. Copy-address button.

### MM Server Agent (added in 1.5)

- **What it is:** a small read-only Windows service that runs on each server machine. Setup steps are in `agent-package/AGENT-SETUP.txt`.
- **What it reads:** each running DayZ server's startup line (`-mod=`, `-config=`, `-port=`), the `meta.cpp` workshop IDs, and `serverDZ.cfg`.
- **What it serves:** the complete client mod list, the full server name, uptime and an optional message. These are served at `http://<host>:<port>/mm-agent/v1/servers`.
- **How the launcher uses it:** it checks ports 2994–2999 on each MM host and uses the agent's list when it gets one (shown as "VERIFIED BY MM AGENT"). If no agent answers, it falls back to the server's status reply. Servers an agent reports but the scan missed are added automatically.

### Join + mod sync (milestones 2 and 3)

**JOIN SERVER** does four things in order:
1. Works out which of the server's mods are missing or outdated. It uses the agent's list when an agent is running, otherwise the server's status reply.
2. Downloads them through the player's own Steam client using the Steamworks API. While this runs, the launcher identifies itself to Steam as DayZ, so Steam shows "Playing DayZ". It disconnects as soon as the download finishes.
3. Subscribes the player to every mod the server uses, so Steam keeps them updated.
4. Starts DayZ through BattlEye's launcher (`DayZ_BE.exe -exe DayZ_x64.exe`, so BattlEye is always on) and connects with the server's mods in its load order. The mods are passed as short `DayZ\!MMWorkshop\@<id>` junctions, the same approach the official launcher's `!Workshop` folder uses.

**Update mods** does steps 1–3 without launching. Passworded servers ask for the password; it is never written to the log.

Every step is logged to `%APPDATA%\MMLauncher\launcher.log`. The panel's **Open log** link opens it.

These launch options in `settings.jsonc` are each explained inside the file:

| Option | Values |
|---|---|
| `LaunchMethod` | `Direct` (default, via DayZ_BE.exe) or `Steam` |
| `ModFolderMode` | `Junction` or `Absolute` |
| `PlayerName` | profile name passed to DayZ |
| `ExtraLaunchParameters` | default `-nolauncher -world=empty` |

For testing without Steam, set the environment variable `MMLAUNCHER_FAKE_STEAM=1` to simulate downloads and `MMLAUNCHER_DRY_RUN=1` to log the launch command instead of starting DayZ.

### Settings, Verify files and Repair

- **Settings** (header button): Steam, DayZ and workshop folders. Each is found automatically, or you can set it with Browse…; a status line shows what was found. Also launch method, profile name, extra parameters, refresh interval, and maintenance tools (rebuild mod links, open log).
- **Verify files**: checks each downloaded mod on disk without downloading anything. It looks for the mod folder, its `addons` folder, missing or empty `.pbo` files, and a `.bisign` signature for every `.pbo` when the author signs the mod. Damaged mods get a red badge.
- **Repair** (per mod, or for all damaged or all mods): moves each `<workshop>\<id>` folder aside in one step, never following links. A mod with a file open in another program is skipped untouched and named, instead of being left half-deleted. Steam then downloads fresh copies; if it doesn't start re-downloading, the launcher unsubscribes and resubscribes the mod. If a download fails or is cancelled, the old copy is put back. Old copies are deleted once the fresh ones arrive, and any left over are cleaned up the next time the launcher starts. If a fresh copy is still damaged, the launcher says the Workshop upload itself is likely broken.
- **JOIN SERVER** runs the file check before launching. If anything is damaged it offers "Repair" or "Join anyway".

### Report a problem (milestone 4)

- **Report a problem** (header button) collects:
  - the latest DayZ session's logs: RPT, crash, script, error and warning logs;
  - an optional crash dump from that session;
  - the launcher log;
  - the selected server's details and mod list, with what's installed;
  - PC basics: Windows version, CPU, GPU and driver, RAM.

  The Windows username and PC name are removed from every text file. The window shows exactly what's included before anything leaves the PC.
- **Save zip to Desktop** works offline. **Send to staff** uploads the zip to the MM Server Agent.
- **Agent side:** checks the zip, enforces size and rate limits, saves it under `reports\`, and posts it to a Discord webhook. The post contains player, server, crash reason, error codes, PC info, a reference number and the zip, with all mentions disabled. The webhook lives only in `agent.jsonc` on the server.

Coming next: auto-updates, installer and code signing, and the full DayZ server browser.

## Running it

Double-click `MMLauncher.exe`. Windows may show "Windows protected your PC" because the exe isn't code-signed yet. Click **More info**, then **Run anyway**.

Settings and the server cache live in `%APPDATA%\MMLauncher` (the **Settings folder** link in the app opens it):

| File | What it is |
|---|---|
| `settings.jsonc` | Hosts to scan, port range, refresh interval, Steam path override. Every option is explained in comments inside the file. |
| `servers.jsonc` | Servers found by the last scan (written automatically). |
| `launcher-crash.txt` | Only appears if the launcher itself crashes. Send it to Claude. |

## Building from source

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). This is only needed on the PC that builds the exe.
2. Double-click `build.bat`.
3. The exe lands in `dist\MMLauncher.exe`.

## Project layout

```
src/MMLauncher.Core     engine: server queries, DayZ mod-list decoding, scanning,
                        Steam Workshop + local Steam detection, settings files
src/MMLauncher.App      the window (Avalonia), MM orange/black/silver theme
src/MMServerAgent       server-side agent (Windows service), reports true mod lists
agent-package/          helper .bat files + setup guide shipped with the agent
src/MMLauncher.Tests    self-check program (run-tests.bat)
tools/fake_dayz_servers.py   fake DayZ servers on localhost, for testing without real ones
```

## How the server data is read

- **Status:** the Steam A2S_INFO query on each server's query port.
- **Mods:** DayZ packs its required client mod list as an escaped binary blob inside the A2S_RULES reply. The decoder follows Bohemia's ServerBrowserProtocol3 format and was cross-checked against the open-source `dayzquery` and `node-gamedig` decoders.
- **Workshop info:** Steam's public `GetPublishedFileDetails` endpoint. No API key or login needed.
- **Installed mods:** read from Steam's `libraryfolders.vdf` and `appworkshop_221100.acf`. These are read only; nothing is modified.
