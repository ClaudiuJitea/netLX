using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using NetLX.UI.Controls;

namespace NetLX.UI.Converters;

public sealed class WifiColorConverter : IValueConverter
{
    public static readonly WifiColorConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new SolidColorBrush(WifiChart.NetworkColor(value as string ?? ""));
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
