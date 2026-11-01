using System.Net;
using MMLauncher.Core.Agent;
using MMLauncher.Core.Config;
using MMLauncher.Core.DayZ;
using MMLauncher.Core.Discovery;
using MMLauncher.Core.Launch;
using MMLauncher.Core.Query;
using MMLauncher.Core.Services;
using MMLauncher.Core.Steam;
using MMLauncher.Core.Updates;
using MMLauncher.Core.Workshop;

int failures = 0;
void Check(bool ok, string what)
{
    Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
    Console.Write(ok ? "  PASS " : "  FAIL ");
    Console.ResetColor();
    Console.WriteLine(what);
    if (!ok) failures++;
}

string host = args.Length > 0 ? args[0] : "localhost";
int start = args.Length > 1 ? int.Parse(args[1]) : 2290;
int end = args.Length > 2 ? int.Parse(args[2]) : 2340;
bool fake = host == "localhost";

Console.WriteLine("== Escaping / mod blob ==");
Check(DayZRulesParser.Unescape([0x01, 0x01, 0x01, 0x02, 0x01, 0x03, 0x41]).SequenceEqual(new byte[] { 0x01, 0x00, 0xFF, 0x41 }),
    "01 01 -> 01, 01 02 -> 00, 01 03 -> FF");
Check(DayZRulesParser.Unescape([0x01, 0x05]).SequenceEqual(new byte[] { 0x01, 0x05 }), "unknown escape left alone");

// Hand-built blob: version 2, no overflow, Badlands DLC (one hash), one mod (CF), no signatures.
byte[] blob =
[
    2, 0, 4, 0, 0xAA, 0xBB, 0xCC, 0xDD,
    1, 0x11, 0x22, 0x33, 0x44, 4, 4, 176, 239, 92, 2, (byte)'C', (byte)'F',
    0,
];
var parsed = DayZRulesParser.ParseBlob(blob);
Check(parsed.Mods.Count == 1 && parsed.Mods[0].WorkshopId == 1559212036 && parsed.Mods[0].Name == "CF", "decodes mod id + name");
Check(parsed.RequiredDlcNames.SequenceEqual(["Badlands"]), "Badlands DLC flag");
Check(!parsed.MayBeTruncated, "complete blob not flagged as truncated");
var cut = DayZRulesParser.ParseBlob(blob[..14]);
Check(cut.MayBeTruncated && cut.Mods.Count == 0, "cut-off blob flagged as truncated, no crash");
var flagged = blob.ToArray(); flagged[1] = 1; // overflow flag set, mod list complete (seen on real MM Deer Isle)
Check(!DayZRulesParser.ParseBlob(flagged).MayBeTruncated, "overflow flag alone doesn't flag a complete mod list");
var noSig = DayZRulesParser.ParseBlob(blob[..^1]); // ends right after the mods, signatures cut
Check(noSig.Mods.Count == 1 && !noSig.MayBeTruncated, "cut in signatures/description is harmless");

var tags = DayZTags.Parse("battleye,no3rd,external,privHive,shard,lqs3,etm4.000000,entm8.000000,mod,14:32");
Check(tags is { TimeOfDay: "14:32", Queue: 3, FirstPersonOnly: true, BattlEye: true, DayAcceleration: 4, NightAcceleration: 8 },
    "DayZ keyword tags");

Console.WriteLine("== VDF ==");
var vdf = VdfNode.Parse("\"libraryfolders\" { \"0\" { \"path\" \"C:\\\\Program Files (x86)\\\\Steam\" \"apps\" { \"221100\" \"123\" } } }");
Check(vdf["libraryfolders"]?["0"]?["path"]?.Value == @"C:\Program Files (x86)\Steam", "library path with escaped backslashes");

Console.WriteLine($"== Discovery: {host} {start}-{end} ==");
var progress = new Progress<string>(m => Console.WriteLine("   ... " + m));
var sw = System.Diagnostics.Stopwatch.StartNew();
var found = await ServerScanner.ScanAsync([host], start, end, progress);
Console.WriteLine($"   scan took {sw.Elapsed.TotalSeconds:0.0}s, found {found.Count}");
foreach (var s in found)
    Console.WriteLine($"   {s.Host}:{s.QueryPort} (game {s.Info.GamePort}) {s.Info.Name} [{s.Info.Map}] {s.Info.Players}/{s.Info.MaxPlayers} {s.PingMs}ms");
if (fake)
{
    Check(found.Count == 3, "found all 3 fake servers, ignored the junk responder");
    Check(found.Any(s => s.QueryPort == 2305 && s.Info.GamePort == 2302), "challenge server answered after handshake");
}
else Check(found.Count > 0, "found at least one server");

Console.WriteLine("== Per-server info + rules ==");
foreach (var s in found)
{
    var ep = new IPEndPoint(s.Address, s.QueryPort);
    var (info, ping) = await A2sClient.QueryInfoAsync(ep);
    var rules = DayZRulesParser.Parse(await A2sClient.QueryRulesAsync(ep));
    Console.WriteLine($"   {info.Name}: {ping}ms, {rules.Mods.Count} mods, DLC [{string.Join(",", rules.RequiredDlcNames)}], island={rules.Plain.GetValueOrDefault("island")}, desc=\"{rules.Description}\"");
    if (fake)
    {
        int expected = s.QueryPort switch { 2305 => 12, 2315 => 3, 2325 => 80, _ => -1 };
        Check(rules.Mods.Count == expected, $"{s.QueryPort}: {expected} mods decoded" + (expected == 80 ? " (split reply reassembled)" : ""));
        Check(rules.Mods.All(m => m.WorkshopId > 1_000_000 && m.Name.Length > 0), $"{s.QueryPort}: ids and names look sane");
    }
}

Console.WriteLine("== Steam Workshop lookup ==");
try
{
    var ws = new WorkshopClient(new HttpClient());
    var items = await ws.GetDetailsAsync([1559212036UL, 1828439124UL, 1UL]);
    foreach (var i in items.Values)
        Console.WriteLine($"   {i.Id}: exists={i.Exists} \"{i.Title}\" {i.SizeBytes / 1024 / 1024.0:0.0} MB updated {i.Updated:yyyy-MM-dd}");
    Check(items.TryGetValue(1559212036, out var cf) && cf.Exists && cf.Title.Length > 0, "CF found on workshop");
    Check(items.TryGetValue(1, out var bogus) && !bogus.Exists, "bogus id reported as unavailable");
}
catch (Exception ex)
{
    Check(false, "workshop lookup threw: " + ex.Message);
}

Console.WriteLine("== Local Steam detection (fake folder) ==");
var root = Path.Combine(Path.GetTempPath(), "mml-fake-steam");
if (Directory.Exists(root)) Directory.Delete(root, true);
var lib2 = Path.Combine(root, "Library2");
Directory.CreateDirectory(Path.Combine(root, "Steam", "steamapps"));
Directory.CreateDirectory(Path.Combine(lib2, "steamapps", "common", "DayZ"));
File.WriteAllText(Path.Combine(root, "Steam", "steamapps", "libraryfolders.vdf"),
    $"\"libraryfolders\"\n{{\n \"0\" {{ \"path\" \"{Path.Combine(root, "Steam").Replace("\\", "\\\\")}\" }}\n \"1\" {{ \"path\" \"{lib2.Replace("\\", "\\\\")}\" }}\n}}");
File.WriteAllText(Path.Combine(lib2, "steamapps", "appmanifest_221100.acf"), "\"AppState\" { \"appid\" \"221100\" \"installdir\" \"DayZ\" }");
var content = Path.Combine(lib2, "steamapps", "workshop", "content", "221100");
Directory.CreateDirectory(Path.Combine(content, "1559212036", "addons"));
File.WriteAllText(Path.Combine(content, "1559212036", "addons", "cf.pbo"), "x");
Directory.CreateDirectory(Path.Combine(content, "1828439124", "addons")); // only empty folders = interrupted download
Directory.CreateDirectory(Path.Combine(content, "1828439124")); // empty = incomplete download
File.WriteAllText(Path.Combine(lib2, "steamapps", "workshop", "appworkshop_221100.acf"),
    "\"AppWorkshop\" { \"WorkshopItemsInstalled\" { \"1559212036\" { \"size\" \"538560\" \"timeupdated\" \"1500000000\" } } }");
var local = LocalSteam.Detect(Path.Combine(root, "Steam"));
Check(local != null && local.DayZPath != null && local.DayZPath.StartsWith(lib2), "DayZ found in second library");
Check(local != null && local.Mods.ContainsKey(1559212036) && !local.Mods.ContainsKey(1828439124), "installed mod found; empty folder and file-less folders ignored");
var oldCf = local?.Mods.GetValueOrDefault(1559212036UL);
Check(ModStatus.Evaluate(oldCf, new WorkshopItem(1559212036, true, "CF", 1, DateTimeOffset.UtcNow, null), true) == ModState.UpdateAvailable,
    "old local copy -> update available");
Check(ModStatus.Evaluate(null, null, true) == ModState.Missing, "not downloaded -> missing");

Console.WriteLine("== Settings file ==");
Environment.SetEnvironmentVariable("MMLAUNCHER_DATA", Path.Combine(root, "data"));
var settings = LauncherSettings.Load();
var text = File.ReadAllText(LauncherSettings.FilePath);
Check(text.Contains("// MM Launcher settings") && settings.Hosts.Count == 2, "settings.jsonc written with comments and defaults");
var reread = LauncherSettings.Load();
Check(reread.ScanPortStart == 2000 && reread.ScanPortEnd == 3000, "commented file reads back");

Console.WriteLine("== MM Server Agent parsing ==");
var cmd = MMServerAgent.ServerDetector.SplitWindowsCommandLine(
    "\"C:\\Apps\\Installs\\CFTools\\Agent\\deployments\\DayZ DI\\DayZServer_x64.exe\" -config=serverDZ.cfg -port=2902 \"-mod=@CF;@Dabs Framework;@MM Weapons 2.0\" -servermod=@Srv -profiles=\"C:\\x y\\profiles\" -dologs");
Check(cmd.Count == 7 && cmd[0].EndsWith("DayZServer_x64.exe") && cmd[3] == "-mod=@CF;@Dabs Framework;@MM Weapons 2.0", "Windows command line split like Windows does");
var opts = MMServerAgent.ServerDetector.ParseOptions(cmd.Skip(1));
Check(opts["port"] == "2902" && opts["profiles"] == @"C:\x y\profiles" && opts.ContainsKey("dologs"), "-key=value options parsed");
Check(MMServerAgent.ServerDetector.SplitModList(opts["mod"]).SequenceEqual(["@CF", "@Dabs Framework", "@MM Weapons 2.0"]), "mod list split on ;");
var cfg = MMServerAgent.ServerDetector.ReadServerCfg(
    "hostname = \"MM // not a comment\"; // trailing\n// steamQueryPort = 1;\n/* maxPlayers = 1; */\nmaxPlayers = 60;\nsteamQueryPort = 2903;\nclass Missions { class DayZ { template=\"dayzOffline.deerisle\"; }; };");
Check(cfg is { Hostname: "MM // not a comment", MaxPlayers: 60, SteamQueryPort: 2903, Map: "deerisle" }, "serverDZ.cfg read, commented-out lines ignored");

Console.WriteLine("== Launch command ==");
foreach (var raw in new[] { "-mod=!MMWorkshop\\@1;!MMWorkshop\\@2", "-password=pa ss\"word", @"C:\a b\", "plain" })
{
    var line = MMLauncher.Core.Launch.GameLauncher.Quote(raw);
    var back = MMServerAgent.ServerDetector.SplitWindowsCommandLine("x.exe " + line).Skip(1).Single();
    Check(back == raw, $"quote round-trips: {raw}  ->  {line}");
}
{
    var lsteam = LocalSteam.Detect(Path.Combine(root, "Steam"))!;
    File.WriteAllText(Path.Combine(lsteam.DayZPath!, "DayZ_BE.exe"), "");
    var ls = new LauncherSettings { LaunchMethod = "Direct", ModFolderMode = "Absolute" };
    var cmd2 = MMLauncher.Core.Launch.GameLauncher.Build(
        new MMLauncher.Core.Launch.LaunchRequest("1.2.3.4", 2402, [new(1559212036, "CF")], null), ls, lsteam);
    Console.WriteLine("   " + cmd2);
    Check(cmd2.FileName.EndsWith("DayZ_BE.exe") && cmd2.Arguments[0] == "-exe" && cmd2.Arguments[1] == "DayZ_x64.exe"
          && cmd2.Arguments.Contains("-connect=1.2.3.4") && cmd2.Arguments.Contains("-port=2402") && !cmd2.Arguments.Contains("-nolauncher"),
        "Direct launch: DayZ_BE.exe -exe DayZ_x64.exe + server args");
}
{
    // Old settings file (no SettingsVersion, LaunchMethod Steam) is moved to Direct.
    File.WriteAllText(LauncherSettings.FilePath, "{ \"LaunchMethod\": \"Steam\", \"Hosts\": [\"x\"] }");
    var migrated = LauncherSettings.Load();
    Check(migrated.LaunchMethod == "Direct" && File.ReadAllText(LauncherSettings.FilePath).Contains("\"SettingsVersion\": 4"), "old settings migrated to Direct launch");
    File.WriteAllText(LauncherSettings.FilePath, File.ReadAllText(LauncherSettings.FilePath).Replace("\"LaunchMethod\": \"Direct\"", "\"LaunchMethod\": \"Steam\""));
    Check(LauncherSettings.Load().LaunchMethod == "Steam", "a deliberate Steam choice in a new file is kept");
    // v0.4.x file (version 2): rewritten so the new AutoRejoin option and its comment appear; choices kept.
    File.WriteAllText(LauncherSettings.FilePath, "{ \"LaunchMethod\": \"Steam\", \"Hosts\": [\"x\"], \"SettingsVersion\": 2 }");
    var v2 = LauncherSettings.Load();
    var v2text = File.ReadAllText(LauncherSettings.FilePath);
    Check(v2.LaunchMethod == "Steam" && !v2.AutoRejoin && v2text.Contains("\"AutoRejoin\": false") && v2text.Contains("// Rejoin automatically")
          && v2text.Contains("\"SettingsVersion\": 4") && v2text.Contains("\"UsageStats\": \"ask\"") && v2text.Contains("\"Theme\": \"mm-orange\""), "older settings gain AutoRejoin (off), Theme and UsageStats (ask) and keep choices");
    v2.AutoRejoin = true; v2.Save();
    Check(LauncherSettings.Load().AutoRejoin, "AutoRejoin choice is saved");
}

Console.WriteLine("== Folder overrides + mod file check ==");
{
    var lib2b = Path.Combine(root, "Library2");
    var dayzDir = Path.Combine(lib2b, "steamapps", "common", "DayZ");
    var contentDir = Path.Combine(lib2b, "steamapps", "workshop", "content", "221100");
    // Manual DayZ + workshop folders, no Steam folder at all.
    var manual = LocalSteam.Detect(Path.Combine(root, "no-such-steam"), dayzDir, contentDir);
    Check(manual != null && manual.DayZIsManual && manual.WorkshopIsManual && manual.DayZPath == dayzDir && manual.Mods.ContainsKey(1559212036),
        "manual DayZ + workshop folders work without Steam");
    var auto = LocalSteam.Detect(Path.Combine(root, "Steam"));
    Check(auto?.WorkshopPath == contentDir && !auto.WorkshopIsManual, "workshop folder found automatically from DayZ's library");

    // A healthy signed mod, then break it in the usual ways.
    var mod = Path.Combine(contentDir, "999000111");
    var addonsDir = Path.Combine(mod, "addons");
    Directory.CreateDirectory(addonsDir);
    File.WriteAllText(Path.Combine(addonsDir, "a.pbo"), "x");
    File.WriteAllText(Path.Combine(addonsDir, "a.pbo.mm.bisign"), "s");
    File.WriteAllText(Path.Combine(addonsDir, "b.pbo"), "x");
    File.WriteAllText(Path.Combine(addonsDir, "b.pbo.mm.bisign"), "s");
    Check(MMLauncher.Core.Launch.ModHealth.Check(999000111, mod).Ok, "healthy signed mod passes");
    File.Delete(Path.Combine(addonsDir, "b.pbo.mm.bisign"));
    var r1 = MMLauncher.Core.Launch.ModHealth.Check(999000111, mod);
    Check(!r1.Ok && r1.Summary.Contains("signature") && r1.Summary.Contains("b.pbo"), "missing .bisign detected: " + r1.Summary);
    File.WriteAllText(Path.Combine(addonsDir, "b.pbo"), "");
    Check(MMLauncher.Core.Launch.ModHealth.Check(999000111, mod).Summary.Contains("empty"), "empty .pbo detected");
    // An unsigned-by-design mod (no .bisign at all) is not flagged.
    var unsignedMod = Path.Combine(contentDir, "999000222", "Addons");
    Directory.CreateDirectory(unsignedMod);
    File.WriteAllText(Path.Combine(unsignedMod, "c.pbo"), "x");
    Check(MMLauncher.Core.Launch.ModHealth.Check(999000222, Path.GetDirectoryName(unsignedMod)!).Ok, "mod that ships no signatures isn't flagged (and 'Addons' casing is fine)");

    // Repair step 1: the whole folder moves aside in one go; nothing else is touched.
    var s2 = LocalSteam.Detect(Path.Combine(root, "Steam"))!;
    var oldCopy = MMLauncher.Core.Launch.ModHealth.MoveAsideForRepair(s2, 999000111, "Test mod");
    Check(oldCopy != null && !Directory.Exists(mod) && Directory.Exists(oldCopy) && File.Exists(Path.Combine(oldCopy, "addons", "a.pbo"))
          && Directory.Exists(Path.Combine(contentDir, "1559212036")),
        "repair moves only that mod's folder aside, intact");
    // Download "failed": the old copy goes back.
    Check(MMLauncher.Core.Launch.ModHealth.RestoreIfMissing(s2, 999000111, oldCopy!) && File.Exists(Path.Combine(mod, "addons", "a.pbo")),
        "failed repair restores the old copy");
    // Download "worked": a fresh folder is there, so the old copy is not restored and the trash is emptied.
    oldCopy = MMLauncher.Core.Launch.ModHealth.MoveAsideForRepair(s2, 999000111, "Test mod");
    Directory.CreateDirectory(Path.Combine(mod, "addons"));
    File.WriteAllText(Path.Combine(mod, "addons", "fresh.pbo"), "new");
    Check(!MMLauncher.Core.Launch.ModHealth.RestoreIfMissing(s2, 999000111, oldCopy!), "successful repair keeps the fresh copy");
    Check(MMLauncher.Core.Launch.ModHealth.PurgeTrash(s2) == 0 && !Directory.Exists(MMLauncher.Core.Launch.ModHealth.TrashFolder(s2)),
        "repair trash emptied afterwards");
    // A link inside the workshop folder is removed as a link, its target survives.
    var outside = Path.Combine(root, "outside-target");
    Directory.CreateDirectory(outside);
    File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
    Directory.CreateSymbolicLink(Path.Combine(contentDir, "999000333"), outside);
    MMLauncher.Core.Launch.ModHealth.MoveAsideForRepair(s2, 999000333, "Linked mod");
    Check(File.Exists(Path.Combine(outside, "keep.txt")) && !Directory.Exists(Path.Combine(contentDir, "999000333")),
        "repair never follows a link into another folder");
}

Console.WriteLine("== Problem report builder ==");
{
    // Uses copies of real DayZ logs if MMLAUNCHER_TEST_DAYZLOGS points at a folder of them.
    var logs = Environment.GetEnvironmentVariable("MMLAUNCHER_TEST_DAYZLOGS") ?? Path.Combine(root, "dayzlogs");
    Directory.CreateDirectory(logs);
    if (!Directory.EnumerateFiles(logs, "*.RPT").Any())
    {
        File.WriteAllText(Path.Combine(logs, "DayZ_x64_2026-10-30_17-54-58.RPT"),
            "== C:\\Users\\Max\\x\n17:55:54.530 [ErrorModuleHandler] :: Error thrown: 0x000400F0 (Game restart required)\n");
        File.WriteAllText(Path.Combine(logs, "crash_2026-10-30_17-54-58.log"),
            "Virtual Machine Exception\n\nReason: NULL pointer to instance\n\nClass:      'ExpansionP2PTraderAIIrena'\nFunction: 'eAIBase'\nStack trace:\nAirborneAI/scripts/4_World/entity.c:13 Function eAIBase\n");
    }
    // An older session's files must not be included.
    File.WriteAllText(Path.Combine(logs, "script_2020-01-01_00-00-00.log"), "old session");
    var plan = MMLauncher.Core.Diagnostics.ReportBuilder.Plan(logs, null);
    foreach (var f in plan.Files) Console.WriteLine($"   {f.ZipName}  {f.Size / 1024} KB  ({f.Description})");
    Console.WriteLine($"   crash: {plan.CrashSummary}");
    Console.WriteLine($"   codes: {string.Join("; ", plan.ErrorCodes)}");
    Check(plan.Files.Any(f => f.ZipName.EndsWith(".RPT")) && plan.Files.All(f => !f.ZipName.Contains("2020-01-01")), "latest session's logs picked, older ones left out");
    Check(plan.CrashSummary.Contains("NULL pointer") && plan.CrashSummary.Contains("ExpansionP2PTraderAIIrena") && plan.CrashSummary.Contains("entity.c:13"),
        "crash log summarized");
    Check(plan.ErrorCodes.Any(c => c.StartsWith("0x000400F0")), "on-screen error code found");

    var meta = new MMLauncher.Core.Diagnostics.ReportMeta { Player = "Max", Description = "test @everyone", Created = DateTimeOffset.Now, ServerName = "MM Test" };
    var zipBytes = MMLauncher.Core.Diagnostics.ReportBuilder.Build(plan, meta, true, new Dictionary<string, string> { ["server/mods.txt"] = "1 | 1559212036 | CF" });
    using (var z = new System.IO.Compression.ZipArchive(new MemoryStream(zipBytes)))
    {
        var names = z.Entries.Select(e => e.FullName).ToList();
        Console.WriteLine($"   zip {zipBytes.Length / 1024} KB: {string.Join(", ", names)}");
        Check(names.Contains("report.json") && names.Contains("report.txt") && names.Contains("server/mods.txt"), "zip has summary + mod list");
        var allText = string.Join("\n", z.Entries.Where(e => !e.Name.EndsWith(".mdmp")).Select(e => new StreamReader(e.Open()).ReadToEnd()));
        Check(!System.Text.RegularExpressions.Regex.IsMatch(allText, @"[\\/]Users[\\/](?!<user>)\w"), "no real \\Users\\<name> path left in any text file");
    }
    var bigPlan = new MMLauncher.Core.Diagnostics.ReportPlan { Files = [new("dayz/huge.log", Path.Combine(root, "huge.log"), 0, "big", false)] };
    File.WriteAllText(Path.Combine(root, "huge.log"), string.Concat(Enumerable.Range(0, 400_000).Select(i => $"line {i} {Guid.NewGuid()}\n")));
    var small = MMLauncher.Core.Diagnostics.ReportBuilder.Build(bigPlan, new() { Created = DateTimeOffset.Now }, false, new Dictionary<string, string>(), 2 * 1024 * 1024);
    Check(small.Length <= 2 * 1024 * 1024, $"oversized logs trimmed to fit the limit ({small.Length / 1024} KB)");
    File.WriteAllBytes(Path.Combine(root, "report-test.zip"), zipBytes);
}

Console.WriteLine("== Update checker (fake GitHub release) ==");
{
    var good = System.Text.Encoding.UTF8.GetBytes("pretend this is MMLauncher.exe v9.9.9");
    var goodHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(good)).ToLowerInvariant();
    const string Base = "http://127.0.0.1:8097/";
    string releaseJson = $$"""
    { "tag_name": "v9.9.9", "draft": false, "prerelease": false, "body": "- Faster joins", "html_url": "{{Base}}page",
      "assets": [
        { "name": "MMLauncher.exe", "browser_download_url": "{{Base}}MMLauncher.exe", "size": {{good.Length}} },
        { "name": "Tampered.exe", "browser_download_url": "{{Base}}Tampered.exe", "size": 5 },
        { "name": "SHA256SUMS.txt", "browser_download_url": "{{Base}}SHA256SUMS.txt", "size": 200 } ] }
    """;
    var files = new Dictionary<string, byte[]>
    {
        ["/latest"] = System.Text.Encoding.UTF8.GetBytes(releaseJson),
        ["/MMLauncher.exe"] = good,
        ["/Tampered.exe"] = System.Text.Encoding.UTF8.GetBytes("evil!"),
        ["/SHA256SUMS.txt"] = System.Text.Encoding.UTF8.GetBytes($"{goodHash}  MMLauncher.exe\n{goodHash} *Tampered.exe\n"),
    };
    var listener = new HttpListener();
    listener.Prefixes.Add(Base);
    listener.Start();
    var serve = Task.Run(async () =>
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); } catch { break; }
            if (files.TryGetValue(ctx.Request.Url!.AbsolutePath, out var body)) { ctx.Response.StatusCode = 200; await ctx.Response.OutputStream.WriteAsync(body); }
            else ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }
    });

    Environment.SetEnvironmentVariable("MMLAUNCHER_UPDATE_URL", Base + "latest");
    var checker = new UpdateChecker(new HttpClient());
    var rel = await checker.GetLatestAsync();
    Check(rel != null && rel.Version == new Version(9, 9, 9) && rel.Assets.ContainsKey("MMLauncher.exe"), "latest release read (v9.9.9, assets listed)");
    Check(UpdateChecker.IsNewer(new Version(0, 5, 0), new Version(0, 4, 0)) && !UpdateChecker.IsNewer(new Version(0, 4, 0), new Version(0, 4, 0))
          && UpdateChecker.IsNewer(new Version(0, 10, 0), new Version(0, 9, 3)), "version comparison (0.10 > 0.9)");
    Check(UpdateChecker.TryParseVersion("v1.2.3-beta", out var pv) && pv == new Version(1, 2, 3), "tag parsing");

    var dest = Path.Combine(root, "upd", "MMLauncher.exe");
    await checker.DownloadVerifiedAsync(rel!, "MMLauncher.exe", dest);
    Check(File.ReadAllBytes(dest).SequenceEqual(good), "download verified against SHA256SUMS");

    var bad = Path.Combine(root, "upd", "Tampered.exe");
    bool refused = false;
    try { await checker.DownloadVerifiedAsync(rel!, "Tampered.exe", bad); } catch (InvalidDataException) { refused = true; }
    Check(refused && !File.Exists(bad) && !File.Exists(bad + ".part"), "tampered download refused and deleted");

    files["/latest"] = System.Text.Encoding.UTF8.GetBytes("""{ "tag_name": "v10.0.0", "prerelease": true, "assets": [] }""");
    Check(await checker.GetLatestAsync() == null, "pre-releases ignored");
    listener.Stop();
    Environment.SetEnvironmentVariable("MMLAUNCHER_UPDATE_URL", null);
}

Console.WriteLine("== Hardening checks ==");
{
    string bigName = new string('N', 5000) + "\r\nFAKE LOG LINE", bigMsg = new string('M', 9000), bigMod = new string('x', 9000);
    var hostileMods = Enumerable.Range(0, 2000).Select(j => new AgentMod { Id = (ulong)j, Name = bigMod }).ToList();
    var hostile = new AgentResponse
    {
        Servers = Enumerable.Range(0, 100).Select(i => new AgentServerInfo { Name = bigName, Message = bigMsg, Mods = hostileMods }).ToList(),
    };
    var clean = AgentClient.Sanitize(hostile);
    Check(clean.Servers.Count <= 32 && clean.Servers.All(s => s.Name.Length <= 200 && !s.Name.Contains('\n') && (s.Message?.Length ?? 0) <= 500
          && s.Mods.Count <= 300 && s.Mods.All(m => m.Name.Length <= 200)), "hostile agent reply clipped (servers, mods, text, control chars)");
    Check(UpdateChecker.IsTrustedDownload("https://github.com/meccmax/mm-launcher/releases/download/v1/MMLauncher.exe")
          && UpdateChecker.IsTrustedDownload("https://objects.githubusercontent.com/x")
          && !UpdateChecker.IsTrustedDownload("http://github.com/x") && !UpdateChecker.IsTrustedDownload("https://evil.example/MMLauncher.exe")
          && !UpdateChecker.IsTrustedDownload("https://github.com.evil.example/x"), "update downloads only from GitHub over HTTPS");
    Check(UpdateChecker.IsSafeLink("https://github.com/meccmax/mm-launcher/releases") && !UpdateChecker.IsSafeLink("file:///C:/Windows/System32/calc.exe")
          && !UpdateChecker.IsSafeLink("https://evil.example"), "only GitHub/Steam links are opened");
    var pwCmd = new MMLauncher.Core.Launch.LaunchCommand(@"C:\DayZ\DayZ_BE.exe", ["-connect=1.2.3.4", "-password=my secret \"pass\"", "-mod=@1"], @"C:\DayZ");
    var logged = MMLauncher.Core.Launch.GameLauncher.RedactPassword(pwCmd);
    Check(!logged.Contains("secret") && !logged.Contains("pass\\") && logged.Contains("-password=***") && logged.Contains("-connect=1.2.3.4"),
          "server password hidden in the launcher log (spaces and quotes)");
}

Console.WriteLine("== Encrypted agent connection ==");
{
    var dir = Path.Combine(root, "trust");
    if (Directory.Exists(dir)) Directory.Delete(dir, true);
    Directory.CreateDirectory(dir);
    using var certA = MMServerAgent.TlsIdentity.Load(MMServerAgent.TlsIdentity.Create());
    using var certB = MMServerAgent.TlsIdentity.Load(MMServerAgent.TlsIdentity.Create());
    string fpA = AgentTrust.Fingerprint(certA), fpB = AgentTrust.Fingerprint(certB);
    Check(fpA.Length == 43 && fpA != fpB, "agent keys get distinct 43-character fingerprints");

    var pfxPath = Path.Combine(dir, "agent-tls.pfx");
    using (var made = MMServerAgent.TlsIdentity.LoadOrCreate(pfxPath))
    using (var again = MMServerAgent.TlsIdentity.LoadOrCreate(pfxPath))
        Check(AgentTrust.Fingerprint(made) == AgentTrust.Fingerprint(again) && MMServerAgent.TlsIdentity.ReadFingerprint(pfxPath) == AgentTrust.Fingerprint(made),
              "agent key is made once and kept");

    Func<MMServerAgent.HttpRequestInfo, Task<MMServerAgent.HttpReply>> handler = r => Task.FromResult(r.Method == "POST"
        ? new MMServerAgent.HttpReply(200, "application/json", System.Text.Json.JsonSerializer.Serialize(new ReportUploadResult { Ok = true, Message = r.Encrypted ? "enc" : "plain" }, AgentResponse.Json))
        : new MMServerAgent.HttpReply(200, "application/json", System.Text.Json.JsonSerializer.Serialize(
            new AgentResponse { Version = "t", Servers = [new AgentServerInfo { Name = r.Encrypted ? "enc" : "plain" }] }, AgentResponse.Json)));
    using var stop = new CancellationTokenSource();
    _ = new MMServerAgent.MiniHttpServer(2988, 1 << 20, handler, null, certA, allowPlain: true).RunAsync(stop.Token);   // new agent
    _ = new MMServerAgent.MiniHttpServer(2987, 1 << 20, handler).RunAsync(stop.Token);                                  // pre-0.5 agent
    _ = new MMServerAgent.MiniHttpServer(2986, 1 << 20, handler, null, certB, allowPlain: false).RunAsync(stop.Token);  // encrypted only
    await Task.Delay(400);
    const string lo = "127.0.0.1";

    var trust = new AgentTrust(dir);
    var client = new AgentClient(trust);
    var r1 = await client.QueryAsync(lo, 2988);
    Check(r1 is { Encrypted: true } && r1.Response.Servers[0].Name == "enc" && trust.RequiresTls(lo, 2988), "new agent answers encrypted; its key is remembered");
    var r2 = await new AgentClient(new AgentTrust(dir)).QueryAsync(lo, 2988);
    Check(r2 is { Encrypted: true }, "remembered key survives a launcher restart");
    var up = await client.UploadReportAsync(lo, 2988, [1, 2, 3]);
    Check(up.Ok && up.Message == "enc", "reports go encrypted to an agent that supports it");

    var old = await client.QueryAsync(lo, 2987);
    Check(old is { Encrypted: false } && !trust.RequiresTls(lo, 2987), "older agent without encryption still works");

    trust.Remember(lo, 2987, fpA);
    Check(await client.QueryAsync(lo, 2987) == null, "no fallback to unencrypted once an agent has used encryption");

    trust.Remember(lo, 2986, fpA); // we "know" this agent with key A, but it now shows key B
    Check(await client.QueryAsync(lo, 2986) == null && trust.TakeProblems().Any(p => p.Contains("different encryption key")),
          "agent with a changed key is refused, with a warning");

    var published = new AgentTrust(Path.Combine(dir, "pub"));
    Directory.CreateDirectory(Path.Combine(dir, "pub"));
    published.SetPublished($"// test\n{{ \"Agents\": [ {{ \"Host\": \"{lo}\", \"Port\": 2988, \"Fingerprint\": \"{fpB}\" }} ] }}");
    Check(await new AgentClient(published).QueryAsync(lo, 2988) == null, "agent whose key doesn't match the published list is refused");
    published.SetPublished($"{{ \"Agents\": [ {{ \"Host\": \"{lo}\", \"Port\": 2988, \"Fingerprint\": \"{fpB}\" }}, {{ \"Host\": \"{lo}\", \"Port\": 2988, \"Fingerprint\": \"{fpA}\" }} ] }}");
    Check(await new AgentClient(published).QueryAsync(lo, 2988) is { Encrypted: true }, "agent matching the published list is accepted");

    using (var raw = new HttpClient())
    {
        var res = await raw.GetAsync($"http://{lo}:2986{AgentClient.Path}");
        Check(!res.IsSuccessStatusCode && (await res.Content.ReadAsStringAsync()).Contains("only accepts encrypted"), "encrypted-only agent refuses unencrypted requests");
    }

    // A refused upload (here: always "rate limited") must still deliver its reason, not a
    // connection reset, even though the body was never read (Windows resets otherwise).
    _ = new MMServerAgent.MiniHttpServer(2985, 9 << 20, handler,
        (_, _, _, _) => new MMServerAgent.HttpReply(429, "application/json", "{\"ok\":false,\"message\":\"slow down\"}")).RunAsync(stop.Token);
    await Task.Delay(200);
    using (var raw = new HttpClient())
    {
        string got;
        try
        {
            var res = await raw.PostAsync($"http://{lo}:2985{AgentClient.ReportPath}", new ByteArrayContent(new byte[2 << 20]));
            got = $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}";
        }
        catch (Exception ex) { got = ex.GetBaseException().Message; }
        Check(got.StartsWith("429") && got.Contains("slow down"), $"refused upload still gets its reason ({got})");
    }

    using (var s = typeof(AgentTrust).Assembly.GetManifestResourceStream(AgentTrust.PublishedFileName))
        Check(s != null && AgentTrust.ParsePublished(new StreamReader(s).ReadToEnd()) != null, "published key list is built in and valid");
    stop.Cancel();
}

Console.WriteLine("== Server browser ==");
{
    // Valve's GetServerList reply shape, with junk mixed in.
    var steamJson = """
    {"response":{"servers":[
      {"addr":"1.2.3.4:27016","gameport":2302,"name":"Alpha PvE | 1PP","appid":221100,"map":"chernarusplus","players":40,"max_players":60,"gametype":"battleye,no3rd,mod,lqs0,12:00","version":"1.29.162510"},
      {"addr":"5.6.7.8:2303","gameport":2302,"name":"Bravo Deer Isle","appid":221100,"map":"deerisle","players":0,"max_players":50,"gametype":"battleye,08:00","version":"1.29.162510"},
      {"addr":"9.9.9.9:27016","gameport":2302,"name":"Charlie Full","appid":221100,"map":"chernarusplus","players":60,"max_players":60,"gametype":"battleye,mod","version":"1.29.162510"},
      {"addr":"not-an-address","gameport":1,"name":"bad","appid":221100},
      {"addr":"4.4.4.4:27016","gameport":2302,"name":"Other game","appid":107410,"map":"x","players":1,"max_players":2},
      {"addr":"3.3.3.3:27016","gameport":2302,"name":"Evil\u0007Name","appid":221100,"map":"enoch","players":5,"max_players":10,"gametype":"","version":"1"}
    ]}}
    """;
    var feed = MMLauncher.Core.Browser.BrowserFeed.FromSteamReply(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(steamJson)));
    Check(feed.Servers.Count == 4 && feed.Servers.All(e => !e.Name.Any(char.IsControl)), "Steam server list parsed (bad and other-game entries dropped, names cleaned)");
    var round = MMLauncher.Core.Browser.BrowserFeed.FromGzip(feed.ToGzip());
    Check(round?.Servers.Count == 4 && round.Servers[0].Keywords.Contains("no3rd"), "server list survives compression");
    Check(MMLauncher.Core.Browser.BrowserFeed.FromGzip([1, 2, 3]) == null, "damaged server list is refused");

    var B = MMLauncher.Core.Browser.BrowserService.Apply;
    var all = feed.Servers;
    Check(B(all, new(), null).Select(e => e.Name).First() == "Charlie Full", "default sort: most players first");
    Check(B(all, new(HideEmpty: true, HideFull: true), null).Select(e => e.Name).SequenceEqual(["Alpha PvE | 1PP", "EvilName"]), "hide empty / hide full");
    Check(B(all, new(Map: "chernarusplus"), null).Count == 2 && B(all, new(Search: "deer"), null).Single().Name == "Bravo Deer Isle", "map filter and search");
    Check(B(all, new(Search: "5.6.7"), null).Single().Name == "Bravo Deer Isle", "search by IP address");
    Check(B(all, new(Perspective: MMLauncher.Core.Browser.PerspectiveFilter.FirstPersonOnly), null).Single().Name == "Alpha PvE | 1PP"
          && B(all, new(Perspective: MMLauncher.Core.Browser.PerspectiveFilter.ThirdPersonAllowed), null).Count == 3, "1PP / 3PP filter");
    Check(B(all, new(Mods: MMLauncher.Core.Browser.ModFilter.Vanilla), null).Count == 2, "modded / vanilla filter");
    var pings = new Dictionary<string, int?> { ["5.6.7.8:2303"] = 20, ["1.2.3.4:27016"] = 90 };
    Check(B(all, new(Sort: MMLauncher.Core.Browser.BrowserSort.Ping), e => pings.GetValueOrDefault(e.Address)).Select(e => e.Address).Take(2)
          .SequenceEqual(["5.6.7.8:2303", "1.2.3.4:27016"]), "sort by ping (unmeasured last)");
    Check(MMLauncher.Core.Browser.BrowserService.Maps(all).First() is ("chernarusplus", 2), "map list with counts");

    // Favorites and recent
    var lists = new ServerLists();
    Check(lists.ToggleFavorite("1.2.3.4", 27016, 2302, "Alpha") && lists.IsFavorite("1.2.3.4", 27016) && !lists.ToggleFavorite("1.2.3.4", 27016, 2302, "Alpha"),
          "favorite on and off");
    for (int i = 0; i < 30; i++) lists.AddRecent($"10.0.0.{i}", 2303, 2302, $"S{i}");
    lists.AddRecent("10.0.0.5", 2303, 2302, "S5");
    Check(lists.Recent.Count == ServerLists.MaxRecent && lists.Recent[0].Name == "S5" && lists.Recent.Count(r => r.Name == "S5") == 1,
          "recent list: newest first, no duplicates, capped");
    Check(File.ReadAllText(ServerLists.FilePath).StartsWith("// MM Launcher") && ServerLists.Load().Recent.Count == ServerLists.MaxRecent, "favorites.jsonc saved with comments");

    // Agent fetches from (fake) Steam and serves it; launcher downloads it.
    using var fakeSteam = new HttpListener();
    fakeSteam.Prefixes.Add("http://127.0.0.1:8098/");
    fakeSteam.Start();
    string? askedKey = null;
    _ = Task.Run(async () =>
    {
        while (fakeSteam.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await fakeSteam.GetContextAsync(); } catch { break; }
            askedKey = ctx.Request.QueryString["key"];
            var bytes = System.Text.Encoding.UTF8.GetBytes(ctx.Request.QueryString["key"] == "good" ? steamJson : "denied");
            ctx.Response.StatusCode = ctx.Request.QueryString["key"] == "good" ? 200 : 403;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    });
    Environment.SetEnvironmentVariable("MMAGENT_STEAM_API", "http://127.0.0.1:8098");
    File.Delete(Path.Combine(AppContext.BaseDirectory, MMServerAgent.ServerListFeed.CacheFile)); // no copy from an earlier run
    var agentLog = new List<string>();
    var feedSvc = new MMServerAgent.ServerListFeed(new MMServerAgent.AgentConfig { SteamWebApiKey = "good" }, m => { lock (agentLog) agentLog.Add(m); });
    using var feedStop = new CancellationTokenSource();
    var feedRun = feedSvc.RunAsync(feedStop.Token);
    for (int i = 0; i < 50 && feedSvc.Gzip == null; i++) await Task.Delay(100);
    Check(feedSvc.Count == 4 && askedKey == "good", "agent gets the list from Steam with its key");
    lock (agentLog) Check(agentLog.All(l => !l.Contains("good")), "agent never logs the Steam key");

    using var cert = MMServerAgent.TlsIdentity.Load(MMServerAgent.TlsIdentity.Create());
    using var stopFeedServer = new CancellationTokenSource();
    _ = new MMServerAgent.MiniHttpServer(2984, 1 << 20, r => Task.FromResult(r.Path == MMLauncher.Core.Browser.BrowserFeed.Path
        ? new MMServerAgent.HttpReply(200, "application/json", "", feedSvc.Gzip, "gzip")
        : new MMServerAgent.HttpReply(404, "text/plain", "no")), null, cert).RunAsync(stopFeedServer.Token);
    await Task.Delay(200);
    var trustDir = Path.Combine(root, "trust-feed");
    Directory.CreateDirectory(trustDir);
    var feedTrust = new AgentTrust(trustDir);
    feedTrust.Remember("127.0.0.1", 2984, AgentTrust.Fingerprint(cert));
    var got = await new AgentClient(feedTrust).GetBrowserFeedAsync("127.0.0.1", 2984);
    Check(got?.Servers.Count == 4, "launcher downloads the server list from the agent (encrypted, compressed)");
    feedStop.Cancel();
    stopFeedServer.Cancel();

    // Valve answers at most 10,000 servers per request; the agent must ask in parts.
    var universe = Enumerable.Range(0, 26000).Select(i => (
        Addr: $"10.{i / 65536 % 256}.{i / 256 % 256}.{i % 256}:2303",
        Map: (i % 13) switch { < 5 => "chernarusplus", < 9 => "nasdara", < 11 => "enoch", < 12 => "namalsk", _ => "sakhal" },
        Players: i % 9 == 0 ? 5 : 0)).ToList();
    bool Matches((string Addr, string Map, int Players) e, string filter)
    {
        var t = filter.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        bool Cond(string k, string v) => k switch
        {
            "empty" => e.Players > 0,
            "noplayers" => e.Players == 0,
            "map" => e.Map.Equals(v, StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
        for (int i = 0; i + 1 < t.Length; i += 2)
        {
            if (t[i] == "nor")
            {
                int n = int.Parse(t[i + 1]);
                for (int j = 0; j < n; j++, i += 2)
                    if (Cond(t[i + 2], t[i + 3])) return false;
                continue;
            }
            if (!Cond(t[i], t[i + 1])) return false;
        }
        return true;
    }
    using var cappedSteam = new HttpListener();
    cappedSteam.Prefixes.Add("http://127.0.0.1:8096/");
    cappedSteam.Start();
    int cappedRequests = 0;
    _ = Task.Run(async () =>
    {
        while (cappedSteam.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await cappedSteam.GetContextAsync(); } catch { break; }
            Interlocked.Increment(ref cappedRequests);
            var filter = ctx.Request.QueryString["filter"] ?? "";
            var hits = universe.Where(e => Matches(e, filter)).Take(10000).Select(e => new Dictionary<string, object>
            {
                ["addr"] = e.Addr, ["gameport"] = 2302, ["name"] = "S " + e.Addr, ["appid"] = 221100, ["map"] = e.Map,
                ["players"] = e.Players, ["max_players"] = 60, ["gametype"] = "battleye", ["version"] = "1",
            });
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { response = new { servers = hits } });
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    });
    Environment.SetEnvironmentVariable("MMAGENT_STEAM_API", "http://127.0.0.1:8096");
    File.Delete(Path.Combine(AppContext.BaseDirectory, MMServerAgent.ServerListFeed.CacheFile)); // start without the earlier test's copy
    var capLog = new List<string>();
    var capSvc = new MMServerAgent.ServerListFeed(new MMServerAgent.AgentConfig { SteamWebApiKey = "good" }, m => { lock (capLog) capLog.Add(m); });
    using var capStop = new CancellationTokenSource();
    var capRun = capSvc.RunAsync(capStop.Token);
    for (int i = 0; i < 300 && capSvc.Gzip == null; i++) await Task.Delay(100);
    capStop.Cancel();
    cappedSteam.Stop();
    lock (capLog) Check(capSvc.Count == 26000 && capLog.All(l => !l.Contains("Warning")),
        $"agent gets past Steam's 10,000-per-request limit ({capSvc.Count} of 26000 servers in {cappedRequests} requests)");
    Environment.SetEnvironmentVariable("MMAGENT_STEAM_API", "http://127.0.0.1:8098");

    var badLog = new List<string>();
    var badSvc = new MMServerAgent.ServerListFeed(new MMServerAgent.AgentConfig { SteamWebApiKey = "wrongkey" }, m => { lock (badLog) badLog.Add(m); });
    using var badStop = new CancellationTokenSource();
    var badRun = badSvc.RunAsync(badStop.Token);
    for (int i = 0; i < 50 && badLog.Count < 1; i++) await Task.Delay(100);
    badStop.Cancel();
    lock (badLog) Check(badLog.Any(l => l.Contains("rejected the SteamWebApiKey")) && badLog.All(l => !l.Contains("wrongkey")), "a wrong Steam key is reported clearly, without showing it");
    fakeSteam.Stop();
    Environment.SetEnvironmentVariable("MMAGENT_STEAM_API", null);
}

Console.WriteLine("== Restart schedule ==");
{
    var tz = TimeZoneInfo.CreateCustomTimeZone("test-5", TimeSpan.FromHours(-5), "test", "test");
    var now = new DateTimeOffset(2026, 10, 31, 2, 50, 0, TimeSpan.FromHours(-5));
    var started = now.AddHours(-3).AddMinutes(-10); // up 3h10m, restarts every 4h
    var r = RestartSchedule.Next(now, started, 4, null, tz);
    Check(r is { Approximate: true } && r.Value.At == started.AddHours(4) && RestartSchedule.Describe(r.Value.At, true, now) == "in ~50 min",
          "every 4 hours from start: next restart in ~50 min");
    var overdue = RestartSchedule.Next(now, now.AddHours(-4).AddMinutes(-3), 4, null, tz);
    Check(overdue != null && RestartSchedule.Describe(overdue.Value.At, true, now) == "due now", "a restart a few minutes overdue shows as due");
    var paused = RestartSchedule.Next(now, now.AddHours(-9), 4, null, tz);
    Check(paused?.At == now.AddHours(-9).AddHours(12), "server up past several intervals gets its next slot");
    var fixedT = RestartSchedule.Next(now, started, 4, ["00:00", "06:00", "bad", "12:00", "18:00"], tz);
    Check(fixedT is { Approximate: false } && fixedT.Value.At == new DateTimeOffset(2026, 10, 31, 6, 0, 0, TimeSpan.FromHours(-5))
          && RestartSchedule.Describe(fixedT.Value.At, false, now) == "in 3h 10m", "fixed clock times win and wrap correctly");
    var late = RestartSchedule.Next(now.AddHours(20), null, 0, ["00:00", "06:00"], tz);
    Check(late?.At == new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-5)), "fixed times roll over to the next day");
    Check(RestartSchedule.Next(now, started, 0, [], tz) == null, "no schedule, no restart time");
}

Console.WriteLine("== DayZ Editor presets ==");
{
    var html = """
    <?xml version="1.0" encoding="utf-8"?>
    <html><head><meta name="arma:Type" content="list" /></head><body>
    <h1>DayZ Mods - <strong>MM Deer Isle &amp; Co</strong></h1>
    <div class="mod-list"><table>
      <tr data-type="ModContainer"><td data-type="DisplayName">CF</td><td><span class="from-steam">Steam</span></td>
        <td><a href="https://steamcommunity.com/sharedfiles/filedetails/?id=1559212036" data-type="Link">x</a></td></tr>
      <tr data-type="ModContainer"><td data-type="DisplayName">DeerIsle</td><td><span class="from-steam">Steam</span></td>
        <td><a href="https://steamcommunity.com/sharedfiles/filedetails/?id=1602372402" data-type="Link">x</a></td></tr>
      <tr data-type="ModContainer"><td data-type="DisplayName">Local thing</td><td><span class="from-local">Local</span></td><td>@Local</td></tr>
      <tr data-type="ModContainer"><td data-type="DisplayName">Roads &amp; <b>Parts</b></td><td><span class="from-steam">Steam</span></td>
        <td><a href="https://steamcommunity.com/workshop/filedetails/?id=2353800408" data-type="Link">x</a></td></tr>
      <tr data-type="ModContainer"><td data-type="DisplayName">DeerIsle again</td><td></td>
        <td><a href="https://steamcommunity.com/sharedfiles/filedetails/?id=1602372402" data-type="Link">x</a></td></tr>
    </table></div></body></html>
    """;
    var preset1 = MMLauncher.Core.Editor.PresetFormat.Parse(html, "file name");
    Check(preset1.Name == "MM Deer Isle & Co" && preset1.Mods.Select(m => m.Id).SequenceEqual([1602372402UL, 2353800408UL])
          && preset1.Mods[1].Name == "Roads & Parts" && preset1.SkippedLocal == 1,
          "DayZ launcher HTML preset: name, order, local mod skipped, duplicates and always-loaded mods dropped");
    var ids = MMLauncher.Core.Editor.PresetFormat.Parse("1602372402\r\n@2353800408; 2276010135\nnot an id\n1559212036", "Typed");
    Check(ids.Name == "Typed" && ids.Mods.Select(m => m.Id).SequenceEqual([1602372402UL, 2353800408UL, 2276010135UL]), "plain list of Workshop IDs");
    var links = MMLauncher.Core.Editor.PresetFormat.Parse("see https://steamcommunity.com/sharedfiles/filedetails/?id=2276010135 and https://steamcommunity.com/sharedfiles/filedetails/?l=german&id=1602372402", "L");
    Check(links.Mods.Select(m => m.Id).SequenceEqual([2276010135UL, 1602372402UL]), "Workshop links in any text, including extra URL parameters");
    Check(MMLauncher.Core.Editor.PresetFormat.Parse("<html>nothing here</html>", "x").Mods.Count == 0, "file without mods gives an empty preset");

    var full = new MMLauncher.Core.Editor.EditorPreset { Name = "Round <trip> & \"back\"", Mods = MMLauncher.Core.Editor.DayZEditor.LoadOrder(preset1) };
    var again = MMLauncher.Core.Editor.PresetFormat.Parse(MMLauncher.Core.Editor.PresetFormat.ToHtml(full), "x");
    Check(again.Name == "Round <trip> & \"back\"" && again.Mods.Select(m => m.Id).SequenceEqual(preset1.Mods.Select(m => m.Id)),
          "exported HTML imports back the same (special characters too)");
    Check(MMLauncher.Core.Editor.DayZEditor.LoadOrder(preset1).Select(m => m.Id).Take(3).SequenceEqual([1559212036UL, 2545327648UL, 2250764298UL]),
          "load order: CF, Dabs Framework, DayZ-Editor first");

    var store = new MMLauncher.Core.Editor.EditorPresetStore { Presets = [preset1] };
    store.Save();
    var loaded = MMLauncher.Core.Editor.EditorPresetStore.Load();
    Check(File.ReadAllText(MMLauncher.Core.Editor.EditorPresetStore.FilePath).StartsWith("// MM Launcher") && loaded.Presets.Single().Mods.Count == 2,
          "editor-presets.jsonc saved with comments and read back");

    // Agent shares the files in its editor-presets folder.
    var folder = MMServerAgent.EditorPresetFolder.FolderPath;
    if (Directory.Exists(folder)) Directory.Delete(folder, true);
    MMServerAgent.EditorPresetFolder.EnsureFolder();
    File.WriteAllText(Path.Combine(folder, "Deer Isle.html"), html);
    File.WriteAllText(Path.Combine(folder, "Roads.txt"), "2353800408\n2276010135");
    File.WriteAllText(Path.Combine(folder, "Empty.txt"), "nothing");
    var agentLog = new List<string>();
    var shared = new MMServerAgent.EditorPresetFolder(m => agentLog.Add(m));
    var served = System.Text.Json.JsonSerializer.Deserialize<List<MMLauncher.Core.Editor.EditorPreset>>(shared.Json, AgentResponse.Json)!;
    Check(shared.Count == 2 && served.Select(p => p.Name).OrderBy(n => n).SequenceEqual(["MM Deer Isle & Co", "Roads"])
          && File.Exists(Path.Combine(folder, "README.txt")) && agentLog.Any(l => l.Contains("Empty.txt")),
          "agent shares the presets in its folder (README added, empty file skipped)");
    File.WriteAllText(Path.Combine(folder, "Roads.txt"), "2353800408");
    var after = System.Text.Json.JsonSerializer.Deserialize<List<MMLauncher.Core.Editor.EditorPreset>>(shared.Json, AgentResponse.Json)!;
    Check(after.Single(p => p.Name == "Roads").Mods.Count == 1, "agent picks up changed preset files without a restart");
    Directory.Delete(folder, true);
}

Console.WriteLine("== Crash or normal quit ==");
{
    var logs = Path.Combine(root, "exitlogs");
    if (Directory.Exists(logs)) Directory.Delete(logs, true);
    Directory.CreateDirectory(logs);
    var started = DateTime.UtcNow;
    File.WriteAllText(Path.Combine(logs, "crash_2026-10-31_02-32-58.log"), "NULL pointer to instance"); // script error, not a crash
    var old = Path.Combine(logs, "DayZ_x64_old.mdmp");
    File.WriteAllText(old, "x");
    File.SetLastWriteTimeUtc(old, started.AddHours(-2));
    Check(!MMLauncher.Core.Launch.GameExit.LooksLikeCrash(0, started, logs), "normal quit (exit 0, only a script-error log and an old dump) is not a crash");
    Check(!MMLauncher.Core.Launch.GameExit.LooksLikeCrash(1, started, logs) && !MMLauncher.Core.Launch.GameExit.LooksLikeCrash(null, started, logs),
          "ordinary exit codes, or none, are not a crash");
    Check(MMLauncher.Core.Launch.GameExit.LooksLikeCrash(unchecked((int)0xC0000005), started, logs), "access violation exit code is a crash");
    File.WriteAllText(Path.Combine(logs, "DayZ_x64_2026-10-31_18-50-00.mdmp"), "dump");
    Check(MMLauncher.Core.Launch.GameExit.LooksLikeCrash(0, started, logs), "a new crash dump is a crash");
    Directory.Delete(logs, true);
}

Console.WriteLine("== Friends ==");
{
    var dir = Path.Combine(root, "friends");
    if (Directory.Exists(dir)) Directory.Delete(dir, true);
    Directory.CreateDirectory(dir);
    var login = Path.Combine(dir, "loginusers.vdf");
    File.WriteAllText(login, """
    "users"
    {
        "76561198000000001" { "AccountName" "alt" "MostRecent" "0" }
        "76561198012345678" { "AccountName" "max" "PersonaName" "Max" "MostRecent" "1" }
    }
    """);
    Check(MMLauncher.Core.Friends.SteamAccount.FromLoginUsers(login) == 76561198012345678UL, "signed-in Steam account read from Steam's loginusers.vdf");
    Check(!MMLauncher.Core.Friends.SteamAccount.IsValid(42) && MMLauncher.Core.Friends.SteamAccount.IsValid(76561198012345678UL), "Steam ID check");

    var hist = new MMLauncher.Core.Friends.FriendHistory();
    var t0 = DateTimeOffset.Now.AddDays(-40);
    hist.Record([new() { Name = "Old", Server = "1.1.1.1:2302" }], _ => "Old server", t0);
    hist.Record([new() { Name = "Nully", Server = "2.2.2.2:2302" }, new() { Name = "Max", Server = "2.2.2.2:2302" }, new() { Name = "Menu", Server = "" }],
                k => k == "2.2.2.2:2302" ? "MM Deer Isle" : null, DateTimeOffset.Now);
    Check(hist.Servers.Count == 1 && hist.Servers[0].ServerName == "MM Deer Isle" && hist.Servers[0].Friends.Count == 2,
          "friend history: servers recorded with names, entries older than 30 days dropped, menu-only friends skipped");

    // Agent asks (fake) Steam for friends and what they play.
    using var steam = new HttpListener();
    steam.Prefixes.Add("http://127.0.0.1:8095/");
    steam.Start();
    var asked = new List<string>();
    _ = Task.Run(async () =>
    {
        while (steam.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await steam.GetContextAsync(); } catch { break; }
            var url = ctx.Request.Url!.AbsolutePath;
            lock (asked) asked.Add(url + "?" + ctx.Request.QueryString["steamid"] + ctx.Request.QueryString["steamids"]);
            string body; int code = 200;
            if (url.Contains("GetFriendList"))
            {
                if (ctx.Request.QueryString["steamid"] == "76561198000000666") { code = 401; body = "<html>Unauthorized</html>"; }
                else body = """{"friendslist":{"friends":[{"steamid":"76561198000000002"},{"steamid":"76561198000000003"},{"steamid":"76561198000000004"},{"steamid":"bad"}]}}""";
            }
            else
                body = """
                {"response":{"players":[
                  {"steamid":"76561198000000002","personaname":"Nully","gameid":"221100","gameserverip":"2.2.2.2:2302"},
                  {"steamid":"76561198000000003","personaname":"In Menu\u0007","gameid":"221100"},
                  {"steamid":"76561198000000004","personaname":"Arma Guy","gameid":"107410","gameserverip":"3.3.3.3:2302"}
                ]}}
                """;
            ctx.Response.StatusCode = code;
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    });
    Environment.SetEnvironmentVariable("MMAGENT_STEAM_API", "http://127.0.0.1:8095");
    var fsvc = new MMServerAgent.FriendsService(new MMServerAgent.AgentConfig { SteamWebApiKey = "k" }, _ => { });
    using var fstop = new CancellationTokenSource();
    _ = new MMServerAgent.MiniHttpServer(2983, 1 << 20, r => r.Path == MMLauncher.Core.Friends.FriendsReply.Path
        ? fsvc.HandleAsync(r.Query, r.RemoteAddress) : Task.FromResult(new MMServerAgent.HttpReply(404, "text/plain", "no"))).RunAsync(fstop.Token);
    await Task.Delay(200);
    var fclient = new AgentClient(new AgentTrust(dir));
    var fr = await fclient.GetFriendsAsync("127.0.0.1", 2983, 76561198012345678UL);
    Check(fr is { Ok: true, FriendCount: 3 } && fr.InDayZ.Count == 2 && fr.InDayZ.Single(f => f.Name == "Nully").Server == "2.2.2.2:2302"
          && fr.InDayZ.Single(f => f.Name == "In Menu").Server == "", "friends in DayZ and their servers (other games and junk IDs left out, names cleaned)");
    int before; lock (asked) before = asked.Count;
    await fclient.GetFriendsAsync("127.0.0.1", 2983, 76561198012345678UL);
    lock (asked) Check(asked.Count == before, "friends answer cached for a minute (no second Steam call)");
    var priv = await fclient.GetFriendsAsync("127.0.0.1", 2983, 76561198000000666UL);
    Check(priv is { Ok: false } && priv.Message.Contains("private"), "private friends list explained");
    var bad = await new HttpClient().GetAsync($"http://127.0.0.1:2983{MMLauncher.Core.Friends.FriendsReply.Path}?steamid=12345");
    Check((int)bad.StatusCode == 400, "agent refuses a Steam ID that isn't one");
    for (int i = 0; i < 15; i++) await new HttpClient().GetAsync($"http://127.0.0.1:2983{MMLauncher.Core.Friends.FriendsReply.Path}?steamid={76561198000001000UL + (ulong)i}");
    var limited = await new HttpClient().GetAsync($"http://127.0.0.1:2983{MMLauncher.Core.Friends.FriendsReply.Path}?steamid=76561198000002000");
    Check((int)limited.StatusCode == 429, "friend lookups are rate limited per address");
    fstop.Cancel();
    steam.Stop();
    Environment.SetEnvironmentVariable("MMAGENT_STEAM_API", null);
}

Console.WriteLine("== Workshop search (Mods page) ==");
{
    var dir = Path.Combine(root, "workshop");
    Directory.CreateDirectory(dir);
    Check(WorkshopSearchReply.IsSafePreview("https://images.steamusercontent.com/ugc/1/abc/") &&
          !WorkshopSearchReply.IsSafePreview("http://images.steamusercontent.com/ugc/1/") &&
          !WorkshopSearchReply.IsSafePreview("https://evil.example/steamusercontent.com/x") &&
          !WorkshopSearchReply.IsSafePreview("https://steamusercontent.com.evil.example/x") &&
          !WorkshopSearchReply.IsSafePreview("file:///C:/Windows/win.ini"), "thumbnails only from Steam's own https image hosts");
    Check(WorkshopSearchReply.TryParseItem("https://steamcommunity.com/sharedfiles/filedetails/?id=1559212036", out var pid) && pid == 1559212036 &&
          WorkshopSearchReply.TryParseItem("steamcommunity.com/workshop/filedetails/?l=english&id=2545327648&searchtext=", out pid) && pid == 2545327648 &&
          WorkshopSearchReply.TryParseItem(" 2250764298 ", out pid) && pid == 2250764298 &&
          !WorkshopSearchReply.TryParseItem("expansion", out _) && !WorkshopSearchReply.TryParseItem("2250764298 expansion", out _) &&
          !WorkshopSearchReply.TryParseItem("https://evil.example/?id=2250764298", out _), "pasted Workshop links and IDs recognized; plain searches left alone");

    using var steam = new HttpListener();
    steam.Prefixes.Add("http://127.0.0.1:8096/");
    steam.Start();
    var asked = new List<string>();
    _ = Task.Run(async () =>
    {
        while (steam.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await steam.GetContextAsync(); } catch { break; }
            var qs = ctx.Request.QueryString;
            lock (asked) asked.Add($"{qs["query_type"]}|{qs["search_text"]}|{qs["page"]}");
            int code = qs["key"] == "rejected" ? 403 : 200;
            var longTitle = new string('x', 500);
            var body = $$$"""
            {"response":{"total":2,"publishedfiledetails":[
              {"publishedfileid":"2250764298","consumer_appid":221100,"title":"DayZ-Editor\u001b[31m","file_size":"40000000","subscriptions":180000,
               "time_updated":1760000000,"short_description":"Build maps","preview_url":"https://images.steamusercontent.com/ugc/1/x/",
               "children":[{"publishedfileid":"1559212036"},{"publishedfileid":"2545327648"},{"publishedfileid":"0"}]},
              {"publishedfileid":"999","consumer_appid":107410,"title":"Arma mod"},
              {"publishedfileid":"1234","consumer_appid":221100,"title":"{{{longTitle}}}","preview_url":"https://evil.example/a.png"}
            ]}}
            """;
            ctx.Response.StatusCode = code;
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    });
    Environment.SetEnvironmentVariable("MMAGENT_STEAM_API", "http://127.0.0.1:8096");
    var wsvc = new MMServerAgent.WorkshopSearchService(new MMServerAgent.AgentConfig { SteamWebApiKey = "k" }, _ => { });
    var wbad = new MMServerAgent.WorkshopSearchService(new MMServerAgent.AgentConfig { SteamWebApiKey = "rejected" }, _ => { });
    var woff = new MMServerAgent.WorkshopSearchService(new MMServerAgent.AgentConfig { SteamWebApiKey = "" }, _ => { });
    using var wstop = new CancellationTokenSource();
    _ = new MMServerAgent.MiniHttpServer(2984, 1 << 20, r => r.Path == WorkshopSearchReply.Path
        ? wsvc.HandleAsync(r.Query, r.RemoteAddress) : Task.FromResult(new MMServerAgent.HttpReply(404, "text/plain", "no"))).RunAsync(wstop.Token);
    _ = new MMServerAgent.MiniHttpServer(2985, 1 << 20, r => wbad.HandleAsync(r.Query, r.RemoteAddress)).RunAsync(wstop.Token);
    _ = new MMServerAgent.MiniHttpServer(2986, 1 << 20, r => woff.HandleAsync(r.Query, r.RemoteAddress)).RunAsync(wstop.Token);
    await Task.Delay(200);
    var wclient = new AgentClient(new AgentTrust(dir));
    var ws = await wclient.SearchWorkshopAsync("127.0.0.1", 2984, "", "popular", 1);
    Check(ws is { Ok: true, Total: 2 } && ws.Results.Count == 2 && ws.Results.All(r => r.Id != 999), "Workshop results: DayZ items only");
    var ed = ws?.Results.FirstOrDefault(r => r.Id == 2250764298);
    Check(ed != null && ed.Title == "DayZ-Editor[31m" && ed.Requires.SequenceEqual(new ulong[] { 1559212036, 2545327648 }) && ed.PreviewUrl.Length > 0,
          "Workshop result cleaned (control characters), with the mods it needs");
    var other = ws?.Results.FirstOrDefault(r => r.Id == 1234);
    Check(other != null && other.Title.Length <= 120 && other.PreviewUrl == "", "long titles clipped; thumbnails from other hosts dropped");
    await wclient.SearchWorkshopAsync("127.0.0.1", 2984, "Expansion", "subscribed", 2);
    lock (asked) Check(asked.Contains("3||1") && asked.Contains("12|Expansion|2"), "popular uses Steam's trend query; text searches rank by match, with paging");
    int wbefore; lock (asked) wbefore = asked.Count;
    await wclient.SearchWorkshopAsync("127.0.0.1", 2984, "", "popular", 1);
    lock (asked) Check(asked.Count == wbefore, "Workshop searches cached (no second Steam call)");
    var rejected = await wclient.SearchWorkshopAsync("127.0.0.1", 2985, "x", "popular", 1);
    Check(rejected is { Ok: false } && rejected.Message.Contains("Try again"), "Steam refusing the key gives a plain message (no key in it)");
    var off = await wclient.SearchWorkshopAsync("127.0.0.1", 2986, "x", "popular", 1);
    Check(off is { Ok: false } && off.Message.Contains("turned on"), "agents without a Steam key say Workshop search is off");
    var silly = await new HttpClient().GetStringAsync($"http://127.0.0.1:2984{WorkshopSearchReply.Path}?q=&sort=../../x&page=99999");
    lock (asked) Check(asked.Contains("3||50"), "unknown sort and huge page numbers are clamped, not passed to Steam");
    for (int i = 0; i < 30; i++) await new HttpClient().GetAsync($"http://127.0.0.1:2984{WorkshopSearchReply.Path}?q=spam{i}");
    var wlimited = await new HttpClient().GetAsync($"http://127.0.0.1:2984{WorkshopSearchReply.Path}?q=one-more");
    Check((int)wlimited.StatusCode == 429, "Workshop searches are rate limited per address");
    wstop.Cancel();
    steam.Stop();
    Environment.SetEnvironmentVariable("MMAGENT_STEAM_API", null);

}

Console.WriteLine("== Steam permissions (\"Unable to locate a running instance of Steam\") ==");
{
    Check(SteamPermissions.Diagnose(false, true) == SteamPermissions.Problem.SteamIsAdmin, "Steam as administrator, launcher not: flagged");
    Check(SteamPermissions.Diagnose(true, false) == SteamPermissions.Problem.LauncherIsAdmin, "launcher as administrator, Steam not: flagged");
    Check(SteamPermissions.Diagnose(true, true) == SteamPermissions.Problem.None && SteamPermissions.Diagnose(false, false) == SteamPermissions.Problem.None
          && SteamPermissions.Diagnose(false, null) == SteamPermissions.Problem.None, "matching (or unknown) permissions: no warning");
    Check(SteamPermissions.Check() == SteamPermissions.Problem.None || OperatingSystem.IsWindows(), "check never throws");
}

Console.WriteLine("== Release notes in the update window ==");
{
    var notesDir = new DirectoryInfo(AppContext.BaseDirectory);
    while (notesDir != null && !Directory.Exists(Path.Combine(notesDir.FullName, "release-notes"))) notesDir = notesDir.Parent;
    var md = File.ReadAllText(Path.Combine(notesDir!.FullName, "release-notes", "v0.12.0.md"));
    var noteLines = MMLauncher.Core.Updates.ReleaseNotes.Parse(md);
    var notesText = string.Join("\n", noteLines.Select(l => l.Text));
    Check(noteLines.Count > 5 && !notesText.Contains("**") && !notesText.Contains("##") && !notesText.Contains("](") && !notesText.Contains('`'),
          "markdown marks removed (no ##, **, links or backticks)");
    Check(noteLines.Any(l => l.Kind == MMLauncher.Core.Updates.NoteKind.Heading && l.Text == "New: color themes")
          && noteLines.Count(l => l.Kind == MMLauncher.Core.Updates.NoteKind.Bullet) >= 4, "headings and bullet points kept as such");
    Check(!notesText.Contains("MMServerAgent.zip") && !notesText.Contains("update-agent.bat") && notesText.Contains("Nothing is sent unless you say yes"),
          "Downloads and Server admins sections left for the web page; nothing else cut");
    var combined = MMLauncher.Core.Updates.ReleaseNotes.Combine([(new Version(0, 11, 0), "## A\n- one"), (new Version(0, 12, 0), "## B\n- two")]);
    Check(combined[0].Text == "v0.12.0" && combined.Any(l => l.Text == "v0.11.0") && combined.FindIndex(l => l.Text == "v0.12.0") < combined.FindIndex(l => l.Text == "v0.11.0"),
          "skipped versions all shown, newest first");
}

Console.WriteLine("== Usage stats ==");
{
    var now = DateTimeOffset.Now;
    var id = Guid.NewGuid().ToString("N");
    var raw = new MMLauncher.Core.Usage.UsageBatch
    {
        Id = id, Version = "0.12.0", Os = "Windows 11 (26100)\u0007",
        Events =
        [
            new() { Type = "join", At = now, Mm = true, Server = "MM Deer Isle", Count = 52 },
            new() { Type = "join", At = now, Mm = false, Server = "Somebody's private server", Count = 3 },
            new() { Type = "play", At = now, Mm = true, Server = "MM Deer Isle", Seconds = 5400, Ok = false },
            new() { Type = "sync", At = now, Seconds = -5, Count = 3, Bytes = -1, Ok = true },
            new() { Type = "error", At = now, Code = "Launch Failed: C:\\Users\\max\\x" },
            new() { Type = "steal-passwords", At = now },
            new() { Type = "start", At = now.AddDays(-90) },
        ],
    };
    var clean = MMLauncher.Core.Usage.UsageBatch.Clean(raw, now)!;
    Check(clean.Events.Count == 5, $"only known event types from the last 30 days are kept ({clean.Events.Count})");
    Check(clean.Events[1].Server == "" && clean.Events[0].Server == "MM Deer Isle", "non-MM servers are never named");
    Check(clean.Events[3].Seconds == 0 && clean.Events[3].Bytes == 0 && clean.Os == "Windows 11 (26100)", "numbers clamped, text cleaned");
    Check(clean.Events[4].Code == "launchfailedcusersmaxx".Replace("launchfailed", "launch-failed") || clean.Events[4].Code.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'),
          "error codes are reduced to letters, digits and dashes (no paths or free text)");
    Check(MMLauncher.Core.Usage.UsageBatch.Clean(new() { Id = "not-an-id" }, now) == null, "a batch without a proper random ID is refused");

    var st = new LauncherSettings();
    st.SetUsageStats(true);
    var firstId = st.UsageId;
    st.SetUsageStats(true);
    Check(st.SendsUsageStats && Guid.TryParseExact(firstId, "N", out _) && st.UsageId == firstId, "turning stats on makes one random ID and keeps it");
    st.SetUsageStats(false);
    Check(!st.SendsUsageStats && st.UsageId == "", "turning stats off forgets the ID");
    Check(new LauncherSettings().UsageStats == "ask" && !new LauncherSettings().SendsUsageStats, "new installs send nothing until the player answers");

    // Agent: store, refuse, limit, summarize.
    var usageDir = MMServerAgent.UsageReceiver.FolderPath;
    if (Directory.Exists(usageDir)) Directory.Delete(usageDir, true);
    var urecv = new MMServerAgent.UsageReceiver(new MMServerAgent.AgentConfig { AcceptUsageStats = true }, _ => { });
    var uoff = new MMServerAgent.UsageReceiver(new MMServerAgent.AgentConfig { AcceptUsageStats = false }, _ => { });
    using var ustop = new CancellationTokenSource();
    Task<MMServerAgent.HttpReply> Route(MMServerAgent.UsageReceiver r, MMServerAgent.HttpRequestInfo q) => Task.FromResult(r.Handle(q));
    _ = new MMServerAgent.MiniHttpServer(2987, 1 << 20, q => Route(urecv, q), (m, p, a, l) => urecv.Admit(a, l)).RunAsync(ustop.Token);
    _ = new MMServerAgent.MiniHttpServer(2988, 1 << 20, q => Route(uoff, q), (m, p, a, l) => uoff.Admit(a, l)).RunAsync(ustop.Token);
    await Task.Delay(200);
    var json = System.Text.Json.JsonSerializer.Serialize(raw, MMLauncher.Core.Agent.AgentResponse.Json);
    var posted = await new HttpClient().PostAsync($"http://127.0.0.1:2987{MMLauncher.Core.Usage.UsageBatch.Path}", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
    var lines = Directory.Exists(usageDir) ? Directory.GetFiles(usageDir, "usage-*.jsonl").SelectMany(File.ReadAllLines).ToList() : [];
    Check(posted.IsSuccessStatusCode && lines.Count == 5 && !lines.Any(l => l.Contains("127.0.0.1") || l.Contains("private server")),
          $"agent stores the cleaned events, without the sender's address ({lines.Count} lines)");
    var offReply = await new HttpClient().PostAsync($"http://127.0.0.1:2988{MMLauncher.Core.Usage.UsageBatch.Path}", new StringContent(json));
    var junk = await new HttpClient().PostAsync($"http://127.0.0.1:2987{MMLauncher.Core.Usage.UsageBatch.Path}", new StringContent("{not json"));
    Check((int)offReply.StatusCode == 403 && (int)junk.StatusCode == 400, "agents with stats off refuse them; junk is refused");
    int limitedAt = 0;
    for (int i = 0; i < 70 && limitedAt == 0; i++)
        if ((int)(await new HttpClient().PostAsync($"http://127.0.0.1:2987{MMLauncher.Core.Usage.UsageBatch.Path}", new StringContent(json))).StatusCode == 429) limitedAt = i;
    Check(limitedAt > 0, "usage stats are rate limited per address");
    ustop.Cancel();

    var summary = MMServerAgent.UsageReceiver.BuildSummary(MMServerAgent.UsageReceiver.ReadEvents(DateTime.Now.AddDays(-30)), DateTimeOffset.Now);
    Check(summary.Contains("Joins to MM servers") && summary.Contains("MM Deer Isle") && summary.Contains("(100%)") && !summary.Contains("private server"),
          "summary.txt: joins, MM servers, crash share; other servers never named");
    Directory.Delete(usageDir, true);
}

Console.WriteLine("== Hostile preset file ==");
{
    var evil = string.Concat(Enumerable.Repeat("<tr data-type=\"ModContainer\"><td data-type=\"DisplayName\">x", 30000));
    var evilClock = System.Diagnostics.Stopwatch.StartNew();
    var evilParsed = MMLauncher.Core.Editor.PresetFormat.Parse(evil, "evil");
    Check(evilClock.Elapsed < TimeSpan.FromSeconds(6) && evilParsed.Mods.Count == 0, $"a preset file built to be slow gives up quickly ({evilClock.Elapsed.TotalSeconds:0.0}s)");
}

Console.WriteLine("== Player history ==");
{
    var store = new MMLauncher.Core.History.PlayerHistoryStore();
    var t0 = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    for (int i = 0; i < 8 * 288; i++)            // 8 days, every 5 minutes
        store.Record(2302, t0.AddMinutes(5 * i), i % 48 == 0 ? null : 10 + (i % 288) / 12, 60);
    var now = t0.AddMinutes(5 * (8 * 288 - 1));
    var reply = store.Reply(2302, now)!;
    Check(reply.Players.Count == 7 * 288 && reply.StepSeconds == 300 && reply.MaxPlayers == 60, $"7 days kept, older readings dropped ({reply.Players.Count})");
    Check(reply.Players.Contains(-1) && reply.Players.Max() == 33 && reply.TimeAt(reply.Players.Count - 1) == now, "restarts are gaps; last reading is now");
    store.Record(2302, now, 5, 60);
    Check(store.Reply(2302, now)!.Players[^1] == 33, "two readings in one slot keep the higher");
    Check(store.Reply(2399, now) == null, "unknown port: no history");

    var path = Path.Combine(root, "player-history.jsonc");
    store.Save(path);
    var loaded = MMLauncher.Core.History.PlayerHistoryStore.Load(path);
    Check(File.ReadAllText(path).StartsWith("//") && loaded.Servers[2302].Count > 2000, "history file has its explaining comments and loads back");

    var bad = MMLauncher.Core.History.PlayerHistoryReply.Sanitize(new() { Start = 1, StepSeconds = 5, Players = [1] });
    var big = MMLauncher.Core.History.PlayerHistoryReply.Sanitize(new()
        { Start = 1_700_000_000, StepSeconds = 300, MaxPlayers = 99999, Players = Enumerable.Range(0, 20000).Select(i => i % 2 == 0 ? -7 : 5000).ToList() });
    Check(bad == null && big != null && big.Players.Count == MMLauncher.Core.History.PlayerHistoryReply.MaxSamples
          && big.Players.All(p => p == -1 || p == 1000) && big.MaxPlayers == 1000, "history from an agent is checked: step, length and values clamped");

    var stats = MMLauncher.Core.History.HistoryStats.Summarize(reply, now.AddHours(-24), TimeZoneInfo.Utc);
    Check(stats.PeakPlayers == 33 && stats.Readings > 250 && stats.BusiestText == "Usually busiest 9 PM – 12 AM",
          $"peak, average and busiest hours ({stats.BusiestText})");
    var thin = new MMLauncher.Core.History.PlayerHistoryReply { Start = t0.ToUnixTimeSeconds(), StepSeconds = 300, Players = [5, 6, 7] };
    Check(MMLauncher.Core.History.HistoryStats.BusiestHours(thin, TimeZoneInfo.Utc) == "", "no 'busiest' claim from a few readings");

    var recorder = new MMServerAgent.PlayerHistoryRecorder(_ => { });
    Check(recorder.Handle("port=abc").Status == 400 && recorder.Handle("port=70000").Status == 400, "agent refuses a bad port");
}

Console.WriteLine("== Server news ==");
{
    var newsText = "// a comment\r\nstray text before any post\r\n" +
                   "# 2026-11-07 Deer Isle wipe Friday\r\nFull wipe at 6 PM.\r\n\r\n\r\n\r\nBring snacks.\r\nlink: https://discord.gg/abc\r\n" +
                   "#! Downtime tonight\r\nBack by 4 AM.\r\nlink: javascript:alert(1)\r\n" +
                   "# 2026-02-30 Bad date stays in the title\r\n" +
                   "#   \r\n" +
                   "# Bidi \u202Etxt.exe\u202C and bell \u0007 cleaned\r\nlink: https://user:pw@evil.example/\r\n";
    var posts = MMLauncher.Core.News.NewsFormat.Parse(newsText);
    Check(posts.Count == 4, $"news posts read from # headings, comments and stray text ignored ({posts.Count})");
    Check(posts[0].Date == new DateOnly(2026, 11, 7) && posts[0].Title == "Deer Isle wipe Friday" && posts[0].Body == "Full wipe at 6 PM.\n\nBring snacks."
          && posts[0].Link == "https://discord.gg/abc" && !posts[0].Important, "dated post: title, text (blank lines squeezed) and link");
    Check(posts[1].Important && posts[1].Date == null && posts[1].Link == "", "#! marks a post important; non-https links dropped");
    Check(posts[2].Date == null && posts[2].Title.StartsWith("2026-02-30"), "an impossible date isn't treated as a date");
    Check(!posts[3].Title.Contains('\u202E') && !posts[3].Title.Contains('\u0007') && posts[3].Link == "", "control and right-to-left characters removed; links with logins dropped");
    var many = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"# Post {i}\n" + new string('x', 5000)));
    var capped = MMLauncher.Core.News.NewsFormat.Parse(many);
    Check(capped.Count == MMLauncher.Core.News.NewsFormat.MaxItems && capped.All(p => p.Body.Length <= MMLauncher.Core.News.NewsFormat.MaxBody),
          "at most 8 posts, each text capped");
    var hostile = MMLauncher.Core.News.NewsFormat.Sanitize([new() { Title = new string('t', 900), Body = "a\u0000b", Link = "file:///C:/Windows" }, new() { Title = "", Body = "" }]);
    Check(hostile.Count == 1 && hostile[0].Title.Length <= 100 && hostile[0].Body == "ab" && hostile[0].Link == "", "news from an agent is clipped and cleaned again by the launcher");

    var seen = new MMLauncher.Core.News.NewsSeen();
    Check(!seen.IsSeen("h:1", posts[0]) && seen.MarkSeen("h:1", posts) && seen.IsSeen("h:1", posts[0]) && !seen.MarkSeen("h:1", posts),
          "seen posts remembered per server");
    Check(!seen.IsSeen("h:2", posts[0]), "seen on one server doesn't hide it on another");

    // Agent: <port>.txt first, then all.txt.
    var newsDir = MMServerAgent.NewsFolder.FolderPath;
    Directory.CreateDirectory(newsDir);
    File.WriteAllText(Path.Combine(newsDir, "all.txt"), "# For everyone\nhello\n");
    File.WriteAllText(Path.Combine(newsDir, "2312.txt"), "# Nasdara only\nhi\n");
    File.WriteAllText(Path.Combine(newsDir, "notes.txt"), "# Not a server file\n");
    var folder = new MMServerAgent.NewsFolder(_ => { });
    var forNasdara = folder.For(2312);
    var forOther = folder.For(2302);
    Check(forNasdara is { Count: 2 } && forNasdara[0].Title == "Nasdara only" && forNasdara[1].Title == "For everyone"
          && forOther is { Count: 1 } && forOther[0].Title == "For everyone", "agent news: the server's own file first, then all.txt; other files ignored");
    File.Delete(Path.Combine(newsDir, "2312.txt"));
    File.WriteAllText(Path.Combine(newsDir, "all.txt"), MMServerAgent.NewsFolder.StarterText);
    Check(folder.For(2312) == null, "changes picked up; the commented starter file shows nothing");
    Directory.Delete(newsDir, true);
}

Console.WriteLine("== Agent over HTTP (if one is running on localhost:2999) ==");
var agentReply = await new AgentClient(new AgentTrust(Path.Combine(root, "trust-live"))).QueryAsync("localhost", 2999);
if (agentReply == null) Console.WriteLine("   (no agent running; skipped)");
else
{
    foreach (var s in agentReply.Response.Servers)
        Console.WriteLine($"   agent {agentReply.Response.Version}: {s.Name} game {s.GamePort} query {s.QueryPort}, {s.Mods.Count} mods");
    Check(agentReply.Response.Servers.Count > 0 && agentReply.Response.Servers.All(s => s.Mods.All(m => !m.Folder.Contains('/') && !m.Folder.Contains('\\'))),
        "agent replied; no file paths leaked");
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
