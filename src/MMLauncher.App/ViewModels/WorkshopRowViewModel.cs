using System.Diagnostics;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMLauncher.Core.Workshop;

namespace MMLauncher.App.ViewModels;

/// <summary>One Workshop search result on the Mods page.</summary>
public partial class WorkshopRowViewModel(WorkshopResult item) : ObservableObject
{
    private static readonly HttpClient Images = new() { Timeout = TimeSpan.FromSeconds(15) };
    private const int MaxImageBytes = 3 * 1024 * 1024;

    public WorkshopResult Item { get; } = item;
    public ulong Id => Item.Id;
    public string Title => Item.Title;
    public string Description => Item.Description;
    public string SizeText => Item.SizeBytes > 0 ? Format.Size(Item.SizeBytes) : "";
    public string UpdatedText => Item.Updated is { } u ? "updated " + Format.Ago(u) : "";
    public string SubsText => Item.Subscriptions switch
    {
        >= 1_000_000 => $"{Item.Subscriptions / 1_000_000.0:0.#}M subscribers",
        >= 1_000 => $"{Item.Subscriptions / 1_000.0:0.#}k subscribers",
        > 0 => $"{Item.Subscriptions} subscribers",
        _ => "",
    };
    /// <summary>"1.2 GB  ·  290k subscribers  ·  updated 3 d ago", skipping anything unknown.</summary>
    public string MetaText => string.Join("  ·  ", new[] { SizeText, SubsText, UpdatedText }.Where(x => x.Length > 0));
    public string RequiresText => Item.Requires.Count == 0 ? "" : $"needs {Item.Requires.Count} other mod{(Item.Requires.Count == 1 ? "" : "s")} (installed with it)";
    public bool HasRequires => Item.Requires.Count > 0;

    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private string _stateText = "";   // "Installed" / "Update" / ""
    public bool IsInstalled => StateText == "Installed";
    public bool IsUpdate => StateText == "Update";
    public bool NotInstalled => StateText.Length == 0;
    partial void OnStateTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsUpdate));
        OnPropertyChanged(nameof(NotInstalled));
    }

    /// <summary>Loads the preview picture (Steam's image servers only, size-capped).</summary>
    public async Task LoadThumbnailAsync()
    {
        if (Thumbnail != null || !WorkshopSearchReply.IsSafePreview(Item.PreviewUrl)) return;
        try
        {
            using var res = await Images.GetAsync(Item.PreviewUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!res.IsSuccessStatusCode || res.Content.Headers.ContentLength > MaxImageBytes) return;
            await using var stream = await res.Content.ReadAsStreamAsync();
            using var ms = new MemoryStream();
            var buffer = new byte[65536];
            int n;
            while ((n = await stream.ReadAsync(buffer)) > 0)
            {
                ms.Write(buffer, 0, n);
                if (ms.Length > MaxImageBytes) return;
            }
            ms.Position = 0;
            Thumbnail = Bitmap.DecodeToWidth(ms, 192);
        }
        catch { /* no picture; the row still works */ }
    }

    [RelayCommand]
    private void OpenInSteam()
    {
        // Opens the item in the Steam client (Subscribe there; Steam downloads it in the background).
        try { Process.Start(new ProcessStartInfo($"steam://url/CommunityFilePage/{Id}") { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private void OpenWorkshopPage()
    {
        try { Process.Start(new ProcessStartInfo($"https://steamcommunity.com/sharedfiles/filedetails/?id={Id}") { UseShellExecute = true }); } catch { }
    }
}
