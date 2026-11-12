using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMLauncher.Core.Config;
using MMLauncher.Core.DayZ;
using MMLauncher.Core.Editor;
using MMLauncher.Core.Launch;
using MMLauncher.Core.Logging;
using MMLauncher.Core.Steam;

namespace MMLauncher.App.ViewModels;

/// <summary>A mod the player has downloaded, for the "Add installed mod" picker.</summary>
public sealed record InstalledChoice(ulong Id, string Name)
{
    public string Label => $"{Name}  ({Id})";
}

/// <summary>
/// The DayZ Editor page: start DayZ offline with CF + Dabs Framework + DayZ-Editor and a
/// preset's mods. Presets are the player's own (editable, import/export as the DayZ
/// launcher's HTML) or shared by an MM agent (read-only; copy to edit).
/// </summary>
public partial class MainViewModel
{
    private EditorPresetStore _presetStore = EditorPresetStore.Load();
    private List<EditorPreset> _sharedPresets = [];
    private Dictionary<ulong, WorkshopItem> _editorWorkshop = new();

    [ObservableProperty] private string _page = "servers";
    [ObservableProperty] private EditorPreset? _selectedPreset;
    [ObservableProperty] private string _presetName = "";
    [ObservableProperty] private string _addModText = "";
    [ObservableProperty] private string _installedFilter = "";
    [ObservableProperty] private InstalledChoice? _selectedInstalled;
    [ObservableProperty] private IReadOnlyList<InstalledChoice> _installedChoices = [];
    [ObservableProperty] private string _editorNote = "";

    public ObservableCollection<EditorPreset> Presets { get; } = [];
    public ObservableCollection<ModViewModel> EditorMods { get; } = [];

    public bool IsServersPage => Page == "servers";
    public bool IsEditorPage => Page == "editor";
    public bool HasPreset => SelectedPreset != null;
    public bool PresetEditable => SelectedPreset is { Shared: false };
    public bool PresetShared => SelectedPreset is { Shared: true };
    public string EditorSummary
    {
        get
        {
            if (SelectedPreset == null) return "";
            var installed = EditorMods.Count(m => m.IsInstalled);
            var size = EditorMods.Sum(m => m.SizeBytes);
            return $"{EditorMods.Count} mods  ·  {Format.Size(size)}  ·  {installed} installed" +
                   (EditorMods.Count(m => m.IsUpdate) is > 0 and var u ? $"  ·  {u} need update" : "") +
                   (EditorMods.Count(m => m.IsMissing) is > 0 and var x ? $"  ·  {x} missing" : "");
        }
    }

    /// <summary>Set by the window: pick a preset file to import (null = cancelled).</summary>
    public Func<Task<string?>>? PickPresetFile { get; set; }
    /// <summary>Set by the window: choose where to save an exported preset (suggested name; null = cancelled).</summary>
    public Func<string, Task<string?>>? PickExportFile { get; set; }

    partial void OnPageChanged(string value)
    {
        OnPropertyChanged(nameof(IsServersPage));
        OnPropertyChanged(nameof(IsEditorPage));
        OnPropertyChanged(nameof(IsModsPage));
        if (value == "editor") _ = LoadEditorAsync();
    }

    [RelayCommand] private void ShowServersPage() => Page = "servers";
    [RelayCommand] private void ShowEditorPage() => Page = "editor";

    private async Task LoadEditorAsync()
    {
        DetectSteam();
        await LoadSharedPresetsAsync();
        RebuildPresetList(SelectedPreset);
        RefreshInstalledChoices();
    }

    /// <summary>Shared MM presets from whichever agents offer them.</summary>
    private async Task LoadSharedPresetsAsync()
    {
        var shared = new List<EditorPreset>();
        foreach (var (host, port) in _service.LastAgents.PresetAgents)
            shared.AddRange(await _service.Agents.GetEditorPresetsAsync(host, port));
        _sharedPresets = shared.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    private void RebuildPresetList(EditorPreset? keep)
    {
        Presets.Clear();
        foreach (var p in _sharedPresets) Presets.Add(p);
        foreach (var p in _presetStore.Presets) Presets.Add(p);
        if (Presets.Count == 0)
        {
            // First visit: one starter preset so there's something to launch.
            var starter = new EditorPreset { Name = "My editor preset" };
            _presetStore.Presets.Add(starter);
            SavePresets();
            Presets.Add(starter);
        }
        SelectedPreset = keep != null && Presets.Contains(keep) ? keep
            : Presets.FirstOrDefault(p => keep != null && p.Name == keep.Name && p.Shared == keep.Shared) ?? Presets.First();
    }

    partial void OnSelectedPresetChanged(EditorPreset? value)
    {
        PresetName = value?.Name ?? "";
        RebuildEditorMods();
        foreach (var n in new[] { nameof(HasPreset), nameof(PresetEditable), nameof(PresetShared), nameof(CanStartEditor) }) OnPropertyChanged(n);
        EditorNote = value is { SkippedLocal: > 0 } ? $"{value.SkippedLocal} local mod(s) in the imported preset aren't on the Workshop and were left out." : "";
    }

    partial void OnPresetNameChanged(string value)
    {
        if (SelectedPreset is not { Shared: false } p || p.Name == value || value.Trim().Length == 0) return;
        p.Name = value.Trim();
        SavePresets();
    }

    private void RebuildEditorMods()
    {
        EditorMods.Clear();
        if (SelectedPreset is not { } preset) { OnPropertyChanged(nameof(EditorSummary)); return; }
        preset.Touch();
        var requiredIds = DayZEditor.Required.Select(m => m.Id).ToHashSet();
        foreach (var m in DayZEditor.LoadOrder(preset))
            EditorMods.Add(new ModViewModel(new DayZServerMod(m.Id, m.Name, 0)) { Locked = requiredIds.Contains(m.Id) });
        RefreshEditorModStates(null);
        _ = LoadEditorWorkshopAsync();
    }

    private async Task LoadEditorWorkshopAsync()
    {
        var ids = EditorMods.Select(m => m.WorkshopId).Where(id => id != 0 && !_editorWorkshop.ContainsKey(id)).Distinct().ToList();
        if (ids.Count > 0)
        {
            try
            {
                foreach (var (id, item) in await _service.Workshop.GetDetailsAsync(ids)) _editorWorkshop[id] = item;
            }
            catch (Exception ex) { Log.Warn("Workshop lookup for the editor preset failed: " + ex.Message); }
        }
        RefreshEditorModStates(null);
    }

    /// <summary>Re-reads installed/outdated state for the editor rows (after downloads, etc.).</summary>
    private void RefreshEditorModStates(IReadOnlyDictionary<ulong, WorkshopItem>? fresh)
    {
        if (fresh != null) foreach (var (id, item) in fresh) _editorWorkshop[id] = item;
        foreach (var m in EditorMods)
            m.Apply(_editorWorkshop.GetValueOrDefault(m.WorkshopId), _steam?.Mods.GetValueOrDefault(m.WorkshopId), _steam != null);
        // Keep names in the saved preset in step with the Workshop titles.
        if (SelectedPreset is { Shared: false } p)
        {
            bool changed = false;
            foreach (var pm in p.Mods)
                if (_editorWorkshop.GetValueOrDefault(pm.Id) is { Exists: true, Title: { Length: > 0 } t } && pm.Name != t) { pm.Name = t; changed = true; }
            if (changed) SavePresets();
        }
        OnPropertyChanged(nameof(EditorSummary));
    }

    private void SavePresets()
    {
        try { _presetStore.Save(); }
        catch (Exception ex) { StatusMessage = "Couldn't save editor presets: " + ex.Message; }
    }

    // ------------------------------------------------------------------ preset commands

    [RelayCommand]
    private void NewPreset()
    {
        var p = new EditorPreset { Name = UniqueName("New preset") };
        _presetStore.Presets.Add(p);
        SavePresets();
        RebuildPresetList(p);
    }

    [RelayCommand]
    private void CopyPreset()
    {
        if (SelectedPreset is not { } src) return;
        var p = new EditorPreset
        {
            Name = UniqueName(src.Name + (src.Shared ? " (my copy)" : " (copy)")),
            Mods = src.Mods.Select(m => new PresetMod { Id = m.Id, Name = m.Name }).ToList(),
        };
        _presetStore.Presets.Add(p);
        SavePresets();
        RebuildPresetList(p);
        StatusMessage = $"Made your own copy: {p.Name}";
    }

    [RelayCommand]
    private async Task DeletePresetAsync()
    {
        if (SelectedPreset is not { Shared: false } p) return;
        if (AskConfirm != null && !await AskConfirm($"Delete \"{p.Name}\"?", "This removes the preset from the launcher. The mods themselves stay downloaded.", "Delete"))
            return;
        _presetStore.Presets.Remove(p);
        SavePresets();
        RebuildPresetList(null);
    }

    [RelayCommand]
    private async Task ImportPresetAsync()
    {
        if (PickPresetFile == null || await PickPresetFile() is not { } path) return;
        try
        {
            if (new FileInfo(path).Length > PresetFormat.MaxBytes) { StatusMessage = "That file is too large to be a preset."; return; }
            var preset = PresetFormat.Parse(await File.ReadAllTextAsync(path), Path.GetFileNameWithoutExtension(path));
            if (preset.Mods.Count == 0)
            {
                ShowResult(false, "No mods found", "The file didn't contain any Steam Workshop mods. Export a preset from the DayZ launcher (Mods > Presets > Export), or use a text file with one Workshop ID or link per line.");
                return;
            }
            preset.Name = UniqueName(preset.Name);
            _presetStore.Presets.Add(preset);
            SavePresets();
            RebuildPresetList(preset);
            EditorNote = preset.SkippedLocal > 0 ? $"{preset.SkippedLocal} local mod(s) in that file aren't on the Workshop and were left out." : "";
            StatusMessage = $"Imported {preset.Name} ({preset.Mods.Count} mods)";
            Log.Info($"Imported editor preset {preset.Name}: {preset.Mods.Count} mods from {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            ShowResult(false, "Couldn't import the preset", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ExportPresetAsync()
    {
        if (SelectedPreset is not { } p || PickExportFile == null) return;
        var safe = string.Concat(p.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (await PickExportFile(safe + ".html") is not { } path) return;
        try
        {
            // Export the full load order, so the file works on its own in the DayZ launcher too.
            var full = new EditorPreset { Name = p.Name, Mods = DayZEditor.LoadOrder(p) };
            await File.WriteAllTextAsync(path, PresetFormat.ToHtml(full));
            StatusMessage = $"Exported {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            ShowResult(false, "Couldn't export the preset", ex.Message);
        }
    }

    private string UniqueName(string wanted)
    {
        var name = wanted.Trim().Length > 0 ? wanted.Trim() : "Preset";
        var taken = Presets.Select(p => p.Name).Concat(_presetStore.Presets.Select(p => p.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (int i = 2; ; i++) if (!taken.Contains($"{name} {i}")) return $"{name} {i}";
    }

    // ------------------------------------------------------------------ editing the mod list

    [RelayCommand]
    private void MoveModUp(ModViewModel? row) => MoveMod(row, -1);

    [RelayCommand]
    private void MoveModDown(ModViewModel? row) => MoveMod(row, +1);

    private void MoveMod(ModViewModel? row, int delta)
    {
        if (row == null || row.Locked || SelectedPreset is not { Shared: false } p) return;
        int i = p.Mods.FindIndex(m => m.Id == row.WorkshopId);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= p.Mods.Count) return;
        (p.Mods[i], p.Mods[j]) = (p.Mods[j], p.Mods[i]);
        SavePresets();
        RebuildEditorMods();
    }

    [RelayCommand]
    private void RemoveMod(ModViewModel? row)
    {
        if (row == null || row.Locked || SelectedPreset is not { Shared: false } p) return;
        p.Mods.RemoveAll(m => m.Id == row.WorkshopId);
        SavePresets();
        RebuildEditorMods();
    }

    /// <summary>Adds Workshop IDs or links typed or pasted into the box (several at once is fine).</summary>
    [RelayCommand]
    private void AddModsFromText()
    {
        if (SelectedPreset is not { Shared: false } p) return;
        var parsed = PresetFormat.Parse(AddModText, "");
        int added = AddToPreset(p, parsed.Mods);
        StatusMessage = added > 0 ? $"Added {added} mod(s)" : "No new Workshop IDs or links found in that text.";
        if (added > 0) AddModText = "";
    }

    [RelayCommand]
    private void AddInstalledMod()
    {
        if (SelectedPreset is not { Shared: false } p || SelectedInstalled is not { } choice) return;
        if (AddToPreset(p, [new PresetMod { Id = choice.Id, Name = choice.Name }]) > 0) StatusMessage = $"Added {choice.Name}";
    }

    private int AddToPreset(EditorPreset p, IEnumerable<PresetMod> mods)
    {
        var have = DayZEditor.LoadOrder(p).Select(m => m.Id).ToHashSet();
        int added = 0;
        foreach (var m in mods)
            if (m.Id != 0 && have.Add(m.Id)) { p.Mods.Add(new PresetMod { Id = m.Id, Name = m.Name }); added++; }
        if (added > 0) { SavePresets(); RebuildEditorMods(); }
        return added;
    }

    partial void OnInstalledFilterChanged(string value) => RefreshInstalledChoices();

    private void RefreshInstalledChoices()
    {
        if (_steam == null) { InstalledChoices = []; return; }
        var words = InstalledFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        InstalledChoices = _steam.Mods.Keys
            .Select(id => new InstalledChoice(id, _editorWorkshop.GetValueOrDefault(id)?.Title is { Length: > 0 } t ? t : NameFromDisk(id)))
            .Where(c => words.All(w => c.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || c.Id.ToString().StartsWith(w)))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        SelectedInstalled ??= InstalledChoices.FirstOrDefault();
        // Fill in proper names for the installed list in the background.
        var unknown = _steam.Mods.Keys.Where(id => !_editorWorkshop.ContainsKey(id)).ToList();
        if (unknown.Count > 0 && !_loadingInstalledNames)
        {
            _loadingInstalledNames = true;
            _ = Task.Run(async () =>
            {
                try { return await _service.Workshop.GetDetailsAsync(unknown); }
                catch { return new Dictionary<ulong, WorkshopItem>(); }
            }).ContinueWith(t =>
            {
                foreach (var (id, item) in t.Result) _editorWorkshop[id] = item;
                RefreshInstalledChoices();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    private bool _loadingInstalledNames;

    private string NameFromDisk(ulong id) =>
        _steam?.Mods.GetValueOrDefault(id) is { } m ? Path.GetFileName(m.Folder) : id.ToString();

    // ------------------------------------------------------------------ start

    public bool CanStartEditor => !IsSyncing && SteamFound && SelectedPreset != null;

    [RelayCommand]
    private async Task UpdateEditorModsAsync()
    {
        if (SelectedPreset is not { } p || IsSyncing) return;
        var mods = await PrepareModListAsync($"DayZ Editor: {p.Name}", EditorLoadOrder(p), askSteamAnyway: true);
        if (mods != null) ShowResult(true, "Mods ready", $"All {mods.Count} mods for {p.Name} are downloaded and up to date.");
    }

    [RelayCommand]
    private async Task StartEditorAsync()
    {
        if (SelectedPreset is not { } p || IsSyncing) return;
        Log.Info($"DayZ Editor requested: {p.Name} ({p.Mods.Count} preset mods)");
        if (GameLauncher.DayZIsRunning()) { ShowResult(false, "DayZ is already running", "Close DayZ first, then start the Editor."); return; }

        var mods = await PrepareModListAsync($"DayZ Editor: {p.Name}", EditorLoadOrder(p), askSteamAnyway: false);
        if (mods == null) return;
        try
        {
            var command = GameLauncher.Build(new LaunchRequest(null, 0, mods, null), _service.Settings, _steam!);
            if (!GameLauncher.SteamIsRunning()) throw new LaunchException("Steam isn't running. Start Steam, then try again.");
            if (!await SteamPermissionsOkAsync()) return;
            Log.Info(GameDisplay.Apply(_service.Settings));
            GameLauncher.Start(command);
            ClearPanel();
            IsSyncing = true;
            SyncIndeterminate = true;
            SyncTitle = "Starting DayZ with the Editor…";
            SyncDetail = $"{mods.Count} mods, offline.";
            bool started = GameLauncher.DryRun || await GameLauncher.WaitForDayZAsync(TimeSpan.FromSeconds(45));
            IsSyncing = false;
            if (started)
            {
                TrackUsage(new() { Type = "editor", Count = mods.Count });
                ModUsage.Current.MarkUsed(mods.Select(m => m.Id));
                ShowResult(true, "DayZ is starting with the Editor",
                    "Once you're at the main menu, open the Editor from there. Your work is saved from inside the Editor (File > Save), as .dze files.");
            }
            else
                ShowResult(false, "DayZ didn't start", "The start command was sent but DayZ didn't open within 45 seconds. If a Windows prompt asked to let BattlEye make changes, accept it and try again.");
        }
        catch (Exception ex)
        {
            Log.Error("Editor launch failed: " + ex.Message);
            ShowResult(false, "Couldn't start DayZ", ex.Message);
        }
    }

    private static List<RequiredMod> EditorLoadOrder(EditorPreset p) =>
        DayZEditor.LoadOrder(p).Select(m => new RequiredMod(m.Id, m.Name)).ToList();
}
