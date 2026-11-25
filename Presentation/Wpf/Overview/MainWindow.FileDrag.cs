using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using static WindowScatter.Win32Interop;

namespace WindowScatter;

public partial class MainWindow
{
    private OverviewFileDrag? fileDrag;
    private POINT fileDragStart;

    private static bool IsFileDragButtonDown() => GetAsyncKeyState(VK_LBUTTON) < 0;

    private bool BlocksThumbnailInput => coordinator.fileDrag != null;

    private void BeginFileDrag(POINT point)
    {
        if (monitors.Count != 1) return;
        fileDrag = new OverviewFileDrag();
        fileDragStart = point;
        chromeWindow.AllowDrop = true;
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
        if (fileDrag is not { } state || !chromeWindow.AllowDrop) return;
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        WindowActivationTarget? target = null;
        if (!state.Cancelled && FileDragReady &&
            (e.KeyStates & (DragDropKeyStates.LeftMouseButton | DragDropKeyStates.RightMouseButton)) ==
            DragDropKeyStates.LeftMouseButton)
        {
            Point point = e.GetPosition(chromeWindow);
            Point screen = chromeWindow.PointToScreen(point);
            if (Math.Round(screen.X) != fileDragStart.X || Math.Round(screen.Y) != fileDragStart.Y)
            {
                try
                {
                    if (e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: false) &&
                        ReferenceEquals(fileDrag, state) && FileDragReady)
                        target = FileDragHit(point);
                }
                catch (ExternalException error)
                {
                    Trace.TraceWarning($"Could not query the drag format: 0x{error.HResult:X8}.");
                }
            }
        }
        if (ReferenceEquals(fileDrag, state)) SetFileDragHover(target);
    }

    private void EndFileDragHover(object sender, DragEventArgs e)
    {
        if (fileDrag == null || !chromeWindow.AllowDrop) return;
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        SetFileDragHover(null, cancel: e.RoutedEvent == DragDrop.PreviewDropEvent);
    }

    private void SetFileDragHover(WindowActivationTarget? target, bool cancel = false)
    {
        if (fileDrag is not { } dragState) return;
        var old = dragState.Current;
        if (cancel) dragState.Cancel();
        else dragState.Observe(target);
        if (dragState.Current is { } hover) SelectWindow(hover.Target.Handle);
        else if (old?.Target.Handle == selectedWindowHandle) SelectWindow(IntPtr.Zero);
    }

    private bool FileDragReady => scatterInteraction.IsOpen && chromeWindow.IsVisible && !isClosing &&
        !isScatterActive && !AnyAnimating && !animationManager.IsPreviewAnimating && !isTransitioning &&
        !movingWindow && !drag.IsPressed && !navigationPending && !windowClose.HasPending && monitors.Count == 1;

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

    private WindowActivationTarget? FileDragHitAtCursor()
    {
        if (!GetCursorPos(out var cursor))
        {
            Trace.TraceWarning("Could not read the pointer position for file drag confirmation.");
            return null;
        }
        return WindowFromPoint(cursor) == chromeHandle
            ? FileDragHit(chromeWindow.PointFromScreen(new Point(cursor.X, cursor.Y))) : null;
    }

    private void ConfirmFileDrag(OverviewFileDrag state, OverviewFileDrag.Hover? atShortcut, bool heldAtShortcut)
    {
        if (!ReferenceEquals(fileDrag, state) || !scatterInteraction.IsOpen || !IsVisible || isTransitioning)
            return;

        WindowActivationTarget? target = null;
        if (FileDragReady && heldAtShortcut && IsFileDragButtonDown() &&
            state.Confirm(atShortcut, true, FileDragHitAtCursor()) is { } hit &&
            MonitorHelper.SameTopology(monitors, MonitorHelper.GetMonitors()) &&
            IsEligibleFileDragWindow(hit) && CurrentVirtualDesktop.Contains(hit.Handle) &&
            ReferenceEquals(fileDrag, state) && FileDragReady)
        {
            target = state.Confirm(atShortcut, IsFileDragButtonDown(), FileDragHitAtCursor());
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
        state.Cancel();
        fileDrag = null;
        if (!chromeClosed) chromeWindow.AllowDrop = false;
    }
}
