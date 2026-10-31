using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMLauncher.App.Diagnostics;
using MMLauncher.Core.Config;
using MMLauncher.Core.Diagnostics;
using MMLauncher.Core.Logging;
using MMLauncher.Core.Services;

namespace MMLauncher.App.ViewModels;

/// <summary>One line in the "what's included" list.</summary>
public sealed record ReportItem(string Description, string FileName, string SizeText);

/// <summary>
/// "Report a problem": shows exactly what will be collected, then saves it as a zip or sends
/// it to staff through the MM Server Agent. Nothing leaves the PC until the player clicks Send.
/// </summary>
public partial class ReportViewModel : ObservableObject
{
    private readonly LauncherService _service;
    private readonly IReadOnlyList<ServerViewModel> _servers;
    private ReportPlan _plan = new();
    private SystemSummary? _system;

    public ReportViewModel(LauncherService service, IReadOnlyList<ServerViewModel> servers, ServerViewModel? selected)
    {
        _service = service;
        _servers = servers;
        ServerNames = ["(no particular server)", .. servers.Select(s => s.Name)];
        _selectedServerIndex = selected != null ? servers.ToList().IndexOf(selected) + 1 : 0;
        _player = !string.IsNullOrWhiteSpace(service.Settings.ReportName) ? service.Settings.ReportName : service.Settings.PlayerName;
        Rebuild();
        _ = Task.Run(() =>
        {
            var info = SystemInfo.Collect();
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { _system = info; SystemText = $"{info.Windows} · {info.Cpu} · {info.Gpu} · {info.Ram} RAM"; });
        });
    }

    public static string LauncherVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public Action? Close { get; set; }

    public string[] ServerNames { get; }
    public ObservableCollection<ReportItem> Items { get; } = [];

    [ObservableProperty] private int _selectedServerIndex;
    [ObservableProperty] private string _player;
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private bool _includeCrashDump = true;
    [ObservableProperty] private bool _hasCrashDump;
    [ObservableProperty] private string _sessionText = "";
    [ObservableProperty] private string _crashText = "";
    [ObservableProperty] private string _systemText = "Reading PC details…";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private bool _resultOk;
    [ObservableProperty] private string? _savedPath;

    public bool HasResult => ResultText.Length > 0;
    public bool HasSavedPath => SavedPath != null;
    public bool HasCrash => CrashText.Length > 0;

    /// <summary>An agent that accepts reports is reachable.</summary>
    public bool CanSend => !Busy && ReportTarget != null;
    public bool CanSave => !Busy;
    public string SendHint => ReportTarget != null
        ? "Sends the report to the MM server, which posts it to the staff channel."
        : "Sending isn't available right now (no MM server agent accepts reports). Save the zip and post it in Discord instead.";

    private ServerViewModel? Server => SelectedServerIndex > 0 && SelectedServerIndex <= _servers.Count ? _servers[SelectedServerIndex - 1] : null;

    private (string Host, int Port)? ReportTarget
    {
        get
        {
            var s = Server;
            System.Net.IPAddress.TryParse(s?.ConnectIp, out var ip);
            return _service.LastAgents.ReportAgentFor(ip, s?.Cached.QueryPort ?? 0);
        }
    }

    partial void OnBusyChanged(bool value) { OnPropertyChanged(nameof(CanSend)); OnPropertyChanged(nameof(CanSave)); }
    partial void OnResultTextChanged(string value) => OnPropertyChanged(nameof(HasResult));
    partial void OnSavedPathChanged(string? value) => OnPropertyChanged(nameof(HasSavedPath));
    partial void OnCrashTextChanged(string value) => OnPropertyChanged(nameof(HasCrash));
    partial void OnSelectedServerIndexChanged(int value) { OnPropertyChanged(nameof(CanSend)); OnPropertyChanged(nameof(SendHint)); }
    partial void OnIncludeCrashDumpChanged(bool value) => RefreshItems();

    /// <summary>Works out what would be included and fills the list.</summary>
    [RelayCommand]
    private void Rebuild()
    {
        _plan = ReportBuilder.Plan(ReportBuilder.DefaultDayZLogFolder, Log.FilePath);
        HasCrashDump = _plan.HasCrashDump;
        SessionText = _plan.SessionStarted is { } s
            ? $"Latest DayZ session: started {s:ddd MMM d, h:mm tt}"
            : "No DayZ logs found on this PC (has DayZ been started yet?).";
        CrashText = _plan.CrashSummary.Length > 0 ? "Script crash: " + _plan.CrashSummary : "";
        if (_plan.ErrorCodes.Count > 0)
            CrashText += (CrashText.Length > 0 ? "\n" : "") + "Errors shown in game: " + string.Join("; ", _plan.ErrorCodes);
        RefreshItems();
    }

    private void RefreshItems()
    {
        Items.Clear();
        foreach (var f in _plan.Files)
        {
            if (f.IsCrashDump && !IncludeCrashDump) continue;
            Items.Add(new ReportItem(f.Description, Path.GetFileName(f.SourcePath), Size(f.Size)));
        }
        Items.Add(new ReportItem("Server details and its mod list (with what's installed)", "server.txt, mods.txt", ""));
        Items.Add(new ReportItem("PC basics: Windows version, CPU, graphics card + driver, RAM", "report.txt", ""));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        Busy = true;
        ResultText = "";
        try
        {
            var zip = await Task.Run(BuildZip);
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) desktop = LauncherSettings.DataFolder;
            var path = Path.Combine(desktop, $"MM-Report-{DateTime.Now:yyyy-MM-dd_HH-mm}.zip");
            await File.WriteAllBytesAsync(path, zip);
            SavedPath = path;
            ResultOk = true;
            ResultText = $"Saved {Path.GetFileName(path)} to your Desktop ({Size(zip.LongLength)}). Post it in the MM Discord, or send it to staff.";
            Log.Info($"Report saved: {path}");
        }
        catch (Exception ex)
        {
            ResultOk = false;
            ResultText = "Couldn't save the report: " + ex.Message;
            Log.Error("Report save failed: " + ex);
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (ReportTarget is not { } target) return;
        Busy = true;
        ResultText = "Packing and sending…";
        ResultOk = true;
        try
        {
            var zip = await Task.Run(BuildZip);
            var result = await _service.ReportUploads.UploadReportAsync(target.Host, target.Port, zip);
            ResultOk = result.Ok;
            ResultText = result.Ok
                ? $"{result.Message} Mention that reference if staff ask about it."
                : result.Message;
            Log.Info($"Report upload to {target.Host}:{target.Port}: {(result.Ok ? "ok " + result.Reference : "failed: " + result.Message)}");
        }
        catch (Exception ex)
        {
            ResultOk = false;
            ResultText = "Couldn't send the report: " + ex.Message + " Save the zip instead and post it in Discord.";
            Log.Error("Report send failed: " + ex);
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void OpenSavedFolder()
    {
        if (SavedPath == null) return;
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start("explorer.exe", $"/select,\"{SavedPath}\"");
            else Process.Start(new ProcessStartInfo(Path.GetDirectoryName(SavedPath)!) { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private void CloseWindow() => Close?.Invoke();

    private byte[] BuildZip()
    {
        // Remember the name for next time.
        var name = (Player ?? "").Trim();
        if (name != _service.Settings.ReportName)
        {
            var s = LauncherSettings.Load();
            s.ReportName = name;
            s.Save();
            _service.ReloadSettings();
        }

        var system = _system ?? SystemInfo.Collect();
        var server = Server;
        var meta = new ReportMeta
        {
            LauncherVersion = LauncherVersion,
            Created = DateTimeOffset.Now,
            Player = Clip(name, 80),
            Description = Clip((Description ?? "").Trim(), 3000),
            ServerName = server?.Name ?? "",
            ServerAddress = server?.ConnectText ?? "",
            ModCount = server?.Mods.Count ?? 0,
            SessionStarted = _plan.SessionStarted is { } t ? new DateTimeOffset(t) : null,
            CrashSummary = _plan.CrashSummary,
            ErrorCodes = _plan.ErrorCodes,
            Windows = system.Windows,
            Cpu = system.Cpu,
            Gpu = system.Gpu,
            Ram = system.Ram,
        };

        var extra = new Dictionary<string, string>();
        if (server != null)
        {
            var sv = new StringBuilder();
            sv.AppendLine($"Name:     {server.Name}");
            sv.AppendLine($"Address:  {server.ConnectText} (query port {server.Cached.QueryPort})");
            sv.AppendLine($"Status:   {server.StatusText}, {server.PlayersText} players, map {server.MapText}, version {server.Version}");
            sv.AppendLine($"Mod list: {(server.ModsFromAgent ? "from the MM agent (complete)" : "from the server's status reply")}");
            extra["server/server.txt"] = sv.ToString();

            var mods = new StringBuilder();
            mods.AppendLine("Load order | Workshop ID | State on this PC | Size | Name");
            int i = 1;
            foreach (var m in server.Mods)
                mods.AppendLine($"{i++,3} | {m.WorkshopId,-11} | {m.StateText,-9} | {m.SizeText,9} | {m.Name}{(m.IsDamaged ? "  [" + m.HealthProblems + "]" : "")}");
            extra["server/mods.txt"] = mods.ToString();
        }
        return ReportBuilder.Build(_plan, meta, IncludeCrashDump && HasCrashDump, extra);
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    private static string Size(long bytes) => bytes switch
    {
        <= 0 => "",
        < 1024 * 1024 => $"{Math.Max(1, bytes / 1024)} KB",
        _ => $"{bytes / 1024.0 / 1024:0.#} MB",
    };
}
