using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowScatter;
using Xunit;

public class NormalThumbnailAutomationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedCloseHidesOnlyTheOutlineAndAnExplicitSelectionCycleRestoresIt(bool minimized)
        => RunSta(() =>
        {
            var identity = new WindowActivationTarget(new(42), 1, 2);
            var window = new WindowInfo { Handle = identity.Handle, Identity = identity, Title = "Synthetic document", WasMinimized = minimized };
            var strip = minimized
                ? new MinimizedWindowStrip([window], 900, 700, 1, _ => { }, _ => { }, _ => null) : null;
            var thumb = strip?.Thumbs.Single() ?? new WindowThumb
            {
                WindowHandle = identity.Handle, Identity = identity, Title = window.Title,
                ClickBorder = new OverviewThumbnail(window.Title), HighlightBorder = new Border(), TitleLabel = new TextBlock()
            };
            var manager = new ThumbnailManager(new Canvas(), minimized ? [] : [thumb], IntPtr.Zero, _ => { }, _ => { });
            void Select(IntPtr handle)
            {
                if (strip != null) strip.SetSelected(handle);
                else manager.SetSelectedWindow(handle);
            }
            bool OutlineVisible() => thumb.MinimizedButton?.SelectionOutlineVisible ??
                (thumb.HighlightBorder.Visibility == Visibility.Visible && thumb.HighlightBorder.Opacity == 1);
            var close = new WindowClose(_ => identity, _ => (true, 0));
            Select(identity.Handle);
            Assert.True(OutlineVisible());
            close.Request(identity);
            thumb.SuppressSelectionOutlineForClose();
            Assert.True(close.IsPending(identity));
            Assert.False(OutlineVisible());
            if (strip != null)
            {
                var button = thumb.MinimizedButton!;
                button.ApplyTemplate();
                var frame = (Border)button.Template.FindName("Frame", button);
                Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(frame.BorderBrush).Color);
                Assert.True(button.IsSelected);
                strip.SetSelectionOutlinesVisible(false);
                strip.SetSelected(identity.Handle);
                strip.SetSelectionOutlinesVisible(true);
            }
            else
            {
                manager.BeginTransition(() => manager.SetSelectedWindow(identity.Handle));
                manager.CompleteTransition(1, identity.Handle);
            }
            Assert.False(OutlineVisible());
            Assert.Equal(Visibility.Visible, thumb.ClickBorder.Visibility);
            Assert.True(thumb.MatchesTitle(""));
            Select(identity.Handle);
            Assert.False(OutlineVisible());
            Assert.Empty(close.ConfirmClosed());
            Select(IntPtr.Zero);
            Select(identity.Handle);
            Assert.True(OutlineVisible());
            Assert.Equal(Visibility.Visible, thumb.TitleLabel.Visibility);
        });

    [Fact]
    public void UnchangedMinimizedSourcesCanBeRetainedButReusedOrRemovedSourcesCannot()
        => RunSta(() =>
        {
            var identity = new WindowActivationTarget(new(42), 1, 2);
            var window = new WindowInfo { Handle = identity.Handle, Identity = identity, Title = "Synthetic document", WasMinimized = true };
            var strip = new MinimizedWindowStrip([window], 900, 700, 1, _ => { }, _ => { }, _ => null);
            var thumb = Assert.Single(strip.Thumbs);
            Assert.True(strip.CanKeepWindows([window]));
            window.Identity = new(identity.Handle, 9, 2);
            Assert.False(strip.CanKeepWindows([window]));
            Assert.False(strip.CanKeepWindows([]));
            Assert.Same(thumb, Assert.Single(strip.Thumbs));
        });

    [Fact]
    public void RejectedCloseLeavesTheSelectedOutlineVisible()
        => RunSta(() =>
        {
            var identity = new WindowActivationTarget(new(42), 1, 2);
            var thumb = new WindowThumb
            {
                WindowHandle = identity.Handle, Identity = identity, ClickBorder = new OverviewThumbnail("Synthetic document"),
                HighlightBorder = new Border(), TitleLabel = new TextBlock()
            };
            var manager = new ThumbnailManager(new Canvas(), [thumb], IntPtr.Zero, _ => { }, _ => { });
            manager.SetSelectedWindow(identity.Handle);
            var close = new WindowClose(_ => identity, _ => (false, 5));
            Assert.Throws<System.ComponentModel.Win32Exception>(() =>
            {
                close.Request(identity);
                thumb.SuppressSelectionOutlineForClose();
            });
            Assert.False(close.HasPending);
            Assert.Equal(Visibility.Visible, thumb.HighlightBorder.Visibility);
            Assert.Equal(1, thumb.HighlightBorder.Opacity);
        });

    [Fact]
    public void SelectionAndFilterCannotRevealAnOutlineDuringTransitionAndSettlementUsesFinalBounds()
        => RunSta(() =>
        {
            var thumb = new WindowThumb
            {
                WindowHandle = new(42), Title = "Synthetic document",
                ClickBorder = new OverviewThumbnail("Synthetic document"), HighlightBorder = new Border(), TitleLabel = new TextBlock(),
                CurrentX = 40, CurrentY = 60, CurrentWidth = 300, CurrentHeight = 200
            };
            var manager = new ThumbnailManager(new Canvas(), [thumb], IntPtr.Zero, _ => { }, _ => { });
            manager.SetSelectedWindow(thumb.WindowHandle);
            manager.BeginTransition(() =>
            {
                manager.SetSelectedWindow(thumb.WindowHandle);
                manager.SetAutomationSelection(thumb.WindowHandle);
                manager.ApplyTitleFilter("");
                manager.ApplyPalette(true, false);
                manager.ShowLabels(true);
                Assert.Equal(Visibility.Hidden, thumb.HighlightBorder.Visibility);
                Assert.Equal(0, thumb.HighlightBorder.Opacity);
            });
            thumb.CurrentX = 120;
            thumb.CurrentY = 80;
            manager.SyncClickBordersToThumbs(2);
            Assert.Equal(Visibility.Hidden, thumb.HighlightBorder.Visibility);
            manager.CompleteTransition(2, thumb.WindowHandle);
            Assert.Equal((55d, 35d, 160d, 110d),
                (Canvas.GetLeft(thumb.HighlightBorder), Canvas.GetTop(thumb.HighlightBorder), thumb.HighlightBorder.Width, thumb.HighlightBorder.Height));
            Assert.Equal(Visibility.Visible, thumb.HighlightBorder.Visibility);
            Assert.Equal(1, thumb.HighlightBorder.Opacity);
        });

    [Fact]
    public void LiveRefreshRegistersOnlyTheNewSourceAndKeepsTheSurvivorsSlotAndSearchLabels()
        => RunSta(() =>
        {
            var identity = new WindowActivationTarget(new(42), 1, 2);
            var thumb = new WindowThumb
            {
                WindowHandle = identity.Handle, Identity = identity, Title = "Synthetic document", ThumbnailHandle = new(701),
                ClickBorder = new OverviewThumbnail("Synthetic document"), TitleLabel = new TextBlock(), HighlightBorder = new Border(),
                StartX = 100, StartY = 200, StartWidth = 1000, StartHeight = 800,
                CurrentX = 50, CurrentY = 60, CurrentWidth = 500, CurrentHeight = 400,
                TargetX = 50, TargetY = 60, TargetWidth = 500, TargetHeight = 400
            };
            OverviewCloseInput.Attach(thumb, thumb.Title, () => true, () => { });
            Assert.True(thumb.MatchesTitle("synthetic"));
            var survivor = new WindowInfo
            {
                Handle = identity.Handle, Identity = identity, Title = "Updated document",
                OriginalRect = new() { Left = 120, Top = 200, Right = 1120, Bottom = 1000 }
            };
            var added = new WindowInfo { Handle = new(43), Identity = new(new(43), 1, 2), Title = "New window" };
            var thumbs = new List<WindowThumb> { thumb };
            var registrations = new List<IntPtr>();
            var manager = new ThumbnailManager(new Canvas(), thumbs, IntPtr.Zero, _ => { }, _ => { },
                register: handle => { registrations.Add(handle); return (-1, IntPtr.Zero); });
            manager.RegisterThumbnails([
                new() { Window = survivor, X = 50, Y = 60, Width = 500, Height = 400 },
                new() { Window = added, X = 600, Y = 60, Width = 200, Height = 400 }
            ], new WindowAnimationManager(thumbs), settled: true, foldedQuery: "updated", retainExisting: true);

            Assert.Equal(added.Handle, Assert.Single(registrations));
            Assert.Same(thumb, Assert.Single(thumbs));
            Assert.Equal(new IntPtr(701), thumb.ThumbnailHandle);
            Assert.Equal((50d, 60d, 500d, 400d), (thumb.CurrentX, thumb.CurrentY, thumb.CurrentWidth, thumb.CurrentHeight));
            Assert.Equal(120d, thumb.StartX);
            Assert.False(thumb.MatchesTitle("synthetic"));
            Assert.Equal(survivor.Title, AutomationProperties.GetName(thumb.ClickBorder));
            Assert.Equal("Close Updated document", AutomationProperties.GetName(thumb.CloseButton!));
            Assert.Equal(Visibility.Visible, thumb.ClickBorder.Visibility);
        });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RequestedOrExternalCloseRetiresOnlyDeadSourceBeforeLayout(bool reusedHandle, bool requested)
        => RunSta(() =>
        {
            var closedIdentity = new WindowActivationTarget(new(42), 1, 2);
            var pendingIdentity = new WindowActivationTarget(new(43), 1, 2);
            var identities = new Dictionary<IntPtr, WindowActivationTarget>
            { [closedIdentity.Handle] = closedIdentity, [pendingIdentity.Handle] = pendingIdentity };
            var close = new WindowClose(handle => identities.GetValueOrDefault(handle), _ => (true, 0));
            WindowThumb Thumb(WindowActivationTarget identity)
            {
                var thumb = new WindowThumb
                {
                    WindowHandle = identity.Handle, Identity = identity, Title = "Synthetic document",
                    ClickBorder = new OverviewThumbnail("Synthetic document"), HighlightBorder = new Border(),
                    TitleLabel = new TextBlock()
                };
                OverviewCloseInput.Attach(thumb, thumb.Title, () => false, () => { });
                return thumb;
            }
            var dead = Thumb(closedIdentity);
            var pending = Thumb(pendingIdentity);
            dead.ThumbnailHandle = new(701);
            pending.ThumbnailHandle = new(702);
            var released = new List<IntPtr>();
            int Release(IntPtr thumbnail) { released.Add(thumbnail); return 0; }
            var thumbs = new List<WindowThumb> { dead, pending };
            var manager = new ThumbnailManager(new Canvas(), thumbs, IntPtr.Zero, _ => { }, _ => { });
            if (requested) close.Request(closedIdentity);
            close.Request(pendingIdentity);
            manager.SetSelectedWindow(closedIdentity.Handle);
            manager.HideClosedSources([closedIdentity, pendingIdentity], close.IsClosed, Release);
            Assert.Empty(released);
            Assert.Equal(Visibility.Visible, dead.HighlightBorder.Visibility);
            if (reusedHandle) identities[closedIdentity.Handle] = new(closedIdentity.Handle, 9, 2);
            else identities.Remove(closedIdentity.Handle);
            var confirmed = close.ConfirmClosed();
            if (requested) Assert.Equal(closedIdentity, Assert.Single(confirmed));
            else Assert.Empty(confirmed);
            var closed = thumbs.Select(t => t.Identity).Where(close.IsClosed).ToHashSet();
            Assert.Equal(closedIdentity, Assert.Single(closed));
            manager.HideClosedSources(closed, close.IsClosed, Release);
            manager.HideClosedSources(closed, close.IsClosed, Release);
            Assert.Equal(new IntPtr(701), Assert.Single(released));
            Assert.Equal(IntPtr.Zero, dead.ThumbnailHandle);
            Assert.Equal(new IntPtr(702), pending.ThumbnailHandle);
            Assert.Same(pending, Assert.Single(thumbs));
            Assert.True(close.IsPending(pendingIdentity));
            manager.SetSelectedWindow(closedIdentity.Handle);
            manager.ApplyTitleFilter("");
            manager.CompleteTransition(2, closedIdentity.Handle);
            Assert.Equal(Visibility.Hidden, dead.ClickBorder.Visibility);
            Assert.Equal(Visibility.Hidden, dead.HighlightBorder.Visibility);
            Assert.Equal(Visibility.Hidden, dead.TitleLabel.Visibility);
            Assert.False(dead.CloseButton!.IsEnabled);
            Assert.False(dead.MatchesTitle(""));
            Assert.Equal(Visibility.Visible, pending.ClickBorder.Visibility);
            Assert.Equal(Visibility.Visible, pending.HighlightBorder.Visibility);
        });

    [Theory]
    [InlineData(unchecked((int)0x80070057), true)]
    [InlineData(unchecked((int)0x80004005), false)]
    public void ConfirmedCloseHandlesOnlyAnAlreadyAbsentDwmRelationAsReleased(int result, bool absent)
        => RunSta(() =>
        {
            var identity = new WindowActivationTarget(new(42), 1, 2);
            var thumb = new WindowThumb
            {
                WindowHandle = identity.Handle, Identity = identity, ThumbnailHandle = new(701),
                ClickBorder = new OverviewThumbnail("Synthetic document"),
                HighlightBorder = new Border(), TitleLabel = new TextBlock()
            };
            var manager = new ThumbnailManager(new Canvas(), [thumb], IntPtr.Zero, _ => { }, _ => { });
            if (absent)
            {
                manager.HideClosedSources([identity], _ => true, _ => result);
                Assert.Equal(IntPtr.Zero, thumb.ThumbnailHandle);
            }
            else
            {
                var error = Assert.Throws<InvalidOperationException>(() =>
                    manager.HideClosedSources([identity], _ => true, _ => result));
                Assert.Contains("DWM", error.Message);
                Assert.Equal(new IntPtr(701), thumb.ThumbnailHandle);
            }
            Assert.Equal(Visibility.Hidden, thumb.HighlightBorder.Visibility);
        });

    [Fact]
    public void FilteringShelfRevokesQueuedCloseAndRestoresTheSamePeerWhenCleared()
        => RunSta(() =>
        {
            var window = new WindowInfo { Handle = new(42), Title = "Draft", WasMinimized = true };
            int requested = 0;
            var strip = new MinimizedWindowStrip([window], 900, 700, 1, _ => { }, _ => { }, _ => null);
            var thumb = Assert.Single(strip.Thumbs);
            OverviewCloseInput.Attach(thumb, window.Title, () => true, () => requested++);
            var list = UIElementAutomationPeer.CreatePeerForElement(strip)!;
            var close = list.GetChildren().Single(peer => peer.GetName() == "Close Draft");
            var invoke = Assert.IsAssignableFrom<IInvokeProvider>(close.GetPattern(PatternInterface.Invoke));
            invoke.Invoke();

            strip.ApplyTitleFilter("other");
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Empty(list.GetChildren());
            Assert.Throws<ElementNotEnabledException>(invoke.Invoke);
            Assert.Equal(0, requested);

            strip.ApplyTitleFilter("");
            Assert.Contains(close, list.GetChildren());
            invoke.Invoke();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, requested);
        });

    [Fact]
    public void RebuiltCloseControlStaysHiddenUntilThumbnailTargetsAreSynchronized()
        => RunSta(() =>
        {
            var target = new OverviewThumbnail("Document") { IsHitTestVisible = false };
            var thumb = new WindowThumb
            {
                WindowHandle = new(42), ClickBorder = target, HighlightBorder = new Border(),
                TitleLabel = new TextBlock(), CurrentX = 120, CurrentY = 80, CurrentWidth = 300, CurrentHeight = 200
            };
            OverviewCloseInput.Attach(thumb, "Document", () => false, () => { });
            var manager = new ThumbnailManager(new Canvas(), [thumb], IntPtr.Zero, _ => { }, _ => { });
            manager.SetSelectedWindow(thumb.WindowHandle);
            Assert.Equal(Visibility.Hidden, thumb.CloseButton!.Visibility);
            manager.SyncClickBordersToThumbs(2);
            Assert.Equal(Visibility.Hidden, thumb.CloseButton.Visibility);
            manager.CompleteTransition(2, thumb.WindowHandle);
            Assert.Equal((60d, 40d, 150d, 100d),
                (Canvas.GetLeft(target), Canvas.GetTop(target), target.Width, target.Height));
            Assert.Equal(Visibility.Visible, thumb.CloseButton.Visibility);
            manager.BeginTransition(() => Assert.Equal(Visibility.Hidden, thumb.CloseButton.Visibility));
            manager.ApplyPalette(true, false);
            manager.SetAutomationSelection(thumb.WindowHandle);
            Assert.Equal(Visibility.Hidden, thumb.CloseButton.Visibility);
        });

    [Fact]
    public void PendingCloseRemainsDisabledWhenThumbnailTransitionEnds()
        => RunSta(() =>
        {
            var thumb = new WindowThumb { ClickBorder = new OverviewThumbnail("Document") };
            int requested = 0;
            OverviewCloseInput.Attach(thumb, "Document", () => true, () => requested++);
            var close = thumb.CloseButton!;
            var peer = UIElementAutomationPeer.CreatePeerForElement(close)!;
            var invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
            var manager = new ThumbnailManager(new Canvas(), [thumb], IntPtr.Zero, _ => { }, _ => { });
            close.IsEnabled = false;

            manager.BeginTransition(() => manager.SetPreviewAnimating(true));
            manager.SetPreviewAnimating(false);

            Assert.Equal(Visibility.Visible, close.Visibility);
            Assert.False(close.IsEnabled);
            Assert.Throws<ElementNotEnabledException>(invoke.Invoke);
            foreach (var routedEvent in new[] { Mouse.PreviewMouseDownEvent, Mouse.PreviewMouseUpEvent })
                thumb.ClickBorder.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Middle)
                    { RoutedEvent = routedEvent });
            Assert.Equal(0, requested);
        });

    [Fact]
    public void SyntheticMinimizedClosePeerUsesItsOwnIdentityWithoutRestoringTheTile()
        => RunSta(() =>
        {
            var window = new WindowInfo { Handle = new(42), Identity = new(new(42), 1, 2), Title = "Minimized document", WasMinimized = true };
            int restored = 0;
            bool pending = false;
            var requested = new List<WindowActivationTarget>();
            var strip = new MinimizedWindowStrip([window], 900, 700, 1, _ => restored++, _ => { }, _ => null);
            var thumb = Assert.Single(strip.Thumbs);
            OverviewCloseInput.Attach(thumb, window.Title, () => !pending, () =>
            {
                requested.Add(thumb.Identity);
                pending = true;
            });
            strip.SetSelected(window.Handle);
            Assert.True(thumb.CloseButton!.Selected);
            Assert.Equal(window.Title, thumb.TitleLabel.Text);
            var content = ((Grid)thumb.ClickBorder.Child).Children.OfType<MinimizedWindowButton>().Single();
            Assert.Equal(34, content.Margin.Left);
            var peer = UIElementAutomationPeer.CreatePeerForElement(strip)!;
            var children = peer.GetChildren();
            Assert.Equal(2, children.Count);
            var close = children.Single(child => child.GetName() == "Close Minimized document");
            Assert.IsAssignableFrom<IInvokeProvider>(close.GetPattern(PatternInterface.Invoke)).Invoke();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(window.Identity, Assert.Single(requested));
            Assert.Equal(0, restored);
        });

    [Theory]
    [InlineData(false, 1d)]
    [InlineData(true, 1d)]
    [InlineData(false, 2d)]
    public void SyntheticCloseRenderKeepsTheCircleInsideThePreviewAtEachDpi(bool highContrast, double dpi)
        => RunSta(() =>
        {
            var canvas = new Canvas { Width = 720, Height = 270, Background = SharedBrushes.Graphite };
            var caption = new TextBlock
            {
                Text = $"Synthetic WPF controls | {(highContrast ? "High contrast" : "Standard")} | {dpi * 100:0}%",
                Foreground = SharedBrushes.Text, FontSize = 16
            };
            Canvas.SetLeft(caption, 24); Canvas.SetTop(caption, 14);
            canvas.Children.Add(caption);
            foreach (int index in new[] { 0, 1 })
            {
                var preview = new OverviewThumbnail($"Synthetic document {index + 1}")
                {
                    Width = 310, Height = 160, Background = SharedBrushes.Raised
                };
                var title = new TextBlock { Text = $"Synthetic document {index + 1}", Foreground = SharedBrushes.Text, FontSize = 13 };
                Canvas.SetLeft(title, 24 + index * 350); Canvas.SetTop(title, 226);
                var thumb = new WindowThumb { ClickBorder = preview, TitleLabel = title };
                OverviewCloseInput.Attach(thumb, title.Text, () => true, () => { });
                thumb.CloseButton!.Selected = index == 0;
                thumb.CloseButton.ApplyPalette(highContrast);
                Canvas.SetLeft(preview, 24 + index * 350); Canvas.SetTop(preview, 54);
                canvas.Children.Add(preview); canvas.Children.Add(title);
            }
            canvas.Measure(new Size(canvas.Width, canvas.Height));
            canvas.Arrange(new Rect(0, 0, canvas.Width, canvas.Height));
            canvas.UpdateLayout();
            var selected = canvas.Children.OfType<OverviewThumbnail>().First();
            var close = ((Grid)selected.Child).Children.OfType<OverviewCloseButton>().Single();
            var position = close.TranslatePoint(new Point(), selected);
            Assert.True(position.X >= 0 && position.Y >= 0);
            Assert.True(position.X + close.ActualWidth <= selected.ActualWidth);
            Assert.True(position.Y + close.ActualHeight <= selected.ActualHeight);
            var image = new RenderTargetBitmap((int)(canvas.Width * dpi), (int)(canvas.Height * dpi),
                96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
            image.Render(canvas);
            Assert.Equal((int)(canvas.Width * dpi), image.PixelWidth);
            Assert.Equal((int)(canvas.Height * dpi), image.PixelHeight);
            string? output = Environment.GetEnvironmentVariable("MISSIONVIEW_CLOSE_RENDER_DIR");
            if (output != null)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = System.IO.File.Create(System.IO.Path.Combine(output,
                    $"synthetic-close-{(highContrast ? "hc" : "standard")}-{dpi:0}x.png"));
                encoder.Save(stream);
            }
        });

    [Fact]
    public void MiddleAndCloseButtonInvokeTheSameTargetOnceWithoutBubblingActivation()
        => RunSta(() =>
        {
            var target = new OverviewThumbnail("Document");
            var thumb = new WindowThumb { WindowHandle = new(42), ClickBorder = target };
            var requested = new List<IntPtr>();
            bool available = true;
            int activated = 0;
            target.MouseLeftButtonDown += (_, _) => activated++;
            target.MouseLeftButtonUp += (_, _) => activated++;
            target.AddHandler(Button.ClickEvent, new RoutedEventHandler((_, _) => activated++));
            OverviewCloseInput.Attach(thumb, "Document", () => available, () =>
            {
                requested.Add(thumb.WindowHandle);
                available = false;
            });
            void Middle(bool down) => target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Middle)
                { RoutedEvent = down ? Mouse.PreviewMouseDownEvent : Mouse.PreviewMouseUpEvent });
            Middle(true);
            Assert.Empty(requested);
            Middle(false);
            Middle(false);
            Assert.Equal(thumb.WindowHandle, Assert.Single(requested));
            Assert.Equal(0, activated);
            available = true;
            var peer = UIElementAutomationPeer.CreatePeerForElement(thumb.CloseButton!)!;
            var invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
            invoke.Invoke();
            invoke.Invoke();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(new[] { thumb.WindowHandle, thumb.WindowHandle }, requested);
            Assert.Equal(0, activated);
            Assert.False(peer.IsEnabled());
            Assert.Throws<ElementNotEnabledException>(invoke.Invoke);
        });

    [Fact]
    public void MiddleReleaseAfterTransitionOrLeavingAndQueuedStaleCloseCannotAct()
        => RunSta(() =>
        {
            var target = new OverviewThumbnail("Document");
            var thumb = new WindowThumb { WindowHandle = new(42), ClickBorder = target };
            bool available = true;
            OverviewCloseInput.Attach(thumb, "Document", () => available,
                () => throw new Exception("A canceled gesture must not close."));
            void Middle(bool down) => target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Middle)
                { RoutedEvent = down ? Mouse.PreviewMouseDownEvent : Mouse.PreviewMouseUpEvent });
            Middle(true);
            available = false;
            Middle(false);
            available = true;
            Middle(true);
            target.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
            Middle(false);
            Middle(true);
            target.IsHitTestVisible = false;
            target.IsHitTestVisible = true;
            Middle(false);
            var peer = UIElementAutomationPeer.CreatePeerForElement(thumb.CloseButton!)!;
            Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
            available = false;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.False(peer.IsEnabled());
        });

    [Fact]
    public void CloseIsAnAccessibleSelectedCircularControlWithSystemColorsAndNoGeometryChange()
        => RunSta(() =>
        {
            var target = new OverviewThumbnail("Full document title") { Width = 300, Height = 200 };
            var label = new TextBlock { Text = "Full document title", Width = 300 };
            var thumb = new WindowThumb { WindowHandle = new(42), ClickBorder = target, TitleLabel = label };
            OverviewCloseInput.Attach(thumb, label.Text, () => true, () => { });
            var button = thumb.CloseButton!;
            Assert.Equal(0, button.Opacity);
            Assert.False(button.IsHitTestVisible);
            button.Selected = true;
            Assert.Equal(1, button.Opacity);
            Assert.True(button.IsHitTestVisible);
            Assert.True(button.Focusable);
            Assert.Equal((28d, 28d), (button.Width, button.Height));
            Assert.Equal(HorizontalAlignment.Left, button.HorizontalAlignment);
            var peer = UIElementAutomationPeer.CreatePeerForElement(target)!;
            var close = Assert.Single(peer.GetChildren());
            Assert.Equal("Close Full document title", close.GetName());
            Assert.NotNull(close.GetPattern(PatternInterface.Invoke));
            button.ApplyPalette(true);
            Assert.Same(SystemColors.WindowBrush, button.Background);
            Assert.Same(SystemColors.WindowTextBrush, button.Foreground);
            Assert.Same(SystemColors.HighlightBrush, button.BorderBrush);
            Assert.Null(button.Effect);
            button.ApplyPalette(false);
            Assert.Same(SharedBrushes.Text, button.Background);
            Assert.Same(SharedBrushes.Graphite, button.Foreground);
            Assert.Same(Brushes.White, button.Resources["CloseHover"]);
            Assert.Equal((300d, 200d, 300d), (target.Width, target.Height, label.Width));
            Assert.Null(button.Effect);
        });

    [Fact]
    public void SelectionAndSystemPalettePreserveLabelGeometryAndAccessibleTitleWithoutThumbnailRegistration()
        => RunSta(() =>
        {
            const string title = "Full accessible title for the selected window";
            var target = new OverviewThumbnail(title);
            var frame = new Border { Width = 310, Height = 210, Child = new Border() };
            var label = new TextBlock { Text = title, Width = 300, Height = 20, FontSize = 13 };
            var thumb = new WindowThumb { WindowHandle = new(1), ClickBorder = target, HighlightBorder = frame, TitleLabel = label };
            var manager = new ThumbnailManager(new Canvas(), [thumb], IntPtr.Zero, _ => { }, _ => { });
            manager.ApplyPalette(false, false);
            Assert.Equal(Colors.White, ((SolidColorBrush)frame.BorderBrush).Color);
            Assert.Null(label.Background);
            Assert.Equal(Colors.White, ((SolidColorBrush)label.Foreground).Color);
            Assert.Null(label.Effect);
            label.Measure(new Size(300, 20));
            label.Arrange(new Rect(0, 0, 300, 20));
            var bounds = label.RenderSize;
            manager.SetSelectedWindow(new(1));
            label.UpdateLayout();
            Assert.Equal(bounds, label.RenderSize);
            Assert.Equal(13, label.FontSize);
            Assert.Equal(1, frame.Opacity);
            Assert.Null(frame.Effect);
            Assert.Null(label.Effect);
            manager.ApplyPalette(true, true);
            Assert.Equal(SystemColors.HighlightColor, ((SolidColorBrush)frame.BorderBrush).Color);
            Assert.Equal(SystemColors.WindowTextColor, ((SolidColorBrush)label.Foreground).Color);
            Assert.Equal(SystemColors.WindowColor, Assert.IsType<SolidColorBrush>(label.Background).Color);
            Assert.Null(frame.Effect);
            manager.SetSelectedWindow(IntPtr.Zero);
            label.UpdateLayout();
            Assert.Equal(bounds, label.RenderSize);
            Assert.Equal(0, frame.Opacity);
            Assert.Equal(title, UIElementAutomationPeer.CreatePeerForElement(target)!.GetName());
            manager.ApplyPalette(false, true);
            Assert.Null(label.Background);
            Assert.Equal(Colors.White, ((SolidColorBrush)label.Foreground).Color);
            Assert.IsType<DropShadowEffect>(label.Effect);
            label.UpdateLayout();
            Assert.Equal(bounds, label.RenderSize);
            manager.ApplyPalette(false, false);
            Assert.Null(label.Effect);
        });

    [Theory]
    [InlineData("White", 255, 255, 255)]
    [InlineData("Blue", 0, 120, 215)]
    [InlineData("Mint", 125, 219, 155)]
    public void SelectionColorSurvivesHighContrastAndReducedMotionWithoutChangingTargets(string color, byte r, byte g, byte b)
        => RunSta(() =>
        {
            var settings = new AppSettings { SelectionBorderColor = color };
            var target = new OverviewThumbnail("Complete window title");
            var label = new TextBlock { Text = "Complete window title", Width = 300, Height = 20, FontSize = 13 };
            var frame = new Border { Width = 310, Height = 210, Child = new Border() };
            var thumb = new WindowThumb { WindowHandle = new(1), ClickBorder = target, TitleLabel = label, HighlightBorder = frame,
                CurrentX = 20, CurrentY = 40, CurrentWidth = 600, CurrentHeight = 400 };
            var manager = new ThumbnailManager(new Canvas(), [thumb], IntPtr.Zero, _ => { }, _ => { },
                selectionBorderColor: () => settings.SelectionBorderColor, effectsEnabled: () => !settings.ReduceAnimations);
            manager.SyncClickBordersToThumbs(2);
            manager.SetSelectedWindow(new(1));
            manager.SetAutomationSelection(new(1));
            manager.ApplyPalette(false, true);
            Assert.Equal(Color.FromRgb(r, g, b), ((SolidColorBrush)frame.BorderBrush).Color);
            Assert.Equal(1, frame.Opacity);
            manager.ApplyPalette(true, true);
            Assert.Same(SystemColors.HighlightBrush, frame.BorderBrush);
            Assert.Same(SystemColors.WindowBrush, label.Background);
            settings.ReduceAnimations = true;
            manager.ApplyPalette(false, true);
            Assert.Equal(Color.FromRgb(r, g, b), ((SolidColorBrush)frame.BorderBrush).Color);
            Assert.Null(frame.Effect);
            Assert.Null(label.Effect);
            Assert.Null(label.Background);
            Assert.Equal((300d, 200d, 310d, 210d, 13d), (target.Width, target.Height, frame.Width, frame.Height, label.FontSize));
            var peer = UIElementAutomationPeer.CreatePeerForElement(target)!;
            Assert.Equal("Complete window title", peer.GetName());
            Assert.True(Assert.IsAssignableFrom<ISelectionItemProvider>(peer.GetPattern(PatternInterface.SelectionItem)).IsSelected);
            settings.SelectionBorderColor = "Blue";
            manager.ApplyPalette(false, false);
            Assert.Equal(Color.FromRgb(0, 120, 215), ((SolidColorBrush)frame.BorderBrush).Color);
        });

    [Fact]
    public void NormalAndMinimizedCloseHoverFollowTheLiveSelectionBorder()
        => RunSta(() =>
        {
            string color = "Blue";
            var frame = new Border();
            var thumb = new WindowThumb { ClickBorder = new OverviewThumbnail("Document"),
                TitleLabel = new TextBlock(), HighlightBorder = frame };
            OverviewCloseInput.Attach(thumb, "Document", () => true, () => { });
            var manager = new ThumbnailManager(new Canvas(), [thumb], IntPtr.Zero, _ => { }, _ => { },
                selectionBorderColor: () => color);
            var shelf = new MinimizedWindowStrip([new WindowInfo { Handle = new(1), Title = "Minimized" }],
                1600, 900, 1, _ => { }, _ => { }, _ => null, selectionBorderColor: () => color);
            var shelfThumb = Assert.Single(shelf.Thumbs);
            OverviewCloseInput.Attach(shelfThumb, "Minimized", () => true, () => { });

            void Check(bool highContrast, Brush expected)
            {
                manager.ApplyPalette(highContrast, false);
                shelf.ApplyPalette(highContrast);
                Assert.Same(expected, frame.BorderBrush);
                Assert.Same(frame.BorderBrush, thumb.CloseButton!.Resources["CloseHover"]);
                Assert.Same(frame.BorderBrush, shelfThumb.CloseButton!.Resources["CloseHover"]);
            }

            Check(false, SharedBrushes.Blue);
            color = "White";
            Check(false, Brushes.White);
            Check(true, SystemColors.HighlightBrush);
        });

    [Fact]
    public void ProductionRegistrationPublishesFullTitleAndActionablePeerWithoutChangingVisualTitle()
        => RunSta(() =>
        {
            using var app = new MainWindow();
            string title = "Full title " + new string('x', 118);
            var thumb = Assert.Single(app.RegisterNormalWindows(title));
            Assert.Equal(129, title.Length);
            Assert.Equal(80, thumb.TitleLabel.Text.Length);
            if (!SystemParameters.HighContrast)
            {
                Assert.Null(thumb.TitleLabel.Background);
                Assert.Equal(new Thickness(0), thumb.TitleLabel.Padding);
                Assert.Equal(Colors.White, ((SolidColorBrush)thumb.TitleLabel.Foreground).Color);
            }
            Assert.Equal(title, AutomationProperties.GetName(thumb.ClickBorder));
            var peer = UIElementAutomationPeer.CreatePeerForElement(thumb.ClickBorder);
            Assert.NotNull(peer);
            Assert.Equal(title, peer.GetName());
            Assert.NotNull(peer.GetPattern(PatternInterface.Invoke));
            Assert.NotNull(peer.GetPattern(PatternInterface.SelectionItem));
            Assert.False(thumb.ClickBorder.Focusable);
            Assert.Null(peer.GetChildren());
        });

    [Fact]
    public void SelectionUsesActualNavigationAcrossNormalMinimizedAndOtherDisplayTargets()
        => RunSta(() =>
        {
            using var app = new MainWindow();
            var thumbs = app.RegisterNormalWindows("First", "Second");
            Assert.NotEqual(IntPtr.Zero, SendMessage(new WindowInteropHelper(app).Handle, 0x003D,
                IntPtr.Zero, new IntPtr(AutomationInteropProvider.RootObjectId)));
            var root = UIElementAutomationPeer.CreatePeerForElement(app);
            Assert.Contains(root.GetChildren(), peer => peer is FrameworkElementAutomationPeer element &&
                ReferenceEquals(element.Owner, app.ThumbnailCanvas));
            var peers = thumbs.Select(t => UIElementAutomationPeer.CreatePeerForElement(t.ClickBorder)).ToArray();
            var first = Assert.IsAssignableFrom<ISelectionItemProvider>(peers[0].GetPattern(PatternInterface.SelectionItem));
            var second = Assert.IsAssignableFrom<ISelectionItemProvider>(peers[1].GetPattern(PatternInterface.SelectionItem));
            var listPeer = UIElementAutomationPeer.CreatePeerForElement(app.ThumbnailCanvas);
            var list = Assert.IsAssignableFrom<ISelectionProvider>(listPeer.GetPattern(PatternInterface.Selection));
            Assert.False(list.CanSelectMultiple);
            Assert.False(list.IsSelectionRequired);
            Assert.Equal(new[] { "First", "Second" }, listPeer.GetChildren().Select(p => p.GetName()));
            Assert.NotNull(first.SelectionContainer);
            Assert.NotNull(second.SelectionContainer);
            app.Pick(thumbs[0].WindowHandle);
            Assert.True(first.IsSelected);
            Assert.False(second.IsSelected);
            Assert.Single(list.GetSelection());
            Assert.All(list.GetSelection(), Assert.NotNull);
            app.Arrow(1, 0);
            Assert.Equal(thumbs[1].WindowHandle, app.Selected);
            Assert.False(first.IsSelected);
            Assert.True(second.IsSelected);
            app.HideNormalSelectionOutline();
            Assert.True(second.IsSelected);
            Assert.Single(list.GetSelection());
            first.RemoveFromSelection();
            Assert.True(second.IsSelected);
            first.AddToSelection();
            Assert.Equal(thumbs[0].WindowHandle, app.Selected);
            Assert.True(first.IsSelected);
            Assert.False(second.IsSelected);
            second.Select();
            app.AddStrip(1, 1600, 1000);
            app.Tab(1);
            Assert.Equal(new IntPtr(10), app.Selected);
            Assert.False(first.IsSelected);
            Assert.False(second.IsSelected);
            Assert.Empty(list.GetSelection());
            app.Tab(-1);
            Assert.True(second.IsSelected);
            second.RemoveFromSelection();
            Assert.Equal(IntPtr.Zero, app.Selected);
            Assert.Empty(list.GetSelection());
            var other = app.AddView(-1800, -200, 1600, 1000, 1.5);
            var remote = Assert.Single(other.RegisterNormalWindows("Other display"));
            var remotePeer = UIElementAutomationPeer.CreatePeerForElement(remote.ClickBorder);
            var remoteSelection = Assert.IsAssignableFrom<ISelectionItemProvider>(remotePeer.GetPattern(PatternInterface.SelectionItem));
            remoteSelection.Select();
            Assert.Equal(remote.WindowHandle, app.Selected);
            Assert.True(remoteSelection.IsSelected);
            Assert.Empty(list.GetSelection());
            first.Select();
            Assert.True(first.IsSelected);
            Assert.False(remoteSelection.IsSelected);
            Assert.Single(list.GetSelection());
            Assert.Equal(0, app.ActivationRequests);
            Assert.Equal(0, other.ActivationRequests);
        });

    [Fact]
    public void EmptyProductionSurfaceHasNoAccessibleTargetsOrSelection()
        => RunSta(() =>
        {
            using var app = new MainWindow();
            Assert.Empty(app.RegisterNormalWindows());
            var peer = UIElementAutomationPeer.CreatePeerForElement(app.ThumbnailCanvas);
            Assert.Empty(peer.GetChildren());
            Assert.Empty(Assert.IsAssignableFrom<ISelectionProvider>(peer.GetPattern(PatternInterface.Selection)).GetSelection());
            app.Arrow(1, 0);
            Assert.Equal(IntPtr.Zero, app.Selected);
            Assert.Equal(0, app.ActivationRequests);
        });

    [Fact]
    public void TwoQueuedInvocationsUseExistingActivationBoundaryOnlyOnce()
        => RunSta(() =>
        {
            using var app = new MainWindow();
            var thumbs = app.RegisterNormalWindows("Previously selected", "Invoke this target");
            app.Pick(thumbs[0].WindowHandle);
            var peer = UIElementAutomationPeer.CreatePeerForElement(thumbs[1].ClickBorder);
            var invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
            invoke.Invoke();
            invoke.Invoke();
            Assert.Equal(0, app.ActivationRequests);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(thumbs[1].WindowHandle, app.Clicked);
            Assert.Equal(1, app.ActivationRequests);
            Assert.False(peer.IsEnabled());
            Assert.Throws<ElementNotEnabledException>(invoke.Invoke);
            Assert.Equal(1, app.ActivationRequests);
        });

    [Fact]
    public void QueuedOrStalePeersCannotActAfterCancellationRestartOrRemoval()
        => RunSta(() =>
        {
            using var app = new MainWindow();
            var thumb = Assert.Single(app.RegisterNormalWindows("Canceled target"));
            var peer = UIElementAutomationPeer.CreatePeerForElement(thumb.ClickBorder);
            var invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
            var selection = Assert.IsAssignableFrom<ISelectionItemProvider>(peer.GetPattern(PatternInterface.SelectionItem));
            invoke.Invoke();
            app.CancelSession();
            app.RestartSession();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(0, app.ActivationRequests);
            Assert.Equal(IntPtr.Zero, app.Clicked);
            Assert.False(peer.IsEnabled());
            Assert.Throws<ElementNotEnabledException>(invoke.Invoke);
            Assert.Throws<ElementNotEnabledException>(selection.Select);
            app.RebindThumbnailAutomation(thumb);
            Assert.True(peer.IsEnabled());
            app.RemoveThumb(thumb.WindowHandle);
            Assert.False(peer.IsEnabled());
            Assert.Throws<ElementNotEnabledException>(invoke.Invoke);
            Assert.Throws<ElementNotEnabledException>(selection.Select);
            Assert.Equal(0, app.ActivationRequests);
        });

    private static void RunSta(Action test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
