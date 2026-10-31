using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMLauncher.Core.Config;
using MMLauncher.Core.Services;
using MMLauncher.Core.Steam;

namespace MMLauncher.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly LauncherService _service = new();
    private readonly DispatcherTimer _autoRefresh = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(5) };
    private LocalSteam? _steam;
    private DateTimeOffset? _lastRefresh;

    public MainViewModel()
    {
        _autoRefresh.Tick += async (_, _) => { if (!IsBusy && !IsSyncing) await RefreshAsync(); };
        _clock.Tick += (_, _) => UpdateLastRefreshText();
        _clock.Start();
    }

    public ObservableCollection<ServerViewModel> Servers { get; } = [];

    /// <summary>Set by the window so the view model can copy text without knowing about UI.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    [ObservableProperty] private ServerViewModel? _selectedServer;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(RescanCommand))] private bool _isBusy;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _statusMessage = "Starting…";
    [ObservableProperty] private string _lastRefreshText = "";
    [ObservableProperty] private string _steamText = "Looking for Steam…";
    [ObservableProperty] private bool _steamFound;
    [ObservableProperty] private int _agentCount;

    public string AgentText => AgentCount switch
    {
        0 => "",
        1 => "1 MM agent connected",
        _ => $"{AgentCount} MM agents connected",
    };

    partial void OnAgentCountChanged(int value) => OnPropertyChanged(nameof(AgentText));

    public bool HasServers => Servers.Count > 0;
    public bool NoServers => Servers.Count == 0 && !IsScanning;
    public bool HasSelection => SelectedServer != null;
    public string ServerCountText => $"SERVERS  ({Servers.Count(s => s.Online)}/{Servers.Count} online)";
    public string HostsText => string.Join(", ", _service.Settings.Hosts);

    partial void OnSelectedServerChanged(ServerViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        if (!IsSyncing) ClearPanel();
        RaiseJoinState();
    }

    partial void OnSteamFoundChanged(bool value) => RaiseJoinState();
    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(NoServers));

    public async Task InitializeAsync()
    {
        DetectSteam();
        // Old copies left behind by an interrupted repair (e.g. a file was still locked).
        if (_steam != null)
        {
            var steam = _steam;
            _ = Task.Run(() => { try { Core.Launch.ModHealth.PurgeTrash(steam); } catch { } });
        }
        if (_service.ScanIsDue)
        {
            await RescanAsync();
        }
        else
        {
            LoadFromCache();
            await RefreshAsync();
        }
        ConfigureAutoRefresh();
    }

    private void ConfigureAutoRefresh()
    {
        _autoRefresh.Stop();
        if (_service.Settings.AutoRefreshSeconds > 0)
        {
            _autoRefresh.Interval = TimeSpan.FromSeconds(_service.Settings.AutoRefreshSeconds);
            _autoRefresh.Start();
        }
    }

    private void DetectSteam()
    {
        _steam = _service.DetectSteam();
        SteamFound = _steam != null;
        SteamText = _steam switch
        {
            null => "Steam not found. Open Settings to choose your Steam or DayZ folder.",
            { DayZPath: null } => $"Steam: {_steam.SteamPath}  ·  DayZ not found. Open Settings to choose its folder.",
            _ => $"DayZ: {_steam.DayZPath}  ·  {_steam.Mods.Count} workshop mods downloaded",
        };
    }

    private void LoadFromCache()
    {
        var previous = SelectedServer?.Key;
        Servers.Clear();
        foreach (var s in _service.Cache.Servers)
            Servers.Add(new ServerViewModel(s));
        SelectedServer = Servers.FirstOrDefault(s => s.Key == previous) ?? Servers.FirstOrDefault();
        RaiseListChanged();
    }

    private void RaiseListChanged()
    {
        OnPropertyChanged(nameof(HasServers));
        OnPropertyChanged(nameof(NoServers));
        OnPropertyChanged(nameof(ServerCountText));
        OnPropertyChanged(nameof(HostsText));
        RaiseJoinState();
    }

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RescanAsync()
    {
        IsBusy = true;
        IsScanning = true;
        try
        {
            _service.ReloadSettings();
            var progress = new Progress<string>(m => StatusMessage = m);
            var found = await _service.ScanAsync(progress);
            LoadFromCache();
            StatusMessage = found.Count switch
            {
                0 when Servers.Count > 0 => "Scan found nothing (offline?) — kept the previous server list",
                0 => "No servers answered. Check your internet connection or firewall.",
                _ => $"Found {found.Count} server{(found.Count == 1 ? "" : "s")}",
            };
        }
        catch (Exception ex)
        {
            StatusMessage = "Scan failed: " + ex.Message;
        }
        finally
        {
            IsScanning = false;
            IsBusy = false;
        }
        await RefreshAsync();
        ConfigureAutoRefresh();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RefreshAsync()
    {
        if (Servers.Count == 0) return;
        IsBusy = true;
        try
        {
            // Agents first: they can add servers the scan hasn't seen yet.
            var agents = await _service.QueryAgentsAsync();
            if (await _service.MergeAgentServersAsync(agents))
                LoadFromCache();
            AgentCount = agents.AgentCount;

            var snapshot = Servers.ToList();
            var results = await Task.WhenAll(snapshot.Select(s => _service.RefreshAsync(s.Cached)));
            for (int i = 0; i < snapshot.Count; i++)
                snapshot[i].Apply(results[i] with { Agent = agents.Find(results[i].Address, snapshot[i].Cached.QueryPort) });

            DetectSteam();
            var ids = snapshot.SelectMany(s => s.Mods).Select(m => m.WorkshopId).Where(id => id != 0).Distinct().ToList();
            IReadOnlyDictionary<ulong, WorkshopItem> workshop = new Dictionary<ulong, WorkshopItem>();
            string? workshopError = null;
            if (ids.Count > 0)
            {
                try { workshop = await _service.Workshop.GetDetailsAsync(ids); }
                catch (Exception ex) { workshopError = "Steam Workshop lookup failed: " + ex.Message; }
            }
            foreach (var s in snapshot)
                s.ApplyModDetails(workshop, _steam);

            _lastRefresh = DateTimeOffset.Now;
            UpdateLastRefreshText();
            int online = snapshot.Count(s => s.Online);
            StatusMessage = workshopError ?? $"{online} of {snapshot.Count} servers online";
            RaiseListChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateLastRefreshText()
    {
        if (_lastRefresh is not { } t) return;
        var secs = (int)(DateTimeOffset.Now - t).TotalSeconds;
        LastRefreshText = secs < 5 ? "updated just now" : secs < 90 ? $"updated {secs}s ago" : $"updated {secs / 60} min ago";
    }

    [RelayCommand]
    private async Task CopyAddressAsync()
    {
        if (SelectedServer == null || CopyToClipboard == null) return;
        await CopyToClipboard(SelectedServer.ConnectText);
        StatusMessage = $"Copied {SelectedServer.ConnectText}";
    }

    /// <summary>Set by the window: shows the Report a problem window.</summary>
    public Func<ReportViewModel, Task>? OpenReportWindow { get; set; }

    [RelayCommand]
    private async Task ReportProblemAsync()
    {
        if (OpenReportWindow == null) return;
        await OpenReportWindow(new ReportViewModel(_service, Servers.ToList(), SelectedServer));
    }

    /// <summary>Set by the window: shows the Settings window and waits for it to close.</summary>
    public Func<SettingsViewModel, Task>? OpenSettingsWindow { get; set; }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        if (OpenSettingsWindow == null) return;
        var vm = new SettingsViewModel(_service.Settings);
        await OpenSettingsWindow(vm);
        if (!vm.Saved) return;

        _service.ReloadSettings();
        ConfigureAutoRefresh();
        DetectSteam();
        StatusMessage = "Settings saved";
        if (!IsBusy && !IsSyncing) await RefreshAsync();
    }

    [RelayCommand]
    private void OpenSettingsFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(LauncherSettings.DataFolder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't open folder: " + ex.Message;
        }
    }
}
