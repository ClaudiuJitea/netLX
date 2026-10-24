using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace NetLX.UI.Views;

internal static class FilePicker
{
    /// <summary>Shows the platform open-file dialog and returns a local path, or null when cancelled.</summary>
    public static async Task<string?> PickLocalFileAsync(Visual owner, string title, string filterName, params string[] patterns)
    {
        var top = TopLevel.GetTopLevel(owner);
        if (top is null) return null;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = patterns }]
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
