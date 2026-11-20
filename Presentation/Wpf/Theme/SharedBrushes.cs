using System.Windows.Media;

namespace WindowScatter;

internal static class SharedBrushes
{
    internal static SolidColorBrush Graphite { get; } = Create("#202522");

    private static SolidColorBrush Create(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
