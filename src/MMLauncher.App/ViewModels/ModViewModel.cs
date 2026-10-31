using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMLauncher.Core.DayZ;
using MMLauncher.Core.Services;
using MMLauncher.Core.Steam;

namespace MMLauncher.App.ViewModels;

/// <summary>One row in a server's "Required mods" list.</summary>
public partial class ModViewModel : ObservableObject
{
    public ModViewModel(DayZServerMod mod)
    {
        WorkshopId = mod.WorkshopId;
        ServerName = mod.Name;
        Name = mod.Name;
    }

    public ulong WorkshopId { get; }

    /// <summary>The name the server reports (the mod's internal name).</summary>
    public string ServerName { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string _updatedText = "";
    [ObservableProperty] private ModState _state = ModState.Unknown;
    [ObservableProperty] private long _sizeBytes;

    /// <summary>Always loaded and can't be moved or removed (the DayZ Editor's own requirements).</summary>
    [ObservableProperty] private bool _locked;
    public bool Editable => !Locked;
    partial void OnLockedChanged(bool value) => OnPropertyChanged(nameof(Editable));

    /// <summary>Problems found by the last file check ("" = none found / not checked).</summary>
    [ObservableProperty] private string _healthProblems = "";

    public string IdText => WorkshopId == 0 ? "local mod (no workshop ID)" : WorkshopId.ToString();
    public string Tooltip => $"Server name: {ServerName}\nWorkshop ID: {WorkshopId}" +
                             (IsDamaged ? $"\nFile check: {HealthProblems}" : "");

    /// <summary>On disk, but the file check found something wrong.</summary>
    public bool IsDamaged => HealthProblems.Length > 0 && State is ModState.Installed or ModState.UpdateAvailable;

    public string StateText => IsDamaged ? "Damaged" : State switch
    {
        ModState.Installed => "Installed",
        ModState.UpdateAvailable => "Update",
        ModState.Missing => "Missing",
        ModState.Unavailable => "Removed",
        ModState.NoWorkshopId => "No ID",
        _ => "Unknown",
    };

    public bool IsInstalled => State == ModState.Installed && !IsDamaged;
    public bool IsUpdate => State == ModState.UpdateAvailable && !IsDamaged;
    public bool IsMissing => State is ModState.Missing or ModState.Unavailable || IsDamaged;

    /// <summary>Repair makes sense for anything that's on disk or should be.</summary>
    public bool CanRepair => WorkshopId != 0 && State is not ModState.Unavailable;

    partial void OnStateChanged(ModState value) => RaiseState();
    partial void OnHealthProblemsChanged(string value) => RaiseState();

    private void RaiseState()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsUpdate));
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(IsDamaged));
        OnPropertyChanged(nameof(CanRepair));
        OnPropertyChanged(nameof(Tooltip));
    }

    public void Apply(WorkshopItem? workshop, InstalledMod? local, bool steamFound)
    {
        if (workshop is { Exists: true })
        {
            if (!string.IsNullOrWhiteSpace(workshop.Title)) Name = workshop.Title;
            SizeBytes = workshop.SizeBytes;
            SizeText = Format.Size(workshop.SizeBytes);
            UpdatedText = workshop.Updated is { } u ? Format.Ago(u) : "";
        }
        State = ModStatus.Evaluate(local, workshop, steamFound, WorkshopId);
    }

    [RelayCommand]
    private void OpenWorkshop()
    {
        try
        {
            Process.Start(new ProcessStartInfo($"https://steamcommunity.com/sharedfiles/filedetails/?id={WorkshopId}")
            { UseShellExecute = true });
        }
        catch { }
    }
}

internal static class Format
{
    public static string Size(long bytes) => bytes switch
    {
        <= 0 => "",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    public static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.Now - when;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} d ago";
        return when.LocalDateTime.ToString("MMM d, yyyy");
    }

    /// <summary>Turns DayZ map folder names into what players call them.</summary>
    public static string MapName(string map) => map.ToLowerInvariant() switch
    {
        "chernarusplus" => "Chernarus",
        "enoch" => "Livonia",
        "sakhal" => "Sakhal",
        "deerisle" => "Deer Isle",
        "nasdara" => "Nasdara",
        "namalsk" => "Namalsk",
        "banov" => "Banov",
        "takistanplus" => "Takistan",
        "" => "",
        _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(map),
    };
}
