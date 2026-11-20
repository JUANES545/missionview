using System.Runtime.InteropServices;
using WindowScatter;
using Xunit;

public class DesktopExitTests
{
    [Fact]
    public void ExitHidesThenRequestsNativeDesktopBeforeDeferredCleanupAndCoalescesCloseRequests()
    {
        var interaction = new ScatterInteraction();
        interaction.BeginSession(default);
        var desktop = new DesktopToggle();
        var order = new List<string>();
        Action? cleanup = null;
        for (int request = 0; request < 2; request++)
            if (interaction.TryBeginClose(null, out _))
                Assert.True(desktop.CompleteOverviewExit(() => order.Add("prepare"),
                    () => order.Add("hide"), () => order.Add("toggle"),
                    () => { order.Add("queue"); cleanup = () => order.Add("cleanup"); }));
        Assert.Equal(["prepare", "hide", "toggle", "queue"], order);
        Assert.True(desktop.RestorePending);
        Assert.NotNull(cleanup);
        cleanup();
        Assert.Equal(["prepare", "hide", "toggle", "queue", "cleanup"], order);
    }

    [Fact]
    public void ComFailureDoesNotAdvancePendingAndStillQueuesCleanup()
    {
        var desktop = new DesktopToggle();
        var order = new List<string>();
        Assert.False(desktop.CompleteOverviewExit(() => order.Add("prepare"), () => order.Add("hide"),
            () => { order.Add("toggle"); throw new COMException("Synthetic failure"); },
            () => order.Add("queue")));
        Assert.False(desktop.RestorePending);
        Assert.Equal(["prepare", "hide", "toggle", "queue"], order);
    }

    [Fact]
    public void HideFailureDoesNotToggleAndStillQueuesCleanup()
    {
        var desktop = new DesktopToggle();
        bool toggled = false, queued = false;
        Assert.Throws<InvalidOperationException>(() => desktop.CompleteOverviewExit(
            () => { }, () => throw new InvalidOperationException("Synthetic failure"),
            () => toggled = true, () => queued = true));
        Assert.False(toggled);
        Assert.True(queued);
        Assert.False(desktop.RestorePending);
    }

    [Fact]
    public void PreparationFailureNeverRequestsNativeDesktopAndStillQueuesCleanup()
    {
        var desktop = new DesktopToggle();
        bool hidden = false, toggled = false, queued = false;
        Assert.Throws<InvalidOperationException>(() => desktop.CompleteOverviewExit(
            () => throw new InvalidOperationException("Synthetic failure"),
            () => hidden = true, () => toggled = true, () => queued = true));
        Assert.False(hidden);
        Assert.False(toggled);
        Assert.True(queued);
        Assert.False(desktop.RestorePending);
    }

    [Fact]
    public void UnexpectedShellFailurePropagatesWithoutAdvancingPendingAndStillQueuesCleanup()
    {
        var desktop = new DesktopToggle();
        bool queued = false;
        Assert.Throws<InvalidOperationException>(() => desktop.CompleteOverviewExit(
            () => { }, () => { }, () => throw new InvalidOperationException("Synthetic failure"), () => queued = true));
        Assert.True(queued);
        Assert.False(desktop.RestorePending);
    }
}
