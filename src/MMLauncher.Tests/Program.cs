using System.Net;
using MMLauncher.Core.Agent;
using MMLauncher.Core.Config;
using MMLauncher.Core.DayZ;
using MMLauncher.Core.Discovery;
using MMLauncher.Core.Query;
using MMLauncher.Core.Services;
using MMLauncher.Core.Steam;
using MMLauncher.Core.Updates;

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
    Check(migrated.LaunchMethod == "Direct" && File.ReadAllText(LauncherSettings.FilePath).Contains("\"SettingsVersion\": 3"), "old settings migrated to Direct launch");
    File.WriteAllText(LauncherSettings.FilePath, File.ReadAllText(LauncherSettings.FilePath).Replace("\"LaunchMethod\": \"Direct\"", "\"LaunchMethod\": \"Steam\""));
    Check(LauncherSettings.Load().LaunchMethod == "Steam", "a deliberate Steam choice in a new file is kept");
    // v0.4.x file (version 2): rewritten so the new AutoRejoin option and its comment appear; choices kept.
    File.WriteAllText(LauncherSettings.FilePath, "{ \"LaunchMethod\": \"Steam\", \"Hosts\": [\"x\"], \"SettingsVersion\": 2 }");
    var v2 = LauncherSettings.Load();
    var v2text = File.ReadAllText(LauncherSettings.FilePath);
    Check(v2.LaunchMethod == "Steam" && !v2.AutoRejoin && v2text.Contains("\"AutoRejoin\": false") && v2text.Contains("// Rejoin automatically")
          && v2text.Contains("\"SettingsVersion\": 3"), "version 2 settings gain the AutoRejoin option (off) and keep choices");
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

Console.WriteLine("== Agent over HTTP (if one is running on localhost:2999) ==");
var agentReply = await new AgentClient(new HttpClient()).QueryAsync("localhost", 2999);
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
