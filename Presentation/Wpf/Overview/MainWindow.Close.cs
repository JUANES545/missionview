using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Threading;

namespace WindowScatter;

public partial class MainWindow
{
    private readonly WindowClose windowClose = new();
    private DispatcherTimer? closeObservationTimer;
    private string PendingCloseStatus => IsSearching
        ? "Close requested. Clear the search, then press Escape to check the last requested window."
        : "Close requested. Press Escape to check the last requested window if needed.";

    private bool CanCloseWindow(WindowThumb thumb, int generation) =>
        scatterInteraction.IsOpen && generation == scatterInteraction.Generation &&
        !isClosing && !isScatterActive && !AnyAnimating && !views.Any(v => v.animationManager.IsPreviewAnimating) && !isTransitioning &&
        !coordinator.movingWindow && !coordinator.navigationPending && !coordinator.drag.IsPressed &&
        !thumb.IsFiltered && ViewThumbs.Contains(thumb) && !coordinator.windowClose.IsPending(thumb.Identity);

    private void ConfigureCloseInput(WindowThumb thumb)
    {
        int generation = scatterInteraction.Generation;
        string title = AutomationProperties.GetName(thumb.ClickBorder);
        if (string.IsNullOrEmpty(title)) title = thumb.TitleLabel.Text;
        OverviewCloseInput.Attach(thumb, title, () => CanCloseWindow(thumb, generation),
            () => RequestWindowClose(thumb, generation));
        if (coordinator.windowClose.IsPending(thumb.Identity)) thumb.CloseButton!.IsEnabled = false;
    }

    private void RequestWindowClose(WindowThumb thumb, int generation)
    {
        if (!CanCloseWindow(thumb, generation)) return;
        try
        {
            coordinator.windowClose.Request(thumb.Identity);
            if (thumb.CloseButton?.IsKeyboardFocused == true) SearchBox.Focus();
            if (thumb.CloseButton != null) thumb.CloseButton.IsEnabled = false;
            coordinator.SetCloseStatus(PendingCloseStatus);
            coordinator.closeObservationTimer ??= CreateCloseObservationTimer();
            coordinator.closeObservationTimer.Start();
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError($"Window close request failed: {error}");
            coordinator.SetCloseStatus($"Could not close the window. {error.Message}");
        }
    }

    private DispatcherTimer CreateCloseObservationTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, coordinator.Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => coordinator.ObserveWindowClosures();
        return timer;
    }

    private void ObserveWindowClosures()
    {
        if (!scatterInteraction.IsOpen || isClosing) { StopCloseObservation(); return; }
        if (AnyAnimating || views.Any(v => v.animationManager.IsPreviewAnimating) ||
            isScatterActive || isTransitioning || movingWindow || drag.IsPressed || navigationPending) return;
        try
        {
            var closed = windowClose.ConfirmClosed();
            if (TryExposeCloseAttention()) return;
            if (closed.Count == 0) return;
            if (!EnsureTopology()) return;
            var selection = AllThumbs().FirstOrDefault(t => t.WindowHandle == selectedWindowHandle)?.Identity;
            previewKey.Release();
            foreach (var view in views) view.ResetPreview();
            var sourceOrder = WindowStacking.Capture();
            var windows = EnumerateOverviewWindows();
            foreach (var view in views) view.RebuildThumbnails(sourceOrder, windows[view.monitor.Handle], settled: true);
            session.Select(IntPtr.Zero);
            var retained = selection is { } target && AllThumbs().Any(t => t.Identity == target)
                ? target.Handle : IntPtr.Zero;
            if (IsSearching) RefreshSearchResults(retained);
            else SelectWindow(retained != IntPtr.Zero ? retained : AllThumbs().FirstOrDefault()?.WindowHandle ?? IntPtr.Zero);
            SetCloseStatus(windowClose.HasPending ? PendingCloseStatus : "Window closed");
            if (!windowClose.HasPending) closeObservationTimer?.Stop();
        }
        catch (Exception error) { AbortOverview(error); }
    }

    private void SetCloseStatus(string text)
    {
        foreach (var view in views)
        {
            if (view.MoveStatus.Text == text) continue;
            AutomationProperties.SetLiveSetting(view.MoveStatus, AutomationLiveSetting.Polite);
            view.MoveStatus.Text = text;
            UIElementAutomationPeer.FromElement(view.MoveStatus)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    private void StopCloseObservation()
    {
        coordinator.closeObservationTimer?.Stop();
        coordinator.windowClose.EndSession();
    }

    private void RestorePendingCloseStatus()
    {
        if (coordinator.windowClose.HasPending) coordinator.SetCloseStatus(PendingCloseStatus);
    }

    private bool TryExposeCloseAttention()
    {
        if (!IsCoordinator) return coordinator.TryExposeCloseAttention();
        if (!scatterInteraction.IsOpen || isClosing || isTransitioning || isScatterActive || AnyAnimating ||
            views.Any(v => v.animationManager.IsPreviewAnimating) || movingWindow || drag.IsPressed || navigationPending) return false;
        var target = windowClose.FindAttentionTarget();
        if (!target.HasValue) return false;
        ExposePendingClose(target.Value);
        return true;
    }

    private void ReturnToPendingClose()
    {
        ExposePendingClose(windowClose.ResolveReturnTarget() ?? default);
    }

    private void ExposePendingClose(WindowActivationTarget target)
    {
        var original = windowClose.LatestRequest;
        bool maximized = AllThumbs().Any(t => t.Identity == target && t.WasMaximized);
        if (!scatterInteraction.TryBeginClose(target, out _)) return;
        isTransitioning = true;
        moveCancellation?.Cancel();
        CloseMoveMenus();
        CancelDrag();
        windowActivation.CloseOverviewAndActivate(target, restore: true, maximized, scatterInteraction.Surfaces,
            HideAllViews, () => original is { } source && windowClose.IsReturnTargetValid(target, source));
        QueueOverviewCleanup();
    }
}
