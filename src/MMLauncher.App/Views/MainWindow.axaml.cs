using Avalonia.Platform.Storage;
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
            vm.BringToFront = () =>
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
            };
            vm.PickPresetFile = async () =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Import a DayZ Editor preset",
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new("Mod presets (*.html, *.txt)") { Patterns = ["*.html", "*.htm", "*.txt"] },
                        new("All files") { Patterns = ["*"] },
                    ],
                });
                return files.Count > 0 ? files[0].TryGetLocalPath() : null;
            };
            vm.PickExportFile = async suggested =>
            {
                var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                {
                    Title = "Export preset",
                    SuggestedFileName = suggested,
                    DefaultExtension = "html",
                    FileTypeChoices = [new("DayZ launcher preset (*.html)") { Patterns = ["*.html"] }],
                });
                return file?.TryGetLocalPath();
            };
            vm.AskPassword = async serverName =>
                await new PasswordDialog(serverName).ShowDialog<string?>(this);
            vm.AskConfirm = async (title, message, ok) =>
                await new ConfirmDialog(title, message, ok).ShowDialog<bool>(this);
            vm.AskYesNo = async (title, message, yes, no) =>
                await new ConfirmDialog(title, message, yes, no).ShowDialog<bool>(this);
            vm.OpenReportWindow = async reportVm =>
                await new ReportWindow { DataContext = reportVm }.ShowDialog(this);
            vm.OpenSettingsWindow = async settingsVm =>
            {
                await new SettingsWindow { DataContext = settingsVm }.ShowDialog(this);
            };
        };
    }
}
