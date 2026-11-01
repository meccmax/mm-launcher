using MMLauncher.Core.Launch;

namespace MMLauncher.App.Steam;

/// <summary>Progress for the download panel.</summary>
public sealed record SyncProgress(
    string Title,          // e.g. "Downloading 3 of 7"
    string Detail,         // e.g. "MM Weapons 2.0  ·  1.2 GB of 4.6 GB"
    double Fraction);      // 0..1 overall, or -1 for "working, no percentage"

public sealed record SyncResult(bool Success, string Message, IReadOnlyList<RequiredMod> Failed);

/// <summary>
/// Mods being repaired: their folders were deleted, so Steam's own "installed" flag can't be
/// trusted. They count as done only once their folder is back on disk and passes the file check.
/// </summary>
public sealed record RepairSpec(string WorkshopPath, IReadOnlyList<ulong> Ids);

/// <summary>Gets a server's mods onto this PC through the player's Steam client.</summary>
public interface IModDownloader
{
    /// <param name="all">Every mod the server needs (all get subscribed so Steam keeps them updated).</param>
    /// <param name="toDownload">The ones that are missing or outdated right now.</param>
    /// <param name="sizes">Workshop sizes in bytes, for overall progress.</param>
    /// <param name="repair">Mods whose folders were deleted for a repair, or null.</param>
    Task<SyncResult> SyncAsync(
        IReadOnlyList<RequiredMod> all,
        IReadOnlyList<RequiredMod> toDownload,
        IReadOnlyDictionary<ulong, long> sizes,
        RepairSpec? repair,
        IProgress<SyncProgress> progress,
        CancellationToken ct);

    /// <summary>Unsubscribes from mods so Steam stops keeping them (the caller deletes the folders).</summary>
    Task<SyncResult> RemoveAsync(IReadOnlyList<RequiredMod> mods, IProgress<SyncProgress> progress, CancellationToken ct);
}

public static class ModDownloader
{
    /// <summary>
    /// The real Steam downloader on Windows. MMLAUNCHER_FAKE_STEAM=1 swaps in a simulator
    /// that writes fake mod folders, used to test the launcher without Steam.
    /// </summary>
    public static IModDownloader Create() =>
        Environment.GetEnvironmentVariable("MMLAUNCHER_FAKE_STEAM") == "1" || !OperatingSystem.IsWindows()
            ? new FakeDownloader()
            : new SyncHelperProcess(); // runs SteamworksDownloader in a separate process

    public static string Size(long bytes) => bytes switch
    {
        <= 0 => "0 MB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };
}
