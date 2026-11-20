using System.ComponentModel;
using System.Runtime.InteropServices;
using WindowScatter;
using Xunit;
using static WindowScatter.Win32Interop;
using static WindowScatter.MonitorHelper;

public class WindowTransferTests
{
    private static readonly WindowActivationTarget Target = new(new(50), 1, 2);
    private static RECT Rect(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };
    private static MonitorBounds Source() => new()
    { Handle = new(1), Left = -1920, Width = 1920, Height = 1080, WorkArea = Rect(-1880, 40, 0, 1080) };
    private static MonitorBounds Destination(double dpi = 1.5) => new()
    { Handle = new(2), Left = 0, Top = -300, Width = 1200, Height = 1600, DpiScale = dpi, WorkArea = Rect(0, -240, 1200, 1300) };
    private static WindowMoveSnapshot Snapshot(RECT rect, bool maximized = false, uint style = 0) => new(
        Target, rect, rect, new WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<WINDOWPLACEMENT>(), showCmd = maximized ? SW_SHOWMAXIMIZED : 1,
            rcNormalPosition = Rect(-1600, 200, -800, 800)
        }, style, true);

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void MoveUsesOneHwndPreservesPhysicalSizeAndClampsNegativeAndTaskbarCoordinatesWithoutDoubleDpi(double dpi)
    {
        var source = Source(); var destination = Destination(dpi);
        var current = Snapshot(Rect(-1700, 200, -900, 800));
        var calls = new List<IntPtr>();
        var service = new WindowTransfer(_ => current, (h, r) =>
        {
            calls.Add(h);
            current = current with { Outer = r, Frame = r };
        }, (_, _) => throw new Exception("Normal window must not be shown/restored."));
        service.Move(Target, source, destination, [source, destination]);
        Assert.Equal(Target.Handle, Assert.Single(calls));
        Assert.Equal((800, 600), (current.Outer.Right - current.Outer.Left, current.Outer.Bottom - current.Outer.Top));
        Assert.Same(destination, FindWindowMonitor(current.Frame, [source, destination]));
        Assert.InRange(current.Frame.Left, destination.WorkArea.Left, destination.WorkArea.Right - 800);
        Assert.InRange(current.Frame.Top, destination.WorkArea.Top, destination.WorkArea.Bottom - 600);
    }

    [Fact]
    public void OversizeWindowIsLimitedToDestinationWorkArea()
    {
        var mapped = WindowTransfer.Map(Rect(-2500, -1000, 1000, 2000), Source().WorkArea, Destination().WorkArea);
        Assert.Equal((0, -240, 1200, 1300), (mapped.Left, mapped.Top, mapped.Right, mapped.Bottom));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x40080u)]
    public void MaximizedMovePreservesStateUpdatesRestoredRectAndNeverUsesActivateOrRestoreCommand(uint style)
    {
        var source = Source(); var destination = Destination();
        var current = Snapshot(source.WorkArea, maximized: true, style);
        var calls = new List<string>();
        var service = new WindowTransfer(_ => current, (h, r) =>
        {
            Assert.Equal(Target.Handle, h);
            calls.Add("position");
            current = current with { Outer = r, Frame = r };
        }, (h, p) =>
        {
            Assert.Equal(Target.Handle, h);
            Assert.Equal(8, p.showCmd);
            Assert.Equal(0, p.flags);
            calls.Add("placement-noactivate");
            current = current with { Placement = p };
            p.showCmd = SW_SHOWMAXIMIZED;
            current = current with { Placement = p };
        });
        service.Move(Target, source, destination, [source, destination]);
        Assert.Equal(["placement-noactivate", "position"], calls);
        Assert.Equal(SW_SHOWMAXIMIZED, current.Placement.showCmd);
        var restored = WindowTransfer.FromWorkspace(current.Placement.rcNormalPosition, destination, style);
        Assert.InRange(restored.Left, destination.WorkArea.Left, destination.WorkArea.Right - 800);
        Assert.InRange(restored.Top, destination.WorkArea.Top, destination.WorkArea.Bottom - 600);
        Assert.Equal(800, restored.Right - restored.Left);
    }

    [Theory]
    [InlineData(0u, 40, 40)]
    [InlineData(0x40000u, 40, 40)]
    [InlineData(0x40080u, 0, 0)]
    public void WorkspaceConversionUsesMonitorWorkOffsetExceptExplicitToolWindows(uint style, int dx, int dy)
    {
        var rect = Rect(-1600, 200, -800, 800);
        var screen = WindowTransfer.FromWorkspace(rect, Source(), style);
        Assert.Equal((rect.Left + dx, rect.Top + dy), (screen.Left, screen.Top));
        Assert.Equal(rect, WindowTransfer.ToWorkspace(screen, Source(), style));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableOrReusedWindowIsNeverMoved(bool reused)
    {
        int mutations = 0;
        var current = Snapshot(Rect(-1700, 200, -900, 800));
        current = reused ? current with { Identity = new(Target.Handle, 7, 8) } : current with { Available = false };
        var service = new WindowTransfer(_ => current, (_, _) => mutations++, (_, _) => mutations++);
        Assert.Throws<InvalidOperationException>(() => service.Move(Target, Source(), Destination(), [Source(), Destination()]));
        Assert.Equal(0, mutations);
    }

    [Fact]
    public void PermissionFailureIsPropagatedAndUnchangedGeometryIsNotSuccessful()
    {
        var current = Snapshot(Rect(-1700, 200, -900, 800));
        var denied = new WindowTransfer(_ => current, (_, _) => throw new Win32Exception(5), (_, _) => { });
        Assert.Equal(5, Assert.Throws<Win32Exception>(() => denied.Move(Target, Source(), Destination(), [Source(), Destination()])).NativeErrorCode);
        int attempts = 0;
        var ignored = new WindowTransfer(_ => current, (_, _) => attempts++, (_, _) => { });
        Assert.Throws<InvalidOperationException>(() => ignored.Move(Target, Source(), Destination(), [Source(), Destination()]));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void SameMonitorDropDoesNotEvenReadOrMutateAndDisconnectedDestinationNeverMoves()
    {
        int reads = 0;
        var service = new WindowTransfer(_ => { reads++; return default; },
            (_, _) => throw new Exception("No move"), (_, _) => throw new Exception("No placement"));
        var source = Source();
        service.Move(Target, source, source, [source]);
        Assert.Equal(0, reads);
        Assert.Throws<InvalidOperationException>(() => service.Move(Target, source, Destination(), [source]));
        Assert.Equal(0, reads);
    }

    [Fact]
    public void WindowThatChangedDisplaysDuringGestureIsNotMovedAgain()
    {
        var current = Snapshot(Rect(100, 100, 900, 700));
        int attempts = 0;
        var service = new WindowTransfer(_ => current, (_, _) => attempts++, (_, _) => attempts++);
        Assert.Throws<InvalidOperationException>(() => service.Move(Target, Source(), Destination(), [Source(), Destination()]));
        Assert.Equal(0, attempts);
    }

    [Fact]
    public void CancellationAfterPlacementStopsTheSecondWriteWithoutClaimingRollback()
    {
        using var cancellation = new CancellationTokenSource();
        var current = Snapshot(Source().WorkArea, maximized: true);
        int placements = 0, positions = 0;
        var service = new WindowTransfer(_ => current, (_, _) => positions++, (_, _) =>
        {
            placements++;
            cancellation.Cancel();
        });
        Assert.Throws<OperationCanceledException>(() =>
            service.Move(Target, Source(), Destination(), [Source(), Destination()], cancellation.Token));
        Assert.Equal(1, placements);
        Assert.Equal(0, positions);
    }
}
