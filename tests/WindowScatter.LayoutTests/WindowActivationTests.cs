using WindowScatter;
using Xunit;
using static WindowScatter.Win32Interop;

public class WindowActivationTests
{
    [Theory]
    [InlineData(true, false, SW_RESTORE)]
    [InlineData(true, true, SW_SHOWMAXIMIZED)]
    [InlineData(false, false, 0)]
    public void SelectionRestoresTargetButCancellationOnlyActivatesIt(bool restore, bool maximized, int command)
    {
        var handle = new IntPtr(10);
        IntPtr foreground = new IntPtr(99);
        var shown = new List<(IntPtr, int)>();
        var activation = new WindowActivation(_ => (1, 2),
            (window, show) => shown.Add((window, show)),
            window => { foreground = window; return true; }, () => foreground);
        var target = activation.Capture(handle);

        Assert.True(activation.TryActivate(target, restore, maximized, ownedForeground: true));
        Assert.Equal(handle, foreground);
        if (restore) Assert.Equal((handle, command), Assert.Single(shown));
        else Assert.Empty(shown);
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(5u, 2u)]
    [InlineData(1u, 9u)]
    public void DestroyedOrReusedHandleNeverActivatesAnotherWindow(uint thread, uint process)
    {
        (uint, uint) identity = (1, 2);
        bool activated = false;
        var activation = new WindowActivation(_ => identity,
            (_, _) => throw new Exception("Invalid target must not be restored."),
            _ => activated = true, () => IntPtr.Zero);
        var target = activation.Capture(new IntPtr(10));
        identity = (thread, process);

        Assert.False(activation.TryActivate(target, true, false, true));
        Assert.False(activated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForegroundMustActuallyBecomeTargetRegardlessOfApiReturn(bool apiResult)
    {
        int calls = 0;
        var activation = new WindowActivation(_ => (1, 2), (_, _) => { },
            _ => { calls++; return apiResult; }, () => new IntPtr(99));
        var target = activation.Capture(new IntPtr(10));
        Assert.False(activation.TryActivate(target, true, false, true));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ExternalFocusChangeDuringTransitionIsRespected()
    {
        var activation = new WindowActivation(_ => (1, 2),
            (_, _) => throw new Exception("Must not restore after losing foreground."),
            _ => throw new Exception("Must not steal foreground."), () => new IntPtr(99));
        var target = activation.Capture(new IntPtr(10));
        Assert.False(activation.TryActivate(target, true, false, ownedForeground: false));
    }

    [Fact]
    public void WindowDestroyedDuringRestoreIsNotActivated()
    {
        (uint, uint) identity = (1, 2);
        var activation = new WindowActivation(_ => identity,
            (_, _) => identity = (0, 0),
            _ => throw new Exception("Destroyed target must not be activated."), () => IntPtr.Zero);
        var target = activation.Capture(new IntPtr(10));
        Assert.False(activation.TryActivate(target, true, false, true));
    }

    [Fact]
    public void MissingPreviousWindowDoesNotChooseAnAlternative()
    {
        var activation = new WindowActivation(_ => throw new Exception("No handle to identify."),
            (_, _) => throw new Exception("No handle to restore."),
            _ => throw new Exception("No handle to activate."), () => new IntPtr(99));
        Assert.False(activation.TryActivate(activation.Capture(IntPtr.Zero), false, false, true));
    }

    [Theory]
    [InlineData(90, true)]
    [InlineData(91, true)]
    [InlineData(99, false)]
    [InlineData(0, false)]
    public void ClosingOverviewRecognizesHostAndChromeBeforeHidingButNotExternalFocus(int activeHandle, bool expected)
    {
        var host = new IntPtr(90);
        var chrome = new IntPtr(91);
        IntPtr active = new IntPtr(activeHandle);
        bool hidden = false;
        int attempts = 0;
        var activation = new WindowActivation(_ => (1, 2), (_, _) => { },
            handle =>
            {
                Assert.True(hidden);
                attempts++;
                active = handle;
                return true;
            }, () => active);
        var target = activation.Capture(new IntPtr(10));
        bool result = activation.CloseOverviewAndActivate(target, true, false, host, chrome,
            () => { hidden = true; active = new IntPtr(99); });
        Assert.True(hidden);
        Assert.Equal(expected, result);
        Assert.Equal(expected ? 1 : 0, attempts);
    }
}
