using WindowScatter;
using Xunit;
using static WindowScatter.Win32Interop;

public class WindowActivationTests
{
    [Theory]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    public void PendingCloseRestoresOnlyExplicitReturnsOrOwnedPromptsWithoutForcingMaximization(
        bool explicitReturn, bool ownedPrompt, bool minimized, bool restored)
    {
        var original = new WindowActivationTarget(new(20), 3, 4);
        var target = ownedPrompt ? new WindowActivationTarget(new(30), 5, 4) : original;
        IntPtr active = new(99);
        var shown = new List<(IntPtr, int)>();
        var activation = new WindowActivation(_ => (target.ThreadId, target.ProcessId),
            (handle, command) => shown.Add((handle, command)),
            handle => { active = handle; return true; }, () => active, _ => minimized);

        Assert.True(activation.CloseOverviewForPendingClose(target, original, explicitReturn,
            new HashSet<IntPtr> { new(99) }, () => { }, () => true));
        Assert.Equal(target.Handle, active);
        if (restored) Assert.Equal((target.Handle, SW_RESTORE), Assert.Single(shown));
        else Assert.Empty(shown);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AutomaticSourceAttentionDoesNotRestoreAWindowThatMinimizesDuringHiding(bool stillActive)
    {
        var original = new WindowActivationTarget(new(20), 3, 4);
        IntPtr active = original.Handle;
        var close = new WindowClose(_ => original, _ => (true, 0),
            _ => new(original, IntPtr.Zero, true, true), foreground: () => active);
        close.Request(original);
        Assert.Equal(original, close.FindAttentionTarget());
        if (!stillActive) active = new(99);
        bool minimized = false;
        var activation = new WindowActivation(_ => (original.ThreadId, original.ProcessId),
            (_, _) => throw new Exception("Automatic source attention must not restore a closing window."),
            handle => { active = handle; return true; }, () => active, _ => minimized);

        Assert.True(activation.CloseOverviewForPendingClose(original, original, explicitReturn: false,
            new HashSet<IntPtr> { new(99) }, () => minimized = true, () => true));
        Assert.Equal(original.Handle, active);
        Assert.True(minimized);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingCloseHandoffNeverMutatesAChangedIdentityOrStealsExternalForeground(bool identityChanged)
    {
        var original = new WindowActivationTarget(new(20), 3, 4);
        IntPtr active = identityChanged ? new(99) : new(88);
        var identity = original;
        var activation = new WindowActivation(_ => (identity.ThreadId, identity.ProcessId),
            (_, _) => throw new Exception("Invalid or externally focused target must not be restored."),
            _ => throw new Exception("Invalid or externally focused target must not be activated."),
            () => active, _ => true);
        Assert.False(activation.CloseOverviewForPendingClose(original, original, explicitReturn: true,
            new HashSet<IntPtr> { new(99) },
            () => { if (identityChanged) identity = original with { ThreadId = 9 }; },
            () => identity == original));
        Assert.Equal(identityChanged ? new IntPtr(99) : new IntPtr(88), active);
    }

    [Fact]
    public void PendingCloseCancellationReturnsToRequestedBInsteadOfPreviousAWithoutReplayingClose()
    {
        var previous = new WindowActivationTarget(new(10), 1, 2);
        var requested = new WindowActivationTarget(new(20), 3, 4);
        int sent = 0;
        var close = new WindowClose(handle => handle == requested.Handle ? requested : previous,
            _ => { sent++; return (true, 0); });
        var interaction = new ScatterInteraction();
        interaction.BeginSession(previous);
        close.Request(requested);
        Assert.True(interaction.TryBeginClose(close.ResolveReturnTarget(), out var target));
        Assert.Equal(requested, target);
        Assert.Equal(1, sent);
    }

    [Theory]
    [InlineData(20, true)]
    [InlineData(30, true)]
    [InlineData(40, false)]
    [InlineData(10, false)]
    public void PendingAttentionRequiresRequestedWindowOrExactOwnedForeground(int activeHandle, bool attention)
    {
        var target = new WindowActivationTarget(new(20), 3, 4);
        var popup = new WindowActivationTarget(new(30), 5, 4);
        var unrelated = new WindowActivationTarget(new(40), 6, 4);
        var surfaces = new Dictionary<IntPtr, CloseWindowSurface>
        {
            [target.Handle] = new(target, IntPtr.Zero, true, true),
            [popup.Handle] = new(popup, target.Handle, true, true),
            [unrelated.Handle] = new(unrelated, IntPtr.Zero, true, true)
        };
        var close = new WindowClose(handle => surfaces.GetValueOrDefault(handle).Identity, _ => (true, 0),
            handle => surfaces.GetValueOrDefault(handle), _ => target.Handle, () => new(activeHandle));
        close.Request(target);
        Assert.Equal(attention ? new IntPtr(activeHandle) : (IntPtr?)null, close.FindAttentionTarget()?.Handle);
        Assert.Equal(attention ? new IntPtr(activeHandle) : target.Handle, close.ResolveReturnTarget()?.Handle);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void NativeOwnedModalRequiresDisabledOwnerAndStableOwnershipIdentity(
        bool unrelatedOwner, bool reusedPopup, bool enabledOwner, bool expected)
    {
        var target = new WindowActivationTarget(new(20), 3, 4);
        var popup = new WindowActivationTarget(new(30), 5, 4);
        int reads = 0;
        CloseWindowSurface Read(IntPtr handle)
        {
            if (handle == target.Handle) return new(target, IntPtr.Zero, true, enabledOwner);
            if (handle != popup.Handle) return default;
            reads++;
            return new(reusedPopup && reads > 1 ? popup with { ThreadId = 9 } : popup,
                unrelatedOwner ? new(40) : target.Handle, true, true);
        }
        var close = new WindowClose(handle => Read(handle).Identity, _ => (true, 0), Read,
            _ => popup.Handle, () => new(99));
        close.Request(target);
        Assert.Equal(expected ? popup : (WindowActivationTarget?)null, close.FindAttentionTarget());
    }

    [Fact]
    public void ChangedOwnerChainCannotHandoffAnUnrelatedWindowEvenInTheSameProcess()
    {
        var target = new WindowActivationTarget(new(20), 3, 4);
        var popup = new WindowActivationTarget(new(30), 5, 4);
        int reads = 0;
        CloseWindowSurface Read(IntPtr handle) => handle == target.Handle
            ? new(target, IntPtr.Zero, true, false)
            : handle == popup.Handle ? new(popup, ++reads == 1 ? target.Handle : new(40), true, true) : default;
        var close = new WindowClose(handle => handle == target.Handle ? target : popup, _ => (true, 0),
            Read, _ => popup.Handle, () => new(99));
        close.Request(target);
        Assert.Null(close.FindAttentionTarget());
    }

    [Theory]
    [InlineData(false, true, 4u)]
    [InlineData(true, false, 4u)]
    [InlineData(true, true, 9u)]
    public void HiddenDisabledOrCrossProcessOwnedWindowsAreNotCloseAttention(bool visible, bool enabled, uint process)
    {
        var original = new WindowActivationTarget(new(20), 3, 4);
        var popup = new WindowActivationTarget(new(30), 5, process);
        CloseWindowSurface Read(IntPtr handle) => handle == original.Handle
            ? new(original, IntPtr.Zero, true, false)
            : handle == popup.Handle ? new(popup, original.Handle, visible, enabled) : default;
        var close = new WindowClose(handle => Read(handle).Identity, _ => (true, 0), Read, _ => popup.Handle);
        close.Request(original);
        Assert.Null(close.FindAttentionTarget());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AttentionHandoffRevalidatesOwnershipAfterHidingWithoutDependingOnClearedSessionState(bool changedOwner)
    {
        var original = new WindowActivationTarget(new(20), 3, 4);
        var popup = new WindowActivationTarget(new(30), 5, 4);
        IntPtr owner = original.Handle, active = new(99);
        int activated = 0;
        CloseWindowSurface Read(IntPtr handle) => handle == original.Handle
            ? new(original, IntPtr.Zero, true, false)
            : handle == popup.Handle ? new(popup, owner, true, true) : default;
        var close = new WindowClose(handle => Read(handle).Identity, _ => (true, 0), Read, _ => popup.Handle);
        close.Request(original);
        Assert.Equal(popup, close.FindAttentionTarget());
        var activation = new WindowActivation(handle => handle == popup.Handle
                ? (popup.ThreadId, popup.ProcessId) : (original.ThreadId, original.ProcessId),
            (_, _) => throw new Exception("Visible prompt must not be restored."),
            handle => { activated++; active = handle; return true; }, () => active, _ => false);
        bool result = activation.CloseOverviewForPendingClose(popup, original, explicitReturn: false, new HashSet<IntPtr> { new(99) },
            () =>
            {
                close.EndSession();
                if (changedOwner) owner = new(40);
            }, () => close.IsReturnTargetValid(popup, original));
        Assert.Equal(!changedOwner, result);
        Assert.Equal(changedOwner ? 0 : 1, activated);
    }

    [Fact]
    public void LatestPendingRequestDoesNotFallBackToAnotherPendingWindowOrAReusedHandle()
    {
        var first = new WindowActivationTarget(new(20), 3, 4);
        var latest = new WindowActivationTarget(new(30), 5, 4);
        var identity = latest;
        var close = new WindowClose(handle => handle == first.Handle ? first : identity, _ => (true, 0));
        close.Request(first);
        close.Request(latest);
        Assert.Equal(latest, close.ResolveReturnTarget());
        identity = latest with { ProcessId = 9 };
        Assert.Null(close.ResolveReturnTarget());
        Assert.Equal(latest, Assert.Single(close.ConfirmClosed()));
        Assert.True(close.IsPending(first));
        Assert.Null(close.ResolveReturnTarget());
        close.EndSession();
        Assert.Null(close.ResolveReturnTarget());
    }

    [Fact]
    public void HiddenPendingOriginalCannotBecomeAnEscapeFocusTarget()
    {
        var target = new WindowActivationTarget(new(20), 3, 4);
        var close = new WindowClose(_ => target, _ => (true, 0),
            _ => new(target, IntPtr.Zero, false, true));
        close.Request(target);
        Assert.Null(close.ResolveReturnTarget());
        Assert.False(close.IsReturnTargetValid(target, target));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void PendingEscapeHandoffPreservesExternalForegroundAndRestoresOnlyIfNeeded(bool ownForeground, bool minimized)
    {
        var target = new WindowActivationTarget(new(20), 3, 4);
        IntPtr active = ownForeground ? new(99) : new(88);
        int restored = 0, activated = 0;
        var close = new WindowClose(_ => target, _ => (true, 0));
        close.Request(target);
        var activation = new WindowActivation(_ => (target.ThreadId, target.ProcessId),
            (_, _) => restored++, handle => { activated++; active = handle; return true; },
            () => active, _ => minimized);
        activation.CloseOverviewForPendingClose(close.ResolveReturnTarget()!.Value, target, explicitReturn: true,
            new HashSet<IntPtr> { new(99) }, () => { }, () => close.IsReturnTargetValid(target, target));
        Assert.Equal(ownForeground ? 1 : 0, activated);
        Assert.Equal(ownForeground && minimized ? 1 : 0, restored);
        Assert.Equal(ownForeground ? target.Handle : new(88), active);
    }

    [Fact]
    public void OrdinaryConfirmedCloseLeavesOverviewOpenWithoutAnAttentionHandoff()
    {
        var target = new WindowActivationTarget(new(20), 3, 4);
        var identity = target;
        var close = new WindowClose(_ => identity, _ => (true, 0));
        var interaction = new ScatterInteraction();
        interaction.BeginSession(new(new(10), 1, 2));
        close.Request(target);
        Assert.Null(close.FindAttentionTarget());
        identity = default;
        Assert.Equal(target, Assert.Single(close.ConfirmClosed()));
        Assert.Null(close.FindAttentionTarget());
        Assert.False(close.HasPending);
        Assert.True(interaction.IsOpen);
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(8u, 2u)]
    [InlineData(1u, 7u)]
    public void CloseRejectsStaleIdentityBeforeSending(uint thread, uint process)
    {
        var target = new WindowActivationTarget(new(10), 1, 2);
        var close = new WindowClose(handle => new(handle, thread, process),
            _ => throw new Exception("A stale preview must not send WM_CLOSE."));
        Assert.Throws<InvalidOperationException>(() => close.Request(target));
        Assert.False(close.IsPending(target));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(1400)]
    public void CloseChecksNativeFailureAndDoesNotLatchARequestThatWasNotQueued(int error)
    {
        var target = new WindowActivationTarget(new(10), 1, 2);
        var close = new WindowClose(_ => target, _ => (false, error));
        var failure = Assert.Throws<System.ComponentModel.Win32Exception>(() => close.Request(target));
        Assert.Equal(error, failure.NativeErrorCode);
        Assert.False(close.IsPending(target));
        Assert.Empty(close.ConfirmClosed());
    }

    [Fact]
    public void QueuedCloseRemainsUnconfirmedForCanceledOrSlowWindowsAndIsSentOnlyOnce()
    {
        var target = new WindowActivationTarget(new(10), 1, 2);
        var identity = target;
        var sent = new List<IntPtr>();
        var close = new WindowClose(_ => identity, handle => { sent.Add(handle); return (true, 0); });
        close.Request(target);
        Assert.Equal(target.Handle, Assert.Single(sent));
        Assert.False(close.IsClosed(target));
        Assert.Empty(close.ConfirmClosed());
        Assert.True(close.IsPending(target));
        Assert.Throws<InvalidOperationException>(() => close.Request(target));
        Assert.Single(sent);
        identity = default;
        Assert.Equal(target, Assert.Single(close.ConfirmClosed()));
        Assert.False(close.IsPending(target));
    }

    [Fact]
    public void CloseObservationNeverTargetsAReusedHandleAndNewSessionsNeedANewExplicitRequest()
    {
        var target = new WindowActivationTarget(new(10), 1, 2);
        var identity = target;
        int requests = 0;
        var close = new WindowClose(_ => identity, _ => { requests++; return (true, 0); });
        close.Request(target);
        close.EndSession();
        Assert.Empty(close.ConfirmClosed());
        Assert.Equal(1, requests);
        close.Request(target);
        identity = target with { ProcessId = 9 };
        Assert.Equal(target, Assert.Single(close.ConfirmClosed()));
        Assert.Equal(2, requests);
        Assert.Throws<InvalidOperationException>(() => close.Request(target));
        Assert.Equal(2, requests);
    }

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
