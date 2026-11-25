using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;

namespace WindowScatter;

public partial class MainWindow
{
    private string searchQuery = "";
    private bool composingSearchText;
    private bool IsSearching => coordinator.searchQuery.Length != 0;

    private void ConfigureSearchInput()
    {
        SearchBox.AddHandler(TextCompositionManager.PreviewTextInputStartEvent,
            new TextCompositionEventHandler((_, _) => composingSearchText = true), handledEventsToo: true);
        SearchBox.AddHandler(TextCompositionManager.PreviewTextInputEvent,
            new TextCompositionEventHandler((_, _) => composingSearchText = false), handledEventsToo: true);
        SearchBox.LostKeyboardFocus += (_, _) => composingSearchText = false;
    }

    private void FocusOverviewInput()
    {
        chromeWindow.Activate();
        if (BlocksThumbnailInput) chromeWindow.Focus();
        else SearchBox.Focus();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (scatterInteraction.IsOpen && !coordinator.isClosing) coordinator.UpdateSearch(SearchBox.Text);
    }

    private void UpdateSearch(string query)
    {
        if (query == searchQuery) return;
        searchQuery = query;
        try
        {
            InvalidateNavigation();
            previewKey.Release();
            foreach (var view in views)
            {
                if (!view.previewExpanded && !view.animationManager.IsPreviewAnimating) continue;
                view.ResetPreview(restoreSourceStacking: true);
            }
            RefreshSearchResults();
            RestorePendingCloseStatus();
        }
        catch (InvalidOperationException error)
        {
            AbortOverview(error);
        }
    }

    private void RefreshSearchResults(IntPtr preferredSelection = default)
    {
        string folded = OverviewTitleSearch.Fold(searchQuery);
        foreach (var view in views)
        {
            view.thumbnailManager.ApplyTitleFilter(folded);
            view.minimizedStrip?.ApplyTitleFilter(folded);
            if (view.SearchBox.Text != searchQuery) view.SearchBox.Text = searchQuery;
        }
        var matches = views.SelectMany(view => view.ViewThumbs)
            .Where(thumb => !thumb.IsFiltered).ToArray();
        var selected = matches.FirstOrDefault(thumb => thumb.WindowHandle ==
            (preferredSelection == IntPtr.Zero ? selectedWindowHandle : preferredSelection)) ?? matches.FirstOrDefault();
        var handle = selected?.WindowHandle ?? IntPtr.Zero;
        bool changed = handle != selectedWindowHandle;
        SelectWindow(handle, keyboard: changed, revealMinimized: changed);

        var status = IsSearching && matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var view in views)
        {
            if (view.NoSearchResults.Visibility == status) continue;
            view.NoSearchResults.Visibility = status;
            if (status == Visibility.Visible && view.SearchBox.IsKeyboardFocused &&
                AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
                UIElementAutomationPeer.CreatePeerForElement(view.NoSearchResults)
                    ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
