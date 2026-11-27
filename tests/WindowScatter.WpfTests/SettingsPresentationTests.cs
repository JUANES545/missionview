using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowScatter;
using Xunit;

public sealed class SettingsPresentationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreditLinksInvokeTheirVerifiedTargetsAndReportOpeningFailuresWithoutSaving(bool fail)
        => Sta(() =>
        {
            var opened = new List<Uri>();
            var source = new AppSettings();
            string before = System.Text.Json.JsonSerializer.Serialize(source);
            using var service = new UpdateService(new RejectNetwork());
            using var controller = new UpdateController(service, new(0, 1, 4), () => throw new Exception("Unexpected shutdown"),
                _ => throw new Exception("Unexpected installation"), null, () => true);
            var updates = new UpdatePanel(controller, (_, _) => throw new Exception("Unexpected installation consent"));
            var window = new SettingsWindow(source, _ => throw new Exception("Link saved preferences"), updates, () => true,
                openProjectLink: address =>
                {
                    opened.Add(address);
                    if (fail) throw new System.ComponentModel.Win32Exception("Synthetic browser-opening failure.");
                });
            try
            {
                window.ShowUpdates();
                var content = (UIElement)window.Content;
                window.Content = null;
                var root = new Border { Background = window.Background, Child = content };
                window.Content = root;
                Layout(root, 720, 480);
                var links = Logical(window).OfType<Hyperlink>().ToArray();
                foreach (var link in links)
                {
                    var invoke = Assert.IsAssignableFrom<System.Windows.Automation.Provider.IInvokeProvider>(
                        new HyperlinkAutomationPeer(link).GetPattern(PatternInterface.Invoke));
                    invoke.Invoke();
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                }
                Assert.Equal(links.Select(link => link.NavigateUri), opened);
                Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(source));
                Assert.Equal(UpdateStatus.Idle, controller.State.Status);
                if (fail)
                    Assert.Contains(Logical(window).OfType<TextBlock>(), text => text.Text.Contains("Could not open this link."));
                else
                    Assert.DoesNotContain(Logical(window).OfType<TextBlock>(), text => text.Text.Contains("Could not open this link."));
                Capture(root, $"settings-about-links-720-480-failure{fail}-offscreen.png");
                SettingsControlStyles.Apply(window.Resources, true);
                Assert.All(links, link => Assert.Equal(SystemColors.HotTrackColor, ((SolidColorBrush)link.Foreground).Color));
                SettingsControlStyles.Apply(window.Resources, false);
                Assert.All(links, link => Assert.Equal(SharedBrushes.Mint.Color, ((SolidColorBrush)link.Foreground).Color));
                window.FontSize = 28;
                Layout(root, 360, 600);
                var line = Assert.IsType<TextBlock>(links[^1].Parent);
                links[^1].BringIntoView();
                root.UpdateLayout();
                var scroll = PageScroll(root);
                var viewport = Visual(scroll).OfType<ScrollContentPresenter>().First();
                double bottom = line.TranslatePoint(new Point(0, line.ActualHeight), viewport).Y;
                Assert.True(bottom <= viewport.ActualHeight + 1, $"Last credit bottom={bottom}, viewport={viewport.ActualHeight}");
                Capture(root, $"settings-about-links-360-600-font28-failure{fail}-offscreen.png");
            }
            finally { window.Close(); }
        });

    [Fact]
    public void SettingsNamesTheWindowOnceAndCreditsTheOriginalForkAndCurrentRepository()
        => Sta(() =>
        {
            var window = Create();
            try
            {
                Assert.Equal("MissionView", window.Title);
                Assert.Equal("MissionView settings", new WindowAutomationPeer(window).GetName());
                Assert.Single(Logical(window).OfType<TextBlock>(), text => text.Text == "MissionView");
                Assert.DoesNotContain(Logical(window).OfType<TextBlock>(), text => text.Text == "Settings");
                Assert.DoesNotContain(Logical(window).OfType<TextBlock>(), text => text.Text == "MissionView settings");
                var links = Logical(window).OfType<Hyperlink>().ToArray();
                Assert.Equal(new[]
                {
                    "https://github.com/miguelo96/WindowScatter",
                    "https://github.com/RuoChen5251/WindowScatter",
                    "https://github.com/JUANES545/missionview"
                }, links.Select(link => link.NavigateUri.AbsoluteUri));
                Assert.All(links, link =>
                {
                    Assert.True(link.Focusable && System.Windows.Input.KeyboardNavigation.GetIsTabStop(link));
                    var peer = new HyperlinkAutomationPeer(link);
                    Assert.NotEmpty(peer.GetName());
                    Assert.NotNull(peer.GetPattern(PatternInterface.Invoke));
                });
            }
            finally { window.Close(); }
        });

    [Theory]
    [InlineData(320, 28)]
    [InlineData(376, 28)]
    [InlineData(720, 14)]
    [InlineData(1024, 14)]
    public void TitleBarKeepsSearchAndBrandOutOfNativeCaptionButtons(double width, double font)
        => Sta(() =>
        {
            var window = Create();
            try
            {
                window.FontSize = font;
                var root = Assert.IsType<DockPanel>(window.Content);
                var search = Logical(root).OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Search settings");
                var field = Assert.IsType<Grid>(search.Parent);
                var header = Assert.IsType<Grid>(field.Parent);
                Layout(root, width, font >= 28 ? window.MinHeight : 480);
                var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
                Assert.True(chrome.UseAeroCaptionButtons);
                Assert.InRange(Math.Abs(chrome.GlassFrameThickness.Top - header.RowDefinitions[0].ActualHeight), 0, 1);
                Assert.Equal("MissionView", window.Title);
                Assert.True(System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(field));
                Assert.True(System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(search));
                var brand = Logical(header).OfType<TextBlock>().Single(t => t.Text == "MissionView");
                var origin = search.TranslatePoint(new Point(), header);
                Assert.True(search.ActualWidth >= (width >= 376 ? 72 : 20));
                Assert.True(search.ActualHeight >= font + search.Padding.Top + search.Padding.Bottom);
                Assert.InRange(Math.Abs(origin.Y + search.ActualHeight / 2 - header.RowDefinitions[0].ActualHeight / 2), 0, 1);
                Assert.True(brand.TranslatePoint(new Point(brand.ActualWidth, 0), header).X < origin.X);
                Assert.True(origin.X + search.ActualWidth < header.ActualWidth - header.ColumnDefinitions[2].ActualWidth);
                Assert.InRange(Math.Abs(origin.X + search.ActualWidth / 2 - header.ActualWidth / 2), 0, 1);
                search.Text = "overview";
                Layout(root, width, 480);
                var results = FilteredSettings(root);
                Assert.True(results.ViewportHeight > 0);
                Assert.True(results.TranslatePoint(new Point(), root).Y >= header.ActualHeight);
                Assert.Equal(Dock.Top, DockPanel.GetDock(header));
                search.Clear();
                Layout(root, width, font >= 28 ? window.MinHeight : 480);
                Assert.True(PageScroll(root).ViewportHeight >= font);
            }
            finally { window.Close(); }
        });

    [Fact]
    public void SettingsPaintsItsSurfaceWithoutCoveringTheNativeCaptionLane()
        => Sta(() =>
        {
            var window = Create();
            try
            {
                var root = Assert.IsType<DockPanel>(window.Content);
                Layout(root, 720, 480);
                Assert.Equal(0, Assert.IsType<SolidColorBrush>(window.Background).Color.A);
                var bitmap = new RenderTargetBitmap(720, 480, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                byte AlphaAt(int x, int y)
                {
                    var pixel = new byte[4];
                    bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
                    return pixel[3];
                }
                Assert.Equal(255, AlphaAt(5, 15));
                Assert.Equal(0, AlphaAt(700, 15));
                Assert.Equal(255, AlphaAt(5, 350));
            }
            finally { window.Close(); }
        });

    [Theory]
    [InlineData(720, 480, 14)]
    [InlineData(720, 720, 14)]
    [InlineData(720, 1000, 14)]
    [InlineData(320, 260, 14)]
    [InlineData(360, 600, 28)]
    [InlineData(1000, 900, 14)]
    public void SettingsUseOneOverflowSurfaceAndKeepTheFirstRowBelowTheTabs(double width, double height, double font)
        => Sta(() =>
        {
            var window = Create();
            try
            {
                window.FontSize = font;
                var content = (UIElement)window.Content;
                window.Content = null;
                var root = new Border { Background = window.Background, Child = content };
                window.Content = root;
                var tabs = Logical(window).OfType<TabControl>().Single();
                Layout(root, width, height);
                var selected = (TabItem)tabs.SelectedItem;
                var form = CategoryForm(selected);
                var outer = PageScroll(root);
                var firstRow = form.Children.OfType<Grid>().First();
                double firstRowTop = firstRow.TranslatePoint(new Point(), root).Y;
                var headers = tabs.Items.Cast<TabItem>().ToArray();
                var headerPositions = headers.Select(t => t.TranslatePoint(new Point(), root)).ToArray();
                double headerBottom = headers.Max(t => t.TranslatePoint(new Point(0, t.ActualHeight), root).Y);
                var surfaces = Visual(root).OfType<ScrollViewer>().Where(firstRow.IsDescendantOf).ToArray();
                Assert.False(headers[0].IsDescendantOf(outer));
                Assert.Same(outer, Assert.Single(surfaces));
                Capture(root, $"settings-fit-client-{width}-{height}-font{font}-offscreen.png");
                string? directory = Environment.GetEnvironmentVariable("MISSIONVIEW_SETTINGS_FIXTURES");
                if (directory != null)
                    System.IO.File.WriteAllText(System.IO.Path.Combine(directory, $"settings-fit-{width}-{height}-font{font}.json"),
                        System.Text.Json.JsonSerializer.Serialize(new { width, height, font, firstRowTop, headerBottom,
                            viewport = outer.ViewportHeight, extent = outer.ExtentHeight, scroll = outer.ScrollableHeight,
                            surfaces = surfaces.Select(s => new { s.ActualHeight, s.ViewportHeight, s.ExtentHeight }) }));
                Assert.InRange(firstRowTop - headerBottom, 0, 25);
                var viewport = Visual(outer).OfType<ScrollContentPresenter>().First();
                Point outerEdge = outer.TranslatePoint(new Point(outer.ActualWidth, outer.ActualHeight), root);
                Assert.InRange(root.ActualWidth - outerEdge.X, -1, 1);
                Assert.InRange(root.ActualHeight - outerEdge.Y, -1, 1);
                Assert.True(firstRow.TranslatePoint(new Point(), viewport).X >= 16);
                if (height >= 720 && font == 14) Assert.Equal(0, outer.ScrollableHeight);
                var reset = NamedButton(window, "Reset settings");
                reset.BringIntoView();
                root.UpdateLayout();
                var point = reset.TranslatePoint(new Point(), outer);
                Assert.InRange(point.Y, 0, outer.ViewportHeight);
                Assert.True(point.Y + reset.ActualHeight <= outer.ViewportHeight + 1);
                Capture(root, $"settings-fit-client-{width}-{height}-font{font}-end-offscreen.png");
                if (outer.ScrollableHeight > 0)
                {
                    outer.ScrollToEnd();
                    root.UpdateLayout();
                    Assert.True(outer.VerticalOffset > 0);
                    Assert.Equal(headerPositions, headers.Select(t => t.TranslatePoint(new Point(), root)));
                    Assert.True(firstRow.TranslatePoint(new Point(), root).Y < firstRowTop);
                    var lastRow = form.Children.OfType<Grid>().Last();
                    double bottom = lastRow.TranslatePoint(new Point(0, lastRow.ActualHeight), viewport).Y;
                    Assert.InRange(viewport.ActualHeight - bottom, 16, 32);
                    var bar = Assert.IsType<ScrollBar>(outer.Template.FindName("PART_VerticalScrollBar", outer));
                    Assert.Equal(Visibility.Visible, bar.Visibility);
                    var barEdge = bar.TranslatePoint(new Point(bar.ActualWidth, bar.ActualHeight), root);
                    Assert.InRange(root.ActualWidth - barEdge.X, -1, 1);
                    Assert.InRange(root.ActualHeight - barEdge.Y, -1, 1);
                    tabs.SelectedIndex = 1;
                    Layout(root, width, height);
                    tabs.SelectedIndex = 0;
                    Layout(root, width, height);
                    Assert.Equal(0, outer.VerticalOffset);
                    Assert.Equal(headerPositions, headers.Select(t => t.TranslatePoint(new Point(), root)));
                }
            }
            finally { window.Close(); }
        });

    [Theory]
    [InlineData("Startup help", 0)]
    [InlineData("Overview shortcut help", 1)]
    [InlineData("Activation help", 2)]
    public void HelpHasNoAutomaticPopupAndOnlyInvocationRevealsInlineDetails(string name, int category)
        => Sta(() =>
        {
            var window = Create();
            try
            {
                var tabs = Logical(window).OfType<TabControl>().Single();
                tabs.SelectedIndex = category;
                var help = NamedButton(window, name);
                var row = (Grid)((Label)((DockPanel)help.Parent).Parent).Parent;
                var detail = row.Children.OfType<TextBlock>().Single(t => !Equals(t.Tag, "ShortcutPreview"));
                Assert.Null(help.ToolTip);
                Assert.Null(Assert.IsAssignableFrom<Control>(((Label)row.Children[0]).Target).ToolTip);
                Assert.Equal(Visibility.Collapsed, detail.Visibility);
                help.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, null, help)
                    { RoutedEvent = Keyboard.GotKeyboardFocusEvent });
                Assert.Null(help.ToolTip);
                Assert.Equal(Visibility.Collapsed, detail.Visibility);
                var peer = UIElementAutomationPeer.CreatePeerForElement(help)!;
                Assert.NotEmpty(peer.GetHelpText());
                ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(Visibility.Visible, detail.Visibility);
                Assert.Equal("Expanded", peer.GetItemStatus());
                tabs.SelectedIndex = (category + 1) % 4;
                Assert.Equal(Visibility.Collapsed, detail.Visibility);
                Assert.Equal("Collapsed", peer.GetItemStatus());
            }
            finally { window.Close(); }
        });

    [Fact]
    public void EnlargingTheSameSettingsWindowExposesMoreContentWithoutMovingTheFirstRowDown()
        => Sta(() =>
        {
            var window = Create();
            try
            {
                var content = (UIElement)window.Content;
                window.Content = null;
                var root = new Border { Background = window.Background, Child = content };
                window.Content = root;
                var tabs = Logical(window).OfType<TabControl>().Single();
                Layout(root, 720, 480);
                Capture(root, "settings-resize-client-720-480-offscreen.png");
                var row = CategoryForm((TabItem)tabs.SelectedItem).Children.OfType<Grid>().First();
                double top = row.TranslatePoint(new Point(), root).Y;
                Layout(root, 720, 720);
                Capture(root, "settings-resize-client-720-720-offscreen.png");
                Layout(root, 720, 1000);
                Capture(root, "settings-resize-client-720-1000-offscreen.png");
                Assert.Equal(top, row.TranslatePoint(new Point(), root).Y);
                var scroll = PageScroll(root);
                Assert.Equal(0, scroll.ScrollableHeight);
            }
            finally { window.Close(); }
        });

    [Fact]
    public void NativeCaptionUsesTheSettingsSurfaceWithoutReplacingTheSystemFrame()
        => Sta(() =>
        {
            var window = Create();
            try
            {
                var handle = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                Assert.False(window.IsVisible);
                Assert.Equal(WindowStyle.SingleBorderWindow, window.WindowStyle);
                Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
                Assert.True(window.ApplyNativeCaption(false));
                var attributes = new Dictionary<int, int>();
                int Set(IntPtr target, int attribute, int value)
                {
                    Assert.Equal(handle, target);
                    attributes[attribute] = value;
                    return 0;
                }
                Assert.True(window.ApplyNativeCaption(true, Set));
                Assert.Equal(0, attributes[20]);
                Assert.Equal(-1, attributes[35]);
                Assert.Equal(-1, attributes[36]);
                Assert.True(window.ApplyNativeCaption(false, Set));
                Assert.Equal(1, attributes[20]);
                Assert.Equal(ColorRef(SharedBrushes.Graphite.Color), attributes[35]);
                Assert.Equal(ColorRef(((SolidColorBrush)window.Foreground).Color), attributes[36]);
                Assert.False(window.ApplyNativeCaption(false, (_, _, _) => unchecked((int)0x80070057)));
            }
            finally { window.Close(); }
        });

    private static int ColorRef(Color color) => color.R | color.G << 8 | color.B << 16;

    private static StackPanel CategoryForm(TabItem category)
        => Assert.IsType<StackPanel>(category.Content);

    [Fact]
    public void WrappedTabHeadersKeepTheirOrderAndBoundsWhenTheSelectionChanges()
        => Sta(() =>
        {
            var window = Create();
            try
            {
                window.FontSize = 28;
                var content = (UIElement)window.Content;
                window.Content = null;
                var root = new Border { Background = window.Background, Child = content };
                window.Content = root;
                var tabs = Logical(window).OfType<TabControl>().Single();
                Layout(root, 360, 600);
                var headers = tabs.Items.Cast<TabItem>().ToArray();
                var bounds = headers.Select(h => new Rect(h.TranslatePoint(new Point(), root), h.RenderSize)).ToArray();
                var selectedUnderline = SystemParameters.HighContrast ? SystemColors.HighlightBrush.Color : Color.FromRgb(125, 219, 155);
                Assert.True(bounds[0].Top <= bounds[1].Top && bounds[1].Top <= bounds[2].Top && bounds[2].Top <= bounds[3].Top);
                for (int index = 0; index < headers.Length; index++)
                {
                    tabs.SelectedIndex = index;
                    Layout(root, 360, 600);
                    for (int header = 0; header < headers.Length; header++)
                    {
                        var tab = headers[header];
                        var frame = Assert.IsType<Border>(tab.Template.FindName("TabFrame", tab));
                        Assert.Same(window.Resources[tab.GetType()], tab.Style);
                        var underline = Assert.IsType<SolidColorBrush>(frame.BorderBrush);
                        Assert.Equal(header == index ? selectedUnderline : Colors.Transparent, underline.Color);
                    }
                    Assert.Equal(bounds, headers.Select(h => new Rect(h.TranslatePoint(new Point(), root), h.RenderSize)));
                    Assert.All(headers, h => Assert.True(h.TranslatePoint(new Point(), root).X + h.ActualWidth <= 360));
                    Capture(root, $"settings-wrapped-tabs-360-600-font28-tab{index}-offscreen.png");
                }
            }
            finally { window.Close(); }
        });

    [Fact]
    public void FluentOverflowKeepsWheelPageThumbAndAutomationScrollingAndRestoresAfterHighContrast()
        => Sta(() =>
        {
            var window = Create();
            try
            {
                var content = (UIElement)window.Content;
                window.Content = null;
                var root = new Border { Background = window.Background, Child = content };
                window.Content = root;
                Layout(root, 360, 480);
                var scroll = PageScroll(root);
                var bar = Visual(scroll).OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical);
                var track = Assert.IsType<Track>(bar.Template.FindName("PART_Track", bar));
                Assert.True(scroll.ScrollableHeight > 0);
                Assert.Equal(Visibility.Visible, bar.Visibility);
                Assert.Same(window.Resources["SettingsScrollBarStyle"], bar.Style);
                var ink = Assert.IsType<Border>(track.Thumb.Template.FindName("Ink", track.Thumb));
                Assert.Same(window.Resources["ScrollThumb"], ink.Background);
                Assert.InRange(ink.ActualWidth, 4, 8);
                Assert.True(BrushContrast(ink.Background, window.Resources["SettingsSurface"]) >= 3);
                scroll.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, -120)
                    { RoutedEvent = UIElement.MouseWheelEvent });
                root.UpdateLayout();
                double wheelOffset = scroll.VerticalOffset;
                Assert.True(wheelOffset > 0);
                ScrollBar.PageDownCommand.Execute(null, bar);
                root.UpdateLayout();
                Assert.True(scroll.VerticalOffset > wheelOffset);
                scroll.ScrollToTop();
                root.UpdateLayout();
                track.Thumb.RaiseEvent(new DragDeltaEventArgs(0, 25));
                root.UpdateLayout();
                Assert.True(scroll.VerticalOffset > 0);
                var peer = UIElementAutomationPeer.CreatePeerForElement(scroll)!;
                var provider = Assert.IsAssignableFrom<System.Windows.Automation.Provider.IScrollProvider>(peer.GetPattern(PatternInterface.Scroll));
                provider.SetScrollPercent(System.Windows.Automation.ScrollPattern.NoScroll, 100);
                root.UpdateLayout();
                Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset);
                Capture(root, "settings-fluent-scroll-end-offscreen.png");
                SettingsControlStyles.Apply(window.Resources, true);
                root.UpdateLayout();
                Assert.NotSame(window.Resources["SettingsScrollBarStyle"], bar.Style);
                Assert.Equal(SystemColors.WindowTextBrush, window.Resources["ScrollThumb"]);
                Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset);
                SettingsControlStyles.Apply(window.Resources, false);
                root.UpdateLayout();
                Assert.Same(window.Resources["SettingsScrollBarStyle"], bar.Style);
                scroll.ScrollToTop();
                Layout(root, 720, 1000);
                Assert.Equal(0, scroll.ScrollableHeight);
                Assert.NotEqual(Visibility.Visible, bar.Visibility);
                var combo = Logical(window).OfType<ComboBox>().First();
                var popup = Assert.IsType<Popup>(combo.Template.FindName("PART_Popup", combo));
                var popupRoot = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
                Layout(popupRoot, 320, 100);
                var popupScroll = Visual(popupRoot).OfType<ScrollViewer>().Single();
                Assert.Equal(ScrollBarVisibility.Auto, popupScroll.VerticalScrollBarVisibility);
                var popupBar = Visual(popupRoot).OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical);
                Assert.Same(window.Resources["SettingsScrollBarStyle"], popupBar.Style);
                Capture(popupRoot, "settings-dropdown-own-offscreen.png");
            }
            finally { window.Close(); }
        });

    [Fact]
    public void DecorativePreferenceGlyphsStayOutOfAutomationWithoutHidingLabelsDescriptionsOrHelp()
        => Sta(() =>
        {
            var window = Create();
            try
            {
                var tabs = Logical(window).OfType<TabControl>().Single();
                var content = (UIElement)window.Content;
                window.Content = null;
                var root = new Border { Background = window.Background, Child = content };
                window.Content = root;
                for (int index = 0; index < 3; index++)
                {
                    tabs.SelectedIndex = index;
                    Layout(root, 720, 440);
                    var panel = CategoryForm((TabItem)tabs.Items[index]);
                    foreach (var row in panel.Children.OfType<Grid>())
                    {
                        var label = Assert.IsType<Label>(row.Children[0]);
                        var labelPeer = UIElementAutomationPeer.CreatePeerForElement(label)!;
                        var peers = AutomationTree(labelPeer).ToArray();
                        Assert.DoesNotContain(peers.Select(p => p.GetName()), ContainsPrivateUseGlyph);
                        var glyph = Assert.Single(Visual(label).OfType<TextBlock>(), t => ContainsPrivateUseGlyph(t.Text));
                        Assert.Null(UIElementAutomationPeer.CreatePeerForElement(glyph));
                        Assert.Same(row.Children[1], label.Target);
                        var caption = Logical(label).OfType<StackPanel>().Single();
                        foreach (var text in caption.Children.OfType<TextBlock>())
                            Assert.Contains(peers, p => p is FrameworkElementAutomationPeer element &&
                                ReferenceEquals(element.Owner, text) && p.GetName() == text.Text);
                        var controlPeer = UIElementAutomationPeer.CreatePeerForElement(label.Target)!;
                        Assert.NotEmpty(controlPeer.GetName());
                        foreach (var help in Logical(label).OfType<Button>())
                        {
                            var helpPeer = Assert.Single(peers, p => p is FrameworkElementAutomationPeer element &&
                                ReferenceEquals(element.Owner, help));
                            Assert.EndsWith(" help", helpPeer.GetName());
                            Assert.NotNull(helpPeer.GetPattern(PatternInterface.Invoke));
                        }
                    }
                }
            }
            finally { window.Close(); }
        });

    private static bool ContainsPrivateUseGlyph(string text)
        => text.Any(c => char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.PrivateUse);

    private static IEnumerable<AutomationPeer> AutomationTree(AutomationPeer root)
    {
        yield return root;
        foreach (var child in root.GetChildren() ?? [])
            foreach (var peer in AutomationTree(child)) yield return peer;
    }

    [Fact]
    public void KeycapsFollowRecordingAndCancellationAndRefreshTheSystemPaletteWithoutChangingTheShortcut()
        => Sta(() =>
        {
            var window = Create();
            try
            {
                var tabs = Logical(window).OfType<TabControl>().Single();
                tabs.SelectedIndex = 1;
                var content = (UIElement)window.Content;
                window.Content = null;
                var root = new Border { Background = window.Background, Child = content };
                window.Content = root;
                Layout(root, 720, 440);
                var recorder = NamedButton(window, "Overview shortcut");
                var keys = Visual(recorder).OfType<ItemsControl>().Single();
                Assert.Equal(new[] { "Win", "F12" }, keys.Items.Cast<string>());
                var peer = UIElementAutomationPeer.CreatePeerForElement(recorder)!;
                Assert.Equal("Overview shortcut: Win+F12", peer.GetName());
                ClickRecorder(recorder);
                Layout(root, 720, 440);
                Assert.Equal(new[] { "Recording..." }, keys.Items.Cast<string>());
                window.RecordVirtualKey(0x1B, 0, true);
                Layout(root, 720, 440);
                Assert.Equal(new[] { "Win", "F12" }, keys.Items.Cast<string>());
                Assert.Equal("Win+F12", recorder.Content);
                Assert.Equal("Overview shortcut: Win+F12", peer.GetName());
                var key = Visual(keys).OfType<Border>().First(b => b.Child is TextBlock);
                var bounds = key.RenderSize;
                SettingsControlStyles.Apply(window.Resources, true);
                Layout(root, 720, 440);
                Assert.Equal(SystemColors.WindowColor, ((SolidColorBrush)key.Background).Color);
                Assert.Equal(SystemColors.WindowTextColor, ((SolidColorBrush)key.BorderBrush).Color);
                Assert.Equal(bounds, key.RenderSize);
                Capture(root, "settings-keycaps-system-palette-offscreen.png");
                SettingsControlStyles.Apply(window.Resources, false);
                Layout(root, 720, 440);
                Assert.Same(window.Resources["KeycapSurface"], key.Background);
                Assert.Equal(bounds, key.RenderSize);
            }
            finally { window.Close(); }
        });

    [Theory]
    [InlineData(720, 560, 14, false)]
    [InlineData(720, 480, 14, false)]
    [InlineData(360, 480, 14, true)]
    [InlineData(320, 300, 20, true)]
    [InlineData(360, 480, 28, true)]
    public void CategoriesReflowWithoutHidingActionsAndScrollOnlyWhenNeeded(double width, double height, double font, bool narrow)
        => Sta(() =>
        {
            var window = Create();
            try
            {
                window.FontSize = font;
                var tabs = Logical(window).OfType<TabControl>().Single();
                Assert.Equal(new[] { "General", "Shortcuts", "Hot corners", "Updates & About", "Experimental" },
                    tabs.Items.Cast<TabItem>().Select(t => t.Header));
                var content = (UIElement)window.Content;
                window.Content = null;
                var root = new Border { Background = window.Background, Child = content };
                window.Content = root;
                for (int index = 0; index < 4; index++)
                {
                    tabs.SelectedIndex = index;
                    Layout(root, width, height - 40);
                    var panel = CategoryForm((TabItem)tabs.Items[index]);
                    foreach (var row in panel.Children.OfType<Grid>())
                    {
                        Assert.Equal(narrow ? 1 : 0, Grid.GetRow(row.Children[1]));
                        Assert.True(((FrameworkElement)row.Children[1]).ActualWidth <= root.ActualWidth);
                    }
                    var scroll = PageScroll(root);
                    if (width == 720)
                    {
                        if (index == 0) Assert.True(scroll.ScrollableHeight > 0,
                            $"General extent={scroll.ExtentHeight}, viewport={scroll.ViewportHeight}, panel={panel.ActualHeight}, scroll={scroll.ScrollableHeight}");
                        else if (index == 1 && height == 480)
                        {
                            Assert.InRange(scroll.ScrollableHeight, 0, 40);
                            var useAltTab = Logical(panel).OfType<Button>().Single(b => Equals(b.Content, "Use Alt+Tab"));
                            Assert.InRange(useAltTab.TranslatePoint(new Point(0, useAltTab.ActualHeight), scroll).Y,
                                0, scroll.ViewportHeight);
                        }
                        else Assert.Equal(0, scroll.ScrollableHeight);
                    }
                    foreach (var label in panel.Children.OfType<Grid>().Select(r => (Label)r.Children[0]))
                        Assert.Same(((Grid)label.Parent).Children[1], label.Target);
                    foreach (var text in Visual(panel).OfType<TextBlock>())
                        Assert.True(text.ActualWidth <= root.ActualWidth + 1);
                    Capture(root, $"settings-{(int)width}-{(int)height}-font{font}-tab{index}.png");
                    if (index == 0 && scroll.ScrollableHeight > 0)
                    {
                        scroll.ScrollToEnd();
                        root.UpdateLayout();
                        Capture(root, $"settings-{(int)width}-{(int)height}-font{font}-general-options-scrolled.png");
                    }
                    if (width == 720 && index == 0)
                    {
                        Capture(root, "settings-general-144dpi-simulated.png", 144);
                        Capture(root, "settings-general-192dpi-simulated.png", 192);
                    }
                    if (height == 300)
                    {
                        Assert.True(scroll.ScrollableHeight > 0);
                        scroll.ScrollToEnd();
                        root.UpdateLayout();
                        Capture(root, $"settings-{(int)width}-{(int)height}-font{font}-tab{index}-scrolled.png");
                    }
                }
                var startup = Logical(window).OfType<CheckBox>().Single(c => Equals(c.Content, "Start with Windows"));
                Assert.Contains(startup, Logical(((TabItem)tabs.Items[0]).Content).OfType<CheckBox>());
                DelayField(window).Text = "invalid";
                window.CommitDelay();
                Layout(root, width, height - 40);
                Assert.Equal(2, tabs.SelectedIndex);
                Assert.Contains(Logical(window).OfType<TextBlock>(), t => t.Text.StartsWith("Enter a delay"));
                Capture(root, $"settings-{(int)width}-{(int)height}-font{font}-error.png");
                Assert.DoesNotContain(Logical(window).OfType<Button>(), b => b.Content is "Save" or "Cancel");
                Assert.True(System.Windows.Input.KeyboardNavigation.GetTabIndex(tabs)
                    < System.Windows.Input.KeyboardNavigation.GetTabIndex(Logical(window).OfType<StackPanel>()
                        .Single(p => p.Children.OfType<ScrollViewer>().Any(s => s.Content is TextBlock t &&
                            AutomationProperties.GetLiveSetting(t) == AutomationLiveSetting.Assertive))));
                Assert.DoesNotContain(Logical(window).OfType<Button>(), b => b.Content is string s && s.StartsWith("MissionView v"));
            }
            finally { window.Close(); }
        });

    [Theory]
    [InlineData("Startup help")]
    [InlineData("Overview shortcut help")]
    [InlineData("App Exposé shortcut help")]
    [InlineData("Activation help")]
    public void ContextualHelpKeepsAccessibleDetailsWithoutAnAutomaticPopup(string name)
        => Sta(() =>
        {
            var window = Create();
            try
            {
                var indicator = NamedButton(window, name);
                Assert.Null(indicator.ToolTip);
                Assert.NotEmpty(AutomationProperties.GetHelpText(indicator));
                Assert.Equal("Collapsed", AutomationProperties.GetItemStatus(indicator));
            }
            finally { window.Close(); }
        });

    [Theory]
    [InlineData(1, "Overview shortcut help")]
    [InlineData(2, "Activation help")]
    public void TabActivationFocusesTheHeaderAndKeyboardTraversalStillFocusesHelp(int index, string helpId)
        => Sta(() =>
        {
            var window = Create();
            window.ShowInTaskbar = false;
            try
            {
                window.Show();
                window.UpdateLayout();
                var tabs = Logical(window).OfType<TabControl>().Single();
                var tab = (TabItem)tabs.Items[index];
                tab.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = tab });
                Assert.Same(tab, tabs.SelectedItem);
                Assert.True(tab.IsKeyboardFocused);
                window.UpdateLayout();
                Assert.NotNull(tab.FocusVisualStyle);
                Assert.IsType<Border>(tab.Template.FindName("TabFrame", tab));
                var help = NamedButton(window, helpId);
                var frame = Assert.IsType<Border>(help.Template.FindName("Frame", help));
                Assert.Same(Brushes.Transparent, frame.BorderBrush);
                Assert.True(tab.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.True(help.IsKeyboardFocused);
                var focusBrush = frame.BorderBrush;
                Assert.NotSame(Brushes.Transparent, focusBrush);
                window.UpdateLayout();
                Assert.Same(focusBrush, frame.BorderBrush);
                Assert.Equal("Collapsed", AutomationProperties.GetItemStatus(help));

                Assert.True(window.HandleSearchKey(Key.K, ModifierKeys.Control));
                var input = Logical(window).OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Search settings");
                Assert.True(input.IsKeyboardFocused);
                input.Text = "selection border";
                Assert.False(window.HandleSearchKey(Key.Enter, ModifierKeys.None));
                Assert.Equal(index, tabs.SelectedIndex);
                Assert.True(input.IsKeyboardFocused);
                Assert.True(window.HandleSearchKey(Key.Escape, ModifierKeys.None));
                Assert.Empty(input.Text);
                Assert.True(help.IsKeyboardFocused);
            }
            finally { window.Close(); }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SearchShowsRealRowsWithoutSavingAndRestoresThePreviousTab(bool hasAutomaticUpdates)
        => Sta(() =>
        {
            var source = new AppSettings();
            int saves = 0;
            bool Save(AppSettings candidate)
            {
                saves++;
                source.CopyFrom(candidate);
                return true;
            }
            using var service = new UpdateService(new RejectNetwork(), () => hasAutomaticUpdates);
            using var controller = new UpdateController(service, new(0, 1, 0), () => throw new InvalidOperationException("Search must not shut down."),
                _ => throw new InvalidOperationException("Search must not start an installer."), null, () => true);
            var updates = new UpdatePanel(controller, (_, _) => throw new InvalidOperationException("Search must not ask for consent."), source, Save);
            var window = new SettingsWindow(source, Save,
                updates, () => true, confirmReset: () => throw new InvalidOperationException("Search must not reset."));
            try
            {
                var root = Assert.IsType<DockPanel>(window.Content);
                var field = Logical(root).OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Search settings");
                Assert.Equal("Ctrl+K", AutomationProperties.GetAcceleratorKey(field));
                var tabs = Logical(root).OfType<TabControl>().Single();
                var results = FilteredSettings(root);
                var startup = Logical(window).OfType<CheckBox>().Single(c => Equals(c.Content, "Start with Windows"));
                var originalOwner = ((FrameworkElement)startup.Parent).Parent;
                Layout(root, 360, 340);
                var page = PageScroll(root);
                page.ScrollToEnd();
                root.UpdateLayout();
                double initialOffset = page.VerticalOffset;
                Assert.True(initialOffset > 0);
                field.Text = "startup";
                Layout(root, 360, 340);
                Assert.Equal(Visibility.Collapsed, tabs.Visibility);
                Assert.Same(startup, Assert.Single(Logical(results).OfType<CheckBox>()));
                Assert.Equal(0, saves);
                startup.IsChecked = !source.RunOnStartup;
                Assert.Equal(1, saves);
                Assert.Equal(source.RunOnStartup, startup.IsChecked == true);
                field.Text = "  ";
                Layout(root, 360, 340);
                Assert.Same(originalOwner, ((FrameworkElement)startup.Parent).Parent);
                Assert.Equal(initialOffset, page.VerticalOffset);

                tabs.SelectedIndex = 1;
                ClickRecorder(NamedButton(window, "Overview shortcut"));
                Assert.False(window.HandleSearchKey(Key.K, ModifierKeys.Control));
                Assert.True(window.IsRecordingShortcut);
                tabs.SelectedIndex = 2;
                Assert.False(window.IsRecordingShortcut);

                field.Text = "restore defaults";
                Layout(root, 360, 340);
                Assert.Contains(NamedButton(window, "Reset settings"), Logical(results).OfType<Button>());
                Assert.Equal(2, tabs.SelectedIndex);
                Assert.False(window.HandleSearchKey(Key.Enter, ModifierKeys.None));

                field.Text = "update";
                Assert.Contains(updates.ActionRow, Logical(results));
                field.Text = "auto update";
                Assert.DoesNotContain(updates.ActionRow, Logical(results));
                if (hasAutomaticUpdates)
                {
                    var automatic = Assert.Single(updates.AutomaticRow!.Children.OfType<CheckBox>());
                    Assert.Same(automatic, Assert.Single(Logical(results).OfType<CheckBox>()));
                    Assert.False(automatic.IsChecked);
                    Assert.False(source.InstallUpdatesAutomatically);
                }
                else
                {
                    Assert.Null(updates.AutomaticRow);
                    Assert.Empty(Logical(results).OfType<CheckBox>());
                    var status = Logical(results).OfType<TextBlock>().Single(t => t.Text == "No matching settings");
                    Assert.Equal(Visibility.Visible, status.Visibility);
                }
                Assert.Equal(1, saves);
                Assert.Equal(UpdateStatus.Idle, controller.State.Status);
                Assert.True(window.HandleSearchKey(Key.Escape, ModifierKeys.None));
                Assert.Empty(field.Text);
                Assert.Equal(Visibility.Collapsed, results.Visibility);
                Assert.Equal(Visibility.Visible, tabs.Visibility);
                Assert.Equal(2, tabs.SelectedIndex);
            }
            finally { window.Close(); }
        });

    [Fact]
    public void CompactHelpPreservesDetailsAndInvalidatesRecordingWithoutLeavingAHelpRow()
        => Sta(() =>
        {
            var window = Create();
            try
            {
                var content = (UIElement)window.Content;
                window.Content = null;
                var root = new Border { Background = window.Background, Child = content };
                window.Content = root;
                var tabs = Logical(window).OfType<TabControl>().Single();
                tabs.SelectedIndex = 1;
                Layout(root, 720, 440);
                Assert.Empty(Logical(window).OfType<Expander>());
                var help = NamedButton(window, "Overview shortcut help");
                Assert.True(help.IsTabStop && help.Focusable);
                Assert.NotNull(UIElementAutomationPeer.CreatePeerForElement(help)!.GetPattern(PatternInterface.Invoke));
                Assert.Equal("Overview shortcut help", AutomationProperties.GetName(help));
                Assert.Contains("Escape", AutomationProperties.GetHelpText(help));
                Assert.Null(help.ToolTip);
                var row = (Grid)((Label)((DockPanel)help.Parent).Parent).Parent;
                var detail = row.Children.OfType<TextBlock>().Single(t => !Equals(t.Tag, "ShortcutPreview"));
                Assert.Equal(Visibility.Collapsed, detail.Visibility);
                double closedHeight = row.ActualHeight;
                var recorder = NamedButton(window, "Overview shortcut");
                ClickRecorder(recorder);
                Assert.Equal("Cancel", recorder.Tag);
                Layout(root, 720, 440);
                Capture(root, "settings-recording.png");
                int generation = window.RecordingGeneration;
                ClickRecorder(help);
                Assert.False(window.IsRecordingShortcut);
                Assert.True(window.RecordingGeneration > generation);
                Assert.Equal("Record", recorder.Tag);
                Assert.Equal(Visibility.Visible, detail.Visibility);
                Assert.Equal("Expanded", AutomationProperties.GetItemStatus(help));
                Layout(root, 720, 440);
                Assert.True(row.ActualHeight > closedHeight);
                Capture(root, "settings-contextual-help-open.png");
                window.RecordVirtualKey(0x78, 3, true);
                Assert.Equal("Win+F12", recorder.Content);
                ClickRecorder(help);
                Layout(root, 720, 440);
                Assert.Equal(Visibility.Collapsed, detail.Visibility);
                Assert.Equal("Collapsed", AutomationProperties.GetItemStatus(help));
                Assert.Equal(closedHeight, row.ActualHeight);
                ClickRecorder(recorder);
                generation = window.RecordingGeneration;
                ClickRecorder(recorder);
                Assert.False(window.IsRecordingShortcut);
                Assert.True(window.RecordingGeneration > generation);
                Assert.Equal("Win+F12", recorder.Content);
                Assert.Equal("Record", recorder.Tag);
                Assert.Contains(Visual(recorder).OfType<TextBlock>(), t => t.Text == "Record");
                ClickRecorder(recorder);
                tabs.SelectedIndex = 0;
                Assert.False(window.IsRecordingShortcut);
                Assert.Equal("Win+F12", recorder.Content);
            }
            finally { window.Close(); }
        });

    [Fact]
    public void NativeFieldsKeepTheirPartsAndDarkStatesAndCanUseSystemPaletteWithoutChangingWindows()
        => Sta(() =>
        {
            var window = Create();
            try
            {
                Assert.Equal(480, window.Height);
                var tabs = Logical(window).OfType<TabControl>().Single();
                tabs.SelectedIndex = 2;
                var content = (UIElement)window.Content;
                window.Content = null;
                var root = new Border { Background = window.Background, Child = content };
                window.Content = root;
                Layout(root, 720, 440);
                var combo = Logical(window).OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "Corner");
                var delay = DelayField(window);
                Assert.IsType<ScrollViewer>(delay.Template.FindName("PART_ContentHost", delay));
                Assert.IsType<Popup>(combo.Template.FindName("PART_Popup", combo));
                var comboPeer = UIElementAutomationPeer.CreatePeerForElement(combo)!;
                Assert.NotNull(comboPeer.GetPattern(PatternInterface.ExpandCollapse));
                Assert.NotNull(comboPeer.GetPattern(PatternInterface.Selection));
                combo.SelectedIndex = 3;
                Assert.Equal("BottomRight", ((ComboBoxItem)combo.SelectedItem).Tag);
                Assert.Equal(Color.FromRgb(48, 59, 52), ((SolidColorBrush)combo.Background).Color);
                Assert.Equal(Color.FromRgb(48, 59, 52), ((SolidColorBrush)delay.Background).Color);
                var frame = (Border)delay.Template.FindName("Frame", delay);
                delay.Text = "invalid";
                window.CommitDelay();
                Layout(root, 720, 440);
                Assert.Equal(Color.FromRgb(255, 180, 171), ((SolidColorBrush)frame.BorderBrush).Color);
                Capture(root, "settings-native-fields-error.png");
                delay.Text = "750";
                Layout(root, 720, 440);
                Assert.Null(delay.Tag);
                Assert.NotEqual(Color.FromRgb(255, 180, 171), ((SolidColorBrush)frame.BorderBrush).Color);
                foreach (string ink in new[] { "FieldForeground", "FieldDisabled" })
                    Assert.True(BrushContrast(window.Resources[ink], window.Resources["FieldBackground"]) >= 4.5);
                Assert.True(BrushContrast(window.Resources["FieldSelectionText"], window.Resources["FieldSelection"]) >= 4.5);
                foreach (string edge in new[] { "FieldBorder", "FieldFocus", "FieldError" })
                    Assert.True(BrushContrast(window.Resources[edge], window.Resources["FieldBackground"]) >= 3);
                Logical(window).OfType<CheckBox>().Single(c => Equals(c.Content, "Enable hot corners")).IsChecked = false;
                Layout(root, 720, 440);
                Assert.False(combo.IsEnabled || delay.IsEnabled);
                Assert.Equal(((SolidColorBrush)window.Resources["FieldDisabled"]).Color, ((SolidColorBrush)delay.Foreground).Color);
                Capture(root, "settings-native-fields-disabled.png");
                SettingsControlStyles.Apply(window.Resources, true);
                Layout(root, 720, 440);
                Assert.Equal(SystemColors.WindowColor, ((SolidColorBrush)delay.Background).Color);
                Assert.Equal(SystemColors.GrayTextColor, ((SolidColorBrush)combo.Foreground).Color);
                Logical(window).OfType<CheckBox>().Single(c => Equals(c.Content, "Enable hot corners")).IsChecked = true;
                Layout(root, 720, 440);
                Assert.Equal(SystemColors.WindowTextColor, ((SolidColorBrush)combo.Foreground).Color);
                Assert.Equal(SystemColors.HighlightColor, ((SolidColorBrush)window.Resources["FieldSelection"]).Color);
                Assert.Equal(SystemColors.HighlightTextColor, ((SolidColorBrush)window.Resources["FieldSelectionText"]).Color);
                SettingsControlStyles.Apply(window.Resources, false);
                Layout(root, 720, 440);
                Assert.Equal(Color.FromRgb(48, 59, 52), ((SolidColorBrush)delay.Background).Color);
                var selectedItem = new ComboBoxItem { Content = "Selected corner (isolated item fixture)", IsSelected = true };
                var itemRoot = new Border { Background = window.Background, Resources = window.Resources, Child = selectedItem };
                Layout(itemRoot, 400, 48);
                var itemFrame = (Border)Visual(selectedItem).Single(v => v is Border);
                Assert.Equal(((SolidColorBrush)window.Resources["FieldSelection"]).Color, ((SolidColorBrush)itemFrame.Background).Color);
                Assert.Equal(((SolidColorBrush)window.Resources["FieldSelectionText"]).Color, ((SolidColorBrush)selectedItem.Foreground).Color);
                Capture(itemRoot, "settings-native-selected-item-isolated.png");
            }
            finally { window.Close(); }
        });

    [Fact]
    public void ActualIdleUpdaterFitsTheShorterWindowWithoutRunningAnUpdate()
        => Sta(() =>
        {
            using var service = new UpdateService(new RejectNetwork());
            using var controller = new UpdateController(service, new(0, 1, 0), () => throw new InvalidOperationException("No shutdown in a layout fixture."),
                _ => throw new InvalidOperationException("No installer in a layout fixture."), null, () => true);
            var panel = new UpdatePanel(controller, (_, _) => throw new InvalidOperationException("No confirmation in a layout fixture."));
            var window = new SettingsWindow(new AppSettings { Hotkey = "Win+F12" }, _ => true, panel, () => true);
            try
            {
                var content = (UIElement)window.Content;
                window.Content = null;
                var root = new Border { Background = window.Background, Child = content };
                window.Content = root;
                window.ShowUpdates();
                Layout(root, 720, 440);
                var scroll = PageScroll(root);
                Assert.Equal(0, scroll.ScrollableHeight);
                Assert.Equal(UpdateStatus.Idle, controller.State.Status);
                Assert.True(panel.ActionRow.Children.OfType<Button>().First().IsEnabled);
                Capture(root, "settings-720-480-actual-idle-updates.png");
            }
            finally { window.Close(); }
        });

    private sealed class RejectNetwork : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("No network in a layout fixture.");
    }

    private static double BrushContrast(object a, object b)
    {
        static System.Drawing.Color DrawingColor(object brush)
        {
            var color = ((SolidColorBrush)brush).Color;
            return System.Drawing.Color.FromArgb(color.R, color.G, color.B);
        }
        return Contrast(DrawingColor(a), DrawingColor(b));
    }

    private static void ClickRecorder(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    [Theory]
    [InlineData(720, 14)]
    [InlineData(360, 28)]
    public void NewGeneralControlsRemainReachableWithLargeTextAndExposeTheirValues(double width, double font)
        => Sta(() =>
        {
            var window = Create();
            try
            {
                window.FontSize = font;
                var content = (UIElement)window.Content;
                window.Content = null;
                var root = new Border { Background = window.Background, Child = content };
                window.Content = root;
                Layout(root, width, 440);
                var outer = PageScroll(root);
                var dimming = Logical(window).OfType<Slider>().Single();
                dimming.Value = 78.5;
                dimming.BringIntoView();
                root.UpdateLayout();
                Assert.Contains(Visual(root).OfType<TextBlock>(), t => t.Text == "79% dimming");
                foreach (var control in new Control[] { dimming, NamedButton(window, "Reset settings") })
                {
                    control.BringIntoView();
                    root.UpdateLayout();
                    var point = control.TranslatePoint(new Point(), outer);
                    Assert.InRange(point.Y, 0, outer.ViewportHeight);
                    Assert.True(point.Y + control.ActualHeight <= outer.ViewportHeight + 1);
                    Assert.True(control.IsEnabled && control.Focusable);
                    Capture(root, $"settings-{(int)width}-font{font}-{(control is Slider ? "dimming" : "reset")}-reachable-offscreen.png");
                }
            }
            finally { window.Close(); }
        });

    [Fact]
    public void EnlargedTextDelayErrorKeepsAnEditableFieldInTheScrollableViewport()
        => Sta(() =>
        {
            AppSettings? saved = null;
            var window = new SettingsWindow(new AppSettings { Hotkey = "Win+F12" },
                candidate => { saved = candidate; return true; }, new TextBlock(), () => true) { FontSize = 28 };
            try
            {
                var content = (UIElement)window.Content;
                window.Content = null;
                var root = new Border { Background = window.Background, Child = content };
                window.Content = root;
                var delay = DelayField(window);
                Layout(root, 360, 440);
                delay.Text = "invalid";
                window.CommitDelay();
                Layout(root, 360, 440);
                var scroll = PageScroll(root);
                Assert.True(scroll.ViewportHeight > 0);
                Assert.True(delay.ActualHeight > 0);
                Assert.True(scroll.ScrollableHeight > 0);
                delay.BringIntoView();
                root.UpdateLayout();
                var point = delay.TranslatePoint(new Point(), scroll);
                Assert.InRange(point.Y, 0, scroll.ViewportHeight);
                Assert.True(point.Y + delay.ActualHeight <= scroll.ViewportHeight + 1);
                Assert.True(delay.IsEnabled && delay.Focusable && delay.IsTabStop);
                Capture(root, "settings-360-480-font28-error-field-revealed.png");
                delay.Text = "750";
                window.CommitDelay();
                Assert.Equal(750, saved?.HotCornerDelay);
            }
            finally { window.Close(); }
        });

    [Fact]
    public void RecorderAutomationNamesExposeTheChordAndRecordingOrErrorState()
        => Sta(() =>
        {
            var window = Create();
            try
            {
                var normal = Logical(window).OfType<Button>().Single(b => Equals(b.Content, "Win+F12"));
                var app = Logical(window).OfType<Button>().Single(b => Equals(b.Content, "Win+Shift+F12"));
                var normalPeer = UIElementAutomationPeer.CreatePeerForElement(normal)!;
                var appPeer = UIElementAutomationPeer.CreatePeerForElement(app)!;
                Assert.Equal("Overview shortcut: Win+F12", normalPeer.GetName());
                Assert.Equal("App Exposé shortcut: Win+Shift+F12", appPeer.GetName());
                normal.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Contains("Recording", normalPeer.GetName());
                Assert.Contains("Win+F12", normalPeer.GetName());
                window.RecordVirtualKey(0x78, 0, true);
                Assert.Contains("Include Ctrl, Alt or Win", normalPeer.GetName());
                window.RecordVirtualKey(0x1B, 0, true);
                Assert.Equal("Overview shortcut: Win+F12", normalPeer.GetName());
                app.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Contains("Recording", appPeer.GetName());
                window.RecordVirtualKey(0x7B, 8, true);
                Assert.Contains("different shortcuts", appPeer.GetName());
                Assert.Equal("Overview shortcut: Win+F12", normalPeer.GetName());
                window.RecordVirtualKey(0x78, 3, true);
                Assert.Equal("App Exposé shortcut: Ctrl+Alt+F9", appPeer.GetName());
                normal.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                window.RecordVirtualKey(0x77, 12, true);
                Assert.Equal("Overview shortcut: Win+Shift+F8", normalPeer.GetName());
                app.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                window.RecordVirtualKey(0x1B, 0, true);
                Assert.Equal("App Exposé shortcut: Ctrl+Alt+F9", appPeer.GetName());
            }
            finally { window.Close(); }
        });

    [Fact]
    public void DelayErrorSelectsItsCategoryAndCancelNeverSavesPendingChanges()
        => Sta(() =>
        {
            int calls = 0;
            var window = new SettingsWindow(new AppSettings(), _ => { calls++; return true; }, new TextBlock(), () => true);
            try
            {
                var tabs = Logical(window).OfType<TabControl>().Single();
                var delay = DelayField(window);
                delay.Text = "invalid";
                window.CommitDelay();
                Assert.Equal(2, tabs.SelectedIndex);
                Assert.Equal(0, calls);
                window.Close();
                Assert.Equal(0, calls);
            }
            finally { window.Close(); }
        });

    [Theory]
    [InlineData(null)]
    [InlineData("Ctrl+Alt+F9")]
    public void SavingOtherPreferencesPreservesNullableShortcut(string? appShortcut)
        => Sta(() =>
        {
            AppSettings? saved = null;
            var source = new AppSettings { Hotkey = "Win+F12", AppExposeHotkey = appShortcut };
            var window = new SettingsWindow(source, value => { saved = value; return true; }, new TextBlock(), () => true);
            try
            {
                DelayField(window).Text = "725";
                window.CommitDelay();
                Assert.NotNull(saved);
                Assert.Equal(appShortcut, saved.AppExposeHotkey);
                Assert.Equal("Win+F12", saved.Hotkey);
                Assert.Equal(725, saved.HotCornerDelay);
            }
            finally { window.Close(); }
        });

    [Fact]
    public void IndependentRecorderValidatesThePairAndAllowsShiftOverviewWithoutLosingGenerationGuard()
        => Sta(() =>
        {
            AppSettings? saved = null;
            var source = new AppSettings { Hotkey = "Win+F12" };
            var window = new SettingsWindow(source,
                value => { saved = value; source.CopyFrom(value); return true; }, new TextBlock(), () => true);
            try
            {
                var normal = NamedButton(window, "Overview shortcut");
                var app = NamedButton(window, "App Exposé shortcut");
                normal.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                int firstGeneration = window.RecordingGeneration;
                app.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.True(window.RecordingGeneration > firstGeneration);
                Assert.Equal("Win+F12", normal.Content);
                window.RecordVirtualKey(0x7B, 8, true);
                Assert.True(window.IsRecordingShortcut);
                Assert.Contains(Logical(window).OfType<TextBlock>(), t => t.Text.Contains("different shortcuts"));
                window.RecordVirtualKey(0x78, 4, true);
                Assert.True(window.IsRecordingShortcut);
                window.RecordVirtualKey(0x78, 3, true);
                Assert.False(window.IsRecordingShortcut);
                Assert.Equal("Ctrl+Alt+F9", app.Content);
                normal.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                window.RecordVirtualKey(0x77, 12, true);
                Assert.False(window.IsRecordingShortcut);
                Assert.Equal("Win+Shift+F8", normal.Content);
                Assert.Equal("Win+Shift+F8", saved?.Hotkey);
                Assert.Equal("Ctrl+Alt+F9", saved?.AppExposeHotkey);
            }
            finally { window.Close(); }
        });

    [Fact]
    public void FailedApplyRestoresBothRecorderValuesAndCancellingRecordingRetainsLegacyNull()
        => Sta(() =>
        {
            AppSettings? attempted = null;
            var window = new SettingsWindow(new AppSettings { Hotkey = "Win+F12" },
                value => { attempted = value; return false; }, new TextBlock(), () => true);
            try
            {
                var app = NamedButton(window, "App Exposé shortcut");
                app.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                window.RecordVirtualKey(0x78, 3, true);
                Assert.Equal("Ctrl+Alt+F9", attempted?.AppExposeHotkey);
                Assert.Equal("Win+Shift+F12", app.Content);
                app.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                int generation = window.RecordingGeneration;
                window.RecordVirtualKey(0x1B, 0, true);
                Assert.True(window.RecordingGeneration > generation);
                Assert.False(window.IsRecordingShortcut);
                Assert.NotNull(attempted);
                Assert.Equal("Ctrl+Alt+F9", attempted.AppExposeHotkey);
                Assert.Equal("Win+F12", attempted.Hotkey);
            }
            finally { window.Close(); }
        });

    [Fact]
    public void TrayStatePairsAvoidTheLightBlueWhiteTextRegression()
    {
        foreach (bool selected in new[] { false, true })
        foreach (bool pressed in new[] { false, true })
        foreach (bool isChecked in new[] { false, true })
        foreach (bool enabled in new[] { false, true })
        {
            var pair = MissionViewMenuRenderer.Palette(selected, pressed, isChecked, enabled, false);
            Assert.True(Contrast(pair.Background, pair.Foreground) >= 4.5);
            var hc = MissionViewMenuRenderer.Palette(selected, pressed, isChecked, enabled, true);
            Assert.Equal(enabled && (selected || pressed) ? System.Drawing.SystemColors.Highlight : System.Drawing.SystemColors.Menu, hc.Background);
            Assert.Equal(enabled && (selected || pressed) ? System.Drawing.SystemColors.HighlightText
                : enabled ? System.Drawing.SystemColors.MenuText : System.Drawing.SystemColors.GrayText, hc.Foreground);
        }
        var version = MissionViewMenuRenderer.Palette(false, false, false, false, false, true);
        Assert.True(Contrast(version.Background, version.Foreground) >= 4.5);
    }

    [Fact]
    public void TrayPaintUsesItsPairedSelectedColorsInsteadOfDefaultGradient()
        => Sta(() =>
        {
            using var menu = new System.Windows.Forms.ContextMenuStrip();
            var renderer = new MissionViewMenuRenderer();
            menu.Renderer = renderer;
            using var item = new System.Windows.Forms.ToolStripMenuItem("Open overview")
                { AutoSize = false, Size = new System.Drawing.Size(290, 34) };
            menu.Items.Add(item);
            item.Select();
            Assert.True(item.Selected);
            using var image = new System.Drawing.Bitmap(300, 44);
            using var graphics = System.Drawing.Graphics.FromImage(image);
            renderer.DrawMenuItemBackground(new System.Windows.Forms.ToolStripItemRenderEventArgs(graphics, item));
            renderer.DrawItemText(new System.Windows.Forms.ToolStripItemTextRenderEventArgs(graphics, item, item.Text,
                new System.Drawing.Rectangle(12, 4, 270, 26), System.Drawing.Color.Magenta, item.Font,
                System.Windows.Forms.TextFormatFlags.Left | System.Windows.Forms.TextFormatFlags.VerticalCenter));
            var expected = MissionViewMenuRenderer.Palette(true, false, false, true, System.Windows.Forms.SystemInformation.HighContrast);
            Assert.Equal(expected.Background.ToArgb(), image.GetPixel(6, 6).ToArgb());
            string? directory = Environment.GetEnvironmentVariable("MISSIONVIEW_SETTINGS_FIXTURES");
            if (directory != null)
            {
                System.IO.Directory.CreateDirectory(directory);
                image.Save(System.IO.Path.Combine(directory, "tray-selected.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
        });

    private static double Contrast(System.Drawing.Color a, System.Drawing.Color b)
    {
        static double L(System.Drawing.Color c)
        {
            static double Linear(byte v) { double s = v / 255.0; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }
        double x = L(a), y = L(b);
        return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }

    private static SettingsWindow Create()
    {
        var source = new AppSettings { Hotkey = "Win+F12" };
        return new(source, value => { source.CopyFrom(value); return true; },
            new StackPanel { Children = { new Button { Content = "Check for updates", MinHeight = 40,
                Padding = new Thickness(16, 10, 16, 10) } } }, () => true);
    }

    private static void Layout(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    private static void Capture(FrameworkElement root, string name, double dpi = 96)
    {
        string? directory = Environment.GetEnvironmentVariable("MISSIONVIEW_SETTINGS_FIXTURES");
        if (directory == null) return;
        System.IO.Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)(root.ActualWidth * dpi / 96), (int)(root.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(System.IO.Path.Combine(directory, name));
        encoder.Save(stream);
    }

    private static void Click(Window window, string caption) => Logical(window).OfType<Button>().Single(b => Equals(b.Content, caption))
        .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    private static Button NamedButton(Window window, string name) =>
        Logical(window).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == name);
    private static TextBox DelayField(Window window) =>
        Logical(window).OfType<TextBox>().Single(t => AutomationProperties.GetAutomationId(t) == "Delay (milliseconds)");

    private static IEnumerable<DependencyObject> Logical(object parent)
    {
        if (parent is not DependencyObject dependency) yield break;
        foreach (var child in LogicalTreeHelper.GetChildren(dependency).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var next in Logical(child)) yield return next;
        }
    }

    private static ScrollViewer PageScroll(DependencyObject root)
    {
        var tabs = Assert.Single(Logical(root).OfType<TabControl>());
        return Assert.IsType<ScrollViewer>(tabs.Template.FindName("PageScroll", tabs));
    }

    private static ScrollViewer FilteredSettings(DependencyObject root) =>
        Assert.Single(Logical(root).OfType<ScrollViewer>().Where(scroll => scroll.Name == "FilteredSettings"));

    private static IEnumerable<DependencyObject> Visual(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var next in Visual(child)) yield return next;
        }
    }

    private static void Sta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
    }
}
