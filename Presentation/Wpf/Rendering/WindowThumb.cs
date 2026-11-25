using System.Windows.Controls;

namespace WindowScatter;

// The owning ThumbnailManager releases ThumbnailHandle; this view object never owns the source HWND.
public sealed class WindowThumb : WindowPreviewState
{
    private string? foldedTitle;
    private string? foldedApplication;
    public string Title { get; init; } = "";
    internal string? ApplicationName { get; init; }
    internal bool MatchesTitle(string foldedQuery) => foldedQuery.Length == 0 ||
        OverviewTitleSearch.Matches(foldedTitle ??= OverviewTitleSearch.Fold(Title), foldedQuery) ||
        (ApplicationName != null &&
            OverviewTitleSearch.Matches(foldedApplication ??= OverviewTitleSearch.Fold(ApplicationName), foldedQuery));
    internal bool IsFiltered => ClickBorder.Visibility == System.Windows.Visibility.Hidden;
    public IntPtr ThumbnailHandle { get; set; }
    public Border ClickBorder { get; set; } = null!;
    public Border HighlightBorder { get; set; } = null!;
    public TextBlock TitleLabel { get; set; } = null!;
    internal OverviewCloseButton? CloseButton { get; set; }
}
