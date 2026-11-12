using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMLauncher.App.Steam;
using MMLauncher.Core.Config;
using MMLauncher.Core.DayZ;
using MMLauncher.Core.Editor;
using MMLauncher.Core.Launch;
using MMLauncher.Core.Logging;
using MMLauncher.Core.Services;
using MMLauncher.Core.Steam;
using MMLauncher.Core.Workshop;

namespace MMLauncher.App.ViewModels;

/// <summary>
/// The MODS page:
///  - Installed: every downloaded DayZ Workshop mod, what uses it, update all, remove
///    (unsubscribe + delete) one or all unused mods.
///  - Workshop: search the DayZ Workshop (through an MM agent's Steam key) and Install
///    (one click; Steam briefly shows "Playing DayZ" while it downloads) or Open in Steam.
/// </summary>
public partial class MainViewModel
{
    private List<ModViewModel> _allInstalled = [];
    private Dictionary<ulong, WorkshopItem> _installedWorkshop = new();
    private int _workshopPage = 1;
    private string _lastSearch = "";

    [ObservableProperty] private string _modsView = "installed";
    [ObservableProperty] private string _installedSearch = "";
    [ObservableProperty] private int _installedFilterIndex;
    [ObservableProperty] private string _installedSummary = "";
    [ObservableProperty] private bool _installedLoading;
    [ObservableProperty] private string _installedEmptyText = "";
    [ObservableProperty] private string _workshopSearch = "";
    [ObservableProperty] private int _workshopSortIndex;
    [ObservableProperty] private string _workshopStatus = "";
    [ObservableProperty] private bool _workshopLoading;
    [ObservableProperty] private bool _workshopHasMore;
    [ObservableProperty] private string _staleNotice = "";

    public ObservableCollection<ModViewModel> InstalledMods { get; } = [];
    public ObservableCollection<WorkshopRowViewModel> WorkshopResults { get; } = [];

    /// <summary>The fixed filters, then one "List: …" entry per imported mod list.</summary>
    [ObservableProperty] private IReadOnlyList<string> _installedFilterChoices = BaseFilters;
    private static readonly IReadOnlyList<string> BaseFilters =
        ["All mods", "Need an update", "Not used by anything", $"Not played in {ModUsage.StaleDays}+ days", "Damaged files"];
    private const int StaleFilter = 3;
    private const int DamagedFilter = 4;
    /// <summary>The imported list picked in the filter, or null.</summary>
    private ModList? FilterList => InstalledFilterIndex >= BaseFilters.Count && InstalledFilterIndex - BaseFilters.Count < ModLists.Current.Lists.Count
        ? ModLists.Current.Lists[InstalledFilterIndex - BaseFilters.Count] : null;

    public bool HasStaleNotice => StaleNotice.Length > 0;
    partial void OnStaleNoticeChanged(string value) => OnPropertyChanged(nameof(HasStaleNotice));

    /// <summary>The toolbar's remove button: "Remove unused…", or "Remove these N…" when showing mods not played lately.</summary>
    public string RemoveSetText => FilterList != null ? "Forget this list…"
        : InstalledFilterIndex == StaleFilter ? $"Remove these {_allInstalled.Count(m => m.IsStale)}…"
        : "Remove unused…";
    public string RemoveSetTip => FilterList != null
        ? "Forget this imported list. Its mods stay downloaded; ones nothing else uses then show as \"not used by anything\"."
        : InstalledFilterIndex == StaleFilter
        ? $"Unsubscribe from and delete every mod you haven't played with in {ModUsage.StaleDays}+ days. Ones a server still uses are downloaded again when you join it."
        : "Unsubscribe from and delete mods no MM server, favorite, recent server, imported list or editor preset uses";
    public IReadOnlyList<string> WorkshopSortChoices { get; } = WorkshopSearchReply.Sorts.Select(s => s.Label).ToList();

    public bool IsModsPage => Page == "mods";
    public bool IsInstalledView => ModsView == "installed";
    public bool IsWorkshopView => ModsView == "workshop";
    public bool CanChangeMods => !IsSyncing && SteamFound;

    partial void OnModsViewChanged(string value)
    {
        OnPropertyChanged(nameof(IsInstalledView));
        OnPropertyChanged(nameof(IsWorkshopView));
        if (value == "workshop" && WorkshopResults.Count == 0) _ = SearchWorkshopAsync();
        if (value == "installed") _ = LoadInstalledModsAsync();
    }

    [RelayCommand] private async Task ShowModsPageAsync() { Page = "mods"; await LoadInstalledModsAsync(); }
    [RelayCommand] private void ShowInstalledView() => ModsView = "installed";
    [RelayCommand] private void ShowWorkshopView() => ModsView = "workshop";

    // ------------------------------------------------------------------ installed

    private async Task LoadInstalledModsAsync()
    {
        if (InstalledLoading) return;
        InstalledLoading = true;
        try
        {
            DetectSteam();
            if (_steam == null) { InstalledSummary = "Steam wasn't found. Check Settings."; return; }
            var ids = _steam.Mods.Keys.ToList();
            var missing = ids.Where(id => !_installedWorkshop.ContainsKey(id)).ToList();
            if (missing.Count > 0)
            {
                try { foreach (var (id, item) in await _service.Workshop.GetDetailsAsync(missing)) _installedWorkshop[id] = item; }
                catch (Exception ex) { Log.Warn("Workshop lookup for installed mods failed: " + ex.Message); }
            }
            if (_sharedPresets.Count == 0) { try { await LoadSharedPresetsAsync(); } catch (Exception ex) { Log.Warn("Shared presets: " + ex.Message); } }
            var usedBy = UsedByMap();
            var usage = ModUsage.Current;
            var now = DateTimeOffset.Now;
            _allInstalled = ids.Select(id =>
            {
                var local = _steam.Mods[id];
                var vm = new ModViewModel(new DayZServerMod(id, MetaName(local.Folder) ?? id.ToString(), 0));
                vm.Apply(_installedWorkshop.GetValueOrDefault(id), local, true);
                if (vm.SizeBytes == 0 && local.SizeBytes > 0) { vm.SizeBytes = local.SizeBytes; vm.SizeText = Format.Size(local.SizeBytes); }
                vm.IsStale = usage.IsStale(id, now);
                vm.LastUsedText = ModUsage.Describe(usage.LastUsedOf(id), now) is { } when ? "last played " + when
                                : vm.IsStale ? $"not played in {(int)(now - usage.Since).TotalDays}+ days" : "";
                if (usedBy.TryGetValue(id, out var users))
                {
                    vm.UsedBy = string.Join(", ", users.Take(8)) + (users.Count > 8 ? $" and {users.Count - 8} more" : "");
                    bool editor = users.Contains("the DayZ Editor");
                    int presets = users.Count(u => u.StartsWith("editor: ", StringComparison.Ordinal));
                    int lists = users.Count(u => u.StartsWith("list: ", StringComparison.Ordinal));
                    int servers = users.Count - presets - lists - (editor ? 1 : 0);
                    vm.UsedByShort = "used by " + string.Join(", ", new[]
                    {
                        servers > 0 ? $"{servers} server{(servers == 1 ? "" : "s")}" : "",
                        lists > 0 ? $"{lists} mod list{(lists == 1 ? "" : "s")}" : "",
                        presets > 0 ? $"{presets} editor preset{(presets == 1 ? "" : "s")}" : "",
                        editor ? "the DayZ Editor" : "",
                    }.Where(x => x.Length > 0));
                }
                if (_damagedInstalled.ContainsKey(id))
                {
                    if (ModHealth.Check(id, local) is { Ok: false } still) vm.HealthProblems = still.Summary;
                    else _damagedInstalled.Remove(id);
                }
                return vm;
            }).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
            ApplyInstalledFilter();
        }
        finally
        {
            InstalledLoading = false;
        }
    }

    /// <summary>
    /// Mod ID → the MM servers, saved servers and editor presets that use it. Favorites and
    /// recent servers count even when their tab hasn't been opened, from the mod lists saved
    /// with them. <paramref name="except"/> leaves one server out (for "remove this server's mods").
    /// </summary>
    private Dictionary<ulong, List<string>> UsedByMap(ServerViewModel? except = null)
    {
        bool IsExcluded(string host, int queryPort) => except != null && queryPort == except.Cached.QueryPort &&
            (host.Equals(except.Cached.Host, StringComparison.OrdinalIgnoreCase) || host.Equals(except.ConnectIp, StringComparison.OrdinalIgnoreCase));
        var map = new Dictionary<ulong, List<string>>();
        void Add(ulong id, string who)
        {
            if (!map.TryGetValue(id, out var list)) map[id] = list = [];
            if (!list.Contains(who)) list.Add(who);
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Servers.Concat(FavoriteServers).Concat(RecentServers))
        {
            if (IsExcluded(s.Cached.Host, s.Cached.QueryPort) || IsExcluded(s.ConnectIp, s.Cached.QueryPort)) continue;
            if (s.Mods.Count > 0) seen.Add(s.Key);
            foreach (var m in s.Mods) Add(m.WorkshopId, ShortName(s.Name));
        }
        foreach (var saved in _lists.Favorites.Concat(_lists.Recent))
        {
            if (IsExcluded(saved.Host, saved.QueryPort) || seen.Contains(saved.Key)) continue;
            foreach (var id in saved.ModIds) Add(id, ShortName(saved.Name.Length > 0 ? saved.Name : saved.Key));
        }
        // The DayZ Editor's own mods count as used by the DAYZ EDITOR page, so "Remove unused"
        // never takes the Editor away.
        foreach (var m in DayZEditor.Required) Add(m.Id, "the DayZ Editor");
        foreach (var list in ModLists.Current.Lists)
            foreach (var m in list.Mods) Add(m.Id, "list: " + list.Name);
        foreach (var p in _presetStore.Presets.Concat(_sharedPresets))
            foreach (var m in DayZEditor.LoadOrder(p).Where(m => DayZEditor.Required.All(r => r.Id != m.Id)))
                Add(m.Id, "editor: " + p.Name);
        return map;
    }

    /// <summary>The mod's own name from its meta.cpp (used when the Workshop can't be reached).</summary>
    private static string? MetaName(string folder)
    {
        try
        {
            var path = Path.Combine(folder, "meta.cpp");
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return null;
            var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(path), @"(?m)^\s*name\s*=\s*""([^""\r\n]{1,120})""");
            return m.Success && m.Groups[1].Value.Trim() is { Length: > 0 } n ? n : null;
        }
        catch { return null; }
    }

    private static string ShortName(string name) => name.Length <= 40 ? name : name[..39] + "…";

    partial void OnInstalledSearchChanged(string value) => ApplyInstalledFilter();
    partial void OnInstalledFilterIndexChanged(int value)
    {
        if (value < 0) return; // the choices list is being replaced
        ApplyInstalledFilter();
        OnPropertyChanged(nameof(RemoveSetText));
        OnPropertyChanged(nameof(RemoveSetTip));
    }

    [RelayCommand] private void ShowStaleMods() => InstalledFilterIndex = StaleFilter;

    private void ApplyInstalledFilter()
    {
        var words = InstalledSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var list = _allInstalled.Where(m =>
            words.All(w => m.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || m.WorkshopId.ToString().StartsWith(w)) &&
            (FilterList is { } fl ? fl.Mods.Any(x => x.Id == m.WorkshopId)
                : InstalledFilterIndex switch { 1 => m.IsUpdate, 2 => m.IsUnused, StaleFilter => m.IsStale, DamagedFilter => m.IsDamaged, _ => true })).ToList();
        InstalledMods.Clear();
        foreach (var m in list) InstalledMods.Add(m);
        InstalledEmptyText = list.Count > 0 ? "" : _allInstalled.Count == 0 ? "No DayZ mods downloaded yet."
            : InstalledFilterIndex == DamagedFilter ? "No damaged mods found. Click Check files to look again."
            : FilterList != null ? "None of this list's mods are downloaded yet." : "No mods match.";

        long total = _allInstalled.Sum(m => m.SizeBytes);
        var unused = _allInstalled.Where(m => m.IsUnused).ToList();
        int updates = _allInstalled.Count(m => m.IsUpdate);
        InstalledSummary = $"{_allInstalled.Count} mods  ·  {Format.Size(total)}" +
                           (updates > 0 ? $"  ·  {updates} need an update" : "") +
                           (unused.Count > 0 ? $"  ·  {unused.Count} not used by anything" + SizeNote(unused) : "");
        var stale = _allInstalled.Where(m => m.IsStale).ToList();
        StaleNotice = stale.Count == 0 || InstalledFilterIndex == StaleFilter ? ""
            : $"{stale.Count} mod{(stale.Count == 1 ? "" : "s")} you haven't played with in {ModUsage.StaleDays}+ days" +
              (stale.Sum(m => m.SizeBytes) is > 0 and var bytes ? $" take{(stale.Count == 1 ? "s" : "")} up {Format.Size(bytes)}." : ".");
        OnPropertyChanged(nameof(RemoveSetText));
    }

    /// <summary>" (1.2 GB)", or "" when the sizes aren't known.</summary>
    private static string SizeNote(IEnumerable<ModViewModel> mods) => mods.Sum(m => m.SizeBytes) is > 0 and var b ? $" ({Format.Size(b)})" : "";

    [RelayCommand]
    private async Task UpdateAllModsAsync()
    {
        var outdated = _allInstalled.Where(m => m.IsUpdate).Select(m => new RequiredMod(m.WorkshopId, m.Name)).ToList();
        if (outdated.Count == 0) { ShowResult(true, "All mods are up to date", "Nothing to update."); return; }
        var done = await PrepareModListAsync("Update all mods", outdated, askSteamAnyway: true);
        if (done != null) ShowResult(true, "Mods updated", $"{outdated.Count} mod(s) are now up to date.");
        _installedWorkshop.Clear();
        await LoadInstalledModsAsync();
    }

    [RelayCommand]
    private Task RemoveInstalledModAsync(ModViewModel? mod) =>
        mod == null ? Task.CompletedTask : RemoveModsAsync([mod]);

    [RelayCommand]
    private Task RemoveUnusedModsAsync() => FilterList is { } list ? ForgetModListAsync(list)
        : InstalledFilterIndex == StaleFilter
        ? RemoveModsAsync(_allInstalled.Where(m => m.IsStale).ToList())
        : RemoveModsAsync(_allInstalled.Where(m => m.IsUnused).ToList());

    /// <summary>
    /// "Remove this server's mods": the selected server's downloaded mods that nothing else
    /// uses (no other MM server, favorite, recent server or editor preset). Shared ones stay.
    /// </summary>
    [RelayCommand]
    private async Task RemoveServerModsAsync()
    {
        if (SelectedServer is not { } server || IsSyncing) return;
        DetectSteam();
        if (_steam == null) { ShowResult(false, "Steam wasn't found", "Check the Steam folder in Settings."); return; }
        var installed = server.Mods.Where(m => m.WorkshopId != 0 && _steam.Mods.ContainsKey(m.WorkshopId))
                                   .GroupBy(m => m.WorkshopId).Select(g => g.First()).ToList();
        if (installed.Count == 0) { ShowResult(true, "Nothing to remove", $"None of {server.Name}'s mods are downloaded."); return; }
        var usedElsewhere = UsedByMap(except: server);
        var onlyHere = installed.Where(m => !usedElsewhere.ContainsKey(m.WorkshopId)).ToList();
        int kept = installed.Count - onlyHere.Count;
        if (onlyHere.Count == 0)
        {
            ShowResult(true, "Nothing to remove", installed.Count == 1
                ? $"{server.Name}'s one downloaded mod is also used by other servers or presets you have, so it's kept."
                : $"All {installed.Count} of {server.Name}'s downloaded mods are also used by other servers or presets you have, so they're kept.");
            return;
        }
        var toRemove = onlyHere.Select(m =>
        {
            var vm = new ModViewModel(new DayZServerMod(m.WorkshopId, m.Name, 0)) { Name = m.Name };
            var size = _steam.Mods[m.WorkshopId].SizeBytes;
            vm.SizeBytes = m.SizeBytes > 0 ? m.SizeBytes : size;
            return vm;
        }).ToList();
        await RemoveModsAsync(toRemove,
            title: $"Remove {server.Name}'s mods?",
            note: kept > 0 ? $"{kept} other mod{(kept == 1 ? "" : "s")} it uses {(kept == 1 ? "is" : "are")} kept, because other servers or presets you have use {(kept == 1 ? "it" : "them")} too." : "");
    }

    /// <summary>Unsubscribes (so Steam doesn't download them again) and deletes the files.</summary>
    private async Task RemoveModsAsync(List<ModViewModel> mods, string? title = null, string note = "")
    {
        if (mods.Count == 0 || IsSyncing) return;
        DetectSteam();
        if (_steam == null) return;
        if (GameLauncher.DayZIsRunning()) { ShowResult(false, "DayZ is running", "Close DayZ before removing mods."); return; }
        var what = mods.Count == 1 ? mods[0].Name : $"{mods.Count} mods";
        var used = mods.Where(m => !m.IsUnused).ToList();
        if (AskConfirm != null && !await AskConfirm(title ?? $"Remove {what}?",
                $"This unsubscribes from {what} in Steam and deletes the files ({Format.Size(mods.Sum(m => m.SizeBytes))}). " +
                "Steam briefly shows you as playing DayZ while it unsubscribes." +
                (used.Count > 0 ? $"\n\n{used.Count} of them are used by a server or preset; they'll be downloaded again when you join or start it." : "") +
                (note.Length > 0 ? "\n\n" + note : ""),
                "Remove"))
            return;

        ClearPanel();
        _syncCts = new CancellationTokenSource();
        IsSyncing = true;
        SyncTitle = $"Removing {what}";
        SyncDetail = "";
        SyncIndeterminate = true;
        var progress = new Progress<SyncProgress>(p =>
        {
            if (!IsSyncing) return;
            SyncTitle = p.Title; SyncDetail = p.Detail;
            SyncIndeterminate = p.Fraction < 0;
            if (p.Fraction >= 0) SyncFraction = p.Fraction;
        });
        SyncResult result;
        var request = mods.Select(m => new RequiredMod(m.WorkshopId, m.Name)).ToList();
        try
        {
            result = await _downloader.RemoveAsync(request, progress, _syncCts.Token);
        }
        catch (OperationCanceledException) { result = new SyncResult(false, "Cancelled. Nothing more was removed.", request); }
        catch (Exception ex) { result = new SyncResult(false, ex.Message, request); }
        finally { IsSyncing = false; }

        // Delete the folders (moved aside first, so a locked file leaves that mod untouched).
        int deleted = 0;
        var locked = new List<string>();
        foreach (var m in mods.Where(m => result.Failed.All(f => f.Id != m.WorkshopId)))
        {
            try { if (ModHealth.MoveAsideForRepair(_steam, m.WorkshopId, m.Name) != null) deleted++; }
            catch (Exception ex) { locked.Add($"{m.Name} ({ex.Message})"); }
        }
        try { ModHealth.PurgeTrash(_steam); } catch { }
        if (_steam.DayZPath != null) { try { ModHealth.RemoveModLinks(_steam.DayZPath); } catch { } }
        Log.Info($"Removed {deleted} mod folder(s); {result.Message}");
        ModUsage.Current.Forget(mods.Where(m => result.Failed.All(f => f.Id != m.WorkshopId)).Select(m => m.WorkshopId));

        await LoadInstalledModsAsync();
        foreach (var s in Servers) s.ApplyModDetails(_installedWorkshop, _steam);
        if (SelectedServer is { } sel && !Servers.Contains(sel)) { sel.ApplyModDetails(_installedWorkshop, _steam); RaiseJoinState(); }
        if (!result.Success) ShowResult(false, "Some mods weren't removed", result.Message);
        else if (locked.Count > 0) ShowResult(false, $"Removed {deleted}, {locked.Count} in use", "Close anything using these, then try again: " + string.Join("; ", locked.Take(3)));
        else ShowResult(true, $"Removed {what}", $"Unsubscribed and deleted {deleted} mod folder(s).");
    }

    // ------------------------------------------------------------------ workshop

    [RelayCommand]
    private Task SearchWorkshopAsync() { _workshopPage = 1; return RunWorkshopSearchAsync(append: false); }

    [RelayCommand]
    private Task MoreWorkshopResultsAsync() { _workshopPage++; return RunWorkshopSearchAsync(append: true); }

    partial void OnWorkshopSortIndexChanged(int value) => _ = SearchWorkshopAsync();

    private async Task RunWorkshopSearchAsync(bool append)
    {
        if (WorkshopLoading) return;
        WorkshopLoading = true;
        try
        {
            if (_service.LastAgents.BrowserAgents.Count == 0) await _service.QueryAgentsAsync();
            var sort = WorkshopSearchReply.Sorts[Math.Clamp(WorkshopSortIndex, 0, WorkshopSearchReply.Sorts.Count - 1)].Key;
            var text = WorkshopSearch.Trim();
            // A pasted Workshop link or ID goes straight to that item.
            WorkshopSearchReply? reply = null;
            if (WorkshopSearchReply.TryParseItem(text, out var pastedId))
                reply = await LookUpOneAsync(pastedId);
            else
                foreach (var (host, port) in _service.LastAgents.BrowserAgents)
                    if ((reply = await _service.Agents.SearchWorkshopAsync(host, port, text, sort, _workshopPage)) != null) break;

            if (reply == null) { WorkshopStatus = "No MM server agent is offering Workshop search right now."; return; }
            if (!reply.Ok) { WorkshopStatus = reply.Message; return; }
            if (!append) WorkshopResults.Clear();
            DetectSteam();
            foreach (var r in reply.Results)
            {
                if (WorkshopResults.Any(w => w.Id == r.Id)) continue;
                var row = new WorkshopRowViewModel(r);
                UpdateWorkshopState(row);
                WorkshopResults.Add(row);
                _ = row.LoadThumbnailAsync();
            }
            _lastSearch = text;
            WorkshopHasMore = WorkshopResults.Count < reply.Total && reply.Results.Count > 0;
            WorkshopStatus = reply.Total == 0 ? "Nothing found." : $"{WorkshopResults.Count} of {reply.Total:N0}" + (text.Length > 0 ? $" for \"{text}\"" : "");
        }
        finally
        {
            WorkshopLoading = false;
        }
    }

    /// <summary>A pasted Workshop link or ID: Steam's public item lookup (no agent needed).</summary>
    private async Task<WorkshopSearchReply> LookUpOneAsync(ulong id)
    {
        try
        {
            var details = await _service.Workshop.GetDetailsAsync([id]);
            if (details.GetValueOrDefault(id) is not { Exists: true } item)
                return new WorkshopSearchReply { Ok = true, Message = "" };
            return new WorkshopSearchReply
            {
                Ok = true, Total = 1,
                Results =
                [
                    new WorkshopResult
                    {
                        Id = id, Title = PresetFormat.Clean(item.Title, 120) is { Length: > 0 } t ? t : id.ToString(),
                        SizeBytes = item.SizeBytes, Updated = item.Updated,
                        PreviewUrl = WorkshopSearchReply.IsSafePreview(item.PreviewUrl) ? item.PreviewUrl! : "",
                    },
                ],
            };
        }
        catch (Exception ex)
        {
            return new WorkshopSearchReply { Message = "Couldn't look up that Workshop item: " + ex.Message };
        }
    }

    private void UpdateWorkshopState(WorkshopRowViewModel row)
    {
        if (_steam?.Mods.GetValueOrDefault(row.Id) is not { } local) { row.StateText = ""; return; }
        var state = ModStatus.Evaluate(local, new WorkshopItem(row.Id, true, row.Title, row.Item.SizeBytes, row.Item.Updated, null), true, row.Id);
        row.StateText = state == ModState.UpdateAvailable ? "Update" : "Installed";
    }

    [RelayCommand]
    private async Task InstallWorkshopItemAsync(WorkshopRowViewModel? row)
    {
        if (row == null || IsSyncing) return;
        if (GameLauncher.DayZIsRunning()) { ShowResult(false, "DayZ is running", "Close DayZ before downloading mods."); return; }
        // The mod plus the items it says it needs, which Steam doesn't download on its own.
        var list = new List<RequiredMod> { new(row.Id, row.Title) };
        list.AddRange(row.Item.Requires.Select(id => new RequiredMod(id, id.ToString())));
        Log.Info($"Workshop install: {row.Id} {row.Title} (+{row.Item.Requires.Count} required)");
        var done = await PrepareModListAsync($"Workshop: {row.Title}", list, askSteamAnyway: true);
        DetectSteam();
        foreach (var w in WorkshopResults) UpdateWorkshopState(w);
        // Just downloaded on purpose: counts as fresh, so it isn't flagged as "not played" straight away.
        if (done != null) ModUsage.Current.MarkUsed(list.Select(m => m.Id));
        if (done != null)
            ShowResult(true, $"Installed {row.Title}", row.Item.Requires.Count > 0
                ? $"Downloaded with the {row.Item.Requires.Count} mod(s) it needs. Steam keeps them updated."
                : "Downloaded. Steam keeps it updated.");
        _installedWorkshop.Remove(row.Id);
    }
}
