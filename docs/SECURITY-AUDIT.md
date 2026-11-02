# Security audit: MM Launcher and MM Server Agent

| | |
|---|---|
| **Date** | 30 October 2026 |
| **Versions reviewed** | v0.4.0. Fixes ship in v0.4.1; A8, L3 and R4 are closed in v0.5.0. |
| **Scope** | MM Launcher (player app), MM Server Agent (runs on the game servers), GitHub release workflow, third-party packages |
| **Method** | Manual code review of every network, file-system and process boundary. Hostile-input tests run against a live agent: path traversal, executables, zip bombs, oversized JSON, fake zip headers, connection floods, rate limits. Automated checks were added to the self-check, which runs on every release build. Package scan with `dotnet list package --vulnerable --include-transitive`. |

## Summary

No critical problems were found. The main risks were in the **server agent's problem-report upload**, because that endpoint has to accept files from anyone on the internet, and in the **launcher's self-update**. Both are now fixed or mitigated. Third-party packages have no known vulnerabilities.

| Severity | Found | Fixed | Mitigated | Accepted | Recommended action |
|---|---|---|---|---|---|
| High | 3 | 3 | 0 | 0 | 0 |
| Medium | 14 | 13 | 1 | 0 | 0 |
| Low | 9 | 8 | 0 | 1 | 0 |
| Process | 4 | 0 | 0 | 0 | 4 |

**Status:** *Fixed* means the problem is gone. *Mitigated* means the risk is reduced but some remains, as described. *Accepted* means a known limitation, judged acceptable for now. *Recommended* means an action for the maintainers outside the code.

## How it fits together (threat model)

```
 Player PC                                      Game server (Windows, CFTools)
 ┌──────────────────────┐   A2S (UDP, public)   ┌──────────────────────────┐
 │ MMLauncher.exe       │ ────────────────────▶ │ DayZ server              │
 │  · server list       │   HTTP :2994-2999     │                          │
 │  · join / mod sync   │ ────────────────────▶ │ MMServerAgent (service)  │
 │  · report a problem  │   GET mod list,       │  · reads DayZ processes  │
 │  · self-update       │   POST report zip     │  · forwards reports ─────┼──▶ Discord webhook
 └─────────┬────────────┘                       └──────────────────────────┘
           │ HTTPS: Steam Web API, GitHub Releases
           ▼
   Steam client (Workshop downloads)       GitHub Actions builds and publishes releases
```

**What's worth protecting:** players' PCs (the launcher starts programs and makes folder links), the game servers (the agent runs as a Windows service), the staff Discord channel and its webhook URL, staff PCs (they open report zips), and the release pipeline (anything published reaches every player through self-update).

**Who might attack:** anyone on the internet who can reach the agent's port. Someone on the same network as a player who can tamper with plain HTTP or DNS. A malicious mod name or server name. Someone who takes over the GitHub account or a GitHub Action.

## Findings: MM Server Agent

| ID | Severity | Finding | Status | What was done |
|---|---|---|---|---|
| A1 | High | **Report upload flooding.** Anyone could send unlimited reports, filling the server's disk and spamming the staff Discord channel. | Fixed | Limits of 3 reports per hour per address and 50 per day are checked *before* the upload is received. Reports are capped at 9 MB. Stored reports are capped at 500 MB, oldest deleted first. The tracking table is capped at 10,000 addresses. All limits are configurable in `agent.jsonc`. |
| A2 | High | **Harmful files sent to staff.** A forged report zip could carry `.exe`, `.lnk`, `.bat` or scripts that staff would download from Discord and might open. | Fixed | Strict allow-list: the zip may only contain the exact file names and types the launcher writes (`report.json`, `report.txt`, `launcher/launcher.log`, `server/*.txt`, `dayz/*.RPT\|log\|mdmp`). Any other file, folder, `..` or absolute path rejects the whole report. |
| A3 | Medium | **Zip bomb or huge summary.** A small upload could unpack to gigabytes or contain a giant `report.json`, using up the agent's memory. | Fixed | At most 64 entries and 200 MB unpacked, checked before anything is read. `report.json` is read with a hard 256 KB cap. The agent never unpacks the zip to disk; it saves it exactly as received. |
| A4 | Medium | **Discord message injection.** Player name, server name and description went into the Discord post as-is, so they could ping `@everyone`, add clickable phishing links, or fake formatting so a report looked official. | Fixed | Mentions are disabled (`allowed_mentions: none`). Short fields are markdown-escaped. Free text goes into code blocks, where links can't be clicked. The timestamp is set by the agent, not by the report. |
| A5 | Medium | **Slow-upload tie-up.** One address could hold all 32 connection slots open by sending slowly. | Fixed | At most 4 connections per address. 10 s limit for headers and 120 s for the body. 8 KB header limit. |
| A6 | Medium | **Webhook URL readable by other users.** `agent.jsonc` holds the Discord webhook. On a default Windows install, any local user can read and change files in a new folder under `C:\`. That would also let them replace the service exe. | Fixed | `install` and `update` now limit the agent folder to Administrators, SYSTEM and the installing admin. They skip this if the folder holds unrelated files, and say so. The webhook is never sent to players, and it must be `https://`. |
| A7 | Medium | **Service runs as LocalSystem with every privilege.** If the agent were ever compromised, the attacker would get full control of the server. | Mitigated | The service now runs with a restricted service SID and only 3 privileges (SeChangeNotify, SeCreateGlobal, SeImpersonate). Install checks the result and undoes the restriction if Windows refuses it. It still runs as LocalSystem, because it needs to read the DayZ server processes' command lines. `--full-privileges` exists for setups that need the old behaviour. |
| A8 | Medium | **No encryption on the agent's HTTP.** The mod list and reports travel as plain HTTP. Someone on the path can read a report: crash logs, PC specs, player name. Usernames and PC names are removed before sending. | Fixed (v0.5.0) | The agent now speaks HTTPS with its own key (`agent-tls.pfx`, made on first start and kept in the locked agent folder). Launchers from v0.5.0 use it automatically, and reports are always sent encrypted to agents that support it. `AllowPlainHttp: false` in `agent.jsonc` turns off unencrypted access once everyone has updated. |
| A9 | Low | **Non-report POSTs** were read in full before being refused. | Fixed | Unknown paths get 404 and wrong methods get 405, before the body is read. |
| A10 | Low | **Log injection.** Player-controlled text with line breaks could forge fake lines in `agent.log`. | Fixed | Control characters are stripped. Addresses in the log are partly masked (`1.2.3.x`). |
| A11 | Low | **Info exposure on `/mm-agent/v1/servers`.** | Fixed (verified) | Only the server name, map, ports, player slots, uptime, mod IDs, names and folder names, and the admin's optional message are returned. Passwords, admin passwords, RCON settings and file paths from `serverDZ.cfg` are never read into the reply. |
| A12 | Low | **Agent update integrity.** | Fixed | Updates only run when the admin starts `update-agent.bat`. The download comes only from GitHub over HTTPS and is checked against the release's SHA-256 checksum before the exe is swapped. The locked-down folder (A6) stops non-admins from replacing it. |

## Findings: MM Launcher

| ID | Severity | Finding | Status | What was done |
|---|---|---|---|---|
| L1 | High | **Self-update could install a tampered exe.** | Fixed | Downloads are accepted only from `github.com` or `*.githubusercontent.com` over HTTPS, and only if the SHA-256 matches the release's `SHA256SUMS.txt`. A mismatch deletes the download and changes nothing. A test proves a tampered download is refused. Residual risk: the checksums live in the same release, so they don't protect against a hijacked GitHub account (R1, R3). |
| L2 | Medium | **Hostile agent reply.** A fake agent, through DNS spoofing or a hijacked network, could send a huge reply to freeze or crash the launcher. | Fixed | Replies are capped at 1 MB, 32 servers and 300 mods per server. Text is clipped and control characters stripped. Agent-supplied folder names are only displayed; real mod folders always come from Steam's own files. |
| L3 | Medium | **Fake agent suggests unwanted mods.** A spoofed agent could list Workshop items for the launcher to subscribe to. | Fixed (v0.5.0) | The launcher checks each agent's key. It must match the fingerprint published in `trust/agents.jsonc` on GitHub, or, for agents not listed there, the key seen the first time (as SSH does). Once an agent has used encryption, the launcher never talks to it unencrypted again, so it can't be downgraded. Items still only come from the Steam Workshop, downloaded by Steam. |
| L4 | Medium | **Opening links from release data.** "Open releases page" passed a URL from GitHub's API to Windows, which would run `file://` or other schemes. | Fixed | Only `https://github.com` and `https://steamcommunity.com` links are opened. Anything else falls back to the releases page. |
| L5 | Medium | **Command injection when launching DayZ.** Server names, passwords and mod paths end up on a command line. | Fixed | Each argument is quoted on its own, and DayZ is started directly, never through `cmd`. Folder links are made with `cmd /c mklink`, and it refuses any path containing `%`, `"` or line breaks; full paths are used instead. |
| L6 | Medium | **Folder links could replace real folders.** | Fixed | An existing link is only replaced if it really is a link. A real folder in its place is never touched. Removal is non-recursive, so mods are never deleted through a link. |
| L7 | Medium | **Server password in the log.** The password was masked in `launcher.log`, which goes into problem reports. Passwords with spaces or quotes were only partly masked. | Fixed | The whole password is now removed from the logged command, whatever it contains. Passwords are never saved to disk. |
| L8 | Low | **Repair moving the wrong folders.** | Fixed | Repair only touches the numbered Workshop folders of the selected mods. Folders are moved aside, not deleted, and restored if the new download fails. |
| L9 | Low | **Private info in reports.** | Fixed | The Windows username and PC name are removed from every file before zipping. Nothing is sent until the player clicks Send. |
| L10 | Low | **Password visible to other programs.** While DayZ runs, its command line (including `-password=`) can be read by other programs on the same PC. | Accepted | This is how DayZ takes passwords. DZSA and Steam do the same. |
| L11 | Low | **Steam helper process.** | Fixed (verified) | The helper talks to the launcher only through its own stdin/stdout, opens no network port, and exits when done. |

## Findings: release pipeline

| ID | Severity | Finding | Status | What was done |
|---|---|---|---|---|
| C1 | Medium | **Build job had write access to the repository.** Tests and build steps ran project code while holding a token that could create releases. | Fixed | The build and test job is now read-only. A separate, small publish job gets write access. It runs no project code, only downloads the built files, re-checks their checksums and creates the Release. |
| C2 | Medium | **Actions referenced by movable tags** (`@v4`). If one of those actions were hijacked, it could inject code into every release. | Fixed | All actions are pinned to exact commit hashes. Dependabot proposes updates weekly, for NuGet packages too. |
| C3 | Low | **Script injection from the workflow input.** The "Publish as version" text was pasted straight into a PowerShell script. | Fixed | The input is now passed as an environment variable and must match `0.0.0` exactly. Checkout no longer leaves the Git token on disk. |

**Dependencies:** Avalonia 11.3.22, CommunityToolkit.Mvvm 8.4.2, Facepunch.Steamworks 2.3.3, System.Management. No known vulnerabilities, including transitive packages, as of the audit date.

## Changes since the audit

| Version | Change | Security notes |
|---|---|---|
| v0.5.0 | Encrypted launcher ↔ agent connection | Closes A8 and L3 (see above). Agent keys are pinned through `trust/agents.jsonc` or on first use. The launcher never downgrades to plain HTTP. |
| v0.8.0 | DayZ Editor presets folder | The agent reads `.html` / `.txt` files from its own `editor-presets` folder (locked with the rest of the agent folder; max 50 files, 1 MB each) and serves only the parsed Workshop IDs and names. Launchers clip and validate every entry. |
| v0.9.0 | Friends lookup through the agent | `/mm-agent/v1/friends?steamid=` only accepts a real 64-bit Steam ID and returns public Steam data (friends playing DayZ and their server addresses). It uses the agent's Steam key (never sent out), caches answers for 60 s, rate-limits fresh lookups to 12 per minute per address, and doesn't log Steam IDs. The launcher validates server addresses and clips names. |
| v0.9.0 | Workshop search through the agent | `/mm-agent/v1/workshop` takes only a search text (control characters removed, 100 characters max), one of four fixed sort names and a page number clamped to 1–50. It uses the agent's Steam key (never sent out or logged), drops non-DayZ items, caches answers for 5 minutes and allows 30 fresh searches a minute per address. The launcher clips every field, keeps at most 100 results and 50 required mods each, and loads thumbnails only over https from Steam's image hosts, capped at 3 MB and decoded at thumbnail size. |
| v0.9.0 | Removing mods | Remove asks first, unsubscribes through the same Steam helper as downloads, then moves the mod folder aside in one step (never following links, skipping any mod with a file in use) before deleting it. Mods Steam didn't unsubscribe are left in place. |
| v0.9.0 | Installer | `MMLauncher-Setup.exe` (Inno Setup) installs per user to `%LOCALAPPDATA%\Programs\MM Launcher`, so it needs no admin rights. It's built and test-installed by the release workflow, and covered by `SHA256SUMS.txt` like the other downloads. |
| v0.10.0 | Server news | The agent reads only `all.txt` and `<port>.txt` from its own `news` folder (locked with the rest of the agent folder; first 64 KB of each, at most 30 files). Admin-written text goes out inside the normal agent reply. Launchers clean it again: 8 posts, clipped titles and text, control and right-to-left characters removed, and links kept only if they are plain `https://` with no login part. Links open in the browser only when the player clicks them. |
| v0.11.0 | Player history | The agent only queries its own servers on 127.0.0.1 and stores counts (no player names or IDs) in `player-history.jsonc` inside the locked agent folder. `/mm-agent/v1/history?port=` accepts only a valid port number and returns public player counts. Launchers reject odd step sizes, keep at most ~4000 readings and clamp values to 0–1000. |
| v0.12.0 | Usage stats | Off until the player says yes, and changeable in Settings. The random ID is deleted when stats are turned off. Sent only to agents that announce stats, and only over their pinned, encrypted connection. The agent checks the body size before reading it, rate-limits per address (60 batches an hour) and overall (20,000 a day), accepts only known event types, reduces error codes to letters, digits and dashes, never stores names for non-MM servers, and never writes the sender's IP address. Files sit in the locked agent folder and are pruned after `KeepUsageDays`. |
| v0.12.0 | Themes | Colors only; a theme name read from `settings.jsonc` that isn't known falls back to the default. |
| v0.6.0 | Server browser feed through the agent | The Steam Web API key lives only in `agent.jsonc` (in the locked agent folder). It's never sent to launchers or written to `agent.log`, and Steam's error messages are logged without the request URL. The feed (`/mm-agent/v1/browser`) is public information (Valve's server list), served compressed. Per-address connection limits still apply. The launcher caps the download at 16 MB compressed and 32 MB unpacked, and treats every entry as untrusted: names are clipped, control characters stripped, and addresses validated. |

## Re-audit: 1 November 2026 (v0.11.0)

Covers everything added since the first audit: Friends, Mods page and Workshop search, removing mods, the installer, server news, the Steam permission check and player history. It also repeats the package, secret and settings checks.

| Check | Result |
|---|---|
| Known-vulnerable packages (`dotnet list package --vulnerable --include-transitive`, all three projects) | None |
| Secrets anywhere in the git history (GitHub tokens, Discord webhooks, private keys, Steam keys, `.pfx`/`.pem` files, real `agent.jsonc`/`settings.jsonc`) | None |
| GitHub settings | Rulesets protect `main` and release tags. Secret scanning and push protection on. Dependabot security updates on. 2FA on (R1). CodeQL runs on every push and passes. |
| Release workflow | Read-only by default; only the publish job can write. Every action pinned to a full commit SHA. No `pull_request_target`, and no untrusted text used inside scripts. |
| New agent endpoints (`/workshop`, `/friends`, `/history`) | Inputs clamped or refused. Steam key never leaves the agent or appears in its log. Answers cached; Workshop and Friends rate-limited per address. `/history` returns only public player counts. |
| New agent files (`news\`, `player-history.jsonc`) | Inside the locked agent folder and on the lockdown allow list. Reads capped (64 KB per news file, 30 files). History written via a temp file, then swapped in. |
| Agent text shown in the launcher (news, Workshop results, history) | Cleaned again by the launcher. Control and right-to-left characters removed, lengths and counts capped. News links kept only if plain `https://` without a login part, opened only on click, with the site name shown on the button. |
| Removing mods | Asks first. Unsubscribes through Steam, then moves the folder aside in one step. Never follows links; a mod in use is skipped untouched. |
| Steam permission check | Read-only Windows calls; every handle closed. Never throws; stays silent when it can't tell. |

**New findings, all fixed in this release:**

| ID | Severity | Finding | Status |
|---|---|---|---|
| L11 | Low | **Workshop thumbnails followed redirects anywhere.** The first image address was checked to be Steam's, but a redirect could send the download to any website. That would reveal the player's IP address to that site. | Fixed: redirects are followed by hand (at most 3), and only to Steam's image hosts. |
| L12 | Low | **A preset file built to be slow could freeze the launcher.** Thousands of unclosed rows made the HTML patterns very slow. Only files a player imports, or an admin puts in the agent's `editor-presets` folder, are parsed this way. | Fixed: the patterns give up after 2 seconds and the file counts as unreadable. Self-check added. |

**Accepted, as before:** a trusted MM agent decides which mods a Workshop result lists as required (they're installed with it), what news says, and what links it carries. Agents are pinned (only keys published in `trust/agents.jsonc` or seen before are accepted). Someone with control of an MM agent could already change the mod lists players download, so this adds no new kind of risk.

**R3 (code signing):** the SignPath Foundation application was declined (not enough public use yet). R3 stays open; reapply once more people use the launcher. Until then, every download is checked against `SHA256SUMS.txt` by the launcher's updater and `update-agent.bat`.

## Review: 2 November 2026 (v0.14.0)

Covers the browser's new country, PvE, player-count, mod search and "Has AI" filters, and the Mods page's cleanup tools. No new packages.

| Check | Result |
|---|---|
| Agent asking other servers for their mods | Only addresses in Valve's own server list, only modded ones, one small standard query each per round (every 30 minutes), at most 24 at once and 40 a second. Someone listing a fake server at a victim's address would get one packet per round, so it can't be used to flood anyone. |
| Replies from unknown servers (parsed on the agent) | Same bounds-checked reader the launcher has always used for every server. At most 400 mods per server and 80 characters per name are kept; control and invisible formatting characters removed. A server that answers with junk is skipped. |
| New endpoint `/mm-agent/v1/browser-mods` | Read-only, no input. Holds only what servers announce publicly. |
| Mod lists received by the launcher | Size-capped (16 MB compressed, 48 MB unpacked). Every mod position is checked against the list, duplicates and junk dropped. Names are only shown as plain text. |
| Country database | Downloaded by the agent over HTTPS from db-ip.com, at most once every 6 hours when due, capped at 64 MB, checked to read before it replaces the old copy. Lookups happen on the agent; no address is sent anywhere. |
| New agent files (`geoip\`, `mod-index-cache.json.gz`) | Added to the lockdown allow list, so `install` / `update` still lock the agent folder. |
| Removing mods | "Remove its mods" and "Remove these" ask first and use the same remove path as before. A mod that another MM server, favorite, recent server, preset or the DayZ Editor uses is never picked by "Remove its mods". Favorites and recent servers now remember their mod lists, so "Remove unused" no longer treats their mods as unused when their tab hasn't been opened yet (a gap found during this work, now fixed). |

**Fixed in this release:** server names from Steam's list are now stripped of invisible right-to-left characters too (news already was), so a name can't be made to read differently from what it is. Self-check added.

## Recommendations for the maintainers

| ID | Recommendation |
|---|---|
| R1 | Turn on **two-factor authentication** on the GitHub account. Anyone who controls the account can publish an update that reaches every player. *Done.* |
| R2 | Add **branch and tag protection**: Settings → Rules → protect `main` and tags `v*` so they can't be force-pushed or deleted. Turn on **Private vulnerability reporting**, **Dependabot alerts** and **secret scanning** under Settings → Code security. *Rulesets, secret scanning and Dependabot security updates confirmed on 1 Nov 2026.* |
| R3 | **Sign the exe files** with a code-signing certificate once the project is ready for it. That removes the Windows SmartScreen warning, and a hijacked account alone couldn't produce a trusted update. *SignPath Foundation declined on 1 Nov 2026 (not enough public use yet); reapply later.* |
| R4 | ~~Put the agent behind HTTPS.~~ **Done in v0.5.0**, built into the agent (no Cloudflare or certificate needed). Remaining step for maintainers: add each agent's fingerprint to `trust/agents.jsonc`, then set `AllowPlainHttp` to `false` once players are on v0.5.0. |

## Re-running this audit

The hostile-input checks are part of `src/MMLauncher.Tests` and run on every release build. Server admins should run `update-agent.bat` (or `install-agent.bat`) once after updating to v0.4.1, so the agent folder gets locked down (A6) and the service privileges are reduced (A7).
