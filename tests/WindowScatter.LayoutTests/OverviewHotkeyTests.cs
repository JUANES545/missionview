using WindowScatter;
using Xunit;

public class OverviewHotkeyTests
{
    private sealed class NativeDouble
    {
        public readonly Dictionary<int, OverviewHotkey> Registered = new();
        public Func<int, OverviewHotkey, bool> Allow = (_, _) => true;
        public Func<int, bool> AllowRelease = _ => true;
        public int Calls;
        public OverviewHotkeys Create() => new((id, key) =>
        {
            Calls++;
            if (!Allow(id, key) || Registered.ContainsKey(id) || Registered.Values.Contains(key)) return false;
            Registered.Add(id, key);
            return true;
        }, id => AllowRelease(id) && Registered.Remove(id));
    }

    [Theory]
    [InlineData("Win+F12", 8, 0x7B)]
    [InlineData("Alt+F1", 2, 0x70)]
    [InlineData("Ctrl+Alt+K", 3, 0x4B)]
    public void DerivesShiftFromConfigurationWithExactModifiersAndNoFixedFunctionKey(string shortcut, int modifiers, int key)
    {
        var native = new NativeDouble();
        var hotkeys = native.Create();
        Assert.True(hotkeys.TryInitialize(shortcut, out var error), error);
        Assert.Empty(error);
        Assert.True(hotkeys.MatchesNormal(key, modifiers));
        Assert.True(hotkeys.MatchesAppExpose(key, modifiers | 4));
        Assert.False(hotkeys.MatchesNormal(key, modifiers | 4));
        Assert.False(hotkeys.MatchesAppExpose(key + 1, modifiers | 4));
        Assert.False(hotkeys.MatchesAppExpose(key, modifiers | 4 | 16));
    }

    [Theory]
    [InlineData("Win+F12", 8u)]
    [InlineData("Ctrl+F12", 2u)]
    [InlineData("Alt+F12", 1u)]
    [InlineData("Ctrl+Alt+Shift+F12", 7u)]
    public void TranslatesHeritageCtrlAltBitsToWin32(string shortcut, uint nativeModifiers)
        => Assert.Equal(nativeModifiers, OverviewHotkey.Parse(shortcut).NativeModifiers);

    [Fact]
    public void UnchangedBindingDoesNotProbeItsOwnReservedCombinations()
    {
        var native = new NativeDouble();
        var hotkeys = native.Create();
        Assert.True(hotkeys.TryInitialize("Win+F12", out _));
        int calls = native.Calls;
        Assert.True(hotkeys.TryApply("win+f12", false, out _));
        Assert.Equal(calls, native.Calls);
        Assert.Equal(2, native.Registered.Count);
    }

    [Fact]
    public void ReconfigurationPublishesBothAndRemovesBothOldCombinations()
    {
        var native = new NativeDouble();
        var hotkeys = native.Create();
        Assert.True(hotkeys.TryInitialize("Win+F12", out _));
        Assert.True(hotkeys.TryApply("Win+Alt+F12", false, out _));
        Assert.False(hotkeys.MatchesNormal(0x7B, 8));
        Assert.False(hotkeys.MatchesAppExpose(0x7B, 12));
        Assert.True(hotkeys.MatchesNormal(0x7B, 10));
        Assert.True(hotkeys.MatchesAppExpose(0x7B, 14));
        Assert.Equal(2, native.Registered.Count);
    }

    [Fact]
    public void DerivedRegistrationFailureRollsBackBothOldCombinationsAndDispatch()
    {
        var native = new NativeDouble();
        var hotkeys = native.Create();
        Assert.True(hotkeys.TryInitialize("Win+F12", out _));
        var before = native.Registered.OrderBy(p => p.Key).ToArray();
        native.Allow = (_, key) => key != OverviewHotkey.Parse("Win+Shift+F1");
        Assert.False(hotkeys.TryApply("Win+F1", false, out var error));
        Assert.Contains("previous shortcuts are unchanged", error);
        Assert.Equal(before, native.Registered.OrderBy(p => p.Key).ToArray());
        Assert.True(hotkeys.MatchesNormal(0x7B, 8));
        Assert.False(hotkeys.MatchesNormal(0x70, 8));
    }

    [Fact]
    public void StartupDerivedConflictPreservesNormalRecallAndReportsUnavailableAction()
    {
        var native = new NativeDouble { Allow = (id, _) => id != OverviewHotkeys.AppExposeId };
        var hotkeys = native.Create();
        Assert.True(hotkeys.TryInitialize("Win+F12", out var warning));
        Assert.Contains("App Expose is unavailable", warning);
        Assert.True(hotkeys.MatchesNormal(0x7B, 8));
        Assert.False(hotkeys.MatchesAppExpose(0x7B, 12));
        Assert.Single(native.Registered);
    }

    [Fact]
    public void InheritedShiftNormalSurvivesUnchangedSaveAndRollbackButNewShiftIsRejected()
    {
        var native = new NativeDouble();
        var hotkeys = native.Create();
        Assert.True(hotkeys.TryInitialize("Win+Shift+F12", out var warning));
        Assert.Contains("includes Shift", warning);
        Assert.True(hotkeys.MatchesNormal(0x7B, 12));
        Assert.False(hotkeys.MatchesAppExpose(0x7B, 12));
        Assert.True(hotkeys.TryApply("win+shift+f12", false, out _));
        Assert.True(hotkeys.TryApply("Win+F12", false, out _));
        Assert.True(hotkeys.TryApply("Win+Shift+F12", false, out _));
        Assert.True(hotkeys.TryApply("Win+F1", false, out _));
        Assert.True(hotkeys.TryApply("Win+Shift+F12", false, out _));
        Assert.False(hotkeys.TryApply("Win+Shift+F2", false, out _));
        Assert.True(hotkeys.MatchesNormal(0x7B, 12));
        Assert.Single(native.Registered);
    }

    [Fact]
    public void FailedRollbackDisablesDispatchAndCanRetryWithoutLeakingReservations()
    {
        var native = new NativeDouble();
        var hotkeys = native.Create();
        Assert.True(hotkeys.TryInitialize("Win+F12", out _));
        native.Allow = (_, _) => false;
        Assert.False(hotkeys.TryApply("Win+F1", false, out var error));
        Assert.Contains("rollback", error);
        Assert.True(hotkeys.IsFaulted);
        Assert.Null(hotkeys.Normal);
        Assert.Null(hotkeys.AppExpose);
        native.Allow = (_, _) => true;
        Assert.True(hotkeys.TryApply("Win+F12", false, out _));
        Assert.False(hotkeys.IsFaulted);
        Assert.True(hotkeys.Release());
        Assert.Empty(native.Registered);
    }

    [Fact]
    public void ReservedSystemBindingKeepsOnlyInheritedHookActionIncludingUnchangedSaveAndRollback()
    {
        var native = new NativeDouble { Allow = (_, _) => false };
        var hotkeys = native.Create();
        Assert.True(hotkeys.TryInitialize("Win+W", out var warning));
        Assert.Contains("preserved", warning);
        Assert.True(hotkeys.MatchesNormal('W', 8));
        Assert.False(hotkeys.MatchesAppExpose('W', 12));
        Assert.Empty(native.Registered);
        Assert.True(hotkeys.TryApply("Win+W", false, out var sameWarning));
        Assert.NotEmpty(sameWarning);
        Assert.False(hotkeys.TryApply("Win+F1", false, out _));
        Assert.True(hotkeys.MatchesNormal('W', 8));
        native.Allow = (_, _) => true;
        Assert.True(hotkeys.TryApply("Win+F1", false, out _));
        Assert.Equal(2, native.Registered.Count);
        Assert.True(hotkeys.TryApply("Win+W", false, out var rollbackWarning));
        Assert.NotEmpty(rollbackWarning);
        Assert.True(hotkeys.MatchesNormal('W', 8));
        Assert.Empty(native.Registered);
    }

    [Fact]
    public void InheritedNormalOnlyReservationDoesNotBlockSavingOtherPreferences()
    {
        var native = new NativeDouble { Allow = (id, _) => id != OverviewHotkeys.AppExposeId };
        var hotkeys = native.Create();
        Assert.True(hotkeys.TryInitialize("Win+F12", out _));
        int calls = native.Calls;
        Assert.True(hotkeys.TryApply("Win+F12", false, out var warning));
        Assert.NotEmpty(warning);
        Assert.Equal(calls, native.Calls);
        Assert.True(hotkeys.MatchesNormal(0x7B, 8));
        Assert.False(hotkeys.MatchesAppExpose(0x7B, 12));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Win++F12")]
    [InlineData("Win+F12+F1")]
    [InlineData("Win+Unknown+F12")]
    [InlineData("Shift+Win")]
    public void InvalidInputLeavesCurrentRegistrationAndDispatchUntouched(string invalid)
    {
        var native = new NativeDouble();
        var hotkeys = native.Create();
        Assert.True(hotkeys.TryInitialize("Win+F12", out _));
        var before = native.Registered.OrderBy(p => p.Key).ToArray();
        Assert.False(hotkeys.TryApply(invalid, false, out var error));
        Assert.NotEmpty(error);
        Assert.Equal(before, native.Registered.OrderBy(p => p.Key).ToArray());
        Assert.True(hotkeys.MatchesNormal(0x7B, 8));
    }

    [Fact]
    public void RecordingSnapshotIsImmutableAndClearCannotLeaveAStuckActivationModifier()
    {
        var state = new KeyboardModifierState();
        state.Update(0x5B, true);
        state.Update(0xA0, true);
        int snapshot = state.Modifiers;
        state.Clear();
        Assert.Equal(12, snapshot);
        Assert.True(state.Matches(0));
        state.Update(0xA0, false);
        state.Update(0x5B, false);
        Assert.True(state.Matches(0));
        var interaction = new ScatterInteraction();
        Assert.True(interaction.ActivationKeyDown(true));
        interaction.ActivationKeyUp();
        Assert.True(interaction.ActivationKeyDown(true));
    }

    [Fact]
    public void AppExposeUsesPointedApplicationWithoutChangingForeground()
    {
        var hotkeys = new NativeDouble().Create();
        Assert.True(hotkeys.TryInitialize("Win+F12", out _));
        var foreground = new WindowActivationTarget(new(10), 1, 100);
        var pointer = new WindowActivationTarget(new(20), 2, 200);
        var pointedApplication = ApplicationIdentity.Create(@"C:\BrowserB\browser.exe", null, null);
        var input = new OverviewShortcutInput(new(), () => foreground, () => pointer);
        input.Process(0x5B, true, false, hotkeys);
        input.Process(0xA0, true, false, hotkeys);
        var queued = input.Process(0x7B, true, false, hotkeys);
        var capturedPointer = pointer;
        pointer = new(new(99), 3, 300);
        var request = AppExposeRequest.Capture(() => queued.ApplicationWindow, _ => false,
            target => target == capturedPointer ? pointedApplication : ApplicationIdentity.Create(@"C:\EditorA\editor.exe", null, null),
            queued.Foreground);
        Assert.Equal(pointedApplication, request.Application);
        Assert.Equal(new WindowActivationTarget(new(10), 1, 100), foreground);
        Assert.Equal(foreground, request.Foreground);
        Assert.Equal(capturedPointer, queued.ApplicationWindow);
        Assert.True(request.Includes(capturedPointer));
    }

    [Fact]
    public void AppExposeRepeatInsideSessionNeverRecapturesOurOverlayAsAnApplication()
    {
        var hotkeys = new NativeDouble().Create();
        Assert.True(hotkeys.TryInitialize("Win+F12", out _));
        var interaction = new ScatterInteraction();
        int foregroundReads = 0, pointerReads = 0;
        var input = new OverviewShortcutInput(interaction,
            () => { foregroundReads++; return new(new(10), 1, 100); },
            () => { pointerReads++; return new(new(20), 2, 200); });
        input.Process(0x5B, true, false, hotkeys);
        input.Process(0xA0, true, false, hotkeys);
        var opening = input.Process(0x7B, true, false, hotkeys);
        Assert.Equal(new IntPtr(20), opening.ApplicationWindow.Handle);
        Assert.False(input.Process(0x7B, true, false, hotkeys).Activate);
        interaction.BeginSession(opening.Foreground);
        input.Process(0x7B, false, false, hotkeys);
        var confirmation = input.Process(0x7B, true, false, hotkeys);
        Assert.True(confirmation.Activate);
        Assert.True(confirmation.AppExpose);
        Assert.Equal(default, confirmation.ApplicationWindow);
        Assert.Null(confirmation.CaptureError);
        Assert.Equal(1, foregroundReads);
        Assert.Equal(1, pointerReads);
        Assert.True(interaction.IsOpen);
    }

    [Fact]
    public void PointerCaptureFailureIsReportedWithoutEscapingHookOrLatchingTheShortcut()
    {
        var hotkeys = new NativeDouble().Create();
        Assert.True(hotkeys.TryInitialize("Win+F12", out _));
        int attempts = 0;
        var input = new OverviewShortcutInput(new(), () => new(new(10), 1, 100),
            () => ++attempts == 1
                ? throw new System.ComponentModel.Win32Exception(5, "Pointer window access denied.")
                : new(new(20), 2, 200));
        input.Process(0x5B, true, false, hotkeys);
        input.Process(0xA0, true, false, hotkeys);
        var denied = input.Process(0x7B, true, false, hotkeys);
        Assert.True(denied.Suppress);
        Assert.True(denied.Activate);
        Assert.True(denied.AppExpose);
        Assert.Contains("access denied", denied.CaptureError);
        Assert.Equal(default, denied.ApplicationWindow);
        input.Process(0x7B, false, false, hotkeys);
        var retry = input.Process(0x7B, true, false, hotkeys);
        Assert.Null(retry.CaptureError);
        Assert.Equal(new IntPtr(20), retry.ApplicationWindow.Handle);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void HookInputCapturesForegroundBeforeQueuedOverlayAndSuppressesOnlyOneActivationPerPress()
    {
        var hotkeys = new NativeDouble().Create();
        Assert.True(hotkeys.TryInitialize("Win+F12", out _));
        var interaction = new ScatterInteraction();
        var foreground = new WindowActivationTarget(new(10), 1, 2);
        int captures = 0;
        var input = new OverviewShortcutInput(interaction, () => { captures++; return foreground; }, () => new(new(20), 2, 20));
        input.Process(0x5B, true, false, hotkeys);
        input.Process(0xA0, true, false, hotkeys);
        var queued = input.Process(0x7B, true, false, hotkeys);
        Assert.True(queued.Suppress);
        Assert.True(queued.Activate);
        Assert.True(queued.AppExpose);
        foreground = new(new(99), 3, 4); // The overlay can activate only after capture returned.
        Assert.Equal(new IntPtr(10), queued.Foreground.Handle);
        var repeat = input.Process(0x7B, true, false, hotkeys);
        Assert.True(repeat.Suppress);
        Assert.False(repeat.Activate);
        Assert.Equal(1, captures);
        input.Process(0x7B, false, false, hotkeys);
        Assert.True(input.Process(0x7B, true, false, hotkeys).Activate);
        Assert.Equal(2, captures);
    }

    [Fact]
    public void HookInputKeepsNormalAndApplicationChordsExactAndDoesNotTriggerOnModifierChangesWhileHeld()
    {
        var hotkeys = new NativeDouble().Create();
        Assert.True(hotkeys.TryInitialize("Win+F12", out _));
        var input = new OverviewShortcutInput(new(), () => new(new(10), 1, 2), () => new(new(20), 2, 20));
        input.Process(0x5B, true, false, hotkeys);
        var normal = input.Process(0x7B, true, false, hotkeys);
        Assert.True(normal.Activate);
        Assert.False(normal.AppExpose);
        input.Process(0xA0, true, false, hotkeys);
        Assert.False(input.Process(0x7B, true, false, hotkeys).Activate);
        input.Process(0x7B, false, false, hotkeys);
        input.Process(0xA2, true, false, hotkeys);
        Assert.False(input.Process(0x7B, true, false, hotkeys).Suppress);
        input.Process(0xA2, false, false, hotkeys);
        Assert.False(input.Process(0x7B, true, false, hotkeys).Activate);
        input.Process(0x7B, false, false, hotkeys);
        var app = input.Process(0x7B, true, false, hotkeys);
        Assert.True(app.Activate);
        Assert.True(app.AppExpose);
    }

    [Fact]
    public void RawRecordingReceivesReservedChordAndCannotDispatchOrLatchModifiersAfterClosing()
    {
        var hotkeys = new NativeDouble().Create();
        Assert.True(hotkeys.TryInitialize("Win+F12", out _));
        int captures = 0;
        var input = new OverviewShortcutInput(new(), () => { captures++; return new(new(10), 1, 2); },
            () => throw new Exception("Recording and the normal shortcut must not capture the pointer."));
        input.Process(0x5B, true, true, hotkeys);
        input.Process(0xA0, true, true, hotkeys);
        var raw = input.Process(0x7B, true, true, hotkeys);
        Assert.Equal(12, raw.Modifiers);
        Assert.False(raw.Suppress);
        Assert.False(raw.Activate);
        Assert.Equal(0, captures);
        Assert.False(input.Process(0x7B, true, false, hotkeys).Activate);
        input.Process(0x7B, false, false, hotkeys);
        input.Process(0xA0, false, false, hotkeys);
        input.Process(0x5B, false, false, hotkeys);
        input.Process(0x5B, true, false, hotkeys);
        Assert.True(input.Process(0x7B, true, false, hotkeys).Activate);
        Assert.Equal(1, captures);
    }
}
