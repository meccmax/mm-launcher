using MMLauncher.Core.Services;
using MMLauncher.Core.Steam;

namespace MMLauncher.Core.Launch;

/// <summary>A mod a server needs, in the server's load order.</summary>
public sealed record RequiredMod(ulong Id, string Name);

/// <summary>
/// Which of a server's mods are ready, which Steam must download or update first,
/// and which can't be fixed by downloading (removed from the Workshop / no ID).
/// </summary>
public sealed record ModPlan(
    IReadOnlyList<RequiredMod> All,
    IReadOnlyList<RequiredMod> ToDownload,
    IReadOnlyList<RequiredMod> Blocked,
    long DownloadBytes)
{
    public bool ReadyToLaunch => ToDownload.Count == 0 && Blocked.Count == 0;

    public static ModPlan Build(IReadOnlyList<RequiredMod> mods, LocalSteam steam, IReadOnlyDictionary<ulong, WorkshopItem> workshop)
    {
        var toDownload = new List<RequiredMod>();
        var blocked = new List<RequiredMod>();
        long bytes = 0;

        foreach (var mod in mods)
        {
            var ws = workshop.GetValueOrDefault(mod.Id);
            var state = ModStatus.Evaluate(steam.Mods.GetValueOrDefault(mod.Id), ws, steamFound: true, mod.Id);
            switch (state)
            {
                case ModState.Installed:
                    break;
                case ModState.Missing:
                case ModState.UpdateAvailable:
                case ModState.Unknown:
                    toDownload.Add(mod);
                    bytes += ws?.SizeBytes ?? 0;
                    break;
                default: // Unavailable, NoWorkshopId
                    blocked.Add(mod);
                    break;
            }
        }
        return new ModPlan(mods, toDownload, blocked, bytes);
    }
}
