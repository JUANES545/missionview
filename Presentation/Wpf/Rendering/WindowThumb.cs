using System.Windows.Controls;

namespace WindowScatter;

// The owning ThumbnailManager releases ThumbnailHandle; this view object never owns the source HWND.
public sealed class WindowThumb : WindowPreviewState
{
    private bool sourceClosed;
    private bool outlineSelected, outlineTransitioning, closeOutlineSuppressed;
    private string? foldedTitle;
    private string? foldedApplication;
    public string Title { get; init; } = "";
    internal string? ApplicationName { get; init; }
    internal bool MatchesTitle(string foldedQuery) => !sourceClosed && (foldedQuery.Length == 0 ||
        OverviewTitleSearch.Matches(foldedTitle ??= OverviewTitleSearch.Fold(Title), foldedQuery) ||
        (ApplicationName != null &&
            OverviewTitleSearch.Matches(foldedApplication ??= OverviewTitleSearch.Fold(ApplicationName), foldedQuery)));
    internal bool HideIfSourceClosed(Func<WindowActivationTarget, bool> isClosed)
    {
        if (!isClosed(Identity)) return false;
        sourceClosed = true;
        ClickBorder.IsHitTestVisible = false;
        ClickBorder.Visibility = HighlightBorder.Visibility = TitleLabel.Visibility = System.Windows.Visibility.Hidden;
        HighlightBorder.Opacity = TitleLabel.Opacity = 0;
        if (CloseButton != null)
        {
            CloseButton.Visibility = System.Windows.Visibility.Hidden;
            CloseButton.IsEnabled = false;
        }
        return true;
    }
    internal void SuppressSelectionOutlineForClose()
    {
        closeOutlineSuppressed = true;
        RefreshSelectionOutline();
    }
    internal void SetSelectionOutline(bool selected)
    {
        if (outlineSelected && !selected && !outlineTransitioning) closeOutlineSuppressed = false;
        outlineSelected = selected;
        RefreshSelectionOutline();
    }
    internal void SetOutlineTransition(bool transitioning)
    {
        outlineTransitioning = transitioning;
        RefreshSelectionOutline();
    }
    internal void RefreshSelectionOutline()
    {
        bool visible = !sourceClosed && !outlineTransitioning && !closeOutlineSuppressed && !IsFiltered;
        if (MinimizedButton != null) MinimizedButton.SelectionOutlineVisible = visible;
        else if (HighlightBorder != null)
        {
            HighlightBorder.Visibility = visible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Hidden;
            HighlightBorder.Opacity = visible && outlineSelected ? 1 : 0;
        }
    }
    internal bool IsFiltered => ClickBorder.Visibility == System.Windows.Visibility.Hidden;
    public IntPtr ThumbnailHandle { get; set; }
    public Border ClickBorder { get; set; } = null!;
    public Border HighlightBorder { get; set; } = null!;
    public TextBlock TitleLabel { get; set; } = null!;
    internal OverviewCloseButton? CloseButton { get; set; }
    internal MinimizedWindowButton? MinimizedButton { get; set; }
}
