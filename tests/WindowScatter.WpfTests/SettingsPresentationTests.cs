using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowScatter;
using Xunit;

public sealed class SettingsPresentationTests
{
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
                    var panel = (StackPanel)((ScrollViewer)((TabItem)tabs.Items[index]).Content).Content;
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
                Assert.Equal(new[] { "General", "Shortcuts", "Hot corners", "Updates & About" },
                    tabs.Items.Cast<TabItem>().Select(t => t.Header));
                var content = (UIElement)window.Content;
                window.Content = null;
                var root = new Border { Background = window.Background, Child = content };
                window.Content = root;
                for (int index = 0; index < 4; index++)
                {
                    tabs.SelectedIndex = index;
                    Layout(root, width, height - 40);
                    var panel = (StackPanel)((ScrollViewer)((TabItem)tabs.Items[index]).Content).Content;
                    foreach (var row in panel.Children.OfType<Grid>())
                    {
                        Assert.Equal(narrow ? 1 : 0, Grid.GetRow(row.Children[1]));
                        Assert.True(((FrameworkElement)row.Children[1]).ActualWidth <= root.ActualWidth);
                    }
                    var scroll = (ScrollViewer)((TabItem)tabs.Items[index]).Content;
                    if (width == 720)
                    {
                        if (index == 0) Assert.True(scroll.ScrollableHeight > 0,
                            $"General extent={scroll.ExtentHeight}, viewport={scroll.ViewportHeight}, panel={panel.ActualHeight}, outer={Visual(root).OfType<ScrollViewer>().Single(s => s.Content is DockPanel).ScrollableHeight}");
                        else Assert.Equal(0, scroll.ScrollableHeight);
                        Assert.Equal(0, Visual(root).OfType<ScrollViewer>().Single(s => s.Content is DockPanel).ScrollableHeight);
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
                        var fallback = Visual(root).OfType<ScrollViewer>().Single(s => s.Content is DockPanel);
                        Assert.True(fallback.ScrollableHeight > 0);
                        fallback.ScrollToEnd();
                        root.UpdateLayout();
                        Capture(root, $"settings-{(int)width}-{(int)height}-font{font}-tab{index}-scrolled.png");
                    }
                }
                var startup = Logical(window).OfType<CheckBox>().Single(c => Equals(c.Content, "Start with Windows"));
                Assert.Contains(startup, Logical(((TabItem)tabs.Items[0]).Content).OfType<CheckBox>());
                Logical(window).OfType<TextBox>().Single().Text = "invalid";
                Click(window, "Save");
                Layout(root, width, height - 40);
                Assert.Equal(2, tabs.SelectedIndex);
                Assert.Contains(Logical(window).OfType<TextBlock>(), t => t.Text.StartsWith("Enter a delay"));
                Capture(root, $"settings-{(int)width}-{(int)height}-font{font}-error.png");
                foreach (var caption in new[] { "Cancel", "Save" })
                {
                    var action = Logical(window).OfType<Button>().Single(b => Equals(b.Content, caption));
                    var point = action.TranslatePoint(new Point(), root);
                    Assert.InRange(point.Y, 0, root.ActualHeight);
                    Assert.True(point.Y + action.ActualHeight <= root.ActualHeight + 1);
                    Assert.True(action.IsEnabled && action.Focusable && action.IsTabStop);
                }
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
    public void ContextualTooltipsAnchorBelowTheirOwnIndicatorWithoutOpeningAWindow(string name)
        => Sta(() =>
        {
            var window = Create();
            try
            {
                var indicator = NamedButton(window, name);
                var tooltip = Assert.IsType<ToolTip>(indicator.ToolTip);
                Assert.Equal(((UIElement?)indicator, PlacementMode.Bottom),
                    (tooltip.PlacementTarget, tooltip.Placement));
                Assert.False(tooltip.IsOpen);
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
                Assert.IsType<ToolTip>(help.ToolTip);
                var row = (Grid)((Label)((DockPanel)help.Parent).Parent).Parent;
                var detail = row.Children.OfType<TextBlock>().Single();
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
                var delay = Logical(window).OfType<TextBox>().Single();
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
                Click(window, "Save");
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
                var scroll = (ScrollViewer)((TabItem)Logical(window).OfType<TabControl>().Single().SelectedItem).Content;
                Assert.Equal(0, scroll.ScrollableHeight);
                Assert.Equal(0, Visual(root).OfType<ScrollViewer>().Single(s => s.Content is DockPanel).ScrollableHeight);
                Assert.Equal(UpdateStatus.Idle, controller.State.Status);
                Assert.True(panel.Children.OfType<Button>().First().IsEnabled);
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
                var outer = Visual(root).OfType<ScrollViewer>().Single(s => s.Content is DockPanel);
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
                var delay = Logical(window).OfType<TextBox>().Single();
                Layout(root, 360, 440);
                delay.Text = "invalid";
                Click(window, "Save");
                Layout(root, 360, 440);
                var scroll = Visual(root).OfType<ScrollViewer>().Single(s => s.Content is DockPanel);
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
                foreach (string caption in new[] { "Save", "Cancel" })
                {
                    var action = Logical(window).OfType<Button>().Single(b => Equals(b.Content, caption));
                    var top = action.TranslatePoint(new Point(), root).Y;
                    Assert.InRange(top, 0, root.ActualHeight - action.ActualHeight + 1);
                }
                delay.Text = "750";
                Click(window, "Save");
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
                var delay = Logical(window).OfType<TextBox>().Single();
                delay.Text = "invalid";
                Click(window, "Save");
                Assert.Equal(2, tabs.SelectedIndex);
                Assert.Equal(0, calls);
                Click(window, "Cancel");
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
                Logical(window).OfType<TextBox>().Single().Text = "725";
                Click(window, "Save");
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
            var window = new SettingsWindow(new AppSettings { Hotkey = "Win+F12" },
                value => { saved = value; return true; }, new TextBlock(), () => true);
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
                Click(window, "Save");
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
                Click(window, "Save");
                Assert.Equal("Ctrl+Alt+F9", attempted?.AppExposeHotkey);
                Assert.Equal("Win+Shift+F12", app.Content);
                app.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                int generation = window.RecordingGeneration;
                window.RecordVirtualKey(0x1B, 0, true);
                Assert.True(window.RecordingGeneration > generation);
                Assert.False(window.IsRecordingShortcut);
                Click(window, "Save");
                Assert.NotNull(attempted);
                Assert.Null(attempted.AppExposeHotkey);
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

    private static SettingsWindow Create() => new(new AppSettings { Hotkey = "Win+F12" }, _ => true,
        new StackPanel { Children = { new TextBlock { Text = "No update check has run.", TextWrapping = TextWrapping.Wrap },
            new Button { Content = "Check for updates..." } } }, () => true);

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

    private static IEnumerable<DependencyObject> Logical(object parent)
    {
        if (parent is not DependencyObject dependency) yield break;
        foreach (var child in LogicalTreeHelper.GetChildren(dependency).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var next in Logical(child)) yield return next;
        }
    }

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
