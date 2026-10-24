using Avalonia.Controls;
using Avalonia.Interactivity;
using NetLX.UI.ViewModels;

namespace NetLX.UI.Views;

public partial class ProfilesView : UserControl
{
    public ProfilesView() => InitializeComponent();

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProfilesViewModel vm) return;
        var path = await FilePicker.PickLocalFileAsync(this, "Import netLX profile", "Profile JSON", "*.json");
        if (path is not null) await vm.ImportAsync(path);
    }
}
