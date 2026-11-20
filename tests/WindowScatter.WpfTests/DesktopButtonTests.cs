using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
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
    public void ActualXamlUsesDecorativeVectorInsideNamedKeyboardAccessibleButton()
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
                Assert.True(button.Focusable);
                Assert.True(button.IsTabStop);
                Assert.Equal((16d, 16d), (Canvas.GetLeft(button), Canvas.GetTop(button)));
                Assert.Equal("Show desktop; press the activation shortcut again to restore windows.", button.ToolTip);
                var peer = new ButtonAutomationPeer(button);
                Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
                Assert.Equal("Show desktop", peer.GetName());
                Assert.Empty(peer.GetChildren() ?? []);
                button.ApplyTemplate();
                Assert.NotNull(button.Template.Triggers.Cast<Trigger>().Single(t =>
                    t.Property == UIElement.IsKeyboardFocusedProperty));
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

    private static Window LoadWindow()
    {
        using var stream = typeof(DesktopButtonTests).Assembly.GetManifestResourceStream("MainWindow.xaml")!;
        var document = XDocument.Load(stream);
        document.Root!.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))!.Remove();
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
