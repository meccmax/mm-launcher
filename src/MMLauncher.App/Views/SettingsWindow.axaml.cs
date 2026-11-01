using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MMLauncher.App.ViewModels;

namespace MMLauncher.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not SettingsViewModel vm) return;
            vm.Close = _ => Close();
            Closed += (_, _) => vm.RevertPreview();
            vm.PickFolder = async (title, start) =>
            {
                IStorageFolder? startFolder = null;
                if (start != null && Directory.Exists(start))
                    startFolder = await StorageProvider.TryGetFolderFromPathAsync(start);
                var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false,
                    SuggestedStartLocation = startFolder,
                });
                return result.Count > 0 ? result[0].TryGetLocalPath() : null;
            };
        };
    }
}
