# MM Launcher

Server browser and launcher for **Misfit Mercenaries** DayZ servers.
Built with C# / .NET 10 and Avalonia.

## Download

From [Releases](https://github.com/meccmax/mm-launcher/releases/latest), get either:

- **MMLauncher-Setup.exe** (recommended): installs for your Windows user only, so there's no admin prompt. It adds a Start menu shortcut, an optional desktop shortcut and an uninstaller in Settings > Apps.
- **MMLauncher.exe** (portable): run it from anywhere, with nothing to install.

Both keep their settings in `%APPDATA%\MMLauncher` and update themselves when a new version is out.

Server admins: download **MMServerAgent.zip** from the same page and follow `AGENT-SETUP.txt`.

[Code signing policy](docs/CODE-SIGNING-POLICY.md) (including what the launcher sends and where).

## Releasing (maintainers)

1. Bump nothing by hand. The version comes from the tag.
2. Optionally write player-facing notes in `release-notes/vX.Y.Z.md`.
3. Push a tag: `git tag v0.5.0 && git push origin v0.5.0`.

[`.github/workflows/release.yml`](.github/workflows/release.yml) runs the self-check on Windows, builds both programs, writes `SHA256SUMS.txt`, and publishes the Release. The launcher's updater and `update-agent.bat` only accept downloads that match those checksums.

## Milestone 1

- **Finds MM servers automatically.** Scans ports 2000–3000 on `game.misfitmercenaries.com` and `servers.misfitmercenaries.com`. New servers appear after a Rescan; nobody edits a list.
- **Live status, queried straight from each server** (no middleman service): name, map, players, queue, in-game time, ping, 1PP/3PP, required DLC, version and password flag.
- **Required mods for each server**, read from the server's own status reply. Each mod shows its real Steam Workshop title, size and last update.
- **Checks this PC's Steam install** and marks each mod as Installed, Update, Missing, or Not on Workshop (deleted or hidden by its author).
- Auto-refreshes every 30 seconds. Copy-address button.

### MM Server Agent (added in 1.5)

- **What it is:** a small read-only Windows service that runs on each server machine. Setup steps are in `agent-package/AGENT-SETUP.txt`.
- **What it reads:** each running DayZ server's startup line (`-mod=`, `-config=`, `-port=`), the `meta.cpp` workshop IDs, and `serverDZ.cfg`.
- **What it serves:** the complete client mod list, the full server name, uptime and an optional message. These are served at `https://<host>:<port>/mm-agent/v1/servers`. Older launchers can also use `http://` on the same port while `AllowPlainHttp` is on.
- **Encryption:** the agent makes its own key on first start (`agent-tls.pfx`). The launcher accepts an agent only if its key matches the fingerprint listed in [`trust/agents.jsonc`](trust/agents.jsonc), or, for unlisted agents, the key it saw the first time. `check-agent.bat` prints the fingerprint to add to that list.
- **How the launcher uses it:** it checks ports 2994–2999 on each MM host and uses the agent's list when it gets one (shown as "VERIFIED BY MM AGENT"). If no agent answers, it falls back to the server's status reply. Servers an agent reports but the scan missed are added automatically.

### Server browser (v0.6)

The left panel has four tabs:
- **MM servers:** the Misfit Mercenaries servers, as before.
- **All servers:** every public DayZ server. Search by name, map or IP address, and filter by map (with server counts), 1PP or 3PP, modded or vanilla, hide empty, hide full. Sort by players, name, map or ping. Ping and live player counts are measured for the servers near the top of the list; sorting by ping measures a few hundred.
- **Favorites:** servers starred with the ☆ Favorite button.
- **Recent:** the last 25 servers joined.

Any server picked in any tab opens in the same details panel, with the full mod list, JOIN SERVER (with mod sync), Verify files, Repair and auto-rejoin. Vanilla servers with no mods can be joined too.

The full list comes from Valve's Steam Web API (`IGameServersService/GetServerList`) through the MM Server Agent, which holds the API key (`SteamWebApiKey` in `agent.jsonc`) so it never ships in the launcher. Valve answers at most 10,000 servers per request, so the agent asks in parts (servers with players, empty servers, then by map where a part is still too big) and merges them. It refreshes the list every few minutes and serves it gzip-compressed over the encrypted connection at `/mm-agent/v1/browser`. The launcher keeps a copy (`browser-cache.json.gz`), so the tab opens instantly. Favorites and recent servers are saved in `favorites.jsonc`.

### Restart warnings (v0.7)

When the agent knows a server's restart schedule (`RestartEveryHours`, counted from when the server started, or fixed `RestartTimes` in `agent.jsonc`), the launcher shows:
- a **NEXT RESTART** tile ("in ~1h 52m"; "~" means estimated from uptime);
- "restart ~8 min" in the server list once a restart is within 30 minutes;
- a warning in the details panel once it's within 15 minutes;
- a question before joining when it's within 5 minutes.

Auto-rejoin uses it too: if DayZ closes right around a scheduled restart, the launcher waits for the restart to happen before rejoining.

### Friends (v0.9)

The **Friends** tab shows which Steam friends are playing DayZ and on which server ("PLAYING NOW"), plus servers friends were seen on before ("seen 3 h ago", kept for 30 days in `friends-history.jsonc`). Clicking a server opens it like any other, with JOIN. The details panel also says "Friends here: …" on any server where friends are playing.

- **How it works:** the launcher reads which Steam account is signed in (it never starts Steam as DayZ). An MM agent with a Steam Web API key then asks Steam for that account's friends (`GetFriendList`) and what they're playing (`GetPlayerSummaries`).
- **Requirements:** the player's friends list must be public, and a friend's server only shows if their game details are public. Both are Steam's defaults.
- **Agent safeguards:** answers are cached for a minute and limited per address. Steam IDs aren't logged.

### Server news (v0.10)

Each MM server's details have a **NEWS** tab next to REQUIRED MODS, for wipes, events and changes. Admins write posts as plain text in the agent's `news` folder: `all.txt` for every server, `<game port>.txt` for one server (shown first). A post starts with `# ` (an optional `yyyy-mm-dd` date, then the title), followed by its text and an optional `link: https://…` line; `#!` highlights it. The agent creates a commented `all.txt` explaining this.

- Servers with posts you haven't read show **NEW** in the server list and open on the NEWS tab. What you've read is remembered in `news-seen.jsonc`.
- **Limits:** 8 posts per server, titles up to 100 characters, text up to 1500. Links must be `https://` and are opened in the browser only when clicked. Both the agent and the launcher strip control and right-to-left characters.

### Mods page (v0.9)

The **MODS** page has two views:

- **Downloaded:** every DayZ Workshop mod on this PC, with its size, last update and what uses it (MM servers, favorite and recent servers, editor presets, and the DayZ Editor itself). Filter by name or ID, by "Need an update" or by "Not used by anything". **Update all** downloads every outdated mod. **Remove** (one mod, or all unused ones) unsubscribes in Steam, so it isn't downloaded again, and deletes the folder.
- **Steam Workshop:** search the DayZ Workshop or browse it by popular this week, most subscribed, recently updated or newest. Pasting a Workshop link or ID finds that item. Each result shows its thumbnail, size, subscribers and whether it's already installed. **Install** subscribes and downloads it through Steam, along with any mods it lists as required. **Open in Steam** shows its page in the Steam app.

- **How it works:** the search goes through an MM agent's Steam Web API key (`IPublishedFileService/QueryFiles`), so the launcher needs no key. Answers are cached for 5 minutes and limited to 30 searches a minute per address. Only DayZ items are returned; thumbnails load only from Steam's own image hosts (https).
- **Steam status:** browsing never touches Steam. Installing, updating and removing use the same short Steam helper as joining, so Steam shows "Playing DayZ" only while that runs.

### DayZ Editor (v0.8)

The **DAYZ EDITOR** page starts DayZ offline (main menu, no server) with Community Framework, Dabs Framework and [DayZ-Editor](https://steamcommunity.com/sharedfiles/filedetails/?id=2250764298) loaded first, then a preset's mods in order. Missing or outdated mods are downloaded through Steam first, the same way as for servers.

- **Presets:** players' own presets are saved in `editor-presets.jsonc`. Mods can be added from what's already downloaded, or by pasting Workshop IDs or links. The load order can be changed with ▲/▼.
- **Import / Export:** uses the DayZ launcher's preset file (Mods > Presets > Export, `.html`). A text file of Workshop IDs or links also imports.
- **Shared MM presets:** `.html` or `.txt` files placed in the agent's `editor-presets` folder appear in every launcher as read-only "shared by MM" presets. Players click Copy to make their own version.

### Join + mod sync (milestones 2 and 3)

**JOIN SERVER** does four things in order:
1. Works out which of the server's mods are missing or outdated. It uses the agent's list when an agent is running, otherwise the server's status reply.
2. Downloads them through the player's own Steam client using the Steamworks API. While this runs, the launcher identifies itself to Steam as DayZ, so Steam shows "Playing DayZ". It disconnects as soon as the download finishes.
3. Subscribes the player to every mod the server uses, so Steam keeps them updated.
4. Starts DayZ through BattlEye's launcher (`DayZ_BE.exe -exe DayZ_x64.exe`, so BattlEye is always on) and connects with the server's mods in its load order. The mods are passed as short `DayZ\!MMWorkshop\@<id>` junctions, the same approach the official launcher's `!Workshop` folder uses.

**JOIN WHEN READY.** When a server is restarting or down, JOIN SERVER turns into JOIN WHEN READY. The player opts in each time, the launcher waits for the server to answer again, waits a short random time (30–90 s after a restart, so not everyone connects at once), updates mods and starts DayZ. If DayZ is still open (kicked to the menu or connection screen), it asks first, then closes DayZ and starts it again connected. It can be cancelled at any time.

**Rejoin automatically if DayZ crashes** (checkbox, off by default). This only reacts to a real crash: a new crash dump (`*.mdmp`) in DayZ's log folder, or a Windows crash exit code. Quitting DayZ normally never rejoins; if the server is restarting at that moment, the launcher offers "Join when it's ready" instead. Passwords are kept in memory only. If DayZ crashes within 90 seconds of starting three times in a row, it stops and suggests Report a problem.

**Update mods** does steps 1–3 without launching. Passworded servers ask for the password; it is never written to the log.

Every step is logged to `%APPDATA%\MMLauncher\launcher.log`. The panel's **Open log** link opens it.

These launch options in `settings.jsonc` are each explained inside the file:

| Option | Values |
|---|---|
| `LaunchMethod` | `Direct` (default, via DayZ_BE.exe) or `Steam` |
| `ModFolderMode` | `Junction` or `Absolute` |
| `PlayerName` | profile name passed to DayZ |
| `ExtraLaunchParameters` | default `-nolauncher -world=empty` |
| `AutoRejoin` | `true`/`false`, same as the checkbox |

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

### Updates

- **Launcher:** checks GitHub on startup and every 6 hours (can be turned off in Settings). When a newer release exists, an **Update to vX** button appears. Clicking it shows the release notes, downloads the new exe, checks it against `SHA256SUMS.txt`, swaps it in and restarts.
- **Agent:** never updates on its own. `check-agent.bat` says when a newer version exists. `update-agent.bat` (run as admin) downloads and verifies the new version, stops the service, swaps the exe, and starts it again. `agent.jsonc` and saved reports are left alone.

Coming next: installer and code signing, and the full DayZ server browser.

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

## Security

A security audit of the launcher, the server agent and the release process is in [docs/SECURITY-AUDIT.md](docs/SECURITY-AUDIT.md), with every finding and what was done about it. To report a vulnerability privately, see [SECURITY.md](SECURITY.md).

In short:
- Updates are only taken from this repository's GitHub Releases and must match the published SHA-256 checksums.
- Launcher and agent talk over HTTPS. Agent keys are checked against the list in `trust/agents.jsonc`, and the launcher never drops back to unencrypted for an agent that supports encryption.
- The server agent rate-limits problem reports, accepts only the files the launcher makes, and locks its folder so the Discord webhook stays private.
- Release builds run with read-only access; only a separate publish step can create a Release, and every GitHub Action is pinned to an exact version.

## License

MIT, see [LICENSE](LICENSE). Not affiliated with Bohemia Interactive or Valve. DayZ is a trademark of Bohemia Interactive; Steam and Steamworks are trademarks of Valve.
