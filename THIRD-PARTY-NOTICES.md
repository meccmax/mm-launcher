# Third-party notices

MM Launcher and MM Server Agent are MIT-licensed (see [LICENSE](LICENSE)). They are built with the open-source packages below, which are included in the downloads under their own licenses. None of them is a DayZ launcher. They are general libraries for windows, graphics, text, Steam and Windows services.

| Package | Used for | License | Copyright |
|---|---|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Skia, Avalonia.Win32 (and its other platform parts) | The launcher's window and controls | MIT | © The AvaloniaUI Project |
| Avalonia.Fonts.Inter | Font package | MIT (the Inter typeface itself: SIL Open Font License 1.1, © The Inter Project Authors) | © The AvaloniaUI Project |
| Avalonia.Angle.Windows.Natives | Graphics on Windows (Google's ANGLE) | BSD-3-Clause | © The ANGLE Project Authors / The AvaloniaUI Project |
| CommunityToolkit.Mvvm | View-model helpers | MIT | © .NET Foundation and Contributors |
| Facepunch.Steamworks | Talking to the player's Steam client (mod downloads) | MIT | © Facepunch Studios |
| steam_api64.dll (inside Facepunch.Steamworks) | Valve's Steamworks API library | Steamworks SDK license (redistributable) | © Valve Corporation |
| SkiaSharp, HarfBuzzSharp | Drawing and text shaping | MIT | © Microsoft Corporation (Skia: BSD-3-Clause, © Google; HarfBuzz: Old MIT) |
| MicroCom.Runtime | Windows interop used by Avalonia | MIT | © Nikita Tsukanov |
| Tmds.DBus.Protocol | Linux desktop interop used by Avalonia | MIT | © Tom Deseyn |
| System.Management, System.CodeDom | Reading process details on Windows (agent) | MIT | © Microsoft Corporation |
| Microsoft.Extensions.Hosting.WindowsServices | Running the agent as a Windows service | MIT | © Microsoft Corporation |
| .NET runtime | Everything | MIT | © .NET Foundation and Contributors |

The MM Server Agent downloads (it isn't included in any download) **DB-IP's IP to Country Lite** database to show which country each server is in: [IP Geolocation by DB-IP](https://db-ip.com), licensed under [Creative Commons Attribution 4.0](https://creativecommons.org/licenses/by/4.0/). The launcher credits it in the Country filter's tooltip.

The Windows installer (`MMLauncher-Setup.exe`) is built with [Inno Setup](https://jrsoftware.org/isinfo.php) (© Jordan Russell and Martijn Laan; free for any use).

The full license texts come with each package on [nuget.org](https://www.nuget.org/). This list was checked against each package's own license information on 1 November 2026.
