using WindowScatter;
using Xunit;

public class OverviewNavigationTests
{
    private static NavigationTarget Target(int id, double x, double y)
        => new(new WindowActivationTarget(new IntPtr(id), 1, 2), x, y);

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 50)]
    [InlineData(true, 0)]
    [InlineData(true, 50)]
    public void ArrowsSelectIntermediateWindowInBothDirections(bool vertical, double deviation)
    {
        NavigationTarget[] targets = vertical
            ? [Target(1, 300, 200), Target(2, 300 + deviation, 600), Target(3, 300, 1600)]
            : [Target(1, 200, 300), Target(2, 600, 300 + deviation), Target(3, 1600, 300)];
        Assert.Equal(new IntPtr(2), OverviewNavigation.Select(targets, new IntPtr(1), vertical ? 0 : 1, vertical ? 1 : 0));
        Assert.Equal(new IntPtr(2), OverviewNavigation.Select(targets, new IntPtr(3), vertical ? 0 : -1, vertical ? -1 : 0));
        var translated = targets.Select(t => t with { X = t.X - 2400, Y = t.Y - 1200 }).ToArray();
        Assert.Equal(new IntPtr(2), OverviewNavigation.Select(translated, new IntPtr(1), vertical ? 0 : 1, vertical ? 1 : 0));
        Assert.Equal(new IntPtr(2), OverviewNavigation.Select(translated, new IntPtr(3), vertical ? 0 : -1, vertical ? -1 : 0));
    }

    [Fact]
    public void EqualDistanceUsesPerpendicularDeviationThenStableIdentity()
    {
        NavigationTarget[] targets = [Target(1, 0, 0), Target(9, 3, 4), Target(8, 4, 3), Target(2, 4, -3)];
        Assert.Equal(new IntPtr(2), OverviewNavigation.Select(targets, new IntPtr(1), 1, 0));
        Assert.Equal(new IntPtr(2), OverviewNavigation.Select(targets.Reverse().ToArray(), new IntPtr(1), 1, 0));
    }

    [Fact]
    public void NoDirectionalCandidateRetainsSequentialWrapAndEmptySelectionBehavior()
    {
        NavigationTarget[] targets = [Target(1, 200, 300), Target(2, 600, 300), Target(3, 1600, 300)];
        Assert.Equal(new IntPtr(1), OverviewNavigation.Select(targets, new IntPtr(3), 1, 0));
        Assert.Equal(new IntPtr(3), OverviewNavigation.Select(targets, new IntPtr(1), -1, 0));
        Assert.Equal(new IntPtr(1), OverviewNavigation.Select(targets, IntPtr.Zero, 0, 1));
        Assert.Equal(new IntPtr(3), OverviewNavigation.Select(targets, IntPtr.Zero, 0, -1));
        Assert.Equal(new IntPtr(1), OverviewNavigation.Select([targets[0]], new IntPtr(1), 1, 0));
        Assert.Equal(IntPtr.Zero, OverviewNavigation.Select([], IntPtr.Zero, 1, 0));
    }
}
