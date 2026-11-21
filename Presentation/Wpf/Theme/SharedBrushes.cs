using System.Windows.Media;

namespace WindowScatter;

internal static class SharedBrushes
{
    internal static SolidColorBrush Graphite { get; } = Create("#202522");
    internal static SolidColorBrush Surface { get; } = Create("#28322C");
    internal static SolidColorBrush Raised { get; } = Create("#303B34");
    internal static SolidColorBrush Mint { get; } = Create("#7DDB9B");
    internal static SolidColorBrush Text { get; } = Create("#F3F5F4");
    internal static SolidColorBrush Secondary { get; } = Create("#BCC5BE");
    internal static SolidColorBrush Dock { get; } = Create("#F0202522");

    private static SolidColorBrush Create(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
