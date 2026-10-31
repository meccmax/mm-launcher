using Avalonia.Controls;
using MMLauncher.App.ViewModels;

namespace MMLauncher.App.Views;

public partial class ReportWindow : Window
{
    public ReportWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not ReportViewModel vm) return;
            vm.Close = Close;
            vm.CopyToClipboard = async text => { if (Clipboard != null) await Clipboard.SetTextAsync(text); };
        };
    }
}
