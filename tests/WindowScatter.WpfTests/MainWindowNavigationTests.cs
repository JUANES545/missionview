using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WindowScatter;
using Xunit;

public class MainWindowNavigationTests
{
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
        private int enumeratedWindowCount => ViewThumbs.Count();
        private readonly OverviewThumbnailCanvas canvas = new();
        private readonly List<Window> sources = [];
        private readonly WindowAnimationManager animationManager;
        private ThumbnailManager thumbnailManager;
        private MainWindow? activeView;
        private MinimizedWindowStrip? minimizedStrip;
        private readonly MonitorHelper.MonitorBounds monitor;
        private readonly TextBlock MoveStatus = new();
        private readonly Window chromeWindow = new() { ShowActivated = false };
        private AppExposeRequest? appExposeRequest => null;
        private string IdleMoveStatus => "";
        private bool isScatterActive => false;
        private bool isTransitioning => !scatterInteraction.IsOpen;
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
        private double OverviewContentHeight => monitorPixelHeight - (minimizedStrip?.Geometry.ReservedHeight ?? 0) * currentDpiScale;
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
        internal IEnumerable<IntPtr> FixtureHandles => sources.Prepend(this).Select(w => new WindowInteropHelper(w).Handle);

        internal MainWindow(MainWindow? parent = null, int left = 0, int top = 0, int width = 1600, int height = 1000, double dpi = 1)
        {
            coordinator = parent ?? this;
            session = parent?.session ?? new();
            views = parent?.views ?? [];
            views.Add(this);
            monitor = new() { Handle = new(views.Count), Left = left, Top = top, Width = width, Height = height, DpiScale = dpi };
            scatterInteraction = session.Interaction;
            previewKey = session.PreviewKey;
            if (parent == null) scatterInteraction.BeginSession(default);
            activeView = this;
            Width = width; Height = height; Left = -30000; Top = -28000;
            ShowActivated = false; ShowInTaskbar = false; WindowStyle = WindowStyle.None;
            Content = canvas;
            animationManager = new(windowThumbs);
            animationManager.SetMotionEnabled(false);
            thumbnailManager = new(canvas, windowThumbs, IntPtr.Zero, _ => { }, _ => { });
        }

        internal MainWindow AddView(int x, int y, int width, int height, double dpi) => new(this, x, y, width, height, dpi);
        internal void Active(MainWindow view) => activeView = view;
        internal void Pick(int id) => SelectWindow(new(id));
        internal void Reveal(int id) => SelectWindow(new(id), keyboard: true, revealMinimized: true);
        internal void Pick(IntPtr handle) => SelectWindow(handle);
        internal void Tab(int direction) => SelectNextWindow(direction);
        internal void Arrow(int dx, int dy) => SelectWindowInDirection(dx, dy);
        internal IReadOnlyList<OverviewDisplaySnapshot> Snapshot() => NavigationSnapshot();
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
            thumbnailManager = new(canvas, windowThumbs, new WindowInteropHelper(this).Handle, OnWindowClicked, OnWindowHovered);
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

        private IEnumerable<WindowThumb> ViewThumbs => windowThumbs.Concat(minimizedStrip?.Thumbs ?? []);
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
