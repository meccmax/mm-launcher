using System.Reflection;
using System.Runtime.InteropServices;
using MMLauncher.Core.Launch;

using MMLauncher.Core.Logging;
using Steamworks;
using Steamworks.Ugc;
using MMLauncher.Core.Steam;

namespace MMLauncher.App.Steam;

/// <summary>
/// Downloads mods through the player's own Steam client using the Steamworks API.
/// The launcher connects to Steam as DayZ (app 221100) only for the duration of a sync,
/// so Steam shows "Playing DayZ" just while downloading, then disconnects.
/// Mods stay subscribed afterwards, so Steam keeps them updated like normal.
/// </summary>
public sealed class SteamworksDownloader : IModDownloader
{
    private const uint DayZAppId = 221100;
    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan QueuedLimit = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RepairNudgeAfter = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StaleGiveUp = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StateLogEvery = TimeSpan.FromSeconds(10);
    private static bool _nativeReady;

    public async Task<SyncResult> SyncAsync(
        IReadOnlyList<RequiredMod> all,
        IReadOnlyList<RequiredMod> toDownload,
        IReadOnlyDictionary<ulong, long> sizes,
        RepairSpec? repair,
        IProgress<SyncProgress> progress,
        CancellationToken ct)
    {
        try
        {
            EnsureNativeLibrary();
        }
        catch (Exception ex)
        {
            Log.Error("Steam API library setup failed: " + ex);
            return new SyncResult(false, "Couldn't load the Steam API library: " + ex.Message, toDownload);
        }

        progress.Report(new SyncProgress("Connecting to Steam…", "Steam will show you as playing DayZ while mods download.", -1));
        try
        {
            if (SteamClient.IsValid) SteamClient.Shutdown();
            SteamClient.Init(DayZAppId, asyncCallbacks: true);
            Log.Info($"Connected to Steam as {SteamClient.Name}.");
        }
        catch (Exception ex)
        {
            Log.Error("SteamClient.Init failed: " + ex.Message);
            return new SyncResult(false,
                "Couldn't connect to Steam. Make sure Steam is running and logged in to the account that owns DayZ, then try again.",
                toDownload);
        }

        var failed = new List<RequiredMod>();
        try
        {
            // 1) Subscribe to everything the server needs so Steam keeps it updated.
            int n = 0;
            foreach (var mod in all)
            {
                ct.ThrowIfCancellationRequested();
                n++;
                var item = new Item(mod.Id);
                if (item.IsSubscribed) continue;
                progress.Report(new SyncProgress($"Subscribing ({n} of {all.Count})", mod.Name, -1));
                bool ok = await item.Subscribe();
                Log.Info($"Subscribe {mod.Id} {mod.Name}: {(ok ? "ok" : "FAILED")}");
            }

            // 2) Download what's missing or outdated. Steam has the final say on what
            //    needs updating, so ask it about every mod, not just our own guess.
            //    Steam's own "installed" flag goes stale if a mod folder is deleted outside
            //    Steam, so a mod also counts as missing when its folder isn't on disk.
            var wanted = toDownload.Select(m => m.Id).ToHashSet();
            if (repair != null) wanted.UnionWith(repair.Ids);
            var queue = all.Where(m =>
            {
                if (wanted.Contains(m.Id)) return true;
                var it = new Item(m.Id);
                return !it.IsInstalled || it.NeedsUpdate || !OnDisk(FolderOf(repair, m.Id));
            }).ToList();

            long totalBytes = Math.Max(1, queue.Sum(m => sizes.GetValueOrDefault(m.Id)));
            long finishedBytes = 0;
            Log.Info($"Downloading {queue.Count} mod(s), about {ModDownloader.Size(totalBytes)}.");

            for (int i = 0; i < queue.Count; i++)
            {
                var mod = queue[i];
                long size = sizes.GetValueOrDefault(mod.Id);
                string title = $"Downloading {i + 1} of {queue.Count}";
                bool isRepair = repair != null && repair.Ids.Contains(mod.Id);
                if (isRepair) title = $"Repairing {i + 1} of {queue.Count}";
                long? mustReach = repair?.MustReach?.TryGetValue(mod.Id, out var v) == true ? v : null;
                bool ok = await DownloadOneAsync(mod, FolderOf(repair, mod.Id), isRepair, mustReach, repair?.WorkshopPath, ct, (done, total) =>
                {
                    long current = total > 0 ? Math.Min(done, total) : 0;
                    double fraction = (finishedBytes + (size > 0 && total > 0 ? (double)current / total * size : 0)) / totalBytes;
                    string detail = total > 0
                        ? $"{mod.Name}  ·  {ModDownloader.Size(current)} of {ModDownloader.Size(total)}"
                        : $"{mod.Name}  ·  waiting for Steam…";
                    progress.Report(new SyncProgress(title, detail, Math.Clamp(fraction, 0, 1)));
                });
                finishedBytes += size;
                if (!ok) failed.Add(mod);
            }

            return failed.Count == 0
                ? new SyncResult(true, queue.Count == 0 ? "All mods were already up to date." : $"Downloaded {queue.Count} mod(s).", failed)
                : new SyncResult(false, $"{failed.Count} mod(s) didn't finish downloading: {string.Join(", ", failed.Select(f => f.Name))}. Check Steam's Downloads page. If they show as paused there, turn on Steam > Settings > Downloads > \"Allow downloads during gameplay\" and try again.", failed);
        }
        catch (OperationCanceledException)
        {
            Log.Info("Sync cancelled by user.");
            return new SyncResult(false, "Cancelled. Steam may finish any download already in progress.", toDownload);
        }
        finally
        {
            try { SteamClient.Shutdown(); } catch { }
            Log.Info("Disconnected from Steam.");
        }
    }

    public async Task<SyncResult> RemoveAsync(IReadOnlyList<RequiredMod> mods, IProgress<SyncProgress> progress, CancellationToken ct)
    {
        try { EnsureNativeLibrary(); }
        catch (Exception ex) { return new SyncResult(false, "Couldn't load the Steam API library: " + ex.Message, mods); }

        progress.Report(new SyncProgress("Connecting to Steam…", "Steam shows you as playing DayZ for a moment.", -1));
        try
        {
            if (SteamClient.IsValid) SteamClient.Shutdown();
            SteamClient.Init(DayZAppId, asyncCallbacks: true);
        }
        catch (Exception ex)
        {
            Log.Error("SteamClient.Init failed: " + ex.Message);
            return new SyncResult(false, "Couldn't connect to Steam. Make sure Steam is running and logged in, then try again.", mods);
        }
        var failed = new List<RequiredMod>();
        try
        {
            int n = 0;
            foreach (var mod in mods)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(new SyncProgress($"Unsubscribing ({++n} of {mods.Count})", mod.Name, (double)n / mods.Count));
                var item = new Item(mod.Id);
                bool ok = !item.IsSubscribed || await item.Unsubscribe();
                Log.Info($"Unsubscribe {mod.Id} {mod.Name}: {(ok ? "ok" : "FAILED")}");
                if (!ok) failed.Add(mod);
            }
            return failed.Count == 0
                ? new SyncResult(true, $"Unsubscribed from {mods.Count} mod(s).", [])
                : new SyncResult(false, $"Steam didn't unsubscribe {failed.Count} mod(s): {string.Join(", ", failed.Select(f => f.Name))}.", failed);
        }
        finally
        {
            try { SteamClient.Shutdown(); } catch { }
        }
    }

    private static string? FolderOf(RepairSpec? spec, ulong id) =>
        string.IsNullOrEmpty(spec?.WorkshopPath) ? null : Path.Combine(spec.WorkshopPath, id.ToString());

    /// <summary>True when the mod's folder exists and isn't empty (or we can't tell where it is).</summary>
    private static bool OnDisk(string? folder) =>
        folder == null || (Directory.Exists(folder) && LocalSteam.HasAnyFile(folder));

    private static async Task<bool> DownloadOneAsync(RequiredMod mod, string? folder, bool isRepair, long? mustReach, string? contentDir,
        CancellationToken ct, Action<long, long> report)
    {
        // The Workshop has a newer version than this PC (mustReach). Steam's "needs update" flag
        // can still say no for a while after an upload, so check the version Steam actually
        // installed (its acf records), read at most every 2 seconds.
        DateTime lastVersionRead = DateTime.MinValue;
        bool fresh = mustReach == null;
        bool Fresh()
        {
            if (fresh) return true;
            if (contentDir == null) return true;
            if (DateTime.UtcNow - lastVersionRead < TimeSpan.FromSeconds(2)) return false;
            lastVersionRead = DateTime.UtcNow;
            fresh = LocalSteam.InstalledVersion(contentDir, mod.Id) is { } have && have.ToUnixTimeSeconds() >= mustReach!.Value - 60;
            return fresh;
        }

        // Steam can believe a mod is installed when its folder was deleted behind its back
        // (a repair, or files removed by hand / by a cleanup tool). So "done" always also
        // means the folder is really on disk. For a repair, Steam's flags are ignored entirely.
        // (A repaired mod that still fails the file check is reported afterwards: that usually
        // means the author's upload itself is broken, and downloading again won't fix it.)
        bool Done(Item it) =>
            !it.IsDownloading && !it.IsDownloadPending && OnDisk(folder) &&
            (isRepair || (it.IsInstalled && !it.NeedsUpdate)) && Fresh();

        var item = new Item(mod.Id);
        if (Done(item)) { report(1, 1); return true; }
        if (mustReach != null)
            Log.Info($"{mod.Id} {mod.Name}: the Workshop has a newer version (Steam says needsUpdate={item.NeedsUpdate}); asking Steam to fetch it.");
        bool resubscribed = false;
        var started = DateTime.UtcNow;

        if (!SteamUGC.Download(mod.Id, highPriority: true))
            Log.Warn($"Steam declined to start download of {mod.Id} {mod.Name} (will keep watching).");

        long lastBytes = -1;
        var lastChange = DateTime.UtcNow;
        var lastStateLog = DateTime.UtcNow;
        int retries = 0;
        while (true)
        {
            await Task.Delay(PollEvery, ct);
            item = new Item(mod.Id);
            long done = item.DownloadBytesDownloaded, total = item.DownloadBytesTotal;
            bool onDisk = OnDisk(folder);
            // Steam reports "all bytes downloaded" for an item it believes is installed even
            // when the folder is gone; don't show that as 100%.
            if (!onDisk && !item.IsDownloading) report(0, 0);
            else report(done, total);

            if (DateTime.UtcNow - lastStateLog > StateLogEvery)
            {
                lastStateLog = DateTime.UtcNow;
                Log.Info($"  {mod.Id} {mod.Name}: installed={item.IsInstalled} needsUpdate={item.NeedsUpdate} downloading={item.IsDownloading} pending={item.IsDownloadPending} {done}/{total} bytes, folder on disk={onDisk}");
            }

            if (Done(item))
            {
                Log.Info($"Downloaded {mod.Id} {mod.Name} -> {item.Directory}");
                return true;
            }

            if (done != lastBytes)
            {
                lastBytes = done;
                lastChange = DateTime.UtcNow;
                continue;
            }

            // Fallback for a deleted folder, or a new version Steam hasn't noticed: if Steam hasn't
            // started downloading after a while, it still thinks the mod is current. Unsubscribing
            // and subscribing again makes Steam fetch it fresh.
            bool stale = !Fresh();
            if ((!OnDisk(folder) || stale) && !resubscribed && !item.IsDownloading && !item.IsDownloadPending &&
                DateTime.UtcNow - started > RepairNudgeAfter)
            {
                resubscribed = true;
                Log.Warn(stale
                    ? $"{mod.Id} {mod.Name}: the Workshop has a newer version but Steam isn't downloading it; unsubscribing and subscribing again."
                    : $"{mod.Id} {mod.Name}: folder is missing but Steam isn't downloading it; unsubscribing and subscribing again.");
                await item.Unsubscribe();
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                await new Item(mod.Id).Subscribe();
                SteamUGC.Download(mod.Id, highPriority: true);
                lastChange = DateTime.UtcNow;
                continue;
            }

            // Steam says done/installed but the folder still isn't there after the nudge:
            // don't wait the full stall timer on a download that will never move.
            if ((!OnDisk(folder) || stale) && resubscribed && !item.IsDownloading && !item.IsDownloadPending &&
                item.IsInstalled && DateTime.UtcNow - lastChange > StaleGiveUp)
            {
                Log.Error(stale
                    ? $"{mod.Id} {mod.Name}: the Workshop has a newer version, but Steam keeps the old one and won't download it."
                    : $"{mod.Id} {mod.Name}: Steam reports it installed, but its folder is missing and Steam won't re-download it ({item.Directory}).");
                return false;
            }

            var limit = item.IsDownloadPending && !item.IsDownloading ? QueuedLimit : StallLimit;
            if (DateTime.UtcNow - lastChange < limit) continue;

            if (retries++ < 2)
            {
                Log.Warn($"{mod.Id} {mod.Name}: no progress for {limit.TotalSeconds:0}s, asking Steam again (retry {retries}).");
                SteamUGC.Download(mod.Id, highPriority: true);
                lastChange = DateTime.UtcNow;
                continue;
            }
            Log.Error($"{mod.Id} {mod.Name}: gave up (installed={item.IsInstalled}, needsUpdate={item.NeedsUpdate}, downloading={item.IsDownloading}, pending={item.IsDownloadPending}, {done}/{total} bytes).");
            return false;
        }
    }

    /// <summary>
    /// Writes the embedded steam_api64.dll to %LOCALAPPDATA%\MMLauncher\native and tells
    /// .NET to load it from there whenever the Steamworks wrapper asks for "steam_api64".
    /// </summary>
    private static void EnsureNativeLibrary()
    {
        if (_nativeReady) return;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MMLauncher", "native");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "steam_api64.dll");

        using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("steam_api64.dll")
               ?? throw new InvalidOperationException("steam_api64.dll is missing from the launcher."))
        {
            if (!File.Exists(path) || new FileInfo(path).Length != res.Length)
            {
                using var file = File.Create(path);
                res.CopyTo(file);
            }
        }

        NativeLibrary.SetDllImportResolver(typeof(SteamClient).Assembly, (name, _, _) =>
            name.Contains("steam_api", StringComparison.OrdinalIgnoreCase) ? NativeLibrary.Load(path) : IntPtr.Zero);
        _nativeReady = true;
    }
}
