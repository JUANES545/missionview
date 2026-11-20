using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.IO;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;
using WindowScatter;
using Xunit;

public class MinimizedShelfTests
{
    [Fact]
    public void OneItemShelfUsesOnlyItsApprovedFootprint()
        => RunSta(() =>
        {
            var strip = new MinimizedWindowStrip([Info(1)], 1920, 1080, 1, _ => { }, _ => { }, _ => null);
            Arrange(strip, 1920, 1080);
            Assert.Equal(264, strip.Width);
            Assert.Equal(88, strip.Height);
            Assert.Equal(Rounded(264, strip), strip.ActualWidth);
            Assert.Equal(Rounded(88, strip), strip.ActualHeight);
            Assert.Equal(828, Canvas.GetLeft(strip));
            Assert.Equal(980, Canvas.GetTop(strip));
            var tile = strip.Thumbs[0].ClickBorder;
            Assert.Equal(240, tile.Width);
            Assert.Equal(46, tile.Height);
            Assert.Equal(Rounded(240, tile), tile.ActualWidth);
            Assert.Equal(Rounded(46, tile), tile.ActualHeight);
        });

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(20)]
    public void ShelfAndPublishedBoundsStayCenteredOnTheirOwnSurfaceWhileScrolling(int count) => RunSta(() =>
    {
        foreach (var (pixelWidth, pixelHeight, scale) in new[]
        {
            (1920d, 1080d, 1d), (5120d, 1440d, 1.25d), (3440d, 1440d, 1.5d),
            (1200d, 1600d, 2d), (320d, 480d, 1d), (260d, 600d, 1d), (1921d, 1081d, 1.25d)
        })
        {
            double width = pixelWidth / scale, height = pixelHeight / scale;
            var strip = new MinimizedWindowStrip(Enumerable.Range(1, count).Select(Info).ToArray(),
                width, height, scale, _ => { }, _ => { }, _ => null);
            var canvas = Arrange(strip, width, height);
            double left = Canvas.GetLeft(strip), top = Canvas.GetTop(strip);
            double reserved = strip.Geometry.ReservedHeight;
            Assert.Equal(width / 2, left + strip.Width / 2, 8);
            Assert.InRange(Math.Abs(strip.TranslatePoint(new Point(), canvas).X + strip.ActualWidth / 2 - width / 2), 0, .5);
            Assert.InRange(left, 16, width);
            Assert.Equal(left, width - left - strip.Width, 8);
            var content = strip.NavigationTargets(false).ToArray();
            var scroll = Descendants<ScrollViewer>(strip).Single();
            foreach (double offset in new[] { 0d, 17d, scroll.ScrollableHeight })
            {
                scroll.ScrollToVerticalOffset(offset);
                canvas.UpdateLayout();
                Assert.Equal(left, Canvas.GetLeft(strip));
                Assert.Equal(top, Canvas.GetTop(strip));
                Assert.Equal(reserved, strip.Geometry.ReservedHeight);
                var current = strip.NavigationTargets(false);
                Assert.Equal(content.Select(t => t.Identity), current.Select(t => t.Identity));
                for (int i = 0; i < content.Length; i++)
                {
                    Assert.Equal(content[i].Bounds.X, current[i].Bounds.X, 8);
                    Assert.Equal(content[i].Bounds.Y, current[i].Bounds.Y, 8);
                    Assert.Equal(content[i].Bounds.Width, current[i].Bounds.Width);
                    Assert.Equal(content[i].Bounds.Height, current[i].Bounds.Height);
                }
                Assert.All(strip.Thumbs, thumb =>
                {
                    var point = thumb.ClickBorder.TranslatePoint(new Point(), canvas);
                    // The existing origin rounding allowance is separate from the arranged tile dimensions.
                    Assert.InRange(Math.Abs(point.X - thumb.TargetX / scale), 0, .50000001);
                    Assert.InRange(Math.Abs(point.Y - thumb.TargetY / scale), 0, .50000001);
                });
                Assert.NotEmpty(strip.NavigationTargets(true));
                Assert.All(strip.NavigationTargets(true), target =>
                {
                    var thumb = strip.Thumbs.Single(t => t.Identity == target.Identity);
                    var point = thumb.ClickBorder.TranslatePoint(new Point(), canvas);
                    Assert.InRange(Math.Abs(point.X - target.Bounds.X / scale), 0, .50000001);
                    Assert.Equal(thumb.TargetX, target.Bounds.X, 8);
                    Assert.Equal(thumb.ClickBorder.Width * scale, thumb.TargetWidth, 8);
                    Assert.Equal(Rounded(thumb.ClickBorder.Width, thumb.ClickBorder), thumb.ClickBorder.RenderSize.Width);
                    Assert.Equal(thumb.ClickBorder.RenderSize.Width * scale, target.Bounds.Width, 8);
                    Assert.InRange(target.Bounds.X, left * scale, (left + strip.Width) * scale);
                    Assert.True(target.Bounds.X + target.Bounds.Width <= (left + strip.Width) * scale);
                });
            }
        }
    });

    [Theory]
    [InlineData(0, 1920, 1080, 0, 0)]
    [InlineData(1, 1920, 1080, 1, 1)]
    [InlineData(4, 1920, 1080, 4, 1)]
    [InlineData(5, 1920, 1080, 4, 2)]
    [InlineData(8, 1920, 1080, 4, 2)]
    [InlineData(9, 1920, 1080, 4, 2)]
    [InlineData(20, 1920, 1080, 4, 2)]
    [InlineData(9, 1056, 1080, 3, 2)]
    [InlineData(20, 320, 400, 1, 1)]
    [InlineData(20, 260, 600, 1, 2)]
    public void ArrangedShelfUsesOccupiedRowsAndOnlyScrollsForOverflow(int count, double width, double height,
        int columns, int rows) => RunSta(() =>
        {
            var strip = Shelf(count, width, height);
            Arrange(strip, width, height);
            var scroll = Descendants<ScrollViewer>(strip).Single();
            if (count == 0)
            {
                Assert.Equal(Visibility.Collapsed, strip.Visibility);
                Assert.Equal(0, strip.Geometry.ReservedHeight);
                Assert.Empty(strip.NavigationTargets(false));
                return;
            }
            Assert.Equal(columns, strip.Geometry.Columns);
            Assert.Equal(rows, strip.Geometry.Rows);
            Assert.Equal(rows == 1 ? 88 : 142, strip.Height);
            Assert.Equal(Rounded(strip.Height, strip), strip.ActualHeight);
            Assert.InRange(strip.ActualWidth, 1, width - 32);
            Assert.Equal(Canvas.GetTop(strip) - 12, height - strip.Geometry.ReservedHeight);
            Assert.True(strip.Geometry.ReservedHeight <= height * .35);
            Assert.Equal(count > columns * rows, scroll.ScrollableHeight > 0);
            Assert.Equal(count > columns * rows ? ScrollBarVisibility.Visible : ScrollBarVisibility.Hidden,
                scroll.VerticalScrollBarVisibility);
            var content = OverviewNavigation.VisualOrder(strip.NavigationTargets(false));
            var wrap = Descendants<WrapPanel>(strip).Single();
            Assert.Equal(count, content.Count);
            for (int i = 0; i < count; i++)
            {
                var tile = strip.Thumbs[i].ClickBorder;
                Assert.Equal(46, tile.Height);
                Assert.Equal(strip.Geometry.ItemWidth, tile.Width);
                Assert.Equal(new Thickness(0, 0, 8, 8), tile.Margin);
                Assert.Equal(Rounded(46, tile), tile.RenderSize.Height);
                var point = tile.TranslatePoint(new Point(), wrap);
                Assert.Equal(point.X, content[i].Bounds.X, 8);
                Assert.Equal(point.Y, content[i].Bounds.Y, 8);
                Assert.Equal(tile.RenderSize.Width, content[i].Bounds.Width);
                Assert.Equal(tile.RenderSize.Height, content[i].Bounds.Height);
                Assert.Equal((i % columns) * (Rounded(tile.Width, tile) + 8), point.X, 8);
                Assert.Equal((i / columns) * (Rounded(46, tile) + 8), point.Y, 8);
            }
            Assert.All(strip.NavigationTargets(true), t =>
            {
                Assert.True(t.Bounds.Y >= Canvas.GetTop(strip));
                Assert.True(t.Bounds.Y + t.Bounds.Height <= height - 12);
            });
            SaveRender(strip, $"{count}-{width}x{height}");
        });

    [Fact]
    public void SelectionDoesNotScrollUnlessExplicitlyRevealedAndClippedTilesStayOutOfGlobalGeometry()
        => RunSta(() =>
        {
            var strip = Shelf(20, 320, 480);
            Arrange(strip, 320, 480);
            var scroll = Descendants<ScrollViewer>(strip).Single();
            var stable = OverviewNavigation.VisualOrder(strip.NavigationTargets(false)).Select(t => t.Identity).ToArray();
            strip.SetSelected(new(20));
            Assert.Equal(0, scroll.VerticalOffset);
            Assert.DoesNotContain(strip.NavigationTargets(true), t => t.Identity.Handle == new IntPtr(20));
            strip.SetSelected(new(20), reveal: true, keyboardFocus: true);
            Assert.True(scroll.VerticalOffset > 0);
            Assert.Contains(strip.NavigationTargets(true), t => t.Identity.Handle == new IntPtr(20));
            double offset = scroll.VerticalOffset;
            strip.SetSelected(new(1));
            Assert.Equal(offset, scroll.VerticalOffset);
            strip.SetSelected(IntPtr.Zero);
            Assert.Equal(offset, scroll.VerticalOffset);
            scroll.ScrollToVerticalOffset(17);
            scroll.UpdateLayout();
            Assert.Equal(stable, OverviewNavigation.VisualOrder(strip.NavigationTargets(false)).Select(t => t.Identity));
            var clipped = strip.NavigationTargets(true).Single(t => t.Identity.Handle == new IntPtr(1));
            var tile = strip.Thumbs[0].ClickBorder;
            var viewport = Descendants<ScrollContentPresenter>(scroll).Single();
            var tilePoint = tile.TranslatePoint(new Point(), viewport);
            Assert.Equal(17, scroll.VerticalOffset);
            Assert.Equal(tile.RenderSize.Height + tilePoint.Y, clipped.Bounds.Height, 8);
            Assert.Equal(viewport.TranslatePoint(new Point(), strip).Y + Canvas.GetTop(strip), clipped.Bounds.Y, 8);
            Assert.Equal(-Rounded(17, tile), tilePoint.Y, 8);
            Assert.DoesNotContain(strip.NavigationTargets(true), t => t.Identity.Handle == new IntPtr(20));
        });

    [Fact]
    public void AutomationHasOneFullTitleEntryPerWindowAndSeparateSelectAndInvokeActions()
        => RunSta(() =>
        {
            int confirmations = 0;
            string fullTitle = "Window " + new string('a', 180);
            var first = Info(1);
            var second = Info(2);
            first.Title = second.Title = fullTitle;
            var strip = new MinimizedWindowStrip([first, second], 1920, 1080, 1,
                _ => confirmations++, _ => { }, _ => null);
            Arrange(strip, 1920, 1080);
            var peer = UIElementAutomationPeer.CreatePeerForElement(strip);
            var entries = peer.GetChildren();
            Assert.Equal(2, entries.Count);
            Assert.All(entries, entry =>
            {
                Assert.Equal(fullTitle + ", minimized", entry.GetName());
                Assert.Contains("restore", entry.GetHelpText());
                Assert.Null(entry.GetChildren());
                Assert.NotNull(entry.GetPattern(PatternInterface.Invoke));
                Assert.NotNull(entry.GetPattern(PatternInterface.SelectionItem));
            });
            var selection = Assert.IsAssignableFrom<ISelectionItemProvider>(entries[1].GetPattern(PatternInterface.SelectionItem));
            selection.Select();
            Assert.Equal(0, confirmations);
            Assert.True(selection.IsSelected);
            var firstSelection = Assert.IsAssignableFrom<ISelectionItemProvider>(entries[0].GetPattern(PatternInterface.SelectionItem));
            Assert.False(firstSelection.IsSelected);
            firstSelection.RemoveFromSelection();
            Assert.True(selection.IsSelected);
            var list = Assert.IsAssignableFrom<ISelectionProvider>(peer.GetPattern(PatternInterface.Selection));
            Assert.Single(list.GetSelection());
            selection.RemoveFromSelection();
            Assert.Empty(list.GetSelection());
            ((IInvokeProvider)entries[1].GetPattern(PatternInterface.Invoke)).Invoke();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, confirmations);
            var buttons = strip.Thumbs.Select(t => Assert.IsType<MinimizedWindowButton>(t.ClickBorder.Child)).ToArray();
            Assert.All(buttons, b => Assert.Contains("Window", AutomationProperties.GetName(b)));
            Assert.All(buttons, b => Assert.IsType<ToolTip>(b.ToolTip));
            Assert.All(buttons, b => Assert.NotEmpty(Descendants<Path>(b)));
        });

    [Fact]
    public void SelectedAndKeyboardFocusCuesDoNotReflowAndHighContrastUsesSystemBrushes()
        => RunSta(() =>
        {
            var strip = Shelf(2, 1920, 1080);
            Arrange(strip, 1920, 1080);
            var button = Assert.IsType<MinimizedWindowButton>(strip.Thumbs[0].ClickBorder.Child);
            var frame = Assert.IsType<Border>(button.Template.FindName("Frame", button));
            var surface = Assert.IsType<Border>(button.Template.FindName("Surface", button));
            var check = Assert.IsType<Path>(button.Template.FindName("Check", button));
            var focus = Assert.IsType<Rectangle>(button.Template.FindName("Focus", button));
            double titleWidth = strip.Thumbs[0].TitleLabel.ActualWidth;
            Assert.Equal(Visibility.Hidden, check.Visibility);
            Assert.Equal(Visibility.Hidden, focus.Visibility);
            strip.SetSelected(new(1));
            Assert.Equal(Visibility.Visible, check.Visibility);
            Assert.Equal(Visibility.Hidden, focus.Visibility);
            Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(frame.BorderBrush).Color);
            strip.SetSelected(new(1), keyboardFocus: true);
            Assert.Equal(Visibility.Visible, focus.Visibility);
            strip.UpdateLayout();
            Assert.True(frame.TranslatePoint(new Point(), button).X > focus.TranslatePoint(new Point(), button).X);
            Assert.True(frame.ActualWidth < focus.ActualWidth);
            Assert.Equal(titleWidth, strip.Thumbs[0].TitleLabel.ActualWidth);
            strip.ApplyPalette(true);
            strip.UpdateLayout();
            Assert.Equal(SystemColors.WindowColor, Assert.IsType<SolidColorBrush>(strip.Background).Color);
            Assert.Equal(SystemColors.HighlightColor, Assert.IsType<SolidColorBrush>(surface.Background).Color);
            Assert.Equal(SystemColors.HighlightTextColor, Assert.IsType<SolidColorBrush>(button.Foreground).Color);
            Assert.Equal(SystemColors.HighlightTextColor, Assert.IsType<SolidColorBrush>(frame.BorderBrush).Color);
            strip.SetSelected(new(2));
            Assert.Equal(SystemColors.WindowColor, Assert.IsType<SolidColorBrush>(surface.Background).Color);
            Assert.Equal(SystemColors.WindowTextColor, Assert.IsType<SolidColorBrush>(button.Foreground).Color);
            Assert.Equal(Visibility.Hidden, focus.Visibility);
            strip.ApplyPalette(false);
            strip.SetSelected(new(1), keyboardFocus: true);
            SaveRender(strip, "two-items-selected");
        });

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2)]
    public void PublishedPhysicalRectanglesFollowEffectiveScale(double scale) => RunSta(() =>
    {
        var strip = new MinimizedWindowStrip([Info(1), Info(2)], 600, 600, scale, _ => { }, _ => { }, _ => null);
        Arrange(strip, 600, 600);
        var first = strip.NavigationTargets(true)[0];
        var tile = strip.Thumbs[0].ClickBorder;
        var point = tile.TranslatePoint(new Point(), strip);
        Assert.Equal((Canvas.GetLeft(strip) + point.X) * scale, first.Bounds.X, 8);
        Assert.Equal((Canvas.GetTop(strip) + point.Y) * scale, first.Bounds.Y, 8);
        Assert.Equal(240, tile.Width);
        Assert.Equal(46, tile.Height);
        Assert.Equal(Rounded(240, tile) * scale, first.Bounds.Width);
        Assert.Equal(Rounded(46, tile) * scale, first.Bounds.Height);
    });

    [Fact]
    public void ExceptionallySmallSurfaceDoesNotSilentlyClipInteractiveTargets()
    {
        Assert.Throws<InvalidOperationException>(() => MinimizedShelfGeometry.Calculate(800, 300, 1, 17));
        Assert.Throws<InvalidOperationException>(() => MinimizedShelfGeometry.Calculate(100, 800, 1, 17));
    }

    private static MinimizedWindowStrip Shelf(int count, double width, double height) =>
        new(Enumerable.Range(1, count).Select(Info).ToArray(), width, height, 1, _ => { }, _ => { }, _ => null);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T result) yield return result;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void SaveRender(MinimizedWindowStrip strip, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("MISSIONVIEW_SHELF_ARTIFACTS");
        if (directory == null) return;
        var canvas = Assert.IsType<Canvas>(VisualTreeHelper.GetParent(strip));
        var bitmap = new RenderTargetBitmap((int)canvas.ActualWidth, (int)canvas.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var crop = new CroppedBitmap(bitmap, new Int32Rect((int)Canvas.GetLeft(strip), (int)Canvas.GetTop(strip),
            (int)strip.ActualWidth, (int)strip.ActualHeight));
        var pixels = new byte[crop.PixelWidth * crop.PixelHeight * 4];
        crop.CopyPixels(pixels, crop.PixelWidth * 4, 0);
        Assert.True(pixels.Any(b => b != 0), "The arranged component render must not be empty.");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(crop));
        using var output = File.Create(System.IO.Path.Combine(directory, name + ".png"));
        encoder.Save(output);
    }

    private static WindowInfo Info(int id) => new()
    {
        Handle = new(id), Identity = new(new(id), 1, 2), WasMinimized = true,
        Title = $"Window {id}"
    };

    private static Canvas Arrange(MinimizedWindowStrip strip, double width, double height)
    {
        var canvas = new Canvas { Width = width, Height = height };
        canvas.Children.Add(strip);
        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));
        canvas.UpdateLayout();
        return canvas;
    }

    private static double Rounded(double size, Visual visual)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        Assert.Equal(dpi.DpiScaleX, dpi.DpiScaleY);
        return Math.Round(size * dpi.DpiScaleY) / dpi.DpiScaleY;
    }


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
}
