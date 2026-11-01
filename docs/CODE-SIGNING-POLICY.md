# Code signing policy

**Status:** applied to the SignPath Foundation's free program for open-source projects. Until it is approved, releases are unsigned (Windows SmartScreen may warn about them), and each release's `SHA256SUMS.txt` is the way to check the files.

Once approved: free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

## What gets signed

Only files built from this repository's source, by the public [release workflow](../.github/workflows/release.yml) on GitHub Actions:

- `MMLauncher.exe` (portable launcher)
- `MMLauncher-Setup.exe` (installer, and the launcher inside it)
- `MMServerAgent.exe` (in `MMServerAgent.zip`)

Third-party libraries are included as published by their own authors; this project never signs other people's code.

## Team and roles

| Role | Who | What it means |
|------|-----|---------------|
| Author | [meccmax](https://github.com/meccmax) | Writes and commits the source code |
| Reviewer | [meccmax](https://github.com/meccmax) | Reviews every change from anyone outside the team before it's merged |
| Approver | [meccmax](https://github.com/meccmax) | Approves each signing request by hand, one release at a time |

All team members use multi-factor authentication on GitHub (and on SignPath, once set up).

## Releases

Every release is built from a tagged commit on `main` by the release workflow, which also runs the project's self-checks and a test install of the installer. Nothing is built or signed on a personal computer. A signing request is approved by hand only after checking that it comes from that workflow run.

## Privacy

MM Launcher sends anonymous usage stats only if the player says yes (it asks once, and nothing is sent before they answer; changeable any time in Settings). Otherwise there is no analytics or telemetry. What it sends, and where:

- **MM Launcher**
  - **Game servers:** standard Steam status queries (name, players, mods) to the DayZ servers you look at.
  - **MM server agents** (Misfit Mercenaries' own servers): asks for server details, editor presets, the server list and Workshop searches. For the Friends tab, it sends your public Steam ID so the agent can ask Steam which friends are playing. **Report a problem** sends logs only when you click Send; home-folder names are removed first, and you can save the report as a zip instead.
  - **Steam:** public Workshop item lookups. Downloads, updates and removals go through your own Steam client.
  - **Usage stats (only if the player said yes):** launcher and Windows version, joins and play time on MM servers, how many times other servers were joined (never which), mod download times, errors and crashes. Tagged with a random ID that's deleted when stats are turned off. Sent only to MM's own server agents, over their encrypted connection. The agents keep no names, Steam IDs or IP addresses.
  - **GitHub:** checks this repository's releases for updates (downloading one only when you click to install it), and reads the list of trusted MM agents (`trust/agents.jsonc`).
- **MM Server Agent** (run by server admins): sends the Steam Web API requests the admin turns on (server list, friends, Workshop search), and posts problem reports to the admin's own Discord webhook if one is set. Usage stats it receives stay on that server (the `usage` folder) and are deleted after `KeepUsageDays`.

Nothing else is sent anywhere. The installer can be removed in Windows Settings > Apps > Installed apps (or with the uninstaller in the install folder). The portable `MMLauncher.exe` is a single file you can delete. Settings live in `%APPDATA%\MMLauncher`.

## Reporting a problem

Signed files that misbehave, or anything that looks like misuse of the certificate: see [SECURITY.md](../SECURITY.md) to report it privately.
