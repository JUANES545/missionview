using System.Runtime.InteropServices;
using WindowScatter;
using Xunit;

public class OverviewFeatureTests
{
    [Theory]
    [InlineData(3840, 1080, 1920, 1080)]
    [InlineData(600, 1600, 5120, 1440)]
    [InlineData(4000, 2000, 320, 240)]
    [InlineData(600, 400, 1920, 1080)]
    public void ExpandedPreviewFitsMonitorAndPreservesAspect(double w, double h, double monitorW, double monitorH)
    {
        var rect = OverviewPreview.Fit(w, h, monitorW, monitorH);
        Assert.InRange(rect.X, 0, monitorW - rect.Width);
        Assert.InRange(rect.Y, 0, monitorH - rect.Height);
        Assert.InRange(rect.Width, 1, monitorW * .9);
        Assert.InRange(rect.Height, 1, monitorH * .8);
        Assert.Equal(w / h, rect.Width / rect.Height, 8);
        Assert.Equal(monitorW / 2, rect.X + rect.Width / 2, 8);
        Assert.Equal(monitorH / 2 - 11, rect.Y + rect.Height / 2, 8);
    }

    [Fact]
    public void InvalidPreviewGeometryIsRejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => OverviewPreview.Fit(0, 600, 1920, 1080));

    [Theory]
    [InlineData(320, 240, 64)]
    [InlineData(320, 240, 128)]
    [InlineData(5120, 1440, 128)]
    public void ExpandedPreviewLeavesToolbarAccessible(double w, double h, double inset)
    {
        double dpi = inset / 64;
        var rect = OverviewPreview.Fit(1920, 1080, w, h, inset, dpi);
        Assert.True(rect.Y >= inset);
        Assert.True(rect.Y + rect.Height <= h);
        Assert.True(rect.X >= 0 && rect.X + rect.Width <= w);
        Assert.Equal(1920.0 / 1080, rect.Width / rect.Height, 8);
        Assert.True(rect.X >= 14 * dpi);
        Assert.True(rect.Y >= inset + 14 * dpi);
        Assert.True(rect.Y + rect.Height + 36 * dpi <= h);
    }

    [Fact]
    public void InitialSelectionUsesPreviousWindowOrNoneNotAnArbitraryThumbnail()
    {
        var previous = new IntPtr(20);
        Assert.Equal(previous, OverviewPreview.InitialSelection(previous, [new IntPtr(10), previous]));
        Assert.Equal(IntPtr.Zero, OverviewPreview.InitialSelection(previous, [new IntPtr(10)]));
        Assert.Equal(IntPtr.Zero, OverviewPreview.InitialSelection(IntPtr.Zero, []));
    }

    [Fact]
    public void RepeatedHotkeyDuringOpeningConfirmsOnceInsteadOfCancelling()
    {
        var interaction = new ScatterInteraction();
        interaction.BeginSession(new WindowActivationTarget(new IntPtr(10), 1, 2));
        interaction.RequestConfirmation();
        Assert.False(interaction.TakeConfirmationRequest(true));
        Assert.False(interaction.TakeDismissRequest(false));
        Assert.True(interaction.TakeConfirmationRequest(false));
        Assert.False(interaction.TakeConfirmationRequest(false));
        var hovered = new WindowActivationTarget(new IntPtr(20), 3, 4);
        Assert.True(interaction.TryBeginClose(hovered, out var target));
        Assert.Equal(hovered, target);
    }

    [Fact]
    public void EscapeOverridesPendingHotkeyConfirmationAndReturnsPrevious()
    {
        var interaction = new ScatterInteraction();
        var previous = new WindowActivationTarget(new IntPtr(10), 1, 2);
        interaction.BeginSession(previous);
        interaction.RequestConfirmation();
        interaction.RequestDismiss();
        Assert.False(interaction.TakeConfirmationRequest(false));
        Assert.True(interaction.TakeDismissRequest(false));
        Assert.True(interaction.TryBeginClose(null, out var target));
        Assert.Equal(previous, target);
    }

    [Fact]
    public void NewSessionClearsPendingConfirmation()
    {
        var interaction = new ScatterInteraction();
        interaction.RequestConfirmation();
        interaction.BeginSession(default);
        Assert.False(interaction.TakeConfirmationRequest(false));
    }

    [Fact]
    public void DesktopUsesPairedShellRequestsWithoutManipulatingWindows()
    {
        var desktop = new DesktopToggle();
        int requests = 0;
        Assert.True(desktop.TryToggle(() => requests++));
        Assert.True(desktop.RestorePending);
        Assert.True(desktop.TryToggle(() => requests++));
        Assert.False(desktop.RestorePending);
        Assert.Equal(2, requests);
    }

    [Fact]
    public void FailedShellRequestDoesNotPretendToChangeDesktopState()
    {
        var desktop = new DesktopToggle();
        Assert.False(desktop.TryToggle(() => throw new COMException("denied")));
        Assert.False(desktop.RestorePending);
        Assert.True(desktop.TryToggle(() => { }));
        Assert.False(desktop.TryToggle(() => throw new COMException("denied")));
        Assert.True(desktop.RestorePending);
    }
}
