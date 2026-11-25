using WindowScatter;
using Xunit;

public class OverviewFileDragTests
{
    private static readonly WindowActivationTarget First = new(new IntPtr(10), 1, 2);
    private static readonly WindowActivationTarget Second = new(new IntPtr(20), 3, 4);
    private static readonly IntPtr Display = new(1);

    [Fact]
    public void ConfirmsOnlyTheSameLiveHoverWhileTheButtonIsHeld()
    {
        var drag = new OverviewFileDrag();
        var empty = drag.Current;
        drag.Observe(Display, First);
        var shortcut = drag.Current;
        drag.Observe(Display, First);

        Assert.Null(drag.Confirm(empty, true, Display, First));
        Assert.Null(drag.Confirm(shortcut, false, Display, First));
        Assert.Null(drag.Confirm(shortcut, true, Display, null));
        Assert.Null(drag.Confirm(shortcut, true, Display, Second));
        Assert.Equal<WindowActivationTarget?>(First, drag.Confirm(shortcut, true, Display, First));

        drag.Observe(Display, Second);
        Assert.Null(drag.Confirm(shortcut, true, Display, Second));
    }

    [Fact]
    public void LeavingAndReturningCannotReviveAQueuedConfirmation()
    {
        var drag = new OverviewFileDrag();
        drag.Observe(Display, First);
        var shortcut = drag.Current;

        drag.Observe(Display, null);
        Assert.Null(drag.Confirm(shortcut, true, Display, First));
        drag.Observe(Display, First);
        Assert.Null(drag.Confirm(shortcut, true, Display, First));
        Assert.Equal<WindowActivationTarget?>(First, drag.Confirm(drag.Current, true, Display, First));
    }

    [Fact]
    public void CancelRejectsQueuedConfirmationAndLateHover()
    {
        var drag = new OverviewFileDrag();
        drag.Observe(Display, First);
        var shortcut = drag.Current;
        drag.Cancel();
        drag.Observe(Display, First);

        Assert.Null(drag.Current);
        Assert.Null(drag.Confirm(shortcut, true, Display, First));
    }

    [Theory]
    [InlineData("Ctrl+F1", 0xA2, 0x70, false)]
    [InlineData("Win+F12", 0x5B, 0x7B, true)]
    public void HeldModifierAndTwoPhysicalKeyPressesAllowOneConfirmation(string binding, int modifier, int key, bool maskMenus)
    {
        var interaction = new ScatterInteraction();
        var bindings = new OverviewHotkeys((_, _) => false, _ => true, () => true);
        Assert.True(bindings.TryInitialize(binding, out var error), error);
        var input = new OverviewShortcutInput(interaction, () => default, () => default);
        var drag = new OverviewFileDrag();
        input.Process(modifier, true, false, bindings);
        Assert.True(input.Process(key, true, false, bindings).Activate);
        interaction.BeginSession(default);
        drag.Observe(Display, First);
        Assert.False(input.Process(key, true, false, bindings).Activate);
        input.Process(key, false, false, bindings);
        var secondPress = input.Process(key, true, false, bindings);

        Assert.True(secondPress.Suppress);
        Assert.True(secondPress.Activate);
        Assert.False(secondPress.AppExpose);
        Assert.Equal(maskMenus, secondPress.MaskMenus);
        Assert.Equal<WindowActivationTarget?>(First, drag.Confirm(drag.Current, true, Display, First));
        Assert.False(input.Process(key, true, false, bindings).Activate);
    }
}
