using System.Windows.Controls;

namespace WindowScatter;

// The owning ThumbnailManager releases ThumbnailHandle; this view object never owns the source HWND.
public sealed class WindowThumb : WindowPreviewState
{
    public IntPtr ThumbnailHandle { get; set; }
    public Border ClickBorder { get; set; } = null!;
    public Border HighlightBorder { get; set; } = null!;
    public TextBlock TitleLabel { get; set; } = null!;
}
