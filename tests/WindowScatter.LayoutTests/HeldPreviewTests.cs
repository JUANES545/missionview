using WindowScatter;
using Xunit;

public class HeldPreviewTests
{
    [Fact]
    public void HoldIgnoresRepeatAndReleaseAllowsNextPress()
    {
        var state = new HeldPreviewState();
        Assert.False(state.Press(isRepeat: true));
        Assert.True(state.Press(isRepeat: false));
        Assert.False(state.Press(isRepeat: true));
        Assert.False(state.Press(isRepeat: false));
        Assert.True(state.Release());
        Assert.False(state.Release());
        Assert.True(state.Press(isRepeat: false));
    }

    [Fact]
    public void InterruptionClearsHoldWithoutRepeatingExpansion()
    {
        var state = new HeldPreviewState();
        Assert.True(state.Press(false));
        Assert.True(state.Release());
        Assert.False(state.IsHeld);
        Assert.False(state.Press(true));
        Assert.False(state.Release());
    }

    [Fact]
    public void ZoomInterpolatesFromCurrentToExactOriginalSlot()
    {
        var start = new PreviewBounds(10, 20, 800, 400);
        var slot = new PreviewBounds(600, 300, 200, 100);
        Assert.Equal(start, PreviewBounds.Interpolate(start, slot, 0));
        var middle = PreviewBounds.Interpolate(start, slot, .5);
        Assert.InRange(middle.Width, slot.Width, start.Width);
        Assert.Equal(2, middle.Width / middle.Height);
        Assert.Equal(slot, PreviewBounds.Interpolate(start, slot, 1));
        Assert.Equal(slot, PreviewBounds.Interpolate(middle, slot, 1));
    }

    [Fact]
    public void FractionalSlotIsRestoredExactlyAtEndOfAnimation()
    {
        var slot = new PreviewBounds(.1, .2, 300.1, 150.05);
        Assert.Equal(slot, PreviewBounds.Interpolate(new(5120, 1440, 5000, 2500), slot, 1));
    }
}
