using WindowScatter;
using Xunit;

public class OverviewNavigationTests
{
    private static NavigationTarget Target(int id, double x, double y, double width = 200, double height = 200)
        => new(new WindowActivationTarget(new IntPtr(id), 1, 2), new(x, y, width, height));

    public static IEnumerable<object[]> ApprovedCases()
    {
        for (int c = 0; c < 3; c++)
        for (int rotation = 0; rotation < 4; rotation++)
        {
            yield return [c, rotation];
        }
    }

    private static PreviewBounds Rotate(PreviewBounds b, int rotation) => rotation switch
    {
        1 => new(-b.Y - b.Height, b.X, b.Height, b.Width),
        2 => new(-b.X - b.Width, -b.Y - b.Height, b.Width, b.Height),
        3 => new(b.Y, -b.X - b.Width, b.Height, b.Width),
        _ => b
    };

    [Theory]
    [MemberData(nameof(ApprovedCases))]
    public void ArrowsUseAlignedEdgesAndNeverWrap(int example, int rotation)
    {
        NavigationTarget[] targets = example switch
        {
            0 => [Target(1, 100, 300), Target(2, 700, 300), Target(3, 220, 650)],
            1 => [Target(1, 100, 100, 200, 800), Target(2, 340, 150, 1000, 200), Target(3, 700, 600, 100, 200)],
            _ => [Target(1, 700, 100), Target(2, 100, 500)]
        };
        targets = targets.Select(t => t with { Bounds = Rotate(t.Bounds, rotation) }).ToArray();
        int dx = rotation == 0 ? 1 : rotation == 2 ? -1 : 0;
        int dy = rotation == 1 ? 1 : rotation == 3 ? -1 : 0;
        int expected = example == 2 ? 1 : 2;
        foreach (double scale in new[] { 1, 1.5, 2 })
        {
            var physical = targets.Select(t => t with { Bounds = new(
                t.Bounds.X * scale - 3200, t.Bounds.Y * scale - 1600,
                t.Bounds.Width * scale, t.Bounds.Height * scale) }).ToArray();
            Assert.Equal(new IntPtr(expected), OverviewNavigation.Select(physical, new IntPtr(1), dx, dy));
            Assert.Equal(new IntPtr(expected), OverviewNavigation.Select(physical.Reverse().ToArray(), new IntPtr(1), dx, dy));
        }
    }

    [Fact]
    public void RemoteAlignedWindowBeatsNearbyDiagonalAndPartialDirectionalOverlapIsNotANeighbour()
    {
        NavigationTarget[] targets = [Target(1, -1400, 300), Target(2, 1600, 300),
            Target(3, -1100, 550), Target(4, -1300, 300)];
        Assert.Equal(new IntPtr(2), OverviewNavigation.Select(targets, new IntPtr(1), 1, 0));
    }

    [Fact]
    public void WithoutAnAlignedNeighbourRankRectangleDistanceThenGapAndStableIdentity()
    {
        NavigationTarget[] targets = [Target(1, 0, 0), Target(8, 240, 240), Target(2, 240, -240), Target(9, 500, 220)];
        Assert.Equal(new IntPtr(2), OverviewNavigation.Select(targets, new IntPtr(1), 1, 0));
        Assert.Equal(new IntPtr(2), OverviewNavigation.Select(targets.Reverse().ToArray(), new IntPtr(1), 1, 0));
    }

    [Fact]
    public void EmptyLostAndSingleSelectionsDoNotAdvanceTwiceOrSelectInvalidRectangles()
    {
        NavigationTarget[] targets = [Target(1, 100, 100), Target(2, 400, 100)];
        Assert.Equal(new IntPtr(1), OverviewNavigation.Select(targets, IntPtr.Zero, 1, 0));
        Assert.Equal(new IntPtr(1), OverviewNavigation.Select(targets, new IntPtr(99), 0, -1));
        Assert.Equal(new IntPtr(1), OverviewNavigation.Select([targets[0]], new IntPtr(1), -1, 0));
        Assert.Equal(IntPtr.Zero, OverviewNavigation.Select([], IntPtr.Zero, 1, 0));
        Assert.Equal(IntPtr.Zero, OverviewNavigation.Select(
            [Target(1, double.NaN, 0), Target(2, 0, 0, 0), Target(0, 10, 10)], IntPtr.Zero, 1, 0));
    }

    [Fact]
    public void TabReadsMixedHeightRowsLeftToRightThenDownAndReverseIsExact()
    {
        NavigationTarget[] targets = [Target(5, 350, 490, 200, 60), Target(3, 700, 180, 200, 80),
            Target(1, 100, 100, 200, 300), Target(4, 100, 450), Target(2, 350, 140, 200, 60)];
        var order = OverviewNavigation.VisualOrder(targets).Select(t => t.Identity).ToArray();
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, order.Select(t => t.Handle.ToInt64()));
        Assert.Equal(order, OverviewNavigation.VisualOrder(targets.Reverse()).Select(t => t.Identity));
        IntPtr selected = new(1);
        foreach (int expected in new[] { 2, 3, 4, 5, 1 })
            Assert.Equal(new IntPtr(expected), selected = OverviewNavigation.SelectTab(order, selected, 1));
        foreach (int expected in new[] { 5, 4, 3, 2, 1 })
            Assert.Equal(new IntPtr(expected), selected = OverviewNavigation.SelectTab(order, selected, -1));
        Assert.Equal(new IntPtr(1), OverviewNavigation.SelectTab(order, new(99), 1));
        Assert.Equal(new IntPtr(5), OverviewNavigation.SelectTab(order, new(99), -1));
        Assert.Equal(IntPtr.Zero, OverviewNavigation.SelectTab([], IntPtr.Zero, 1));
        Assert.Equal(order[0].Handle, OverviewNavigation.SelectTab([order[0]], order[0].Handle, -1));
    }

    [Fact]
    public void TallTileDoesNotBridgeSeparateVisualRows()
    {
        NavigationTarget[] targets = [Target(1, 100, 100, 100, 800), Target(2, 300, 120, 100, 100),
            Target(3, 280, 400, 100, 100), Target(4, 500, 420, 100, 100)];
        Assert.Equal(new long[] { 1, 2, 3, 4 },
            OverviewNavigation.VisualOrder(targets).Select(t => t.Identity.Handle.ToInt64()));
    }
}
