using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using static WindowScatter.Win32Interop;

namespace WindowScatter;

public partial class MainWindow
{
    private OverviewFileDrag? fileDrag;
    private POINT fileDragStart;
    private DispatcherTimer? fileDragReleaseCheck;
    private int fileDragObservation;

    private static bool IsFileDragButtonDown() => GetAsyncKeyState(VK_LBUTTON) < 0;

    private bool BlocksThumbnailInput => coordinator.fileDrag != null;

    private void BeginFileDrag(POINT point)
    {
        fileDrag = new OverviewFileDrag();
        fileDragStart = point;
        foreach (var view in views) view.chromeWindow.AllowDrop = true;
        fileDragReleaseCheck ??= new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Input,
            (_, _) => { if (!IsFileDragButtonDown()) StopFileDragHover(); }, Dispatcher);
        fileDragReleaseCheck.Start();
    }

    private void ConfigureFileDragInput()
    {
        chromeWindow.PreviewDragEnter += ObserveFileDrag;
        chromeWindow.PreviewDragOver += ObserveFileDrag;
        chromeWindow.PreviewDragLeave += EndFileDragHover;
        chromeWindow.PreviewDrop += EndFileDragHover;
    }

    private void ObserveFileDrag(object sender, DragEventArgs e)
    {
        if (coordinator.fileDrag is not { } state || !chromeWindow.AllowDrop) return;
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        int observation = ++coordinator.fileDragObservation;
        WindowActivationTarget? target = null;
        if (!e.KeyStates.HasFlag(DragDropKeyStates.LeftMouseButton))
        {
            coordinator.StopFileDragHover();
            return;
        }
        if (!state.Cancelled && FileDragReady && !e.KeyStates.HasFlag(DragDropKeyStates.RightMouseButton))
        {
            Point point = e.GetPosition(chromeWindow);
            Point screen = chromeWindow.PointToScreen(point);
            if (Math.Round(screen.X) != coordinator.fileDragStart.X || Math.Round(screen.Y) != coordinator.fileDragStart.Y)
            {
                try
                {
                    if (e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: false) &&
                        ReferenceEquals(coordinator.fileDrag, state) && FileDragReady)
                        target = FileDragHit(point);
                }
                catch (ExternalException error)
                {
                    Trace.TraceWarning($"Could not query the drag format: 0x{error.HResult:X8}.");
                }
            }
        }
        // A native format query can pump OLE messages, including leave/enter on another display.
        if (ReferenceEquals(coordinator.fileDrag, state) && observation == coordinator.fileDragObservation)
            SetFileDragHover(target);
    }

    private void EndFileDragHover(object sender, DragEventArgs e)
    {
        if (coordinator.fileDrag == null || !chromeWindow.AllowDrop) return;
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        ++coordinator.fileDragObservation;
        if (e.RoutedEvent == DragDrop.PreviewDropEvent) coordinator.StopFileDragHover();
        else SetFileDragHover(null);
    }

    private void StopFileDragHover()
    {
        fileDragReleaseCheck?.Stop();
        SetFileDragHover(null, cancel: true);
    }

    private void SetFileDragHover(WindowActivationTarget? target, bool cancel = false)
    {
        if (coordinator.fileDrag is not { } dragState) return;
        var old = dragState.Current;
        if (cancel) dragState.Cancel();
        else dragState.Observe(monitor.Handle, target);
        if (ReferenceEquals(old, dragState.Current)) return;
        if (dragState.Current is { } hover) SelectWindow(hover.Target.Handle);
        else if (old?.Target.Handle == selectedWindowHandle) SelectWindow(IntPtr.Zero);
    }

    private bool FileDragReady => scatterInteraction.IsOpen && chromeWindow.IsVisible && !isClosing &&
        !isScatterActive && !AnyAnimating && !animationManager.IsPreviewAnimating && !isTransitioning &&
        !coordinator.movingWindow && !coordinator.drag.IsPressed && !navigationPending && !coordinator.windowClose.HasPending;

    private WindowActivationTarget? FileDragHit(Point point)
    {
        for (var hit = chromeWindow.InputHitTest(point) as DependencyObject; hit != null;
            hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit))
        {
            if (hit is OverviewCloseButton) return null;
            if (hit is OverviewThumbnail thumbnail)
                return windowThumbs.FirstOrDefault(t => ReferenceEquals(t.ClickBorder, thumbnail) &&
                    !t.IsFiltered && thumbnail.IsVisible)?.Identity;
        }
        return null;
    }

    private (MainWindow? View, WindowActivationTarget? Target) FileDragHitAtCursor()
    {
        if (!GetCursorPos(out var cursor))
        {
            Trace.TraceWarning("Could not read the pointer position for file drag confirmation.");
            return default;
        }
        IntPtr surface = WindowFromPoint(cursor);
        var view = views.FirstOrDefault(v => v.chromeHandle == surface && v.FileDragReady);
        return (view, view?.FileDragHit(view.chromeWindow.PointFromScreen(new Point(cursor.X, cursor.Y))));
    }

    private WindowActivationTarget? ConfirmFileDragHover(OverviewFileDrag.Hover? hover, bool held, WindowActivationTarget? hit) =>
        coordinator.fileDrag?.Confirm(hover, held, monitor.Handle, hit);

    private void ConfirmFileDrag(OverviewFileDrag state, OverviewFileDrag.Hover? atShortcut, bool heldAtShortcut)
    {
        if (!ReferenceEquals(fileDrag, state) || !scatterInteraction.IsOpen || !IsVisible || isTransitioning)
            return;

        WindowActivationTarget? target = null;
        if (heldAtShortcut && IsFileDragButtonDown() && !state.Cancelled)
        {
            var (view, hit) = FileDragHitAtCursor();
            if (view?.ConfirmFileDragHover(atShortcut, true, hit) is { } candidate &&
                MonitorHelper.SameTopology(monitors, MonitorHelper.GetMonitors()) &&
                IsEligibleFileDragWindow(candidate) && CurrentVirtualDesktop.Contains(candidate.Handle) &&
                ReferenceEquals(fileDrag, state))
            {
                var (current, currentHit) = FileDragHitAtCursor();
                if (current == view) target = view.ConfirmFileDragHover(atShortcut, IsFileDragButtonDown(), currentHit);
            }
        }
        if (ReferenceEquals(fileDrag, state)) DismissFileDrag(target);
    }

    private bool IsEligibleFileDragWindow(WindowActivationTarget target) =>
        windowActivation.Capture(target.Handle) == target && IsWindowVisible(target.Handle) &&
        IsWindowEnabled(target.Handle) && !IsIconic(target.Handle) &&
        DwmGetWindowAttributeInt(target.Handle, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked == 0;

    private void DismissFileDrag(WindowActivationTarget? target)
    {
        if (fileDrag == null || !session.TryBeginClose(target, isClosing || isTransitioning, out _)) return;
        HideSearchForTransition();
        // The source still owns the button; skip the return animation and recheck before activation.
        if (target is { } eligible)
            windowActivation.CloseOverviewAndActivate(eligible, restore: false, wasMaximized: false,
                scatterInteraction.Surfaces, HideAllViews,
                () => IsFileDragButtonDown() && IsEligibleFileDragWindow(eligible));
        else
            HideAllViews();
        QueueOverviewCleanup();
    }

    private void ResetFileDrag()
    {
        if (fileDrag is not { } state) return;
        fileDragReleaseCheck?.Stop();
        state.Cancel();
        fileDrag = null;
        foreach (var view in views)
            if (!view.chromeClosed) view.chromeWindow.AllowDrop = false;
    }
}
