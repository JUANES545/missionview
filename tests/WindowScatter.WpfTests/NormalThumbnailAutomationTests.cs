using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Interop;
using System.Windows.Threading;
using WindowScatter;
using Xunit;

public class NormalThumbnailAutomationTests
{
    [Fact]
    public void ProductionRegistrationPublishesFullTitleAndActionablePeerWithoutChangingVisualTitle()
        => RunSta(() =>
        {
            using var app = new MainWindow();
            string title = "Full title " + new string('x', 118);
            var thumb = Assert.Single(app.RegisterNormalWindows(title));
            Assert.Equal(129, title.Length);
            Assert.Equal(80, thumb.TitleLabel.Text.Length);
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
