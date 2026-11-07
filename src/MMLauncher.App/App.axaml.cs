using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MMLauncher.App.ViewModels;
using MMLauncher.App.Views;

namespace MMLauncher.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Themes.AppTheme.Find(Core.Config.LauncherSettings.Load().Theme).Apply();
            // Test runs on Linux (MMLAUNCHER_DRY_RUN=1) have no Windows encryption; a stand-in lets the
            // saved-password screens be tried there. Real Windows installs always use Windows' own.
            if (!OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("MMLAUNCHER_DRY_RUN") == "1")
            {
                Core.Config.SavedPasswords.Protect = b => b.Reverse().ToArray();
                Core.Config.SavedPasswords.Unprotect = b => b.Reverse().ToArray();
            }
            var vm = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            _ = vm.InitializeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
