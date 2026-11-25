using WindowScatter;
using Xunit;

public class OverviewFileDragTests
{
    private static readonly WindowActivationTarget First = new(new IntPtr(10), 1, 2);
    private static readonly WindowActivationTarget Second = new(new IntPtr(20), 3, 4);

    [Fact]
    public void ConfirmsOnlyTheSameLiveHoverWhileTheButtonIsHeld()
    {
        var drag = new OverviewFileDrag();
        var empty = drag.Current;
        drag.Observe(First);
        var shortcut = drag.Current;
        drag.Observe(First);

        Assert.Null(drag.Confirm(empty, true, First));
        Assert.Null(drag.Confirm(shortcut, false, First));
        Assert.Null(drag.Confirm(shortcut, true, null));
        Assert.Null(drag.Confirm(shortcut, true, Second));
        Assert.Equal<WindowActivationTarget?>(First, drag.Confirm(shortcut, true, First));

        drag.Observe(Second);
        Assert.Null(drag.Confirm(shortcut, true, Second));
    }

    [Fact]
    public void LeavingAndReturningCannotReviveAQueuedConfirmation()
    {
        var drag = new OverviewFileDrag();
        drag.Observe(First);
        var shortcut = drag.Current;

        drag.Observe(null);
        Assert.Null(drag.Confirm(shortcut, true, First));
        drag.Observe(First);
        Assert.Null(drag.Confirm(shortcut, true, First));
        Assert.Equal<WindowActivationTarget?>(First, drag.Confirm(drag.Current, true, First));
    }

    [Fact]
    public void CancelRejectsQueuedConfirmationAndLateHover()
    {
        var drag = new OverviewFileDrag();
        drag.Observe(First);
        var shortcut = drag.Current;
        drag.Cancel();
        drag.Observe(First);

        Assert.Null(drag.Current);
        Assert.Null(drag.Confirm(shortcut, true, First));
    }

    [Fact]
    public void HeldModifierAndTwoPhysicalKeyPressesAllowOneConfirmation()
    {
        var interaction = new ScatterInteraction();
        var bindings = new OverviewHotkeys((_, _) => false, _ => true, () => true);
        Assert.True(bindings.TryInitialize("Ctrl+F1", out var error), error);
        var input = new OverviewShortcutInput(interaction, () => default, () => default);
        var drag = new OverviewFileDrag();
        const int F1 = 0x70;
        const int LeftControl = 0xA2;

        input.Process(LeftControl, true, false, bindings);
        Assert.True(input.Process(F1, true, false, bindings).Activate);
        interaction.BeginSession(default);
        drag.Observe(First);
        Assert.False(input.Process(F1, true, false, bindings).Activate);
        input.Process(F1, false, false, bindings);
        var secondPress = input.Process(F1, true, false, bindings);

        Assert.True(secondPress.Suppress);
        Assert.True(secondPress.Activate);
        Assert.False(secondPress.AppExpose);
        Assert.Equal<WindowActivationTarget?>(First, drag.Confirm(drag.Current, true, First));
        Assert.False(input.Process(F1, true, false, bindings).Activate);
    }
}
