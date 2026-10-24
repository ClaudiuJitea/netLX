using Avalonia.Controls;
using Avalonia.Interactivity;
using NetLX.UI.ViewModels;

namespace NetLX.UI.Views;

public partial class ReportsView : UserControl
{
    public ReportsView() => InitializeComponent();

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ReportsViewModel vm) return;
        var path = await FilePicker.PickLocalFileAsync(this, "Import netLX report bundle", "Report bundle", "*.zip");
        if (path is not null) await vm.ImportBundleAsync(path);
    }
}
