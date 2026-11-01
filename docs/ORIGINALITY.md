# Originality report: MM Launcher and MM Server Agent

| | |
|---|---|
| **Date** | 1 November 2026 |
| **Version checked** | `main` at v0.11.0 (85 source files, about 110,000 code tokens) |
| **Question** | Is any of this code copied, traced or reworked from DZSA Launcher, or from anyone else? |
| **Answer** | No copied code was found. Every comparison that could be run came back with only generic, unavoidable boilerplate in common. DZSA Launcher's source code isn't public, so it can't be compared directly; instead, this report shows that MM Launcher uses none of DZSA's services, files or names, and works in a different way. |

## 1. Where the code came from

- **Written for this project.** Every line was written from scratch for Misfit Mercenaries by Claude (Anthropic's AI, in Claude Code), directed by the maintainer ([meccmax](https://github.com/meccmax)). No code was pasted in from other launchers, tutorials or decompiled programs.
- **Complete public history.** The full history is in this repository, starting with the first commit on 30 October 2026 (`2f4a746`). Every commit is dated and marked with its authors (`meccmax` and Claude). The repository is original (not a fork of anything) and MIT-licensed. Anyone can see when and how each file appeared with `git log -p`.
- **Libraries are credited.** Everything not written here comes from published open-source packages, listed with their licenses in [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).
- **Images are MM's own.** The only picture files are MM's own logo (`mm.png`, `mm.ico`).

## 2. DZSA Launcher

- **DZSA's source is closed.** DZSA Launcher is distributed as a free download from dayzsalauncher.com, but its code isn't published. A GitHub search for "dzsa" finds only unrelated tools and hosting scripts, not the launcher.
- **It wasn't decompiled.** Nobody working on MM Launcher has had DZSA's code, and DZSA wasn't decompiled for this check. Decompiling it would be the wrong way to prove independence.
- **DZSA's own systems are separate.** DZSA lists servers through its own central service: server owners register at dayzsalauncher.com, and other tools read its list from `dayzsalauncher.com/api/v1` (for example [dayz-ctl](https://github.com/WoozyMasta/dayz-ctl), which credits DZSA for that API). Server hosts install a DZSA package on the server to be listed.

**MM Launcher uses none of it.** A search of every file in this repository (code, settings, scripts, docs) for `dzsa`, `dayzsalauncher`, `dzsal` and `DZSALModServer` finds only one line. It's in the security audit, which compares how DZSA and Steam handle server passwords. There are no DZSA web addresses, API calls, server packages, file or folder names, or settings anywhere in the code.

**How MM Launcher works instead.** Each piece is built on a public, documented system, not on DZSA:

| Job | How MM Launcher does it | Public basis |
|---|---|---|
| Server status, players, map, time | Valve's standard server query (A2S_INFO / A2S_RULES), parsed by our own code | Valve's [Server queries](https://developer.valvesoftware.com/wiki/Server_queries) protocol |
| A server's mod list | DayZ's mod list inside its A2S_RULES reply, read by our own parser, or our own **MM Server Agent** reading the server's startup line | DayZ's server query reply; our agent |
| List of all DayZ servers | Valve's Steam Web API `IGameServersService/GetServerList`, fetched by our agent with MM's own key | Steam Web API |
| Downloading and updating mods | The player's own Steam client, through Steamworks (`Facepunch.Steamworks`, MIT) | Steamworks UGC |
| Starting DayZ | `DayZ_BE.exe -exe DayZ_x64.exe` (BattlEye's launcher), the form Bohemia staff give for shortcuts | BattlEye / Bohemia |
| Mod folders passed to DayZ | Short links in `DayZ\!MMWorkshop\@<id>`, our own name. It follows the same idea as the **official DayZ launcher's** `!Workshop` folder. | Bohemia's official launcher |
| Friends, Workshop search, news, player history, problem reports, encryption | Our own MM Server Agent and its own endpoints (`/mm-agent/v1/...`) | Our own design |

Server browsers, automatic mod downloads and one-click joining are common to every DayZ launcher: DZSA, the official launcher, and the open-source ones listed below. Having those features in common is not copying.

## 3. Code comparison with every open-source DayZ launcher we could find

Fifteen public projects were downloaded and compared with MM Launcher's source, file by file: 1,271 of their files against 85 of ours. They cover every open-source DayZ launcher GitHub lists, a decompiled closed-source DayZ launcher (DayZavr), other C# game launchers, and the Steamworks library we use. The list is in [tools/originality/repos.txt](../tools/originality/repos.txt).

**Method** (the [tools/originality/simcheck.py](../tools/originality/simcheck.py) script; anyone can re-run it):
- Comments are removed from both sides. In code, the text inside quotes is blanked, so only the code itself is compared.
- **Exact:** the share of a file's 20-token stretches (roughly one to two lines) that appear word for word in any one file of another project.
- **Structure:** the same with every name replaced, to catch copied code where the variables were renamed.

**Results.** The highest share of any of our files found word for word elsewhere is **3.2%**. Every one of the top matches is a short fragment of boilerplate that any C# or Avalonia programmer writes the same way:

| Our file | Exact | What actually matched |
|---|---|---|
| `Workshop/WorkshopSearch.cs` | 3.2% | A list of four `("text", "text")` pairs (the sort options), the same shape as a list in a Python test file |
| `Diagnostics/ReportMeta.cs` | 2.9% | `{ get; set; } = "…"; public string Description { get; set; } = "…";` |
| `Styles.axaml` | 2.7% | Standard style lines such as `<Setter Property="FontWeight" Value="SemiBold"/>` |
| `MMServerAgent/EditorPresetFolder.cs` | 2.2% | `.Where(f => f.EndsWith("…", StringComparison.OrdinalIgnoreCase) \|\| …)` |
| `ViewModels/MainViewModel.Update.cs` | 1.3% | A one-line "shorten text to N characters" helper |
| `MMServerAgent/AgentWorker.cs` | 1.3% | Consecutive `sb.AppendLine(…)` calls |
| `Query/A2sInfo.cs` | 1.2% | `public int Players { get; init; } public int MaxPlayers { get; init; }` |
| every other file | 1.0% or less | the same kind of fragments |

**Notable checks:**
- The heart of the launcher matches nothing, or one stretch at most (0.0–0.1%): the server query client and packet reader, the DayZ mod-list parser, mod repair, the agent's server detection, its web server, encryption keys and agent trust.
- The Steam folder and VDF readers, the Steam download helper, the game launcher and the self-updater are at 0.2–0.8%, all from standard .NET calls. The one query file above 1% (`A2sInfo.cs`, 1.2%) matches only on two property declarations.
- One other open-source DayZ launcher (IconicPvE) also starts DayZ with `DayZ_BE.exe -exe DayZ_x64.exe`, because that's the documented BattlEye form. Its `GameLauncher.cs` and ours share **no** 20-token stretch at all.

## 4. GitHub-wide search for our distinctive code

GitHub's code search (all public repositories) was run on 25 lines and names that only make sense in this project. Matches that weren't from DayZ or Steam projects were checked and are unrelated (a genealogy tool, a print service, an IT-automation readme…).

| Searched for | Matches |
|---|---|
| The DayZ mod-list unescape line, `ExchangeAsync(UdpClient udp, byte[] request, TimeSpan timeout`, `IsChallenge(byte[] packet) => packet.Length >= 9`, `"Packet ended early (needed"`, `"Unexpected reply type … to info request"` | 0 |
| `DayZRulesParser`, `ModListCutOff`, `OverflowFlags` + `DlcFlags`, `DayZServerMod`, `A2S_RULES` + DayZ mods (C#) | 0 |
| `TryJunctions(… RequiredMod Mod, string Folder)`, `MoveAsideForRepair`, `LingerCloseAsync`, `HttpRequestInfo(string Method, string Path, IPAddress RemoteAddress` | 0 |
| `PresetFormat` + `ModContainer` | 0 |
| `RequiredDlcNames`, `RepairBlockedException`, `AgentTrust` + `RequiresTls`, `mm-agent/v1`, `SplitWindowsCommandLine` + `ParseOptions` | 1 to 4 each, all in unrelated, non-game projects |
| `VdfNode? this[string key]` | 24: a common one-line way to read Valve's VDF format, used by many Steam tools. Our `Vdf.cs` shares 0.2% with the closest DayZ one. |
| `DayZ_BE.exe` + `-exe` + `DayZ_x64.exe` (C#) | 1: IconicPvE, covered above, with no shared code |
| `steamProtocolMaxDataSize`, `WorkshopItemsInstalled` | Names defined by Bohemia and Valve, used by many tools |

GitHub hasn't indexed this repository yet, so none of the hits are MM Launcher itself.

## 5. What this does and doesn't prove

- **What it proves.** None of this code is a 1:1 copy of, or a renamed rework of, any open-source DayZ launcher or tool on GitHub. MM Launcher contains no DZSA code, service, file or name, and it works through public Valve, Steam, BattlEye and Bohemia systems plus its own agent. The complete history of how it was written is public.
- **What no check can do.** It can't compare against code that has never been published, like DZSA's. What it can show is that MM Launcher doesn't depend on, call or imitate anything of DZSA's, and that its code is unique even on GitHub as a whole.
- **About AI-written code.** The code was written by an AI that learned from public code. That's why it was checked for word-for-word matches against public code (sections 3 and 4) rather than taken on trust.

## Re-running this check

```
git clone --depth 1 https://github.com/<each name in tools/originality/repos.txt>   # into one folder, e.g. other/
python tools/originality/simcheck.py src other
python tools/originality/simcheck.py --show src/<our file> other/<their file>   # see exactly what matched
```
