using System.Runtime.InteropServices;
using WindowScatter;
using Xunit;
using static WindowScatter.Win32Interop;

public class MultimonitorTests
{
    [Fact]
    public void AddingViewsCannotRecaptureThePreviousApplication()
    {
        var input = new ScatterInteraction();
        var previous = new WindowActivationTarget(new(10), 1, 2);
        input.BeginSession(previous);
        input.BeginSession(new(new(99), 3, 4));
        Assert.True(input.TryBeginClose(null, out var target));
        Assert.Equal(previous, target);
        Assert.False(input.TryBeginClose(null, out _));
    }

    [Fact]
    public void WindowPlacementUsesNativeRectNotDrawingWidthHeight()
    {
        Assert.Equal(typeof(RECT), typeof(WINDOWPLACEMENT).GetField(nameof(WINDOWPLACEMENT.rcNormalPosition))!.FieldType);
        Assert.Equal(44, Marshal.SizeOf<WINDOWPLACEMENT>());
    }

    [Fact]
    public void SurfaceRegistryIncludesEveryViewAndTemporaryPopupButReleasesClosedHandles()
    {
        var input = new ScatterInteraction();
        foreach (int h in new[] { 10, 11, 20, 21, 30 }) input.RegisterSurface(new(h));
        Assert.All(new[] { 10, 11, 20, 21, 30 }, h => Assert.True(input.IsOwnWindow(new(h))));
        Assert.False(input.IsOwnWindow(new(99)));
        Assert.False(input.IsOwnWindow(IntPtr.Zero));
        input.UnregisterSurface(new(30));
        input.UnregisterSurface(new(20));
        Assert.False(input.IsOwnWindow(new(30)));
        Assert.False(input.IsOwnWindow(new(20)));
        Assert.True(input.IsOwnWindow(new(21)));
    }

    [Fact]
    public void OneGeometrySnapshotKeepsSeparateListsEmptyDestinationAndTwentyWindowCapPerView()
    {
        var monitors = new[] { new MonitorHelper.MonitorBounds { Handle = new(1) },
            new MonitorHelper.MonitorBounds { Handle = new(2) }, new MonitorHelper.MonitorBounds { Handle = new(3) } };
        var windows = Enumerable.Range(1, 25).Select(i => (new IntPtr(1), new WindowInfo { Handle = new(i) }))
            .Concat(Enumerable.Range(30, 5).Select(i => (new IntPtr(2), new WindowInfo { Handle = new(i) })));
        var groups = WindowEnumerator.GroupByMonitor(windows, monitors);
        Assert.Equal(20, groups[new(1)].Count);
        Assert.Equal(5, groups[new(2)].Count);
        Assert.Empty(groups[new(3)]);
        Assert.Equal(25, groups.Values.SelectMany(w => w).Select(w => w.Handle).Distinct().Count());
        Assert.Equal(new IntPtr(30), groups[new(2)][0].Handle);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ReturnWaitsForEveryViewAndOnlyHidesActivatesAndCleansOnce(int count)
    {
        var actions = new List<string>();
        var input = new ScatterInteraction();
        input.BeginSession(new(new(99), 1, 2));
        Assert.True(input.TryBeginClose(new(new(50), 3, 4), out var target));
        Assert.Equal(new IntPtr(50), target.Handle);
        int generation = input.Generation;
        var group = new OverviewReturnGroup(count, () => input.Generation == generation,
            () => { actions.Add("hide-all"); actions.Add("activate"); actions.Add("queue-cleanup"); });
        for (int i = count - 1; i > 0; i--)
        {
            group.Returned(i);
            group.Returned(i);
            Assert.Empty(actions);
        }
        group.Returned(0);
        group.Returned(0);
        group.Finish();
        Assert.Equal(["hide-all", "activate", "queue-cleanup"], actions);
        Assert.False(input.TryBeginClose(null, out _));
    }

    [Fact]
    public void ReopenedSessionRejectsOldReturnCallbacks()
    {
        var input = new ScatterInteraction();
        input.BeginSession(default);
        input.TryBeginClose(null, out _);
        int generation = input.Generation;
        var group = new OverviewReturnGroup(2, () => generation == input.Generation,
            () => throw new Exception("Stale completion must not hide a new overview."));
        group.Returned(0);
        input.BeginSession(default);
        group.Returned(1);
        group.Finish();
        Assert.True(input.IsOpen);
    }

    [Fact]
    public void DisplayLossDuringReturnInvalidatesConfirmationAndDeferredCleanupBeforeReopening()
    {
        var input = new ScatterInteraction();
        input.BeginSession(new(new(99), 1, 2));
        Assert.True(input.TryBeginClose(new(new(50), 3, 4), out _));
        int returning = input.Generation;
        int actions = 0;
        var group = new OverviewReturnGroup(2, () => returning == input.Generation, () => actions++);
        group.Returned(0);
        input.InvalidateSession();
        group.Returned(1);
        group.Finish();
        Assert.False(input.IsOpen);
        Assert.NotEqual(returning, input.Generation);
        input.BeginSession(new(new(70), 5, 6));
        group.Finish();
        Assert.True(input.TryBeginClose(null, out var previous));
        Assert.Equal(new IntPtr(70), previous.Handle);
        Assert.Equal(0, actions);
    }

    [Theory]
    [InlineData(21, true)]
    [InlineData(30, true)]
    [InlineData(99, false)]
    public void SecondaryChromeAndPopupAreOwnFocusButExternalApplicationIsNot(int handle, bool expected)
    {
        IntPtr active = new(handle);
        var input = new ScatterInteraction();
        foreach (int h in new[] { 10, 11, 20, 21, 30 }) input.RegisterSurface(new(h));
        int activations = 0;
        var activation = new WindowActivation(_ => (1, 2), (_, _) => { },
            h => { activations++; active = h; return true; }, () => active);
        var target = activation.Capture(new(50));
        var events = new List<string>();
        bool result = activation.CloseOverviewAndActivate(target, false, false, input.Surfaces, () =>
        {
            events.Add("hide-primary"); events.Add("hide-secondary");
            Assert.Equal(0, activations);
        });
        Assert.Equal(expected, result);
        Assert.Equal(expected ? 1 : 0, activations);
        Assert.Equal(["hide-primary", "hide-secondary"], events);
    }

    [Fact]
    public void NativeDesktopHasOneRequestAfterBothViewsHideAndCleanupStaysDeferred()
    {
        var desktop = new DesktopToggle();
        var events = new List<string>();
        Assert.True(desktop.CompleteOverviewExit(() => events.Add("stop-all"),
            () => { events.Add("hide-host-chrome-1"); events.Add("hide-host-chrome-2"); },
            () => events.Add("shell"), () => events.Add("queue")));
        Assert.Equal(["stop-all", "hide-host-chrome-1", "hide-host-chrome-2", "shell", "queue"], events);
    }

    [Theory]
    [InlineData(4, 4, false)]
    [InlineData(5, 0, true)]
    [InlineData(0, -5, true)]
    public void GestureSeparatesClickReleaseFromNativeDragThreshold(int dx, int dy, bool dragging)
    {
        var gesture = new WindowDragGesture();
        var target = new WindowActivationTarget(new(50), 1, 2);
        gesture.Press(target, -500, 300, 4, 4);
        Assert.False(gesture.IsDragging);
        Assert.Equal(dragging, gesture.Move(-500 + dx, 300 + dy));
        var result = gesture.Release();
        Assert.Equal(target, result.Target);
        Assert.Equal(dragging, result.Dragged);
        Assert.False(gesture.IsPressed);
        Assert.Equal(IntPtr.Zero, gesture.Release().Target.Handle);
    }

    [Fact]
    public void DragStaysDragWhenPointerReturnsAndCaptureLossEscapeOrShutdownCancelWithoutActivation()
    {
        var gesture = new WindowDragGesture();
        gesture.Press(new(new(50), 1, 2), -1500, -400, 6, 6);
        Assert.True(gesture.Move(100, 500));
        Assert.True(gesture.Move(-1500, -400));
        gesture.Cancel();
        Assert.False(gesture.IsDragging);
        Assert.Equal(IntPtr.Zero, gesture.Release().Target.Handle);
        Assert.False(gesture.Move(900, 600));
    }

    [Theory]
    [InlineData(false, 0, 0, 1)]
    [InlineData(true, 0, 0, 1)]
    [InlineData(false, -100, 0, 1)]
    [InlineData(false, 0, 20, 1)]
    [InlineData(false, 0, 0, 1.5)]
    public void DisplayUnplugBoundsWorkAreaAndDpiChangesInvalidateSession(bool unplug, int leftDelta, int workDelta, double dpi)
    {
        var first = new MonitorHelper.MonitorBounds
        { Handle = new(1), Width = 1920, Height = 1080, WorkArea = new RECT { Right = 1920, Bottom = 1040 } };
        var second = new MonitorHelper.MonitorBounds
        { Handle = new(1), Left = leftDelta, Width = 1920, Height = 1080, DpiScale = dpi,
            WorkArea = new RECT { Right = 1920, Bottom = 1040 - workDelta } };
        Assert.Equal(!unplug && leftDelta == 0 && workDelta == 0 && dpi == 1,
            MonitorHelper.SameTopology([first], unplug ? [] : [second]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompositingOrderRestoresNativeStackingAfterPreviewWithoutChangingLogicalNavigation(bool confirm)
    {
        var logical = new IntPtr[] { new(1), new(2), new(3) };
        var nativeFrontToBack = new IntPtr[] { new(2), new(3), new(1) };
        var rendered = WindowStacking.BackToFront(logical, h => h, nativeFrontToBack, confirm ? new(3) : IntPtr.Zero).ToArray();
        Assert.Equal(confirm ? new IntPtr[] { new(1), new(2), new(3) } : new IntPtr[] { new(1), new(3), new(2) }, rendered);
        Assert.Equal(new IntPtr[] { new(1), new(2), new(3) }, logical);
        Assert.Equal(new IntPtr[] { new(2), new(1) },
            WindowStacking.BackToFront(new IntPtr[] { new(1), new(2) }, h => h, new IntPtr[] { new(1), new(2) }));
        Assert.Equal(new IntPtr[] { new(3), new(2), new(1) },
            WindowStacking.BackToFront(logical, h => h, new IntPtr[] { new(1), new(2), new(3) }));
    }
}
