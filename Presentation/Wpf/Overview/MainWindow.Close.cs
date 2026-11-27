using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Threading;

namespace WindowScatter;

public partial class MainWindow
{
    private readonly WindowClose windowClose = new();
    private DispatcherTimer? closeObservationTimer;
    private bool closedWindowRefreshRequired;
    private bool closedWindowRefreshQueued;
    private int closeTraceEntries;
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
        if (coordinator.windowClose.IsPending(thumb.Identity))
        {
            thumb.CloseButton!.IsEnabled = false;
            thumb.SuppressSelectionOutlineForClose();
        }
    }

    private void RequestWindowClose(WindowThumb thumb, int generation)
    {
        if (!CanCloseWindow(thumb, generation)) return;
        try
        {
            coordinator.windowClose.Request(thumb.Identity);
            thumb.SuppressSelectionOutlineForClose();
            coordinator.TraceClosePhase("request-queued");
            if (thumb.CloseButton?.IsKeyboardFocused == true)
            {
                coordinator.TraceClosePhase("overview-focus-return");
                FocusOverviewInput();
            }
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
            if (closed.Count != 0)
            {
                TraceClosePhase("identity-closed");
                foreach (var view in views)
                {
                    // Retire the live DWM relation too, before the deferred layout rebuild.
                    view.animationManager.StopKeepAlive();
                    view.thumbnailManager.HideClosedSources(closed, windowClose.IsClosed);
                    foreach (var thumb in view.minimizedStrip?.Thumbs ?? [])
                        if (closed.Contains(thumb.Identity)) thumb.HideIfSourceClosed(windowClose.IsClosed);
                }
                closedWindowRefreshRequired = true;
            }
            if (TryExposeCloseAttention()) return;
            if (!closedWindowRefreshRequired || closedWindowRefreshQueued) return;
            int generation = scatterInteraction.Generation;
            closedWindowRefreshQueued = true;
            // Submit the dead source's hidden chrome before synchronous enumeration/DWM rebuild.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (generation != scatterInteraction.Generation) return;
                closedWindowRefreshQueued = false;
                if (!scatterInteraction.IsOpen || isClosing || isScatterActive || isTransitioning || AnyAnimating ||
                    views.Any(v => v.animationManager.IsPreviewAnimating) || movingWindow || drag.IsPressed || navigationPending) return;
                RefreshAfterConfirmedClose();
            }), DispatcherPriority.Loaded);
        }
        catch (Exception error) { AbortOverview(error); }
    }

    private void RefreshAfterConfirmedClose()
    {
        try
        {
            if (!EnsureTopology()) return;
            var selection = AllThumbs().FirstOrDefault(t => t.WindowHandle == selectedWindowHandle)?.Identity;
            previewKey.Release();
            foreach (var view in views) view.ResetPreview();
            var sourceOrder = WindowStacking.Capture();
            var windows = EnumerateOverviewWindows();
            for (int i = 0; i < views.Count; i++)
            {
                var view = views[i];
                if (view.TryKeepThumbnailsAfterClose(sourceOrder, windows[view.monitor.Handle]))
                {
                    TraceClosePhase("view-retained", i);
                    continue;
                }
                TraceClosePhase("view-rebuild-begin", i);
                view.RebuildThumbnails(sourceOrder, windows[view.monitor.Handle], settled: true);
                TraceClosePhase("view-rebuild-end", i);
            }
            session.Select(IntPtr.Zero);
            var retained = selection is { } target && AllThumbs().Any(t => t.Identity == target)
                ? target.Handle : IntPtr.Zero;
            if (IsSearching) RefreshSearchResults(retained);
            else SelectWindow(retained != IntPtr.Zero ? retained : AllThumbs().FirstOrDefault()?.WindowHandle ?? IntPtr.Zero);
            closedWindowRefreshRequired = false;
            SetCloseStatus(windowClose.HasPending ? PendingCloseStatus : "Window closed");
            if (!windowClose.HasPending) closeObservationTimer?.Stop();
        }
        catch (Exception error) { AbortOverview(error); }
    }

    private void TraceClosePhase(string phase, int view = -1)
    {
        if (Environment.GetEnvironmentVariable("MISSIONVIEW_CLOSETRACE") != "1" || closeTraceEntries >= 64) return;
        closeTraceEntries++;
        System.Diagnostics.Trace.TraceInformation(
            $"MissionView close phase={phase}; view={view}; ticks={System.Diagnostics.Stopwatch.GetTimestamp()}; frequency={System.Diagnostics.Stopwatch.Frequency}");
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
        coordinator.closedWindowRefreshRequired = false;
        coordinator.closedWindowRefreshQueued = false;
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
        TraceClosePhase("attention-detected");
        ExposePendingClose(target.Value, explicitReturn: false);
        return true;
    }

    private void ReturnToPendingClose()
    {
        ExposePendingClose(windowClose.ResolveReturnTarget() ?? default, explicitReturn: true);
    }

    private void ExposePendingClose(WindowActivationTarget target, bool explicitReturn)
    {
        var original = windowClose.LatestRequest;
        if (!scatterInteraction.TryBeginClose(target, out _)) return;
        TraceClosePhase("handoff-begin");
        TraceClosePhase(explicitReturn ? "handoff-explicit-return" :
            target == original ? "handoff-source-no-restore" : "handoff-owned-prompt");
        isTransitioning = true;
        HideSearchForTransition();
        moveCancellation?.Cancel();
        CloseMoveMenus();
        CancelDrag();
        windowActivation.CloseOverviewForPendingClose(target, original ?? default, explicitReturn, scatterInteraction.Surfaces,
            () => { TraceClosePhase("surfaces-hide"); HideAllViews(); },
            () => original is { } source && windowClose.IsReturnTargetValid(target, source));
        TraceClosePhase("handoff-end");
        QueueOverviewCleanup();
    }
}
