using Avalonia.Controls;
using MMLauncher.App.ViewModels;

namespace MMLauncher.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.CopyToClipboard = async text =>
            {
                if (Clipboard != null) await Clipboard.SetTextAsync(text);
            };
            vm.AskPassword = async serverName =>
                await new PasswordDialog(serverName).ShowDialog<string?>(this);
            vm.AskConfirm = async (title, message, ok) =>
                await new ConfirmDialog(title, message, ok).ShowDialog<bool>(this);
            vm.OpenReportWindow = async reportVm =>
                await new ReportWindow { DataContext = reportVm }.ShowDialog(this);
            vm.OpenSettingsWindow = async settingsVm =>
            {
                await new SettingsWindow { DataContext = settingsVm }.ShowDialog(this);
            };
        };
    }
}
