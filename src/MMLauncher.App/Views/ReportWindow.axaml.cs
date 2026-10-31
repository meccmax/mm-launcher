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
            if (DataContext is ReportViewModel vm) vm.Close = Close;
        };
    }
}
