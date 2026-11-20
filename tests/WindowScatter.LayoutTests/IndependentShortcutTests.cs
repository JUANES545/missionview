using WindowScatter;
using Xunit;

public class IndependentShortcutTests
{
    private sealed class Registration
    {
        internal readonly Dictionary<int, OverviewHotkey> Active = new();
        internal Func<int, OverviewHotkey, bool> Allow = (_, _) => true;
        internal Func<int, bool> AllowRelease = _ => true;
        internal int Calls;
        internal OverviewHotkeys Create() => new((id, key) =>
        {
            Calls++;
            if (!Allow(id, key) || Active.ContainsKey(id) || Active.Values.Contains(key)) return false;
            Active.Add(id, key);
            return true;
        }, id => AllowRelease(id) && Active.Remove(id));
    }

    [Theory]
    [InlineData("Win+W", null, 0x57, 12)]
    [InlineData("Win+F12", null, 0x7B, 12)]
    [InlineData("Win+F12", "Ctrl+Alt+E", 0x45, 3)]
    [InlineData("Win+Shift+F12", "Alt+E", 0x45, 2)]
    public void PairRetainsLegacyDerivationOrUsesIndependentKey(string normal, string? app, int key, int modifiers)
    {
        var pair = OverviewShortcutPair.Parse(normal, app);
        Assert.Equal(OverviewHotkey.Parse(normal), pair.Normal);
        Assert.Equal(new OverviewHotkey(modifiers, key), pair.AppExpose);
    }

    [Fact]
    public void LegacyShiftDoesNotEnableAnyUnexpectedApplicationShortcut()
    {
        var pair = OverviewShortcutPair.Parse("Win+Shift+F12", null);
        Assert.Null(pair.AppExpose);
        var native = new Registration();
        var bindings = native.Create();
        Assert.True(bindings.TryInitialize("Win+Shift+F12", null, out var warning));
        Assert.NotEmpty(warning);
        Assert.True(bindings.MatchesNormal(0x7B, 12));
        Assert.Null(bindings.AppExpose);
        Assert.Single(native.Active);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("E")]
    [InlineData("Shift+E")]
    [InlineData("Win++E")]
    [InlineData("Ctrl+E+F")]
    [InlineData("Win+F12")]
    [InlineData("Windows+f12")]
    public void InvalidOrCollidingIndependentChordFailsSharedValidation(string app)
    {
        Assert.False(OverviewShortcutPair.TryParse("Win+F12", app, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void IndependentKeyRoutesToAppWhileNormalStillRoutesToOverview()
    {
        var bindings = new Registration().Create();
        Assert.True(bindings.TryInitialize("Win+F12", "Win+E", out var error), error);
        int foreground = 0, pointer = 0;
        var input = new OverviewShortcutInput(new(),
            () => { foreground++; return new(new(1), 2, 3); },
            () => { pointer++; return new(new(4), 5, 6); });
        input.Process(0x5B, true, false, bindings);
        var app = input.Process('E', true, false, bindings);
        Assert.True(app.Activate);
        Assert.True(app.AppExpose);
        Assert.Equal(new IntPtr(4), app.ApplicationWindow.Handle);
        Assert.True(app.Suppress);
        Assert.False(input.Process('E', true, false, bindings).Activate);
        Assert.True(input.Process('E', false, false, bindings).Suppress);
        var normal = input.Process(0x7B, true, false, bindings);
        Assert.True(normal.Activate);
        Assert.False(normal.AppExpose);
        Assert.Equal(2, foreground);
        Assert.Equal(1, pointer);
    }

    [Fact]
    public void ReleasingOtherActionDoesNotRearmHeldKeyAndSuppressionSurvivesModifierRelease()
    {
        var bindings = new Registration().Create();
        Assert.True(bindings.TryInitialize("Win+F12", "Win+E", out _));
        var input = new OverviewShortcutInput(new(), () => new(new(1), 2, 3), () => new(new(4), 5, 6));
        input.Process(0x5B, true, false, bindings);
        Assert.True(input.Process('E', true, false, bindings).Activate);
        input.Process(0x7B, true, false, bindings);
        input.Process(0x7B, false, false, bindings);
        var repeat = input.Process('E', true, false, bindings);
        Assert.False(repeat.Activate);
        Assert.True(repeat.Suppress);
        input.Process(0x5B, false, false, bindings);
        Assert.True(input.Process('E', true, false, bindings).Suppress);
        Assert.True(input.Process('E', false, false, bindings).Suppress);
        Assert.False(input.Process('E', false, false, bindings).Suppress);
        Assert.False(input.Process('E', true, false, bindings).Activate);
    }

    [Fact]
    public void UnmatchedDownCannotTurnIntoActivationByAddingModifiersMidPress()
    {
        var bindings = new Registration().Create();
        Assert.True(bindings.TryInitialize("Win+F12", "Alt+E", out _));
        var input = new OverviewShortcutInput(new(), () => default, () => new(new(4), 5, 6));
        Assert.False(input.Process('E', true, false, bindings).Suppress);
        input.Process(0xA4, true, false, bindings);
        var repeat = input.Process('E', true, false, bindings);
        Assert.False(repeat.Activate);
        Assert.False(repeat.Suppress);
        input.Process('E', false, false, bindings);
        Assert.True(input.Process('E', true, false, bindings).Activate);
    }

    [Fact]
    public void NoPointedWindowIsSilentAndKeyCanRetryAfterReleaseWithoutForegroundSubstitution()
    {
        var bindings = new Registration().Create();
        Assert.True(bindings.TryInitialize("Win+F12", "Win+E", out _));
        var pointed = default(WindowActivationTarget);
        var input = new OverviewShortcutInput(new(), () => new(new(1), 2, 3), () => pointed);
        input.Process(0x5B, true, false, bindings);
        var ignored = input.Process('E', true, false, bindings);
        Assert.True(ignored.Suppress);
        Assert.False(ignored.Activate);
        Assert.Null(ignored.CaptureError);
        input.Process('E', false, false, bindings);
        pointed = new(new(4), 5, 6);
        Assert.True(input.Process('E', true, false, bindings).Activate);
    }

    [Fact]
    public void IndependentActionInsideSessionConfirmsWithoutRecapturingPointer()
    {
        var bindings = new Registration().Create();
        Assert.True(bindings.TryInitialize("Win+F12", "Win+E", out _));
        var interaction = new ScatterInteraction();
        interaction.BeginSession(new(new(1), 2, 3));
        var input = new OverviewShortcutInput(interaction, () => throw new Exception("No recapture"),
            () => throw new Exception("No pointer recapture"));
        input.Process(0x5B, true, false, bindings);
        var confirm = input.Process('E', true, false, bindings);
        Assert.True(confirm.Activate);
        Assert.True(confirm.AppExpose);
        Assert.Equal(default, confirm.ApplicationWindow);
    }

    [Fact]
    public void IndependentChordRecordingBypassesBothActionsAndDoesNotTriggerAfterRecorderCloses()
    {
        var bindings = new Registration().Create();
        Assert.True(bindings.TryInitialize("Win+F12", "Win+E", out _));
        var input = new OverviewShortcutInput(new(), () => throw new Exception("Recording"),
            () => throw new Exception("Recording"));
        input.Process(0x5B, true, true, bindings);
        var raw = input.Process('E', true, true, bindings);
        Assert.Equal(8, raw.Modifiers);
        Assert.False(raw.Suppress);
        Assert.False(raw.Activate);
        input.Process(0x5B, true, false, bindings);
        Assert.False(input.Process('E', true, false, bindings).Activate);
        input.Process('E', false, false, bindings);
    }

    [Fact]
    public void ExplicitReconfigurationAndSwapKeepExactlyTwoReservations()
    {
        var native = new Registration();
        var bindings = native.Create();
        Assert.True(bindings.TryInitialize("Win+F12", "Alt+E", out _));
        Assert.True(bindings.TryApply("Alt+E", "Win+F12", false, out var error), error);
        Assert.True(bindings.MatchesNormal('E', 2));
        Assert.True(bindings.MatchesAppExpose(0x7B, 8));
        Assert.Equal(2, native.Active.Count);
        int calls = native.Calls;
        Assert.True(bindings.TryApply("Alt+E", "win+f12", false, out _));
        Assert.Equal(calls, native.Calls);
        Assert.True(bindings.Release());
        Assert.Empty(native.Active);
    }

    [Fact]
    public void ExplicitPairCollisionAndReservationFailurePreserveBothOldBindings()
    {
        var native = new Registration();
        var bindings = native.Create();
        Assert.True(bindings.TryInitialize("Win+F12", null, out _));
        var before = native.Active.OrderBy(p => p.Key).ToArray();
        Assert.False(bindings.TryApply("Alt+E", "Alt+E", false, out _));
        Assert.Equal(before, native.Active.OrderBy(p => p.Key));
        native.Allow = (_, key) => key != OverviewHotkey.Parse("Alt+E");
        Assert.False(bindings.TryApply("Ctrl+F1", "Alt+E", false, out var error));
        Assert.Contains("unchanged", error);
        Assert.Equal(before, native.Active.OrderBy(p => p.Key));
        Assert.True(bindings.MatchesNormal(0x7B, 8));
        Assert.True(bindings.MatchesAppExpose(0x7B, 12));
    }

    [Fact]
    public void ExplicitStartupConflictReportsNormalRecallButNeverMakesPreferenceApplySucceed()
    {
        var native = new Registration { Allow = (id, _) => id != OverviewHotkeys.AppExposeId };
        var bindings = native.Create();
        Assert.True(bindings.TryInitialize("Win+F12", "Alt+E", out var warning));
        Assert.NotEmpty(warning);
        Assert.True(bindings.MatchesNormal(0x7B, 8));
        Assert.Null(bindings.AppExpose);
        Assert.False(bindings.TryApply("Win+F12", "Alt+E", false, out var error));
        Assert.NotEmpty(error);
        Assert.True(bindings.MatchesNormal(0x7B, 8));
        Assert.Null(bindings.AppExpose);
    }

    [Fact]
    public void LegacyNormalOnlyExceptionDoesNotHideExplicitAssignmentFailure()
    {
        var native = new Registration { Allow = (_, _) => false };
        var bindings = native.Create();
        Assert.True(bindings.TryInitialize("Win+W", null, out _));
        Assert.False(bindings.TryApply("Win+W", "Alt+E", false, out _));
        Assert.True(bindings.MatchesNormal('W', 8));
        Assert.Null(bindings.AppExpose);
    }

    [Fact]
    public void RollbackFailureOfIndependentPairIsReportedDisablesDispatchAndCanBeRetried()
    {
        var native = new Registration();
        var bindings = native.Create();
        Assert.True(bindings.TryInitialize("Win+F12", "Alt+E", out _));
        native.Allow = (_, key) => key != OverviewHotkey.Parse("Win+X") && key != OverviewHotkey.Parse("Alt+E");
        Assert.False(bindings.TryApply("Ctrl+F1", "Win+X", false, out var error));
        Assert.Contains("rollback", error);
        Assert.True(bindings.IsFaulted);
        Assert.Null(bindings.Normal);
        Assert.Null(bindings.AppExpose);
        native.Allow = (_, _) => true;
        Assert.True(bindings.TryApply("Win+F12", "Alt+E", false, out _));
        Assert.Equal(2, native.Active.Count);
        Assert.True(bindings.Release());
        Assert.Empty(native.Active);
    }

    [Fact]
    public void UnregisterFailureOfOneActionLeavesBothOldBindingsAndReportsFailure()
    {
        var native = new Registration();
        var bindings = native.Create();
        Assert.True(bindings.TryInitialize("Win+F12", "Alt+E", out _));
        var before = native.Active.OrderBy(p => p.Key).ToArray();
        native.AllowRelease = id => id != OverviewHotkeys.AppExposeId;
        Assert.False(bindings.TryApply("Ctrl+F1", "Win+X", false, out var error));
        Assert.Contains("release", error);
        Assert.Equal(before, native.Active.OrderBy(p => p.Key).ToArray());
        Assert.True(bindings.MatchesNormal(0x7B, 8));
        Assert.True(bindings.MatchesAppExpose('E', 2));
    }

    [Fact]
    public void ResetOrReconfigurationWhileKeyIsHeldDoesNotLeakItsKeyUpOrRearmItsRepeat()
    {
        var bindings = new Registration().Create();
        Assert.True(bindings.TryInitialize("Win+F12", "Win+E", out _));
        var input = new OverviewShortcutInput(new(), () => new(new(1), 2, 3), () => new(new(4), 5, 6));
        input.Process(0x5B, true, false, bindings);
        Assert.True(input.Process('E', true, false, bindings).Activate);
        Assert.True(bindings.TryApply("Ctrl+F1", "Alt+X", false, out _));
        input.Reset();
        var repeat = input.Process('E', true, false, bindings);
        Assert.False(repeat.Activate);
        Assert.True(repeat.Suppress);
        Assert.True(input.Process('E', false, false, bindings).Suppress);
        Assert.False(input.Process('E', true, false, bindings).Suppress);
    }

    [Fact]
    public void PointerIdentityErrorUsesExistingErrorChannelWithNoForegroundFallback()
    {
        var bindings = new Registration().Create();
        Assert.True(bindings.TryInitialize("Win+F12", "Win+E", out _));
        var input = new OverviewShortcutInput(new(), () => new(new(1), 2, 3),
            () => throw new UnauthorizedAccessException("Application identity is unavailable."));
        input.Process(0x5B, true, false, bindings);
        var denied = input.Process('E', true, false, bindings);
        Assert.True(denied.Activate);
        Assert.True(denied.AppExpose);
        Assert.True(denied.Suppress);
        Assert.Equal(default, denied.ApplicationWindow);
        Assert.Equal("Application identity is unavailable.", denied.CaptureError);
    }
}
