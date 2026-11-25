using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;

namespace WindowScatter;

public partial class MainWindow
{
    private string searchQuery = "";
    private MainWindow? searchInputView;
    private bool composingSearchText;
    private bool searchPreedit;
    private bool IsSearching => coordinator.searchQuery.Length != 0;
    private bool CanUseSearch => scatterInteraction.IsOpen && !coordinator.isClosing &&
        !coordinator.isScatterActive && !coordinator.isTransitioning && !AnyAnimating && coordinator.fileDrag == null;

    private void ConfigureSearchInput()
    {
        void UpdateComposition(object sender, TextCompositionEventArgs e)
        {
            composingSearchText = e.TextComposition.AutoComplete == TextCompositionAutoComplete.Off;
            searchPreedit = composingSearchText && e.TextComposition.CompositionText.Length != 0;
            RefreshSearchPresentation();
        }
        void EndComposition()
        {
            composingSearchText = searchPreedit = false;
            RefreshSearchPresentation();
        }
        SearchBox.AddHandler(TextCompositionManager.PreviewTextInputStartEvent,
            new TextCompositionEventHandler(UpdateComposition), handledEventsToo: true);
        SearchBox.AddHandler(TextCompositionManager.PreviewTextInputUpdateEvent,
            new TextCompositionEventHandler(UpdateComposition), handledEventsToo: true);
        SearchBox.AddHandler(TextCompositionManager.PreviewTextInputEvent,
            new TextCompositionEventHandler((_, _) => EndComposition()), handledEventsToo: true);
        SearchBox.LostKeyboardFocus += (_, _) => EndComposition();
    }

    private void FocusOverviewInput()
    {
        if (CanUseSearch) SetSearchInputView();
        chromeWindow.Activate();
        if (!CanUseSearch) { chromeWindow.Focus(); return; }
        SearchBox.Focus();
    }

    private void SetSearchInputView()
    {
        var previous = coordinator.searchInputView;
        if (previous == this) return;
        coordinator.searchInputView = this;
        previous?.RefreshSearchPresentation();
        RefreshSearchPresentation();
    }

    private void ShowSettledSearchInput()
    {
        if (CanUseSearch && searchInputView == null)
            (activeView ?? this).FocusOverviewInput();
    }

    private void HideSearchForTransition()
    {
        searchInputView = null;
        foreach (var view in views) view.RefreshSearchPresentation();
    }

    private void RefreshSearchPresentation()
    {
        bool ready = coordinator.searchInputView == this && CanUseSearch;
        bool show = ready && (IsSearching || searchPreedit);
        // A ready, empty editor keeps native keyboard/IME focus, without a mouse target.
        SearchPanel.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        SearchPanel.Opacity = show ? 1 : 0;
        SearchPanel.IsHitTestVisible = show;
    }

    private void LayoutSearchPanel(double width, double height)
    {
        SearchPanel.Width = Math.Min(520, Math.Max(0, width - 24));
        double left = (width - SearchPanel.Width) / 2;
        Canvas.SetLeft(SearchPanel, left);
        bool besideStatus = left >= 232;
        Canvas.SetTop(SearchPanel, besideStatus ? Math.Clamp(height * .05, 10, 36) : 64);
        MoveStatus.Width = Math.Max(0, besideStatus ? left - 84 : width - 88);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (CanUseSearch) coordinator.UpdateSearch(SearchBox.Text);
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

        string status = !IsSearching ? "" : matches.Length switch
        {
            0 => "No matching windows",
            1 => "1 matching window",
            _ => $"{matches.Length} matching windows"
        };
        foreach (var view in views)
        {
            view.RefreshSearchPresentation();
            view.SearchStatusText.Visibility = status.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (view.SearchStatusText.Text == status) continue;
            view.SearchStatusText.Text = status;
            if (status.Length != 0 && view.SearchPanel.IsHitTestVisible && view.SearchBox.IsKeyboardFocused &&
                AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
                UIElementAutomationPeer.CreatePeerForElement(view.SearchStatusText)
                    ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}

public sealed class OverviewSearchBox : TextBox
{
    protected override AutomationPeer OnCreateAutomationPeer() => new SearchPeer(this);

    private sealed class SearchPeer(OverviewSearchBox owner) : TextBoxAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => owner.IsHitTestVisible && base.IsControlElementCore();
        protected override bool IsContentElementCore() => owner.IsHitTestVisible && base.IsContentElementCore();
        protected override bool IsOffscreenCore() => !owner.IsHitTestVisible || base.IsOffscreenCore();
    }
}
