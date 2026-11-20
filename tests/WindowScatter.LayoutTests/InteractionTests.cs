using WindowScatter;
using Xunit;

public class InteractionTests
{
    [Fact]
    public void HeldActivationKeyOnlyTogglesOnceUntilReleased()
    {
        var input = new ScatterInteraction();
        Assert.True(input.ActivationKeyDown(true));
        Assert.False(input.ActivationKeyDown(true));
        input.ActivationKeyUp();
        Assert.True(input.ActivationKeyDown(true));
    }

    [Fact]
    public void NonmatchingModifiersDoNotActivate()
    {
        var input = new ScatterInteraction();
        Assert.False(input.ActivationKeyDown(false));
        Assert.False(input.ActivationKeyDown(true));
        input.ActivationKeyUp();
        Assert.True(input.ActivationKeyDown(true));
    }

    [Fact]
    public void DismissDuringOpeningWaitsUntilReadyAndRunsOnlyOnce()
    {
        var input = new ScatterInteraction();
        input.RequestDismiss();
        Assert.False(input.TakeDismissRequest(isBusy: true));
        Assert.True(input.TakeDismissRequest(isBusy: false));
        Assert.False(input.TakeDismissRequest(isBusy: false));
    }

    [Fact]
    public void NewSessionDoesNotInheritPendingDismissal()
    {
        var input = new ScatterInteraction();
        input.RequestDismiss();
        input.ClearDismissRequest();
        Assert.False(input.TakeDismissRequest(isBusy: false));
    }

    [Fact]
    public void CancelReturnsToWindowCapturedBeforeOpening()
    {
        var input = new ScatterInteraction();
        var previous = new WindowActivationTarget(new IntPtr(10), 1, 2);
        input.BeginSession(previous);
        input.RequestDismiss();
        Assert.False(input.TakeDismissRequest(isBusy: true));
        Assert.True(input.TakeDismissRequest(isBusy: false));
        Assert.True(input.TryBeginClose(null, out var target));
        Assert.Equal(previous, target);
        Assert.False(input.TryBeginClose(null, out _));
    }

    [Fact]
    public void ConfirmedSelectionCannotBeOverriddenByPendingOrLateCancellation()
    {
        var input = new ScatterInteraction();
        input.BeginSession(new WindowActivationTarget(new IntPtr(10), 1, 2));
        var selected = new WindowActivationTarget(new IntPtr(20), 3, 4);
        input.RequestDismiss();
        Assert.True(input.TryBeginClose(selected, out var target));
        Assert.Equal(selected, target);
        Assert.False(input.TakeDismissRequest(isBusy: false));
        Assert.False(input.TryBeginClose(null, out _));
    }

    [Fact]
    public void ClosingSessionRejectsLateSelectionAndNewSessionUsesNewPreviousWindow()
    {
        var input = new ScatterInteraction();
        var first = new WindowActivationTarget(new IntPtr(10), 1, 2);
        var second = new WindowActivationTarget(new IntPtr(20), 3, 4);
        input.BeginSession(first);
        Assert.True(input.TryBeginClose(null, out var target));
        Assert.Equal(first, target);
        Assert.False(input.TryBeginClose(second, out _));
        input.RequestDismiss();
        input.BeginSession(second);
        Assert.False(input.TakeDismissRequest(isBusy: false));
        Assert.True(input.TryBeginClose(null, out target));
        Assert.Equal(second, target);
    }
}
