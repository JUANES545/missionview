using WindowScatter;
using Xunit;

public class OverviewSessionTests
{
    [Fact]
    public void TabSnapshotSurvivesScrollButStructuralInvalidationRebuildsIt()
    {
        var session = new OverviewSession();
        NavigationTarget normal = Target(1, 100, 100), next = Target(2, 400, 100), minimized = Target(3, 0, 0);
        var display = new OverviewDisplaySnapshot(new(10), new(0, 0, 1600, 1000), [normal, next], [minimized], [minimized]);
        session.Select(new(1));
        Assert.Equal(new IntPtr(2), session.SelectTab([display], 1).Handle);
        session.Select(new(2));
        var scrolled = display with { MinimizedVisible = [], Normal = [next, normal] };
        Assert.Equal(new IntPtr(3), session.SelectTab([scrolled], 1).Handle);
        session.Select(new(3));
        Assert.Equal(new IntPtr(2), session.SelectTab([scrolled], -1).Handle);
        session.Select(new(1));
        session.InvalidateNavigation();
        Assert.Equal(new IntPtr(3), session.SelectTab([display with { Normal = [normal] }], 1).Handle);
    }

    [Fact]
    public void HiddenValidMinimizedOriginNeverBecomesLostAndOnlyContentNeighbourRequestsReveal()
    {
        var session = new OverviewSession();
        var display = new OverviewDisplaySnapshot(new(10), new(-1600, 0, 1600, 1000),
            [Target(1, -1500, 100)], [Target(2, 0, 0), Target(3, 0, 54)], []);
        session.Select(new(2));
        Assert.Equal(new OverviewSelection(new(2), true, false), session.SelectDirection([display], new(10), -1, 0));
        Assert.Equal(new OverviewSelection(new(3), true, true), session.SelectDirection([display], new(10), 0, 1));
        session.Select(IntPtr.Zero);
        Assert.Equal(new IntPtr(1), session.SelectDirection([display], new(10), 1, 0).Handle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewReturnOwnsPendingGateRejectsRepeatAndIgnoresStaleCallbacks(bool invalidate)
    {
        var session = new OverviewSession();
        session.Interaction.BeginSession(new(new(99), 1, 2));
        var callbacks = new List<Action>();
        int selected = 0, completed = 0;
        Action<Action>[] effects = [callbacks.Add, callbacks.Add];
        session.Navigate(effects, () => true, () =>
        {
            Assert.True(session.NavigationPending);
            selected++;
        }, () => { Assert.False(session.NavigationPending); completed++; });
        Assert.True(session.NavigationPending);
        session.Navigate([], () => true, () => throw new Exception("Repeat must not queue selection."), () => { });
        callbacks[0]();
        callbacks[0]();
        Assert.Equal(0, selected);
        if (invalidate)
        {
            session.Interaction.InvalidateSession();
            session.Interaction.BeginSession(new(new(98), 1, 2));
            session.CancelNavigation();
        }
        callbacks[1]();
        callbacks[1]();
        Assert.Equal(invalidate ? 0 : 1, selected);
        Assert.Equal(invalidate ? 0 : 1, completed);
        Assert.False(session.NavigationPending);
    }

    [Fact]
    public void CloseCannotStartWhileBusyAndPreservesCancelVersusValidatedConfirmationTarget()
    {
        var previous = new WindowActivationTarget(new(1), 2, 3);
        var confirmed = new WindowActivationTarget(new(2), 4, 5);
        var session = new OverviewSession();
        session.Interaction.BeginSession(previous);
        Assert.False(session.TryBeginClose(confirmed, busy: true, out _));
        Assert.True(session.Interaction.IsOpen);
        Assert.False(session.Transitioning);
        Assert.True(session.TryBeginClose(null, busy: false, out var cancelled));
        Assert.Equal(previous, cancelled);
        Assert.True(session.Transitioning);
        Assert.False(session.TryBeginClose(confirmed, busy: false, out _));
        session.Transitioning = false;
        session.Interaction.BeginSession(previous);
        Assert.True(session.TryBeginClose(confirmed, busy: false, out var target));
        Assert.Equal(confirmed, target);
    }

    [Fact]
    public void DeferredConfirmationAndCancelRemainDistinctAfterOpeningSettles()
    {
        var session = new OverviewSession();
        session.Interaction.BeginSession(new(new(99), 1, 2));
        session.Select(new(1));
        session.Interaction.RequestConfirmation();
        Assert.Equal(OverviewCloseRequest.None, session.TakeCloseRequest(busy: true, visible: true));
        Assert.Equal(OverviewCloseRequest.ConfirmSelection, session.TakeCloseRequest(busy: false, visible: true));
        Assert.Equal(OverviewCloseRequest.None, session.TakeCloseRequest(busy: false, visible: true));
        session.Interaction.RequestConfirmation();
        session.Interaction.RequestDismiss();
        Assert.Equal(OverviewCloseRequest.Cancel, session.TakeCloseRequest(busy: false, visible: true));
        session.Select(IntPtr.Zero);
        session.Interaction.RequestConfirmation();
        Assert.Equal(OverviewCloseRequest.Cancel, session.TakeCloseRequest(busy: false, visible: true));
    }

    private static NavigationTarget Target(int id, double x, double y) => new(new(new(id), 1, 2), new(x, y, 200, 46));
}
