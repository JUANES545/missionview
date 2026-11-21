using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using WindowScatter;
using Xunit;

public class DesktopButtonTests
{
    [Fact]
    public void DetachedChromeRetainsItsReadableForegroundAndFontWithoutTheOverviewHost()
        => RunSta(() =>
        {
            var host = LoadWindow();
            try
            {
                var chrome = (Canvas)host.FindName("ChromeCanvas");
                ((Canvas)host.Content).Children.Remove(chrome);
                var separateSurface = new Border { Child = chrome };
                separateSurface.Measure(new Size(720, 480));
                separateSurface.Arrange(new Rect(0, 0, 720, 480));
                separateSurface.UpdateLayout();
                var status = (TextBlock)host.FindName("MoveStatus");
                var button = (Button)host.FindName("DesktopButton");
                Assert.Equal(SystemParameters.HighContrast ? SystemColors.WindowTextColor : SharedBrushes.Text.Color,
                    ((SolidColorBrush)status.Foreground).Color);
                Assert.Equal(((SolidColorBrush)status.Foreground).Color, ((SolidColorBrush)button.Foreground).Color);
                Assert.Equal("Segoe UI Variable, Segoe UI", status.FontFamily.Source);
                Assert.Equal(((SolidColorBrush)button.Foreground).Color,
                    ((SolidColorBrush)((System.Windows.Shapes.Path)button.Content).Stroke).Color);
            }
            finally { host.Close(); }
        });

    [Fact]
    public void ActualXamlUsesDecorativeVectorInsideNamedMouseAndAutomationButton()
        => RunSta(() =>
        {
            var host = LoadWindow();
            try
            {
                var button = (Button)host.FindName("DesktopButton");
                var icon = Assert.IsType<System.Windows.Shapes.Path>(button.Content);
                Assert.Equal((44d, 44d), (button.Width, button.Height));
                Assert.Equal((24d, 24d), (icon.Width, icon.Height));
                Assert.False(icon.Focusable);
                Assert.False(icon.IsHitTestVisible);
                Assert.Null(UIElementAutomationPeer.CreatePeerForElement(icon));
                Assert.False(button.Focusable);
                Assert.False(button.IsTabStop);
                Assert.Equal((16d, 16d), (Canvas.GetLeft(button), Canvas.GetTop(button)));
                Assert.Equal("Show desktop; press the activation shortcut again to restore windows.", button.ToolTip);
                var peer = new ButtonAutomationPeer(button);
                Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
                Assert.Equal("Show desktop", peer.GetName());
                Assert.Empty(peer.GetChildren() ?? []);
                button.ApplyTemplate();
                var frame = Assert.IsType<Border>(button.Template.FindName("Frame", button));
                Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(frame.BorderBrush).Color);
            }
            finally { host.Close(); }
        });

    [Fact]
    public void DesktopButtonKeepsNeutralBorderAcrossHoverPressAndLegacyFocusStates()
        => RunSta(() =>
        {
            var host = LoadWindow(controllableButtonStates: true);
            try
            {
                var button = (Button)host.FindName("DesktopButton");
                button.ApplyTemplate();
                var frame = Assert.IsType<Border>(button.Template.FindName("Frame", button));
                foreach (string state in new[] { "Normal", "Hover", "Focus", "Pressed" })
                {
                    button.Tag = state;
                    button.UpdateLayout();
                    Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(frame.BorderBrush).Color);
                    Assert.Equal(new Thickness(2), frame.BorderThickness);
                    Assert.Equal(state == "Hover" ? Color.FromRgb(64, 64, 64) : Color.FromRgb(32, 32, 32),
                        Assert.IsType<SolidColorBrush>(frame.Background).Color);
                }
                Assert.Null(button.FocusVisualStyle);
            }
            finally { host.Close(); }
        });

    [Fact]
    public void DesktopActionCannotTakeTabFocusOrSpaceButRetainsAutomationInvoke()
        => RunSta(() =>
        {
            var host = LoadWindow();
            var content = (Canvas)host.FindName("ChromeCanvas");
            var thumbnails = Assert.IsType<OverviewThumbnailCanvas>(host.FindName("ScatterCanvas"));
            Assert.NotNull(UIElementAutomationPeer.CreatePeerForElement(thumbnails).GetPattern(PatternInterface.Selection));
            var button = (Button)host.FindName("DesktopButton");
            ((Canvas)host.Content).Children.Remove(content);
            var chrome = new OverviewChromeWindow(host, content)
            {
                Topmost = false, ShowActivated = false,
                Left = -32000, Top = -32000, Width = 320, Height = 200
            };
            try
            {
                new System.Windows.Interop.WindowInteropHelper(host).EnsureHandle();
                chrome.Show();
                chrome.UpdateLayout();
                var peer = new ButtonAutomationPeer(button);
                Assert.False(peer.IsKeyboardFocusable());
                Assert.False(button.Focus());
                Assert.False(button.IsKeyboardFocusWithin);
                int clicked = 0;
                button.Click += (_, _) => clicked++;
                var source = PresentationSource.FromVisual(button)!;
                void PressSpace()
                {
                    foreach (var routedEvent in new[] { Keyboard.KeyDownEvent, Keyboard.KeyUpEvent })
                    {
                        var input = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = routedEvent };
                        button.RaiseEvent(input);
                        Assert.True(input.Handled);
                        Assert.False(button.IsPressed);
                    }
                }
                PressSpace();
                Assert.Equal(0, clicked);
                Assert.Equal("Show desktop", peer.GetName());
                Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(1, clicked);
                PressSpace();
                Assert.Equal(1, clicked);
                Assert.NotEqual(new System.Windows.Interop.WindowInteropHelper(chrome).Handle, Win32Interop.GetForegroundWindow());
                Assert.NotEqual(new System.Windows.Interop.WindowInteropHelper(host).Handle, Win32Interop.GetForegroundWindow());
            }
            finally { chrome.Close(); host.Close(); }
        });

    [Fact]
    public void MoveFeedbackRetainsStatusWithoutADisplaySizedOutlineLayer()
        => RunSta(() =>
        {
            var host = LoadWindow();
            try
            {
                var content = (Canvas)host.FindName("ChromeCanvas");
                var status = (TextBlock)host.FindName("MoveStatus");
                content.Width = 800;
                content.Height = 600;
                foreach (var frame in content.Children.OfType<Border>())
                {
                    frame.Width = 784;
                    frame.Height = 520;
                    frame.Visibility = Visibility.Visible;
                }
                foreach (string message in new[] { "Release to move here", "Display already shows 20 windows", "Move to another display; Esc cancels" })
                {
                    status.Text = message;
                    content.Measure(new Size(800, 600));
                    content.Arrange(new Rect(0, 0, 800, 600));
                    content.UpdateLayout();
                    Assert.Equal(Visibility.Visible, status.Visibility);
                    Assert.DoesNotContain(content.Children.OfType<Border>(), frame =>
                        frame.Visibility == Visibility.Visible && frame.BorderBrush != null &&
                        frame.BorderThickness != new Thickness(0) && frame.ActualWidth > 700 && frame.ActualHeight > 500);
                }
            }
            finally { host.Close(); }
        });

    [Fact]
    public void RegisteredAccessKeyReachesButtonInOwnedSurfaceAndCanBeUnregistered()
        => RunSta(() =>
        {
            var host = LoadWindow();
            var content = (Canvas)host.FindName("ChromeCanvas");
            var button = (Button)host.FindName("DesktopButton");
            ((Canvas)host.Content).Children.Remove(content);
            var chrome = new OverviewChromeWindow(host, content)
            {
                Topmost = false, ShowActivated = false,
                Left = -32000, Top = -32000, Width = 320, Height = 200
            };
            AccessKeyManager.Register("S", button);
            try
            {
                new System.Windows.Interop.WindowInteropHelper(host).EnsureHandle();
                chrome.Show();
                chrome.UpdateLayout();
                var source = PresentationSource.FromVisual(button);
                Assert.NotNull(source);
                Assert.True(AccessKeyManager.IsKeyRegistered(source, "S"));
                int clicked = 0;
                button.Click += (_, _) => clicked++;
                AccessKeyManager.ProcessKey(source, "S", false);
                Assert.Equal(1, clicked);
                var hit = chrome.InputHitTest(new Point(38, 38)) as DependencyObject;
                while (hit != null && hit != button) hit = VisualTreeHelper.GetParent(hit);
                Assert.Same(button, hit);
                AccessKeyManager.Unregister("S", button);
                Assert.False(AccessKeyManager.IsKeyRegistered(source, "S"));
            }
            finally
            {
                AccessKeyManager.Unregister("S", button);
                chrome.Close();
                host.Close();
            }
        });

    [Fact]
    public void IdleChromeHasOnlyDesktopActionAndShowsMoveFeedbackOnlyWhenNeeded()
        => RunSta(() =>
        {
            var host = LoadWindow();
            try
            {
                var content = (Canvas)host.FindName("ChromeCanvas");
                Assert.Null(host.FindName("MoveButton"));
                Assert.Same(host.FindName("DesktopButton"), Assert.Single(content.Children.OfType<Button>()));
                var status = (TextBlock)host.FindName("MoveStatus");
                Assert.Empty(status.Text);
                Assert.Equal(Visibility.Collapsed, status.Visibility);
                Assert.False(status.Focusable);
                Assert.False(status.IsHitTestVisible);
                Assert.Equal(72, Canvas.GetLeft(status));
                foreach (string feedback in new[] { "Release to move here", "Moving window...", "Window moved", "Some previews unavailable" })
                {
                    status.Text = feedback;
                    Assert.Equal(Visibility.Visible, status.Visibility);
                    status.Text = "";
                    Assert.Equal(Visibility.Collapsed, status.Visibility);
                }
            }
            finally { host.Close(); }
        });

    [Theory]
    [InlineData(Key.Apps, Key.None, ModifierKeys.None, true)]
    [InlineData(Key.F10, Key.None, ModifierKeys.Shift, true)]
    [InlineData(Key.System, Key.M, ModifierKeys.Alt, true)]
    [InlineData(Key.M, Key.None, ModifierKeys.Alt, true)]
    [InlineData(Key.M, Key.None, ModifierKeys.None, false)]
    [InlineData(Key.M, Key.None, ModifierKeys.Control, false)]
    [InlineData(Key.System, Key.M, ModifierKeys.Alt | ModifierKeys.Control, false)]
    [InlineData(Key.F10, Key.None, ModifierKeys.None, false)]
    [InlineData(Key.Tab, Key.None, ModifierKeys.None, false)]
    [InlineData(Key.Space, Key.None, ModifierKeys.None, false)]
    public void KeyboardMoveAlternativesWorkWithoutAVisibleOrHiddenButton(Key key, Key systemKey, ModifierKeys modifiers, bool expected)
        => Assert.Equal(expected, OverviewMoveShortcut.Matches(key, systemKey, modifiers));

    private static Window LoadWindow(bool controllableButtonStates = false)
    {
        using var stream = typeof(DesktopButtonTests).Assembly.GetManifestResourceStream("MainWindow.xaml")!;
        var document = XDocument.Load(stream);
        document.Root!.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))!.Remove();
        string controlsNamespace = $"clr-namespace:WindowScatter;assembly={typeof(OverviewChromeWindow).Assembly.GetName().Name}";
        foreach (var element in document.Descendants().Where(e => e.Name.NamespaceName == "clr-namespace:WindowScatter"))
            element.Name = XName.Get(element.Name.LocalName, controlsNamespace);
        document.Root.SetAttributeValue(XNamespace.Xmlns + "local", controlsNamespace);
        if (controllableButtonStates)
        {
            // Drive the real template setters without moving the mouse or giving a fixture keyboard focus.
            foreach (var trigger in document.Descendants().Where(e => e.Name.LocalName == "Trigger"))
            {
                string? state = trigger.Attribute("Property")?.Value switch
                {
                    "IsMouseOver" => "Hover", "IsKeyboardFocused" => "Focus", "IsPressed" => "Pressed", _ => null
                };
                if (state == null) continue;
                trigger.SetAttributeValue("Property", "Tag");
                trigger.SetAttributeValue("Value", state);
            }
        }
        foreach (var attribute in document.Descendants().Attributes().Where(a =>
            a.Name.LocalName is "PreviewKeyDown" or "Click" or "MouseLeftButtonDown").ToArray())
            attribute.Remove();
        return (Window)XamlReader.Parse(document.ToString());
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
