using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMLauncher.App.Updates;
using MMLauncher.Core.Logging;
using MMLauncher.Core.Updates;

namespace MMLauncher.App.ViewModels;

/// <summary>Launcher self-update: checks GitHub on startup and every few hours, offers one-click update.</summary>
public partial class MainViewModel
{
    private static readonly TimeSpan UpdateCheckEvery = TimeSpan.FromHours(6);
    private readonly UpdateChecker _updates = new(new HttpClient { Timeout = TimeSpan.FromMinutes(5) });
    private readonly DispatcherTimer _updateTimer = new() { Interval = UpdateCheckEvery };
    private ReleaseInfo? _availableRelease;

    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _updateButtonText = "";
    [ObservableProperty] private bool _updating;

    public string VersionText => $"v{SelfUpdater.CurrentVersion}";

    private void StartUpdateChecks()
    {
        SelfUpdater.CleanUp();
        SelfUpdater.SyncInstalledVersion();
        if (Environment.GetCommandLineArgs().Contains(SelfUpdater.UpdatedArgument))
            StatusMessage = $"Updated to {VersionText}";
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(quiet: true);
        _updateTimer.Start();
        _ = CheckForUpdatesAsync(quiet: true);
    }

    /// <summary>Asks GitHub for the latest release. Quiet = don't report "no update" or errors.</summary>
    public async Task<string> CheckForUpdatesAsync(bool quiet)
    {
        if (quiet && !_service.Settings.CheckForUpdates) return "";
        try
        {
            var latest = await _updates.GetLatestAsync();
            if (latest == null) return "No releases found yet.";
            if (!UpdateChecker.IsNewer(latest.Version, SelfUpdater.CurrentVersion))
            {
                UpdateAvailable = false;
                return $"You have the latest version ({VersionText}).";
            }
            _availableRelease = latest;
            UpdateAvailable = true;
            UpdateButtonText = $"Update to v{latest.Version}";
            Log.Info($"Update available: {latest.Tag}");
            return $"Version v{latest.Version} is available.";
        }
        catch (Exception ex)
        {
            if (!quiet) Log.Warn("Update check failed: " + ex.Message);
            return "Couldn't check for updates: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task UpdateNowAsync()
    {
        var release = _availableRelease;
        if (release == null || Updating) return;

        if (IsSyncing)
        {
            StatusMessage = "Finish or cancel the mod download first, then update.";
            return;
        }

        if (!SelfUpdater.CanSelfUpdate)
        {
            // Developer build, or the exe sits somewhere we can't write: send them to the download page.
            OpenUrl(release.PageUrl);
            StatusMessage = "Opened the download page for the new version.";
            return;
        }

        if (AskConfirm != null)
        {
            var notes = string.IsNullOrWhiteSpace(release.Notes) ? "" : "\n\nWhat's new:\n" + Trim(release.Notes, 900);
            if (!await AskConfirm($"Update to v{release.Version}?",
                    $"The launcher downloads the new version from GitHub, checks it, and restarts. You're on {VersionText}.{notes}",
                    "Update and restart"))
                return;
        }

        Updating = true;
        try
        {
            var progress = new Progress<double>(f => StatusMessage = $"Downloading update… {f:P0}");
            await SelfUpdater.ApplyAsync(_updates, release, progress); // exits the app on success
        }
        catch (Exception ex)
        {
            Log.Error("Update failed: " + ex);
            StatusMessage = "Update failed: " + ex.Message;
            if (AskConfirm != null && await AskConfirm("Update didn't work",
                    ex.Message + "\n\nYou can download the new version yourself from the releases page.", "Open releases page"))
                OpenUrl(release.PageUrl);
        }
        finally
        {
            Updating = false;
        }
    }

    [RelayCommand]
    private void OpenReleases() => OpenUrl(_availableRelease?.PageUrl ?? UpdateChecker.ReleasesPage);

    private static void OpenUrl(string url)
    {
        // Release links come from GitHub's API; only ever hand HTTPS GitHub/Steam links to the shell.
        if (!UpdateChecker.IsSafeLink(url)) url = UpdateChecker.ReleasesPage;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
