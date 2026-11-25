using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WindowScatter;
using Xunit;

public class MainWindowNavigationTests
{
    [Fact]
    public void SearchMatchesApplicationNameOrTitleAndPreservesEnye()
    {
        var known = new WindowThumb { Title = "Draft 17", ApplicationName = "Diseño Café" };
        var unknown = new WindowThumb { Title = "Draft 18" };
        Assert.True(known.MatchesTitle(OverviewTitleSearch.Fold("diseño cafe")));
        Assert.False(known.MatchesTitle(OverviewTitleSearch.Fold("diseno cafe")));
        Assert.True(known.MatchesTitle(OverviewTitleSearch.Fold("draft 17")));
        Assert.False(unknown.MatchesTitle(OverviewTitleSearch.Fold("cafe")));
        Assert.True(unknown.MatchesTitle(OverviewTitleSearch.Fold("draft")));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void FileDragIntakeStartsOnEveryOverviewView(int displays, bool armed) => RunSta(() =>
    {
        using var app = new MainWindow();
        using var other = displays == 2 ? app.AddView(-1600, 0, 1600, 900, 1.25) : null;
        app.BeginFixtureFileDrag(displays);
        Assert.Equal(armed, app.FileDragArmed);
        Assert.Equal(armed, app.FileDragRoutesDropEvents);
        if (other != null) Assert.True(other.FileDragRoutesDropEvents);
    });

    [Fact]
    public void FileDragCrossesDisplaysAndRejectsAnOldShortcutOrLateLeave() => RunSta(() =>
    {
        using var app = new MainWindow();
        using var other = app.AddView(-1600, 0, 1600, 900, 1.25);
        var first = app.AddThumb(10, 0, 0, 100, 100).Identity;
        var second = other.AddThumb(20, 0, 0, 100, 100).Identity;
        app.BeginFixtureFileDrag(2);
        app.HoverFixtureFileDrag(first);
        var original = Assert.IsType<OverviewFileDrag.Hover>(app.FixtureDragHover);
        other.HoverFixtureFileDrag(second);
        var current = Assert.IsType<OverviewFileDrag.Hover>(app.FixtureDragHover);
        Assert.Equal((true, DragDropEffects.None), app.EndFixtureFileDrag(DragDrop.PreviewDragLeaveEvent));
        Assert.Equal(second.Handle, app.Selected);
        Assert.Null(other.ConfirmFixtureDrag(original, true, second));
        Assert.Null(app.ConfirmFixtureDrag(current, true, second));
        Assert.Null(other.ConfirmFixtureDrag(current, false, second));
        Assert.Equal(second, other.ConfirmFixtureDrag(current, true, second));
        other.EndFixtureFileDrag(DragDrop.PreviewDragLeaveEvent);
        other.HoverFixtureFileDrag(second);
        Assert.Null(other.ConfirmFixtureDrag(current, true, second));
        Assert.Equal(0, app.ActivationRequests);
        other.EndFixtureFileDrag(DragDrop.PreviewDropEvent);
        app.HoverFixtureFileDrag(first);
        Assert.Null(app.FixtureDragHover);
        app.ResetFixtureFileDrag();
        Assert.False(other.FileDragRoutesDropEvents);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileDragCanReturnAfterLeavingButNotAfterDropping(bool dropped) => RunSta(() =>
    {
        using var app = new MainWindow();
        var target = app.AddThumb(10, 0, 0, 100, 100).Identity;
        app.BeginFixtureFileDrag(1);
        app.HoverFixtureFileDrag(target);
        Assert.Equal(target.Handle, app.Selected);

        var result = app.EndFixtureFileDrag(dropped ? DragDrop.PreviewDropEvent : DragDrop.PreviewDragLeaveEvent);

        Assert.Equal((true, DragDropEffects.None), result);
        Assert.Equal(IntPtr.Zero, app.Selected);
        app.HoverFixtureFileDrag(target);
        Assert.Equal(dropped ? IntPtr.Zero : target.Handle, app.Selected);
        Assert.Equal(0, app.ActivationRequests);
    });

    [Fact]
    public void FileDragDoesNotExposeOrdinaryThumbnailInputAfterTheSourceReleases() => RunSta(() =>
    {
        using var app = new MainWindow();
        Assert.False(app.BlocksFixtureThumbnailInput);
        app.BeginFixtureFileDrag(1);
        var target = app.AddThumb(10, 0, 0, 100, 100).Identity;
        app.HoverFixtureFileDrag(target);
        app.EndFixtureFileDrag(DragDrop.PreviewDropEvent);
        Assert.True(app.BlocksFixtureThumbnailInput);
        app.ResetFixtureFileDrag();
        Assert.False(app.BlocksFixtureThumbnailInput);
    });

    [Fact]
    public void SearchAppearsOnFirstEditOnlyAfterOpeningAndHidesBeforeReturnOnEveryView()
        => RunSta(() =>
        {
            using var app = new MainWindow();
            var other = app.AddView(-1600, 0, 1600, 900, 1.25);
            app.BeginSearchOpening();
            app.UseSearchInput();
            Assert.Equal(Visibility.Collapsed, app.SearchVisibility);
            Assert.Equal(Visibility.Collapsed, other.SearchVisibility);

            other.StartFixtureOpeningAnimation();
            app.EndSearchOpening();
            app.UseSearchInput();
            Assert.Equal(Visibility.Collapsed, app.SearchVisibility);
            other.SettleFixtureOpeningAnimation();

            app.UseSearchInput();
            Assert.Equal(Visibility.Visible, app.SearchVisibility);
            Assert.False(app.SearchShown);
            app.TypeSearch("q");
            Assert.Equal("q", app.Query);
            Assert.True(app.SearchShown);
            Assert.Equal("No matching windows", app.SearchFeedback);
            Assert.Equal(Visibility.Collapsed, other.SearchVisibility);

            other.UseSearchInput();
            Assert.Equal(Visibility.Collapsed, app.SearchVisibility);
            Assert.True(other.SearchShown);
            Assert.Equal("q", other.Query);
            other.TypeSearch("");
            Assert.False(other.SearchShown);
            Assert.Equal("", other.SearchFeedback);

            other.TypeSearch("a");
            app.BeginSearchExit();
            Assert.Equal(Visibility.Collapsed, app.SearchVisibility);
            Assert.Equal(Visibility.Collapsed, other.SearchVisibility);
            other.UseSearchInput();
            Assert.False(other.SearchShown);
        });

    [Theory]
    [InlineData(1920, 1080, 1, 0)]
    [InlineData(3440, 1440, 1.25, -3440)]
    [InlineData(5120, 1440, 2, 1920)]
    [InlineData(1024, 768, 1.5, 0)]
    [InlineData(720, 1280, 2, -720)]
    public void SearchIsCenteredAndCappedInLocalDips(int width, int height, double dpi, int x)
        => RunSta(() =>
        {
            using var app = new MainWindow(left: x, width: width, height: height, dpi: dpi);
            var (left, fieldWidth) = app.LayoutFixtureSearch();
            double dipWidth = width / dpi;
            Assert.Equal(dipWidth / 2, left + fieldWidth / 2, 8);
            Assert.Equal(Math.Min(520, dipWidth - 24), fieldWidth, 8);
            Assert.True(left >= 12);
        });

    [Fact]
    public void SyntheticSearchSkipsHiddenShelfSlotsWithoutRepackingAndRestoresTabOrder()
        => RunSta(() =>
        {
            using var app = new MainWindow();
            app.AddStrip(12, 1600, 1000);
            var strip = app.Strip!;
            var original = strip.NavigationTargets(visibleOnly: false).ToArray();
            app.TypeSearch("window 1");

            var matches = strip.NavigationTargets(visibleOnly: false);
            Assert.Equal(original.Take(10).ToArray(), matches.ToArray());
            Assert.Equal("10 matching windows", app.SearchFeedback);
            Assert.Equal(new IntPtr(10), app.Selected);
            app.Tab(-1);
            Assert.Equal(new IntPtr(19), app.Selected);
            app.TypeSearch("");
            Assert.Equal(original, strip.NavigationTargets(visibleOnly: false).ToArray());
            app.Tab(1);
            Assert.Equal(new IntPtr(20), app.Selected);
        });

    [Fact]
    public void RebuildWithActiveSearchRegistersOnlyMatchingTargetsAsVisibleAndReselects()
        => RunSta(() =>
        {
            using var app = new MainWindow();
            var original = app.RegisterNormalWindows("Draft to close", "Draft to retain", "Other");
            app.TypeSearch("draft");
            app.Pick(original[0].WindowHandle);
            var remaining = original.Skip(1).Select(thumb => new WindowInfo
            {
                Handle = thumb.WindowHandle, Identity = thumb.Identity, Title = thumb.Title,
                OriginalRect = new PhysicalWindowRect { Left = 100, Top = 100, Right = 700, Bottom = 500 }
            }).ToArray();

            app.Rebuild(false, remaining);

            Assert.Equal("draft", app.Query);
            Assert.Equal(2, app.Thumbs.Count);
            Assert.True(app.Thumbs.Single(thumb => thumb.Title == "Other").IsFiltered);
            Assert.Equal(remaining[0].Handle, app.Selected);
            Assert.Equal(remaining[0].Handle, Assert.Single(Assert.Single(app.Snapshot()).Normal).Identity.Handle);
        });

    [Fact]
    public void TitleSearchKeepsSlotsAndRemovesHiddenTargetsAcrossViews()
        => RunSta(() =>
        {
            using var app = new MainWindow();
            var other = app.AddView(1600, 0, 1600, 1000, 1);
            string tail = new('x', 90);
            var normal = app.RegisterNormalWindows("Notes", tail + " Café");
            var remote = Assert.Single(other.RegisterNormalWindows("Cafe calendar"));
            app.AddStrip(2, 1600, 1000);
            var slots = normal.Select(t => (t.ThumbnailHandle, t.TargetX, t.TargetY,
                Canvas.GetLeft(t.ClickBorder), Canvas.GetTop(t.ClickBorder))).ToArray();
            var shelfSlots = app.Strip!.Thumbs.Select(t => (t.TargetX, t.TargetY)).ToArray();
            var list = UIElementAutomationPeer.CreatePeerForElement(app.ThumbnailCanvas);
            var hidden = list.GetChildren().First();
            var hiddenInvoke = Assert.IsAssignableFrom<IInvokeProvider>(hidden.GetPattern(PatternInterface.Invoke));
            var shelf = UIElementAutomationPeer.CreatePeerForElement(app.Strip);
            var shelfEntry = shelf.GetChildren().First();
            var hiddenSelect = Assert.IsAssignableFrom<ISelectionItemProvider>(shelfEntry.GetPattern(PatternInterface.SelectionItem));
            app.Pick(normal[0].WindowHandle);

            other.TypeSearch("cafe");

            Assert.Empty(app.Errors);
            Assert.Equal("cafe", app.Query);
            Assert.Equal("2 matching windows", app.SearchFeedback);
            Assert.Equal(normal[1].WindowHandle, app.Selected);
            Assert.Equal(Visibility.Hidden, normal[0].ClickBorder.Visibility);
            Assert.Equal(slots, normal.Select(t => (t.ThumbnailHandle, t.TargetX, t.TargetY,
                Canvas.GetLeft(t.ClickBorder), Canvas.GetTop(t.ClickBorder))).ToArray());
            Assert.Equal(shelfSlots, app.Strip.Thumbs.Select(t => (t.TargetX, t.TargetY)).ToArray());
            Assert.Equal(tail + " Café", Assert.Single(list.GetChildren()).GetName());
            Assert.Throws<ElementNotEnabledException>(() => hiddenInvoke.Invoke());
            Assert.Empty(shelf.GetChildren());
            Assert.Throws<ElementNotEnabledException>(() => hiddenSelect.Select());
            app.Tab(1);
            Assert.Equal(remote.WindowHandle, app.Selected);

            app.TypeSearch("no matching window");
            Assert.Equal(Visibility.Visible, app.SearchStatus);
            Assert.True(app.Press(Key.Return));
            Assert.Equal(0, app.ActivationRequests);
            Assert.True(app.Press(Key.Escape));
            Assert.True(app.SessionOpen);
            Assert.Empty(other.Query);
            Assert.Equal(Visibility.Collapsed, app.SearchStatus);
            Assert.Equal(2, list.GetChildren().Count);
            Assert.Equal(2, shelf.GetChildren().Count);

            app.TypeSearch("window 11");
            Assert.Equal("1 matching window", app.SearchFeedback);
            Assert.Empty(list.GetChildren());
            Assert.Equal("Window 11, minimized", Assert.Single(shelf.GetChildren()).GetName());
            Assert.Equal(new IntPtr(11), app.Selected);
            app.TypeSearch("cafe calendar");
            Assert.True(app.Press(Key.Return));
            Assert.Equal(remote.WindowHandle, app.Clicked);
        });

    [Theory]
    [InlineData("Compact", 1.5)]
    [InlineData("Standard", 2)]
    [InlineData("Large", 1.25)]
    public void ActualDwmRebuildUsesRequestedSizeAndDimmingWithoutDesynchronizingHitTargets(string size, double dpi)
        => RunSta(() =>
    {
        using var app = new MainWindow();
        var settings = new AppSettings { ThumbnailSize = size, SelectionBorderColor = "Blue", ReduceAnimations = true };
        app.OpenDisplays([new() { Handle = new(1), Width = 1920, Height = 1080, DpiScale = dpi }], settings, new(1));
        var original = Assert.Single(app.RegisterNormalWindows("Full readable title"));
        var normal = new WindowInfo { Handle = original.WindowHandle, Identity = original.Identity, Title = "Full readable title",
            OriginalRect = new PhysicalWindowRect { Left = 100, Top = 100, Right = 700, Bottom = 500 } };
        app.Rebuild(false, normal);
        var thumb = Assert.Single(app.Thumbs);
        var expected = Assert.Single(new WindowLayoutCalculator().CalculateLayouts([normal], 1920, 1080, dpi, size));
        Assert.NotEqual(IntPtr.Zero, thumb.ThumbnailHandle);
        Assert.Equal((expected.X, expected.Y, expected.Width, expected.Height),
            (thumb.CurrentX, thumb.CurrentY, thumb.CurrentWidth, thumb.CurrentHeight));
        Assert.Equal(expected.Width / dpi, thumb.ClickBorder.Width, 8);
        Assert.Equal(expected.Height / dpi, thumb.ClickBorder.Height, 8);
        Assert.Equal(expected.X / dpi, Canvas.GetLeft(thumb.ClickBorder), 8);
        Assert.Equal(expected.Y / dpi, Canvas.GetTop(thumb.ClickBorder), 8);
        Assert.Equal(thumb.ClickBorder.Width + 10, thumb.HighlightBorder.Width);
        Assert.Equal(thumb.ClickBorder.Width, thumb.TitleLabel.Width);
        Assert.Equal(13, thumb.TitleLabel.FontSize);
        app.RefreshPresentation(false, true);
        Assert.Equal(Color.FromArgb(160, 32, 37, 34), ((SolidColorBrush)app.Backdrop).Color);
        Assert.Null(thumb.HighlightBorder.Effect);
        Assert.Null(thumb.TitleLabel.Effect);
        settings.BackgroundDimming = 0;
        app.RefreshPresentation(false, false);
        Assert.Equal(0, ((SolidColorBrush)app.Backdrop).Color.A);
        settings.BackgroundDimming = 100;
        app.RefreshPresentation(false, false);
        Assert.Equal(255, ((SolidColorBrush)app.Backdrop).Color.A);
        app.RefreshPresentation(true, true);
        Assert.Same(SystemColors.WindowBrush, app.Backdrop);
        Assert.Same(SystemColors.HighlightBrush, thumb.HighlightBorder.BorderBrush);
        settings.BackgroundDimming = 80;
        settings.ReduceAnimations = false;
        app.RefreshPresentation(false, false);
        Assert.Equal(204, ((SolidColorBrush)app.Backdrop).Color.A);
        Assert.Null(thumb.TitleLabel.Effect);
        Assert.Equal(Color.FromRgb(0, 120, 215), ((SolidColorBrush)thumb.HighlightBorder.BorderBrush).Color);
        app.Pick(normal.Handle);
        Assert.Equal(normal.Handle, app.Selected);
        Assert.Equal(0, app.ActivationRequests);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ActualRebuildShowsTheMinimizedShelfOnlyWhenEnabledInEitherMode(
        bool appExpose, bool showMinimized) => RunSta(() =>
    {
        using var app = new MainWindow();
        var original = Assert.Single(app.RegisterNormalWindows("Normal window"));
        var normal = new WindowInfo
        {
            Handle = original.WindowHandle, Identity = original.Identity, Title = "Normal window",
            OriginalRect = new PhysicalWindowRect { Left = 100, Top = 100, Right = 700, Bottom = 500 }
        };
        var minimized = app.MinimizedInfo("Minimized window");
        if (appExpose) app.UseAppExpose(normal.Identity);
        bool shelfExpected = showMinimized;
        app.Rebuild(showMinimized, normal, minimized);
        Assert.Equal(shelfExpected, app.Strip != null);
        Assert.Equal(shelfExpected ? 2 : 1, app.DisplayedWindowCount);
        Assert.NotEqual(IntPtr.Zero, Assert.Single(app.Thumbs).ThumbnailHandle);
        Assert.Equal(Visibility.Hidden, app.EmptyState.Visibility);
        app.Tab(1);
        Assert.Equal(normal.Handle, app.Selected);
        app.Tab(1);
        Assert.Equal(shelfExpected ? minimized.Handle : normal.Handle, app.Selected);
        if (!shelfExpected)
        {
            Assert.Equal(1000, app.ContentHeight);
            Assert.Empty(Assert.Single(app.Snapshot()).MinimizedContent);
            Assert.Empty(Assert.Single(app.Snapshot()).MinimizedVisible);
            Assert.Throws<InvalidOperationException>(() => app.Pick(minimized.Handle));
        }
        Assert.Equal(0, app.ActivationRequests);
        Assert.True(minimized.WasMinimized);
        app.Rebuild(showMinimized, minimized);
        app.Tab(1);
        Assert.Equal(shelfExpected ? minimized.Handle : IntPtr.Zero, app.Selected);
        Assert.Equal(shelfExpected ? Visibility.Hidden : Visibility.Visible, app.EmptyState.Visibility);
        if (!shelfExpected)
            Assert.Equal(appExpose ? "No windows from this application on this display." : "No windows on this display.",
                app.EmptyState.Text);
        Assert.Equal(0, app.ActivationRequests);
    });

    [Theory]
    [InlineData("All", 1)]
    [InlineData("All", 2)]
    [InlineData("Current", 1)]
    [InlineData("Current", 2)]
    public void ActualOpeningCreatesOnlyScopedViewsAndSettingsEditsDoNotChangeTheOpenSnapshot(string scope, int pointer)
        => RunSta(() =>
    {
        using var app = new MainWindow();
        var topology = new MonitorHelper.MonitorBounds[]
        {
            new() { Handle = new(1), Left = -1920, Top = -240, Width = 1920, Height = 1080, DpiScale = 1.5 },
            new() { Handle = new(2), Width = 2560, Height = 1440, DpiScale = 2 }
        };
        var settings = new AppSettings { OverviewMonitorScope = scope, ShowMinimizedWindows = false,
            ShowMinimizedWindowsInOverview = false };
        app.OpenDisplays(topology, settings, new(pointer));
        var before = app.Snapshot();
        Assert.Equal(scope == "All" ? 2 : 1, before.Count);
        Assert.Equal(new IntPtr(pointer), app.CurrentDisplay);
        Assert.Equal(2, app.Topology.Count);
        Assert.True(MonitorHelper.SameTopology(topology, app.Topology));
        Assert.All(before, d =>
        {
            var m = topology.Single(m => m.Handle == d.Display);
            Assert.Equal(new PreviewBounds(m.Left, m.Top, m.Width, m.Height), d.Bounds);
        });
        settings.OverviewMonitorScope = scope == "All" ? "Current" : "All";
        settings.ShowMinimizedWindows = true;
        settings.ShowMinimizedWindowsInOverview = true;
        Assert.Equal(before.Select(d => d.Display), app.Snapshot().Select(d => d.Display));
        Assert.False(app.SessionShowsMinimized);
        var selected = app.AddThumb(10, 100, 100, 300, 200);
        app.Tab(1);
        Assert.Equal(selected.WindowHandle, app.Selected);
        if (scope == "Current")
            Assert.Throws<InvalidOperationException>(() => app.Pick(new IntPtr(99)));
    });

    [Theory]
    [InlineData(-1920, -240, 1920, 1080, 1.5)]
    [InlineData(2560, 0, 2560, 1440, 2)]
    public void ActualRebuildHidesShelfTargetsAndReservedSpaceWithoutActivationThenRecreatesThem(
        int x, int y, int width, int height, double dpi) => RunSta(() =>
    {
        using var app = new MainWindow(null, x, y, width, height, dpi);
        var minimized = app.MinimizedInfo("A full minimized window title");
        app.UseAppExpose(minimized.Identity);
        app.Rebuild(true, minimized);
        var oldStrip = app.Strip!;
        Assert.NotNull(oldStrip);
        Assert.True(app.ContentHeight < height);
        Assert.Equal(1, app.DisplayedWindowCount);
        Assert.Equal(Visibility.Hidden, app.EmptyState.Visibility);
        app.Tab(1);
        Assert.Equal(minimized.Handle, app.Selected);
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(oldStrip);
        var oldEntry = Assert.Single(peer.GetChildren());
        Assert.Equal(minimized.Title + ", minimized", oldEntry.GetName());

        app.Rebuild(false, minimized);
        Assert.Equal(IntPtr.Zero, app.Selected);
        Assert.Empty(((System.Windows.Automation.Provider.ISelectionProvider)peer.GetPattern(
            System.Windows.Automation.Peers.PatternInterface.Selection)).GetSelection());
        Assert.Null(app.Strip);
        Assert.Null(oldStrip.Parent);
        Assert.Equal(height, app.ContentHeight);
        Assert.Equal(0, app.DisplayedWindowCount);
        Assert.Empty(Assert.Single(app.Snapshot()).MinimizedContent);
        Assert.Empty(Assert.Single(app.Snapshot()).MinimizedVisible);
        Assert.Equal(Visibility.Visible, app.EmptyState.Visibility);
        Assert.Equal("No windows from this application on this display.", app.EmptyState.Text);
        Assert.Throws<InvalidOperationException>(() => ((System.Windows.Automation.Provider.ISelectionItemProvider)
            oldEntry.GetPattern(System.Windows.Automation.Peers.PatternInterface.SelectionItem)).Select());
        Assert.Throws<System.Windows.Automation.ElementNotEnabledException>(() => ((System.Windows.Automation.Provider.IInvokeProvider)
            oldEntry.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke());
        Assert.Equal(0, app.ActivationRequests);
        Assert.True(minimized.WasMinimized);
        app.Tab(1);
        Assert.Equal(IntPtr.Zero, app.Selected);
        app.Arrow(1, 0);
        Assert.Equal(IntPtr.Zero, app.Selected);

        app.Rebuild(true, minimized);
        app.Tab(1);
        Assert.NotSame(oldStrip, app.Strip);
        Assert.Equal(minimized.Handle, app.Selected);
        Assert.Equal(0, app.ActivationRequests);
    });

    [Fact]
    public void GeneralOverviewShelfOnlyRestoresAnExplicitlySelectedMinimizedWindow() => RunSta(() =>
    {
        using var app = new MainWindow();
        var minimized = app.MinimizedInfo("Quarterly report");
        app.Rebuild(false, minimized);
        Assert.Null(app.Strip);
        app.Rebuild(true, minimized);
        Assert.NotNull(app.Strip);
        Assert.Equal(Visibility.Hidden, app.EmptyState.Visibility);
        Assert.Equal(IntPtr.Zero, app.Selected);
        app.TypeSearch("quarterly");
        Assert.Equal(minimized.Handle, app.Selected);
        Assert.Equal(0, app.ActivationRequests);
        var peer = UIElementAutomationPeer.CreatePeerForElement(app.Strip!);
        var entry = Assert.Single(peer.GetChildren());
        Assert.IsAssignableFrom<IInvokeProvider>(entry.GetPattern(PatternInterface.Invoke)).Invoke();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        Assert.Equal(minimized.Handle, app.Clicked);
        Assert.Equal(1, app.ActivationRequests);
    });

    [Fact]
    public void ActualRebuildKeepsNormalDwmThumbnailAndUsesAllHeightWhenShelfIsHidden() => RunSta(() =>
    {
        using var app = new MainWindow();
        var original = Assert.Single(app.RegisterNormalWindows("Normal window"));
        var normal = new WindowInfo
        {
            Handle = original.WindowHandle, Identity = original.Identity, Title = "Normal window",
            OriginalRect = new PhysicalWindowRect { Left = 100, Top = 100, Right = 700, Bottom = 500 }
        };
        var minimized = app.MinimizedInfo("Minimized window");
        app.UseAppExpose(normal.Identity);
        app.Rebuild(true, normal, minimized);
        app.Pick(minimized.Handle);
        Assert.Equal(2, app.DisplayedWindowCount);
        app.Rebuild(false, normal, minimized);
        app.Tab(1);
        Assert.Equal(normal.Handle, app.Selected);
        var current = Assert.Single(app.Thumbs);
        Assert.NotEqual(IntPtr.Zero, current.ThumbnailHandle);
        var expected = Assert.Single(new WindowLayoutCalculator().CalculateLayouts([normal], 1600, 1000, 1));
        Assert.Equal(expected.X, current.TargetX, 8);
        Assert.Equal(expected.Y, current.TargetY, 8);
        Assert.Equal(expected.Width, current.TargetWidth, 8);
        Assert.Equal(expected.Height, current.TargetHeight, 8);
        Assert.Equal(Visibility.Hidden, app.EmptyState.Visibility);
        Assert.Equal(1, app.DisplayedWindowCount);
        Assert.Equal(0, app.ActivationRequests);
        app.Rebuild(false);
        app.Tab(1);
        Assert.Empty(app.Thumbs);
        Assert.Equal(IntPtr.Zero, app.Selected);
        Assert.Equal(Visibility.Visible, app.EmptyState.Visibility);
    });

    [Theory]
    [InlineData(-5120, -300, 5120, 1440, 1.25)]
    [InlineData(1920, 200, 3440, 1440, 1.5)]
    [InlineData(-1200, -1600, 1200, 1600, 2)]
    public void ActualSnapshotOffsetsCenteredShelfByItsOwnMonitorOrigin(int x, int y, int width, int height, double dpi)
        => RunSta(() =>
        {
            using var app = new MainWindow();
            var other = app.AddView(x, y, width, height, dpi);
            other.AddStrip(20, width / dpi, height / dpi, dpi);
            var strip = other.Strip!;
            var scroll = (ScrollViewer)((DockPanel)strip.Child).Children[1];
            foreach (double offset in new[] { 0d, 17d, scroll.ScrollableHeight })
            {
                scroll.ScrollToVerticalOffset(offset);
                scroll.UpdateLayout();
                Assert.Equal(x + width / 2d, x + (Canvas.GetLeft(strip) + strip.Width / 2) * dpi, 8);
                var display = app.Snapshot().Single(d => d.Display == new IntPtr(2));
                var local = strip.NavigationTargets(true);
                Assert.NotEmpty(local);
                Assert.All(local, target =>
                {
                    var global = display.MinimizedVisible.Single(t => t.Identity == target.Identity);
                    Assert.Equal(x + target.Bounds.X, global.Bounds.X, 8);
                    Assert.Equal(y + target.Bounds.Y, global.Bounds.Y, 8);
                    Assert.Equal(target.Bounds.Width, global.Bounds.Width);
                    Assert.Equal(target.Bounds.Height, global.Bounds.Height);
                    Assert.InRange(global.Bounds.X, x, x + width);
                    Assert.True(global.Bounds.X + global.Bounds.Width <= x + width);
                });
            }
        });

    [Fact]
    public void TabShowsKeyboardCueEvenWhenOnlyMinimizedSelectionDoesNotChange()
        => RunSta(() =>
        {
            using var app = new MainWindow();
            app.AddStrip(1, 320, 480);
            app.Pick(10);
            var button = Assert.IsType<MinimizedWindowButton>(app.Strip!.Thumbs[0].ClickBorder.Child);
            Assert.False(button.KeyboardSelected);
            app.Tab(1);
            Assert.Equal(new IntPtr(10), app.Selected);
            Assert.True(button.KeyboardSelected);
            app.Pick(10);
            Assert.False(button.KeyboardSelected);
        });

    [Fact]
    public void GlobalArrowAndAutomationSelectionDoNotRevealUntilNavigationOriginatesInsideShelf()
        => RunSta(() =>
        {
            using var app = new MainWindow();
            app.AddThumb(1, 100, 100, 200, 100);
            app.AddStrip(20, 320, 480);
            var scroll = (ScrollViewer)((DockPanel)app.Strip!.Child).Children[1];
            scroll.ScrollToVerticalOffset(17);
            scroll.UpdateLayout();
            app.Pick(1);
            app.Arrow(0, 1);
            Assert.Equal(new IntPtr(10), app.Selected);
            Assert.Equal(17, scroll.VerticalOffset);
            app.Arrow(0, 1);
            app.Arrow(0, -1);
            Assert.Equal(new IntPtr(10), app.Selected);
            Assert.Equal(0, scroll.VerticalOffset);
            var button = (Button)app.Strip.Thumbs[^1].ClickBorder.Child;
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(button);
            ((System.Windows.Automation.Provider.ISelectionItemProvider)peer.GetPattern(
                System.Windows.Automation.Peers.PatternInterface.SelectionItem)).Select();
            Assert.Equal(new IntPtr(29), app.Selected);
            Assert.Equal(0, scroll.VerticalOffset);
            Assert.Equal(IntPtr.Zero, app.Clicked);
        });

    [Fact]
    public void ActualTabRouteFinishesMonitorRowsThenMinimizedAndReverseRetracesStableSnapshot()
        => RunSta(() =>
        {
            using var app = new MainWindow();
            var other = app.AddView(-1800, -200, 1600, 1000, 1.5);
            app.AddThumb(3, 700, 180, 200, 80);
            app.AddThumb(1, 100, 100, 200, 300);
            app.AddThumb(5, 400, 490, 200, 60);
            app.AddThumb(4, 100, 450, 200, 200);
            app.AddThumb(2, 400, 140, 200, 60);
            app.AddStrip(20, 320, 480);
            other.AddThumb(41, 100, 100, 200, 200);
            other.AddThumb(40, 500, 100, 200, 200);
            app.Pick(1);
            foreach (int expected in new[] { 2, 3, 4, 5 }.Concat(Enumerable.Range(10, 20)).Concat(new[] { 41, 40, 1 }))
            {
                app.Tab(1);
                Assert.Equal(new IntPtr(expected), app.Selected);
            }
            app.Tab(-1);
            Assert.Equal(new IntPtr(40), app.Selected);
            app.Tab(-1);
            Assert.Equal(new IntPtr(41), app.Selected);
            for (int expected = 29; expected >= 10; expected--)
            {
                app.Tab(-1);
                Assert.Equal(new IntPtr(expected), app.Selected);
            }
            foreach (int expected in new[] { 5, 4, 3, 2, 1 })
            {
                app.Tab(-1);
                Assert.Equal(new IntPtr(expected), app.Selected);
            }
            // Neither scrolling nor the render geometry of an expanded preview changes Tab's snapshot.
            app.Reveal(29);
            app.Thumbs.Single(t => t.WindowHandle == new IntPtr(1)).CurrentWidth = 9999;
            app.Tab(1);
            Assert.Equal(new IntPtr(41), app.Selected);
            app.Pick(2);
            app.RemoveThumb(3);
            app.Tab(1);
            Assert.Equal(new IntPtr(4), app.Selected);
            app.TransferThumb(4, other, 900, 600);
            app.Tab(1);
            Assert.Equal(new IntPtr(1), app.Selected);
        });

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void ActualArrowRouteUsesPhysicalBoundsActiveMonitorAndClippedMinimizedViewport(double dpi)
        => RunSta(() =>
        {
            using var app = new MainWindow();
            var remote = app.AddView(-2000, -300, 1800, 1000, dpi);
            remote.AddThumb(41, 500, 100, 200, 200);
            remote.AddThumb(40, 100, 100, 200, 200);
            app.AddThumb(1, 100, 100, 200, 100);
            app.AddStrip(20, 320, 480, dpi);
            app.Active(remote);
            app.Arrow(1, 0);
            Assert.Equal(new IntPtr(40), app.Selected);
            app.Pick(1);
            var strip = app.Strip!;
            app.Reveal(29);
            app.Pick(1);
            var bottomVisible = strip.NavigationTargets(true);
            app.Arrow(0, 1);
            Assert.Contains(bottomVisible, t => t.Identity.Handle == app.Selected);
            app.Reveal(10);
            app.Pick(1);
            var all = strip.NavigationTargets(false);
            var visible = strip.NavigationTargets(true);
            Assert.True(visible.Count < all.Count);
            foreach (var t in visible)
            {
                Assert.InRange(t.Bounds.Y, 0, 480 * dpi);
                Assert.InRange(t.Bounds.Y + t.Bounds.Height, 0, 480 * dpi);
            }
            IntPtr hidden = all.First(t => !visible.Any(v => v.Identity == t.Identity)).Identity.Handle;
            app.Arrow(0, 1);
            Assert.NotEqual(hidden, app.Selected);
            Assert.Contains(strip.NavigationTargets(true), t => t.Identity.Handle == app.Selected);
            int previous = checked((int)hidden.ToInt64()) - 1;
            app.Reveal(previous);
            double previousOffset = app.ScrollOffset;
            app.Arrow(0, 1);
            Assert.Equal(hidden, app.Selected);
            Assert.True(app.ScrollOffset > previousOffset);
            app.Arrow(0, -1);
            Assert.Equal(new IntPtr(previous), app.Selected);
            app.Reveal(10);
            app.Arrow(0, -1);
            Assert.Equal(new IntPtr(1), app.Selected);
            Assert.All(strip.Thumbs, t => Assert.True(t.WasMinimized));
            Assert.All(strip.Thumbs, t => Assert.Equal(IntPtr.Zero, t.ThumbnailHandle));
        });

    [Fact]
    public void ScrolledOutMinimizedTilesCannotCompeteWithVisibleTilesWhenEnteringFromNormalOverview()
        => RunSta(() =>
        {
            using var app = new MainWindow();
            app.AddThumb(1, 100, 100, 200, 100);
            app.AddStrip(20, 320, 480);
            app.Reveal(29);
            app.Pick(1);
            var visible = app.Strip!.NavigationTargets(true);
            Assert.DoesNotContain(visible, t => t.Identity.Handle == new IntPtr(10));
            app.Arrow(0, 1);
            Assert.Contains(visible, t => t.Identity.Handle == app.Selected);
        });

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void ManuallyHiddenMinimizedSelectionIsNotLostWhenItHasNoContentNeighbour(int dx, int dy)
        => RunSta(() =>
        {
            using var app = new MainWindow();
            app.AddThumb(1, 100, 100, 200, 100);
            app.AddStrip(20, 320, 480);
            app.Pick(10);
            var strip = app.Strip!;
            var scroll = (ScrollViewer)((DockPanel)strip.Child).Children[1];
            scroll.ScrollToEnd();
            scroll.UpdateLayout();
            Assert.Equal(new IntPtr(10), app.Selected);
            Assert.DoesNotContain(strip.NavigationTargets(true), t => t.Identity.Handle == app.Selected);
            double offset = scroll.VerticalOffset;
            app.Arrow(dx, dy);
            Assert.Equal(new IntPtr(10), app.Selected);
            Assert.Equal(offset, scroll.VerticalOffset);
            Assert.Equal(IntPtr.Zero, app.Clicked);
            Assert.All(strip.Thumbs, t =>
            {
                Assert.True(t.WasMinimized);
                Assert.Equal(IntPtr.Zero, t.ThumbnailHandle);
            });
            app.Arrow(0, 1);
            Assert.Equal(new IntPtr(11), app.Selected);
            Assert.Contains(strip.NavigationTargets(true), t => t.Identity.Handle == app.Selected);
            scroll.ScrollToTop();
            scroll.UpdateLayout();
            app.Pick(10);
            app.Arrow(0, -1);
            Assert.Equal(new IntPtr(1), app.Selected);
        });

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public void NativePreviewReturnsToSlotsBeforeActualNavigationAndHonoursReleaseOrCancel(bool release, bool cancel, bool repress)
        => RunSta(() =>
        {
            using var app = new MainWindow();
            var a = app.AddNativeThumb(100, 300, 200, 200);
            var b = app.AddNativeThumb(700, 300, 200, 200);
            app.AddNativeThumb(220, 650, 200, 200);
            app.ShowFixture();
            app.Pick(a.WindowHandle);
            app.HoldPreview();
            Pump(() => !app.PreviewAnimating);
            Assert.True(a.CurrentWidth > a.TargetWidth);
            Assert.True(app.Press(Key.Right));
            Assert.Equal(a.WindowHandle, app.Selected);
            Assert.True(app.Pending);
            app.Press(Key.Right); // Repeat while returning cannot enqueue another navigation.
            if (release) app.ReleasePreview();
            if (repress) app.HoldPreview();
            if (cancel) app.Press(Key.Escape);
            Pump(() => !app.PreviewAnimating && !app.Pending);
            Assert.Empty(app.Errors);
            Assert.Equal(cancel ? a.WindowHandle : b.WindowHandle, app.Selected);
            Assert.Equal(a.TargetWidth, a.CurrentWidth);
            Assert.Equal(a.TargetHeight, a.CurrentHeight);
            if ((!release || repress) && !cancel) Assert.True(b.CurrentWidth > b.TargetWidth);
            else Assert.Equal(b.TargetWidth, b.CurrentWidth);
            Assert.Equal((100d, 300d, 200d, 200d), (a.TargetX, a.TargetY, a.TargetWidth, a.TargetHeight));
            Assert.DoesNotContain(app.FixtureHandles, h => h == Win32Interop.GetForegroundWindow());
            if (!cancel)
            {
                var beforeHover = app.Selected;
                app.HoverWithoutPointerMovement(a.WindowHandle);
                Assert.Equal(beforeHover, app.Selected);
                app.ReleasePreview();
                Pump(() => !app.PreviewAnimating);
                app.Press(Key.Left);
                Assert.Equal(a.WindowHandle, app.Selected);
                app.Press(Key.Right);
                Assert.Equal(b.WindowHandle, app.Selected);
                app.Press(Key.Return);
                Assert.Equal(b.WindowHandle, app.Clicked);
            }
        });

    [Fact]
    public void NativeHeldPreviewCanNavigateToAlignedWindowOnAnotherDisplayAndTabBack()
        => RunSta(() =>
        {
            using var app = new MainWindow();
            var other = app.AddView(2000, -200, 1600, 1000, 1.5);
            var a = app.AddNativeThumb(100, 300, 200, 200);
            app.AddNativeThumb(220, 650, 200, 200);
            var b = other.AddNativeThumb(100, 500, 200, 200);
            app.ShowFixture();
            other.ShowFixture();
            app.Pick(a.WindowHandle);
            app.HoldPreview();
            Pump(() => !app.PreviewAnimating);
            app.Press(Key.Right);
            Assert.Equal(a.WindowHandle, app.Selected);
            Pump(() => !app.Pending && !app.PreviewAnimating);
            Assert.Empty(app.Errors);
            Assert.Equal(b.WindowHandle, app.Selected);
            Assert.Equal(a.TargetWidth, a.CurrentWidth);
            Assert.True(b.CurrentWidth > b.TargetWidth);
            app.Press(Key.Tab);
            Assert.Equal(b.WindowHandle, app.Selected);
            Pump(() => !app.Pending && !app.PreviewAnimating);
            Assert.Equal(a.WindowHandle, app.Selected);
            Assert.True(a.CurrentWidth > a.TargetWidth);
            Assert.Equal(b.TargetWidth, b.CurrentWidth);
            Assert.Empty(app.Errors);
        });

    private static void Pump(Func<bool> done)
    {
        var frame = new DispatcherFrame();
        bool timedOut = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        var timeout = DateTime.UtcNow.AddSeconds(6);
        timer.Tick += (_, _) =>
        {
            if (done()) frame.Continue = false;
            else if (DateTime.UtcNow >= timeout) { timedOut = true; frame.Continue = false; }
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.False(timedOut, "Navigation/preview failed to complete.");
    }

    private static void RunSta(Action test)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
    }
}

namespace WindowScatter
{
    // Compile the actual navigation/preview partial; replace only app startup, closing and native move boundaries.
    public partial class MainWindow : Window, IDisposable
    {
        private readonly MainWindow coordinator;
        private readonly OverviewSession session;
        private readonly List<MainWindow> views;
        private readonly List<WindowThumb> windowThumbs = [];
        private readonly ScatterInteraction scatterInteraction;
        private readonly HeldPreviewState previewKey;
        private readonly WindowDragGesture drag = new();
        private MainWindow? dragSource;
        private WindowThumb? dragThumb;
        private Border? dragCapture;
        private double dragGrabX, dragGrabY;
        private DwmDragPreview? dragPreview;
        private DragPreviewGeometry? dragGeometry;
        private int enumeratedWindowCount;
        private readonly OverviewThumbnailCanvas canvas = new();
        private readonly List<Window> sources = [];
        private readonly WindowAnimationManager animationManager;
        private ThumbnailManager thumbnailManager;
        private MainWindow? activeView;
        private bool showMinimizedWindows = true;
        private WindowSwitcher? windowSwitcher;
        private System.Windows.Controls.Canvas ChromeCanvas => canvas;
        private readonly TextBlock EmptyMessage = new();
        private readonly Border DimOverlay = new();
        private MonitorHelper.MonitorBounds monitor;
        private List<MonitorHelper.MonitorBounds> monitors = [];
        private AppSettings settings = new();
        private readonly TextBlock MoveStatus = new();
        private readonly TextBox SearchBox = new();
        private readonly StackPanel SearchPanel = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        private readonly TextBlock SearchStatusText = new() { Visibility = Visibility.Collapsed };
        private readonly Window chromeWindow = new() { ShowActivated = false };
        private AppExposeRequest? appExposeRequest;
        private string IdleMoveStatus => "";
        private bool isScatterActive => session.Opening;
        private bool isTransitioning => session.Transitioning || !scatterInteraction.IsOpen;
        private bool isClosing => false;
        private bool movingWindow => false;
        private bool AnyAnimating => views.Any(v => v.animationManager.IsAnimating);
        private Win32Interop.POINT lastHoverPosition;
        private bool previewExpanded;
        private bool IsCoordinator => coordinator == this;
        private IntPtr selectedWindowHandle => session.Selection;
        private double currentDpiScale => monitor.DpiScale;
        private double monitorPixelWidth => monitor.Width;
        private double monitorPixelHeight => monitor.Height;
        private readonly List<Exception> errors = [];
        internal List<Exception> Errors => coordinator.errors;
        internal IntPtr Clicked { get; private set; }
        internal int ActivationRequests { get; private set; }
        internal OverviewThumbnailCanvas ThumbnailCanvas => canvas;
        internal IntPtr Selected => selectedWindowHandle;
        internal IReadOnlyList<WindowThumb> Thumbs => windowThumbs;
        internal MinimizedWindowStrip? Strip => minimizedStrip;
        internal double ScrollOffset => ((ScrollViewer)((DockPanel)minimizedStrip!.Child).Children[1]).VerticalOffset;
        internal bool PreviewAnimating => views.Any(v => v.animationManager.IsPreviewAnimating);
        internal bool Pending => navigationPending;
        internal double ContentHeight => OverviewContentHeight;
        internal TextBlock EmptyState => EmptyMessage;
        internal int DisplayedWindowCount => enumeratedWindowCount;
        internal string Query => SearchBox.Text;
        internal bool SessionOpen => scatterInteraction.IsOpen;
        internal Visibility SearchStatus => SearchStatusText.Visibility;
        internal Visibility SearchVisibility => SearchPanel.Visibility;
        internal bool SearchShown => SearchPanel.IsHitTestVisible && SearchPanel.Opacity == 1;
        internal string SearchFeedback => SearchStatusText.Text;
        internal IntPtr CurrentDisplay => monitor.Handle;
        internal IReadOnlyList<MonitorHelper.MonitorBounds> Topology => monitors;
        internal bool SessionShowsMinimized => showMinimizedWindows;
        internal IEnumerable<IntPtr> FixtureHandles => sources.Prepend(this).Select(w => new WindowInteropHelper(w).Handle);
        private bool chromeClosed => false;
        private IntPtr chromeHandle => throw new NotSupportedException();
        private WindowActivation windowActivation => throw new NotSupportedException();
        private WindowClose windowClose => throw new NotSupportedException();
        internal bool FileDragArmed => fileDrag != null;
        internal bool FileDragRoutesDropEvents => chromeWindow.AllowDrop;
        internal bool BlocksFixtureThumbnailInput => BlocksThumbnailInput;

        internal MainWindow(MainWindow? parent = null, int left = 0, int top = 0, int width = 1600, int height = 1000, double dpi = 1)
        {
            coordinator = parent ?? this;
            settings = parent?.settings ?? new();
            session = parent?.session ?? new();
            views = parent?.views ?? [];
            if (parent == null) views.Add(this);
            monitor = new() { Handle = new(parent == null ? 1 : views.Count + 1),
                Left = left, Top = top, Width = width, Height = height, DpiScale = dpi };
            scatterInteraction = session.Interaction;
            SearchBox.TextChanged += SearchBox_TextChanged;
            previewKey = session.PreviewKey;
            if (parent == null) scatterInteraction.BeginSession(default);
            activeView = this;
            Width = width; Height = height; Left = -30000; Top = -28000;
            ShowActivated = false; ShowInTaskbar = false; WindowStyle = WindowStyle.None;
            Content = canvas;
            animationManager = new(windowThumbs);
            animationManager.SetMotionEnabled(false);
            thumbnailManager = new(canvas, windowThumbs, IntPtr.Zero, _ => { }, _ => { },
                selectionBorderColor: () => settings.SelectionBorderColor, effectsEnabled: () => !settings.ReduceAnimations);
        }

        private void InitializeRendering() { }
        private void HideAllViews() => throw new NotSupportedException();
        private void QueueOverviewCleanup() => throw new NotSupportedException();

        internal void BeginFixtureFileDrag(int displayCount)
        {
            monitors = Enumerable.Range(1, displayCount)
                .Select(id => new MonitorHelper.MonitorBounds { Handle = new(id) }).ToList();
            BeginFileDrag(default);
        }

        internal void HoverFixtureFileDrag(WindowActivationTarget target) => SetFileDragHover(target);
        internal OverviewFileDrag.Hover? FixtureDragHover => coordinator.fileDrag?.Current;
        internal WindowActivationTarget? ConfirmFixtureDrag(OverviewFileDrag.Hover? hover, bool held, WindowActivationTarget hit) =>
            ConfirmFileDragHover(hover, held, hit);
        internal void ResetFixtureFileDrag() => ResetFileDrag();

        internal (bool Handled, DragDropEffects Effect) EndFixtureFileDrag(RoutedEvent routedEvent)
        {
            var constructor = typeof(DragEventArgs).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                [typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)],
                null)!;
            var args = (DragEventArgs)constructor.Invoke(
                [new DataObject(), DragDropKeyStates.None, DragDropEffects.Copy, chromeWindow, new Point()]);
            args.RoutedEvent = routedEvent;
            EndFileDragHover(chromeWindow, args);
            return (args.Handled, args.Effects);
        }

        private void ConfigureCloseInput(WindowThumb thumb) { }
        private void RestorePendingCloseStatus() { }
        internal void RefreshPresentation(bool highContrast, bool systemAnimation) => RefreshOverviewPresentation(highContrast, systemAnimation);
        internal Brush Backdrop => DimOverlay.Background;
        internal void UseAppExpose(WindowActivationTarget target) =>
            coordinator.appExposeRequest = AppExposeRequest.Capture(() => target, _ => false,
                _ => ApplicationIdentity.Create(@"C:\MissionView-fixture.exe", null, null));
        internal void OpenDisplays(IReadOnlyList<MonitorHelper.MonitorBounds> topology, AppSettings preferences, IntPtr pointer)
        {
            settings = preferences;
            InitializeOverviewViews(topology, pointer);
        }
        internal MainWindow AddView(int x, int y, int width, int height, double dpi)
        {
            var view = new MainWindow(this, x, y, width, height, dpi);
            views.Add(view);
            return view;
        }
        internal WindowInfo MinimizedInfo(string title)
        {
            var source = new Window
            {
                Title = title, Width = 600, Height = 400, Left = -28000, Top = -26000,
                ShowActivated = false, ShowInTaskbar = false
            };
            sources.Add(source);
            var handle = new WindowInteropHelper(source).EnsureHandle();
            return new WindowInfo
            {
                Handle = handle, Identity = new WindowActivation().Capture(handle),
                Title = title, WasMinimized = true,
                OriginalRect = new PhysicalWindowRect { Left = 100, Top = 100, Right = 700, Bottom = 500 }
            };
        }
        internal void Active(MainWindow view) => activeView = view;
        internal void Pick(int id) => SelectWindow(new(id));
        internal void Reveal(int id) => SelectWindow(new(id), keyboard: true, revealMinimized: true);
        internal void Pick(IntPtr handle) => SelectWindow(handle);
        internal void Tab(int direction) => SelectNextWindow(direction);
        internal void TypeSearch(string query) => SearchBox.Text = query;
        internal void UseSearchInput()
        {
            if (CanUseSearch) SetSearchInputView();
        }
        internal void BeginSearchOpening()
        {
            session.Opening = true;
            HideSearchForTransition();
        }
        internal void EndSearchOpening() => session.Opening = false;
        internal void StartFixtureOpeningAnimation() => animationManager.StartScatterAnimation();
        internal void SettleFixtureOpeningAnimation() => animationManager.StopAllAnimations();
        internal void BeginSearchExit()
        {
            Assert.True(session.TryBeginClose(null, false, out _));
            HideSearchForTransition();
        }
        internal (double Left, double Width) LayoutFixtureSearch()
        {
            LayoutSearchPanel(monitor.Width / currentDpiScale, monitor.Height / currentDpiScale);
            return (Canvas.GetLeft(SearchPanel), SearchPanel.Width);
        }
        internal void Arrow(int dx, int dy) => SelectWindowInDirection(dx, dy);
        internal IReadOnlyList<OverviewDisplaySnapshot> Snapshot() => NavigationSnapshot();
        internal void Rebuild(bool showMinimized, params WindowInfo[] windows)
        {
            coordinator.showMinimizedWindows = showMinimized;
            RebuildThumbnails([], windows.ToList(), settled: true);
            canvas.Measure(new Size(monitor.Width / currentDpiScale, monitor.Height / currentDpiScale));
            canvas.Arrange(new Rect(0, 0, monitor.Width / currentDpiScale, monitor.Height / currentDpiScale));
            canvas.UpdateLayout();
            if (coordinator.IsSearching) coordinator.RefreshSearchResults();
        }
        internal void HoverWithoutPointerMovement(IntPtr handle)
        {
            Assert.True(Win32Interop.GetCursorPos(out lastHoverPosition));
            OnWindowHovered(handle);
        }
        internal bool Press(Key key)
        {
            var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, 0, key)
                { RoutedEvent = Keyboard.KeyDownEvent };
            Window_KeyDown(this, input);
            return input.Handled;
        }
        internal void HoldPreview() => Press(Key.Space);
        internal void ReleasePreview()
        {
            var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, 0, Key.Space)
                { RoutedEvent = Keyboard.KeyUpEvent };
            Window_KeyUp(this, input);
        }
        internal void CancelSession()
        {
            scatterInteraction.InvalidateSession();
            foreach (var view in views) view.ResetPreview();
        }

        internal void RestartSession() => scatterInteraction.BeginSession(default);
        internal void RebindThumbnailAutomation(WindowThumb thumb) => ConfigureThumbnailAutomation(thumb);
        internal void HideNormalSelectionOutline() => thumbnailManager.SetSelectedWindow(IntPtr.Zero);

        internal WindowThumb AddThumb(int id, double x, double y, double width, double height)
        {
            var thumb = new WindowThumb
            {
                WindowHandle = new(id), Identity = new(new(id), 1, 2),
                StartWidth = 1200, StartHeight = 800,
                TargetX = x, TargetY = y, TargetWidth = width, TargetHeight = height,
                CurrentX = x, CurrentY = y, CurrentWidth = width, CurrentHeight = height,
                ClickBorder = new Border(), HighlightBorder = new Border(), TitleLabel = new TextBlock()
            };
            windowThumbs.Add(thumb);
            InvalidateNavigation();
            return thumb;
        }

        internal void RemoveThumb(int id) => RemoveThumb(new IntPtr(id));

        internal void RemoveThumb(IntPtr handle)
        {
            windowThumbs.RemoveAll(t => t.WindowHandle == handle);
            InvalidateNavigation();
        }

        internal void TransferThumb(int id, MainWindow destination, double x, double y)
        {
            var thumb = windowThumbs.Single(t => t.WindowHandle == new IntPtr(id));
            windowThumbs.Remove(thumb);
            thumb.TargetX = x; thumb.TargetY = y;
            destination.windowThumbs.Add(thumb);
            InvalidateNavigation();
        }

        internal void AddStrip(int count, double width, double height, double dpi = 1)
        {
            var windows = Enumerable.Range(10, count).Select(i => new WindowInfo
            { Handle = new(i), Identity = new(new(i), 1, 2), WasMinimized = true, Title = $"Window {i}" }).ToArray();
            minimizedStrip = new(windows, width, height, dpi, _ => { }, _ => { }, _ => null,
                selected: handle => SelectWindow(handle));
            canvas.Children.Add(minimizedStrip);
            canvas.Width = width; canvas.Height = height;
            canvas.Measure(new Size(width, height));
            canvas.Arrange(new Rect(0, 0, width, height));
            canvas.UpdateLayout();
            InvalidateNavigation();
        }

        internal WindowThumb AddNativeThumb(double x, double y, double width, double height)
        {
            var source = new Window { Width = 1200, Height = 800, Left = -28000, Top = -26000,
                ShowActivated = false, ShowInTaskbar = false, Content = new Border { Background = Brushes.DarkGray } };
            sources.Add(source);
            source.Show();
            var handle = new WindowInteropHelper(source).Handle;
            var thumb = AddThumb(1, x, y, width, height);
            thumb.WindowHandle = handle;
            thumb.Identity = new WindowActivation().Capture(handle);
            return thumb;
        }

        internal IReadOnlyList<WindowThumb> RegisterNormalWindows(params string[] titles)
        {
            Show();
            thumbnailManager = new(canvas, windowThumbs, new WindowInteropHelper(this).Handle, OnWindowClicked, OnWindowHovered,
                selectionBorderColor: () => settings.SelectionBorderColor, effectsEnabled: () => !settings.ReduceAnimations);
            thumbnailManager.ConfigureInput = ConfigureThumbnailAutomation;
            var layouts = titles.Select((title, i) =>
            {
                var source = new Window
                {
                    Title = title, Width = 600, Height = 400, Left = -28000, Top = -26000,
                    ShowActivated = false, ShowInTaskbar = false, Content = new Border { Background = Brushes.DarkGray }
                };
                sources.Add(source);
                source.Show();
                var handle = new WindowInteropHelper(source).Handle;
                return new WindowLayout
                {
                    Window = new WindowInfo
                    {
                        Handle = handle, Identity = new WindowActivation().Capture(handle), Title = source.Title,
                        OriginalRect = new PhysicalWindowRect { Left = -28000, Top = -26000, Right = -27400, Bottom = -25600 }
                    },
                    X = 100 + i * 400, Y = 100, Width = 300, Height = 200
                };
            }).ToList();
            thumbnailManager.RegisterThumbnails(layouts, animationManager, settled: true);
            thumbnailManager.SetPreviewAnimating(false);
            canvas.Measure(new Size(1600, 1000));
            canvas.Arrange(new Rect(0, 0, 1600, 1000));
            canvas.UpdateLayout();
            Assert.Equal(titles.Length, windowThumbs.Count);
            Assert.DoesNotContain(FixtureHandles, h => h == Win32Interop.GetForegroundWindow());
            InvalidateNavigation();
            return windowThumbs;
        }

        internal void ShowFixture()
        {
            Show();
            thumbnailManager = new(canvas, windowThumbs, new WindowInteropHelper(this).Handle, _ => { }, _ => { });
            foreach (var thumb in windowThumbs)
                Assert.True(thumbnailManager.BringThumbnailToFront(thumb.WindowHandle, animationManager));
            UpdateLayout();
        }

        private MainWindow? ViewFor(IntPtr handle) => views.FirstOrDefault(v => v.ViewThumbs.Any(t => t.WindowHandle == handle));
        private void OnWindowClicked(IntPtr handle) { ActivationRequests++; Clicked = handle; CancelSession(); }
        private void AbortOverview(Exception error) { Errors.Add(error); CancelSession(); }
        private void RequestDismissScatter() => CancelSession();
        private void ShowMoveMenu() => throw new NotSupportedException("Native move menus are not part of this fixture.");

        public void Dispose()
        {
            foreach (var view in views.ToArray())
            {
                view.dragPreview?.Dispose();
                view.animationManager.StopAllAnimations();
                view.thumbnailManager.CleanupAllThumbnails();
                foreach (var source in view.sources) source.Close();
                view.chromeWindow.Close();
                view.Close();
            }
        }
    }
}
