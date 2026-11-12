using CommunityToolkit.Mvvm.Input;
using MMLauncher.Core.Config;
using MMLauncher.Core.DayZ;
using MMLauncher.Core.Editor;
using MMLauncher.Core.Launch;
using MMLauncher.Core.Logging;
using MMLauncher.Core.Services;

namespace MMLauncher.App.ViewModels;

/// <summary>
/// MODS page tools: repair one downloaded mod, check every downloaded mod's files, and
/// import / export mod lists in the DayZ launcher's preset format.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Workshop ID → problems found by the last "Check files" (kept across reloads of the list).</summary>
    private readonly Dictionary<ulong, string> _damagedInstalled = new();

    // ------------------------------------------------------------------ repair / check files

    [RelayCommand]
    private Task RepairInstalledModAsync(ModViewModel? mod) =>
        mod == null || !mod.CanRepair ? Task.CompletedTask : RepairInstalledAsync([mod], confirm: true);

    /// <summary>Repairs downloaded mods from the MODS page (no server involved).</summary>
    private Task RepairInstalledAsync(IReadOnlyList<ModViewModel> mods, bool confirm)
    {
        var what = mods.Count == 1 ? mods[0].Name : $"{mods.Count} mods";
        var list = mods.Select(m => new RequiredMod(m.WorkshopId, m.Name)).ToList();
        return RepairCoreAsync("the MODS page", mods, confirm,
            prepare: spec => PrepareModListAsync($"Repair {what}", list, askSteamAnyway: true, spec),
            refresh: async () =>
            {
                foreach (var m in mods) _damagedInstalled.Remove(m.WorkshopId);
                _installedWorkshop.Clear();
                await LoadInstalledModsAsync();
                foreach (var s in Servers) s.ApplyModDetails(_installedWorkshop, _steam);
            },
            recheck: ids => CheckInstalledHealth(ids),
            then: null,
            retry: left => RepairInstalledAsync(left, confirm: false));
    }

    /// <summary>"Check files": looks at every downloaded mod's folder, .pbo files and signatures.</summary>
    [RelayCommand]
    private async Task CheckInstalledFilesAsync()
    {
        if (IsSyncing) return;
        DetectSteam();
        if (_steam == null || _allInstalled.Count == 0) return;
        ClearPanel();
        IsSyncing = true;
        SyncTitle = $"Checking {_allInstalled.Count} mods' files…";
        SyncDetail = "Looking for missing or broken files. Nothing is changed.";
        SyncIndeterminate = true;
        List<ModViewModel> damaged;
        try
        {
            var ids = _allInstalled.Select(m => m.WorkshopId).ToList();
            var bad = await Task.Run(() => ScanInstalledHealth(ids));
            damaged = ApplyInstalledHealth(ids, bad);
        }
        finally { IsSyncing = false; }

        Log.Info($"Check files (MODS page): {_allInstalled.Count} checked, {damaged.Count} damaged.");
        if (damaged.Count == 0)
        {
            ShowResult(true, "All mods look good", $"Checked {_allInstalled.Count} downloaded mods: folders, .pbo files and signatures are all in place.");
            return;
        }
        InstalledSearch = "";
        InstalledFilterIndex = DamagedFilter;
        ApplyInstalledFilter();
        ShowResult(false, $"{damaged.Count} mod(s) look damaged", Names(damaged) + ". Hover a red \"Damaged\" badge for details. Repairing downloads them fresh through Steam.");
        SetPanelActions(($"Repair {damaged.Count} mod(s)", () => RepairInstalledAsync(damaged, confirm: false)), null);
    }

    /// <summary>Checks downloaded mods' files (safe to run off the UI thread). ID → problems, for the damaged ones only.</summary>
    private Dictionary<ulong, string> ScanInstalledHealth(IEnumerable<ulong> ids)
    {
        var bad = new Dictionary<ulong, string>();
        var steam = _steam;
        if (steam == null) return bad;
        foreach (var id in ids)
        {
            if (!steam.Mods.TryGetValue(id, out var installed)) continue;
            var report = ModHealth.Check(id, installed);
            if (!report.Ok) bad[id] = report.Summary;
        }
        return bad;
    }

    /// <summary>Records a check's results and marks the MODS page rows; returns the damaged rows. UI thread.</summary>
    private List<ModViewModel> ApplyInstalledHealth(IReadOnlyCollection<ulong> checkedIds, Dictionary<ulong, string> bad)
    {
        foreach (var id in checkedIds)
        {
            if (bad.TryGetValue(id, out var problems)) { _damagedInstalled[id] = problems; Log.Warn($"File check {id}: {problems}"); }
            else _damagedInstalled.Remove(id);
        }
        var damaged = new List<ModViewModel>();
        foreach (var row in _allInstalled.Where(m => checkedIds.Contains(m.WorkshopId)))
        {
            row.HealthProblems = bad.GetValueOrDefault(row.WorkshopId, "");
            if (row.HealthProblems.Length > 0) damaged.Add(row);
        }
        return damaged;
    }

    private List<ModViewModel> CheckInstalledHealth(IReadOnlyCollection<ulong> ids) => ApplyInstalledHealth(ids, ScanInstalledHealth(ids));

    // ------------------------------------------------------------------ mod lists

    /// <summary>Opens a .html/.txt file picker (title shown on it). Set by the window.</summary>
    public Func<string, Task<string?>>? PickModListFile { get; set; }

    /// <summary>Rebuilds the MODS filter after lists change, keeping the current choice where possible.</summary>
    private void RefreshListFilters(string? select = null)
    {
        var current = select ?? (InstalledFilterIndex >= 0 && InstalledFilterIndex < InstalledFilterChoices.Count ? InstalledFilterChoices[InstalledFilterIndex] : null);
        InstalledFilterChoices = [.. BaseFilters, .. ModLists.Current.Lists.Select(l => "List: " + l.Name)];
        var at = current == null ? 0 : InstalledFilterChoices.ToList().IndexOf(current);
        InstalledFilterIndex = at >= 0 ? at : 0;
        OnPropertyChanged(nameof(RemoveSetText));
        OnPropertyChanged(nameof(RemoveSetTip));
    }

    /// <summary>
    /// Imports a DayZ launcher preset (or a text file of Workshop links / IDs), remembers it so
    /// its mods count as used, and offers to download the ones that aren't here yet.
    /// </summary>
    [RelayCommand]
    private async Task ImportModListAsync()
    {
        if (IsSyncing || PickModListFile == null) return;
        var path = await PickModListFile("Import a mod list");
        if (path == null) return;
        ClearPanel();
        string text;
        try
        {
            if (new FileInfo(path).Length > PresetFormat.MaxBytes) { ShowResult(false, "File too big", "A mod list is a small .html or .txt file; this one is over 1 MB."); return; }
            text = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex) { ShowResult(false, "Couldn't read the file", ex.Message); return; }

        var parsed = PresetFormat.ParseList(text, Path.GetFileNameWithoutExtension(path));
        if (parsed.Mods.Count == 0)
        {
            ShowResult(false, "No mods found", "That file has no Workshop mods in it. Use a preset exported from the DayZ launcher or MM Launcher, or a text file of Workshop links or IDs.");
            return;
        }
        var list = ModLists.Current.Add(parsed.Name, parsed.Mods);
        if (list == null) return;
        Log.Info($"Imported mod list \"{list.Name}\": {list.Mods.Count} mods ({parsed.SkippedLocal} local mods skipped).");

        DetectSteam();
        var missing = list.Mods.Where(m => _steam?.Mods.ContainsKey(m.Id) != true).ToList();
        string skipped = parsed.SkippedLocal switch
        {
            0 => "",
            1 => " 1 local mod in the file was skipped (it isn't on the Workshop).",
            var n => $" {n} local mods in the file were skipped (they aren't on the Workshop).",
        };
        await LoadInstalledModsAsync();
        RefreshListFilters("List: " + list.Name);

        if (missing.Count == 0)
        {
            ShowResult(true, $"Imported \"{list.Name}\"", $"All {list.Mods.Count} mods are already downloaded.{skipped}");
            return;
        }
        string size = "";
        try
        {
            var details = await _service.Workshop.GetDetailsAsync(missing.Select(m => m.Id));
            long bytes = details.Values.Sum(d => d.SizeBytes);
            if (bytes > 0) size = $" (about {Format.Size(bytes)})";
        }
        catch (Exception ex) { Log.Warn("Workshop lookup for an imported list failed: " + ex.Message); }

        bool download = AskYesNo == null || await AskYesNo($"Imported \"{list.Name}\"",
            $"{list.Mods.Count} mods. {missing.Count} of them {(missing.Count == 1 ? "isn't" : "aren't")} downloaded yet{size}. Download {(missing.Count == 1 ? "it" : "them")} now through Steam?{skipped}\n\n" +
            "The list is remembered, so \"Remove unused\" keeps its mods. Forget it any time from the filter on this page.",
            "Download", "Later");
        if (!download)
        {
            ShowResult(true, $"Imported \"{list.Name}\"", $"{missing.Count} of its mods {(missing.Count == 1 ? "isn't" : "aren't")} downloaded yet. Import the file again any time to download them.{skipped}");
            return;
        }
        var done = await PrepareModListAsync($"Mod list: {list.Name}", list.Mods.Select(m => new RequiredMod(m.Id, m.Name)).ToList(), askSteamAnyway: true);
        // Just downloaded on purpose: counts as fresh, so it isn't flagged as "not played" straight away.
        if (done != null) ModUsage.Current.MarkUsed(missing.Select(m => m.Id));
        _installedWorkshop.Clear();
        await LoadInstalledModsAsync();
        RefreshListFilters("List: " + list.Name);
        if (done != null) ShowResult(true, $"\"{list.Name}\" is ready", $"All {list.Mods.Count} mods are downloaded and up to date.{skipped}");
    }

    private async Task ForgetModListAsync(ModList list)
    {
        if (AskConfirm != null && !await AskConfirm($"Forget \"{list.Name}\"?",
                "This only forgets the imported list. Its mods stay downloaded; any that nothing else uses will then show as \"not used by anything\", so \"Remove unused\" can clear them.",
                "Forget list"))
            return;
        ModLists.Current.Remove(list.Name);
        Log.Info($"Forgot mod list \"{list.Name}\".");
        RefreshListFilters(BaseFilters[0]);
        await LoadInstalledModsAsync();
        StatusMessage = $"Forgot the list \"{list.Name}\"";
    }

    /// <summary>Exports the mods shown on the MODS page (after search and filter).</summary>
    [RelayCommand]
    private Task ExportShownModsAsync()
    {
        var filterName = InstalledFilterIndex > 0 && InstalledFilterIndex < InstalledFilterChoices.Count ? InstalledFilterChoices[InstalledFilterIndex] : null;
        var name = FilterList?.Name ?? (filterName == null && InstalledSearch.Trim().Length == 0 ? "My DayZ mods"
            : $"My DayZ mods ({filterName ?? "search: " + InstalledSearch.Trim()})");
        // An imported list keeps its own load order; otherwise the order shown.
        var mods = FilterList is { } fl
            ? fl.Mods.Where(x => InstalledMods.Any(m => m.WorkshopId == x.Id)).ToList()
            : InstalledMods.Where(m => m.WorkshopId != 0).Select(m => new PresetMod { Id = m.WorkshopId, Name = m.Name }).ToList();
        return ExportModListAsync(name, mods);
    }

    /// <summary>Exports the selected server's mods, in its load order.</summary>
    [RelayCommand]
    private Task ExportServerModsAsync()
    {
        if (SelectedServer is not { } s) return Task.CompletedTask;
        return ExportModListAsync(s.Name, s.Mods.Where(m => m.WorkshopId != 0).Select(m => new PresetMod { Id = m.WorkshopId, Name = m.Name }).ToList());
    }

    private async Task ExportModListAsync(string name, List<PresetMod> mods)
    {
        if (mods.Count == 0) { ShowResult(false, "Nothing to export", "There are no Workshop mods in this list."); return; }
        if (PickExportFile == null) return;
        var safe = string.Concat(PresetFormat.Clean(name, 60).Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '|' ? '-' : c)).Trim();
        var path = await PickExportFile((safe.Length > 0 ? safe : "DayZ mods") + ".html");
        if (path == null) return;
        try
        {
            await File.WriteAllTextAsync(path, PresetFormat.ToHtml(new EditorPreset { Name = PresetFormat.Clean(name, 80), Mods = mods }));
            Log.Info($"Exported mod list \"{name}\" ({mods.Count} mods).");
            ShowResult(true, $"Exported {mods.Count} mods", $"Saved as {Path.GetFileName(path)}. It opens in MM Launcher (MODS > Import list) and in the DayZ launcher (Mods > Presets > Import).");
        }
        catch (Exception ex) { ShowResult(false, "Couldn't save the file", ex.Message); }
    }
}
