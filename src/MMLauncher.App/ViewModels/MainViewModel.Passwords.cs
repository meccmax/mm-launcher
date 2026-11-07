using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MMLauncher.App.Views;
using MMLauncher.Core.Config;
using MMLauncher.Core.Logging;

namespace MMLauncher.App.ViewModels;

/// <summary>
/// Saved server passwords: "Remember this password" when joining saves it (encrypted for this
/// Windows user, see <see cref="SavedPasswords"/>), so JOIN SERVER uses it without asking. The
/// server panel shows a line to show, change or forget it.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Asks for a password: (server name, password to fill in, main button text, heading or null).</summary>
    public Func<string, string?, string, string?, Task<PasswordAnswer?>>? AskPassword { get; set; }

    [ObservableProperty] private bool _savedPasswordShown;

    private static SavedPasswords Passwords => SavedPasswords.Current;

    /// <summary>The saved password for a server (looked up by its address and by the IP it connects to).</summary>
    private static string? SavedPasswordFor(ServerViewModel s) =>
        Passwords.Get(s.Cached.Host, s.Cached.QueryPort) ??
        (s.ConnectIp.Length > 0 ? Passwords.Get(s.ConnectIp, s.Cached.QueryPort) : null);

    public bool HasSavedPassword => SelectedServer is { } s && SavedPasswordFor(s) != null;
    public bool ShowPasswordLine => SavedPasswords.CanSave && SelectedServer is { } s && (s.Passworded || HasSavedPassword);
    public string PasswordLineText => !HasSavedPassword ? "This server needs a password."
        : SavedPasswordShown ? "Saved password:" : "Password saved on this PC.";
    public string SavedPasswordValue => SavedPasswordShown && SelectedServer is { } s ? SavedPasswordFor(s) ?? "" : "";
    public string ShowPasswordText => SavedPasswordShown ? "Hide" : "Show";
    public string ChangePasswordText => HasSavedPassword ? "Change" : "Save password…";

    partial void OnSavedPasswordShownChanged(bool value) => RaisePasswordState();

    private void RaisePasswordState()
    {
        foreach (var n in new[] { nameof(HasSavedPassword), nameof(ShowPasswordLine), nameof(PasswordLineText), nameof(SavedPasswordValue),
                                  nameof(ShowPasswordText), nameof(ChangePasswordText) })
            OnPropertyChanged(n);
    }

    /// <summary>
    /// The password to join with: the saved one if there is one, otherwise asks (and saves it when
    /// "Remember" is ticked). Null if the player cancelled.
    /// </summary>
    private async Task<string?> GetJoinPasswordAsync(ServerViewModel server)
    {
        if (SavedPasswordFor(server) is { } saved) return saved;
        if (AskPassword == null) return null;
        var answer = await AskPassword(server.Name, null, "Join", null);
        if (answer == null) return null;
        if (answer.Remember) SavePassword(server, answer.Password);
        return answer.Password;
    }

    private void SavePassword(ServerViewModel server, string password)
    {
        if (password.Length == 0) return;
        if (Passwords.Set(server.Cached.Host, server.Cached.QueryPort, password))
            Log.Info($"Saved the password for {server.Name}.");
        else
            Log.Warn($"Couldn't save the password for {server.Name}.");
        RaisePasswordState();
    }

    [RelayCommand]
    private void ToggleSavedPassword() => SavedPasswordShown = !SavedPasswordShown;

    /// <summary>Save a password for the selected server, or change the saved one.</summary>
    [RelayCommand]
    private async Task ChangeSavedPasswordAsync()
    {
        if (SelectedServer is not { } s || AskPassword == null) return;
        var saved = SavedPasswordFor(s);
        var answer = await AskPassword(s.Name, saved, "Save", saved == null ? "Save the server password" : "Change the saved password");
        if (answer == null) return;
        if (answer.Remember && answer.Password.Length > 0)
        {
            ForgetPassword(s, quiet: true); // drop any copy saved under the server's other address
            SavePassword(s, answer.Password);
            StatusMessage = $"Password saved for {s.Name}";
        }
        else if (saved != null)
        {
            ForgetPassword(s);
        }
    }

    [RelayCommand]
    private void ForgetSavedPassword()
    {
        if (SelectedServer is { } s) ForgetPassword(s);
    }

    private void ForgetPassword(ServerViewModel s, bool quiet = false)
    {
        bool removed = Passwords.Remove(s.Cached.Host, s.Cached.QueryPort) | Passwords.Remove(s.ConnectIp, s.Cached.QueryPort);
        SavedPasswordShown = false;
        RaisePasswordState();
        if (removed && !quiet)
        {
            Log.Info($"Forgot the saved password for {s.Name}.");
            StatusMessage = $"Forgot the password for {s.Name}";
        }
    }
}
