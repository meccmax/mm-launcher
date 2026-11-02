using System.Text;
using MMLauncher.Core.Config;
using MMLauncher.Core.Launch;
using MMLauncher.Core.Logging;
using MMLauncher.Core.Steam;

namespace MMLauncher.App.Steam;

/// <summary>
/// Pretend Steam, for testing the launcher without a Steam client (MMLAUNCHER_FAKE_STEAM=1).
/// "Downloads" by creating mod folders in the configured Steam library and updating
/// appworkshop_221100.acf, exactly where the real Steam would put them.
/// </summary>
public sealed class FakeDownloader : IModDownloader
{
    public async Task<SyncResult> SyncAsync(
        IReadOnlyList<RequiredMod> all,
        IReadOnlyList<RequiredMod> toDownload,
        IReadOnlyDictionary<ulong, long> sizes,
        RepairSpec? repair,
        IProgress<SyncProgress> progress,
        CancellationToken ct)
    {
        var settings = LauncherSettings.Load();
        var steam = LocalSteam.Detect(settings.SteamPathOverride, settings.DayZPathOverride, settings.WorkshopPathOverride);
        var content = steam?.WorkshopPath;
        if (content == null)
            return new SyncResult(false, "Fake Steam: no workshop folder.", toDownload);

        progress.Report(new SyncProgress("Connecting to Steam…", "(simulated)", -1));
        await Task.Delay(600, ct);

        try
        {
            long total = Math.Max(1, toDownload.Sum(m => sizes.GetValueOrDefault(m.Id)));
            long finished = 0;
            for (int i = 0; i < toDownload.Count; i++)
            {
                var mod = toDownload[i];
                long size = sizes.GetValueOrDefault(mod.Id);
                for (int step = 1; step <= 10; step++)
                {
                    await Task.Delay(60, ct);
                    long done = size * step / 10;
                    progress.Report(new SyncProgress($"Downloading {i + 1} of {toDownload.Count}",
                        $"{mod.Name}  ·  {ModDownloader.Size(done)} of {ModDownloader.Size(size)}",
                        (finished + done) / (double)total));
                }
                finished += size;
                // MMLAUNCHER_FAKE_STALE=1: act like Steam that hasn't noticed a new upload (an
                // installed mod is left at its old version), to test what the launcher does then.
                bool stale = Environment.GetEnvironmentVariable("MMLAUNCHER_FAKE_STALE") == "1" && LocalSteam.HasAnyFile(Path.Combine(content, mod.Id.ToString()));
                if (!stale) Install(content, mod.Id, mod.Name);
                Log.Info($"(fake) Downloaded {mod.Id} {mod.Name}");
            }
            return new SyncResult(true, $"Downloaded {toDownload.Count} mod(s). (simulated)", []);
        }
        catch (OperationCanceledException)
        {
            return new SyncResult(false, "Cancelled.", toDownload);
        }
    }

    public async Task<SyncResult> RemoveAsync(IReadOnlyList<RequiredMod> mods, IProgress<SyncProgress> progress, CancellationToken ct)
    {
        progress.Report(new SyncProgress("Connecting to Steam…", "(simulated)", -1));
        await Task.Delay(300, ct);
        foreach (var m in mods) Log.Info($"(fake) Unsubscribed {m.Id} {m.Name}");
        return new SyncResult(true, $"Unsubscribed from {mods.Count} mod(s). (simulated)", []);
    }

    private static void Install(string content, ulong id, string name)
    {
        var workshop = Directory.GetParent(content)!.Parent!.FullName;
        var modDir = Path.Combine(content, id.ToString());
        var folder = Path.Combine(modDir, "addons");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "fake.pbo"), "fake");
        File.WriteAllText(Path.Combine(folder, "fake.pbo.mm.bisign"), "sig");
        File.WriteAllText(Path.Combine(modDir, "meta.cpp"), $"protocol = 1;\npublishedid = {id};\nname = \"{name.Replace("\"", "")}\";\n");

        var acf = Path.Combine(workshop, $"appworkshop_{LocalSteam.DayZAppId}.acf");
        var items = new Dictionary<string, (string Size, string Time)>();
        if (File.Exists(acf))
        {
            var installed = VdfNode.Parse(File.ReadAllText(acf))["AppWorkshop"]?["WorkshopItemsInstalled"];
            if (installed != null)
                foreach (var (k, v) in installed.Children)
                    items[k] = (v["size"]?.Value ?? "0", v["timeupdated"]?.Value ?? "0");
        }
        items[id.ToString()] = ("1", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());

        var sb = new StringBuilder("\"AppWorkshop\"\n{\n\t\"WorkshopItemsInstalled\"\n\t{\n");
        foreach (var (k, v) in items)
            sb.Append($"\t\t\"{k}\"\n\t\t{{\n\t\t\t\"size\"\t\t\"{v.Size}\"\n\t\t\t\"timeupdated\"\t\t\"{v.Time}\"\n\t\t}}\n");
        sb.Append("\t}\n}\n");
        File.WriteAllText(acf, sb.ToString());
    }
}
