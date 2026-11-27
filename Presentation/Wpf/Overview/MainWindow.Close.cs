using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Threading;

namespace WindowScatter;

public partial class MainWindow
{
    private readonly WindowClose windowClose = new();
    private DispatcherTimer? windowObservationTimer;
    private bool confirmedCloseAwaitingRefresh;
    private bool windowRefreshQueued;
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
            coordinator.StartWindowObservation();
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError($"Window close request failed: {error}");
            coordinator.SetCloseStatus($"Could not close the window. {error.Message}");
        }
    }

    private void StartWindowObservation()
    {
        if (windowObservationTimer == null)
        {
            windowObservationTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            windowObservationTimer.Tick += (_, _) => ObserveOverviewWindows();
        }
        windowObservationTimer.Interval = TimeSpan.FromMilliseconds(windowClose.HasPending ? 250 : 500);
        windowObservationTimer.Start();
    }

    private bool CanReconcileOverviewWindows => scatterInteraction.IsOpen && !isClosing &&
        !isScatterActive && !isTransitioning && !AnyAnimating && !movingWindow && !drag.IsPressed &&
        !navigationPending && fileDrag == null && !services.Desktops.Pending &&
        !views.Any(v => v.animationManager.IsPreviewAnimating || v.openMoveMenus.Count != 0);

    private void ObserveOverviewWindows()
    {
        if (!scatterInteraction.IsOpen || isClosing) { StopWindowObservation(); return; }
        if (windowRefreshQueued || !CanReconcileOverviewWindows) return;
        try
        {
            bool confirmed = windowClose.ConfirmClosed().Count != 0;
            if (confirmed)
            {
                TraceClosePhase("identity-closed");
                confirmedCloseAwaitingRefresh = true;
            }
            var dead = AllThumbs().Select(t => t.Identity).Where(windowClose.IsClosed).ToHashSet();
            if (dead.Count != 0)
            {
                foreach (var view in views)
                {
                    if (view.windowThumbs.Any(t => dead.Contains(t.Identity)))
                    {
                        view.animationManager.StopKeepAlive();
                        view.thumbnailManager.HideClosedSources(dead, windowClose.IsClosed);
                    }
                    foreach (var thumb in view.minimizedStrip?.Thumbs ?? [])
                        if (dead.Contains(thumb.Identity)) thumb.HideIfSourceClosed(windowClose.IsClosed);
                }
            }
            if (TryExposeCloseAttention()) return;
            if (!confirmed && dead.Count == 0)
            {
                RefreshOverviewWindows();
                return;
            }
            int generation = scatterInteraction.Generation;
            windowRefreshQueued = true;
            // Let WPF submit the hidden contour before enumerating or moving any survivors.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (generation != scatterInteraction.Generation) return;
                windowRefreshQueued = false;
                if (CanReconcileOverviewWindows) RefreshOverviewWindows();
            }), DispatcherPriority.Loaded);
        }
        catch (Exception error) { AbortOverview(error); }
    }

    private void RefreshOverviewWindows()
    {
        try
        {
            var previousDiagnostics = (appExposeRequest?.UnidentifiedWindows, appExposeRequest?.WindowsOverLimit);
            var names = views.SelectMany(v => v.overviewSources).ToDictionary(w => w.Identity, w => w.ApplicationName);
            var windows = EnumerateOverviewWindows(names);
            var changedViews = views.Where(v => !WindowInfo.SameOverviewSources(v.overviewSources,
                windows[v.monitor.Handle])).ToArray();
            if (changedViews.Length != 0)
            {
                if (!EnsureTopology()) return;
                var selection = AllThumbs().FirstOrDefault(t => t.WindowHandle == selectedWindowHandle)?.Identity;
                var sourceOrder = WindowStacking.Capture();
                foreach (var view in changedViews)
                {
                    if (view.previewExpanded) view.ResetPreview();
                    if (confirmedCloseAwaitingRefresh) TraceClosePhase("view-rebuild-begin", views.IndexOf(view));
                    view.RebuildThumbnails(sourceOrder, windows[view.monitor.Handle], settled: true, retainExisting: true);
                    if (confirmedCloseAwaitingRefresh) TraceClosePhase("view-rebuild-end", views.IndexOf(view));
                }
                var retained = selection is { } target && AllThumbs().Any(t => t.Identity == target)
                    ? target.Handle : IntPtr.Zero;
                if (retained == IntPtr.Zero) SelectWindow(IntPtr.Zero);
                else activeView = ViewFor(retained);
                if (IsSearching) RefreshSearchResults(retained, keyboardSelection: false);
                else SelectWindow(retained != IntPtr.Zero ? retained : AllThumbs().FirstOrDefault()?.WindowHandle ?? IntPtr.Zero);
                RestorePendingCloseStatus();
            }
            if (previousDiagnostics != (appExposeRequest?.UnidentifiedWindows, appExposeRequest?.WindowsOverLimit) &&
                !windowClose.HasPending)
            {
                foreach (var view in views) view.MoveStatus.Text = view.IdleMoveStatus;
            }
            if (confirmedCloseAwaitingRefresh)
            {
                confirmedCloseAwaitingRefresh = false;
                SetCloseStatus(windowClose.HasPending ? PendingCloseStatus : "Window closed");
                StartWindowObservation();
            }
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

    private void StopWindowObservation()
    {
        coordinator.windowObservationTimer?.Stop();
        coordinator.windowClose.EndSession();
        coordinator.confirmedCloseAwaitingRefresh = false;
        coordinator.windowRefreshQueued = false;
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
