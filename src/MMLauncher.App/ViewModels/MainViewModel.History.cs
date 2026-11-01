using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMLauncher.Core.History;

namespace MMLauncher.App.ViewModels;

/// <summary>
/// The PLAYERS tab: how busy an MM server has been over the last day or week, from the player
/// counts its MM agent records every 5 minutes.
/// </summary>
public partial class MainViewModel
{
    private readonly Dictionary<string, (DateTimeOffset At, PlayerHistoryReply Reply)> _historyCache = new();
    private static readonly TimeSpan HistoryFresh = TimeSpan.FromMinutes(5);

    [ObservableProperty] private PlayerHistoryReply? _shownHistory;
    [ObservableProperty] private int _historyHours = 24;
    [ObservableProperty] private bool _historyLoading;
    [ObservableProperty] private string _historyStatus = "";
    [ObservableProperty] private string _historyPeak = "";
    [ObservableProperty] private string _historyAverage = "";
    [ObservableProperty] private string _historyBusiest = "";

    public bool HistoryIsDay => HistoryHours == 24;
    public bool HistoryIsWeek => HistoryHours != 24;
    public bool HasHistoryStatus => HistoryStatus.Length > 0;
    public bool HasHistoryBusiest => HistoryBusiest.Length > 0;
    partial void OnHistoryStatusChanged(string value) => OnPropertyChanged(nameof(HasHistoryStatus));
    partial void OnHistoryBusiestChanged(string value) => OnPropertyChanged(nameof(HasHistoryBusiest));

    partial void OnHistoryHoursChanged(int value)
    {
        OnPropertyChanged(nameof(HistoryIsDay));
        OnPropertyChanged(nameof(HistoryIsWeek));
        UpdateHistoryText();
    }

    [RelayCommand] private void ShowHistoryDay() => HistoryHours = 24;
    [RelayCommand] private void ShowHistoryWeek() => HistoryHours = 24 * 7;

    private async Task LoadHistoryAsync()
    {
        if (SelectedServer is not { GamePort: { } gamePort } server || HistoryLoading) return;
        var key = server.Key;
        if (_historyCache.TryGetValue(key, out var hit) && DateTimeOffset.Now - hit.At < HistoryFresh)
        {
            ShownHistory = hit.Reply;
            UpdateHistoryText();
            return;
        }
        HistoryLoading = true;
        HistoryStatus = ShownHistory == null ? "Loading…" : HistoryStatus;
        try
        {
            IPAddress.TryParse(server.ConnectIp, out var address);
            if (_service.LastAgents.AgentFor(address, server.Cached.QueryPort) is not { } agent)
            {
                HistoryStatus = "The MM agent for this server isn't answering right now.";
                return;
            }
            var reply = await _service.Agents.GetPlayerHistoryAsync(agent.Host, agent.Port, gamePort);
            if (SelectedServer?.Key != key) return; // moved on meanwhile
            if (reply == null)
            {
                HistoryStatus = "No player history for this server yet. The MM agent records it every 5 minutes.";
                ShownHistory = null;
                return;
            }
            _historyCache[key] = (DateTimeOffset.Now, reply);
            ShownHistory = reply;
            UpdateHistoryText();
        }
        finally
        {
            HistoryLoading = false;
        }
    }

    private void UpdateHistoryText()
    {
        if (ShownHistory is not { } h) { HistoryPeak = HistoryAverage = HistoryBusiest = ""; return; }
        var now = DateTimeOffset.Now;
        var summary = HistoryStats.Summarize(h, now.AddHours(-HistoryHours), TimeZoneInfo.Local);
        string range = HistoryHours == 24 ? "last 24 hours" : "last 7 days";
        if (summary.Readings == 0)
        {
            HistoryStatus = $"No readings in the {range} yet.";
            HistoryPeak = HistoryAverage = "";
        }
        else
        {
            HistoryStatus = "";
            var at = summary.PeakAt!.Value.ToLocalTime();
            HistoryPeak = $"Peak {summary.PeakPlayers} ({at.ToString(HistoryHours == 24 ? "h:mm tt" : "ddd h:mm tt", CultureInfo.CurrentCulture)})";
            HistoryAverage = $"Average {summary.Average:0.#} over the {range}";
        }
        HistoryBusiest = summary.BusiestText;
    }
}
