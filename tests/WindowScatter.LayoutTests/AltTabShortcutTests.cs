using WindowScatter;
using Xunit;
using static WindowScatter.Win32Interop;

public sealed class AltTabShortcutTests
{
    private static readonly WindowActivationTarget Origin = new(new IntPtr(7), 11, 12);

    [Theory]
    [InlineData(VK_LMENU, 0, true)]
    [InlineData(VK_LMENU, VK_RMENU, false)]
    [InlineData(VK_RMENU, VK_CONTROL, false)]
    [InlineData(VK_LMENU, VK_CONTROL, false)]
    [InlineData(VK_LMENU, VK_SHIFT, false)]
    [InlineData(VK_LMENU, VK_LWIN, false)]
    [InlineData(0, 0, false)]
    public void OnlyUnmodifiedLeftAltQualifies(int first, int second, bool expected) =>
        Assert.Equal(expected, OverviewShortcutInput.IsExactLeftAlt(key => key == first || key == second));

    [Fact]
    public void AltReleaseLeavesOverviewOpenAndOnlyADistinctSecondTapQueuesConfirmation()
    {
        var session = new OverviewSession();
        var input = new OverviewShortcutInput(session.Interaction, () => Origin, () => default);
        var hotkeys = new OverviewHotkeys((_, _) => throw new InvalidOperationException("Alt+Tab must not register natively."),
            _ => true, () => true);
        Assert.True(hotkeys.TryInitialize("Alt+Tab", out var error), error);
        OverviewShortcutEvent Key(int key, bool down = true, bool injected = false) =>
            input.Process(key, down, false, hotkeys, injected, canUseAltTab: () => true);

        Assert.False(Key(VK_LMENU).Suppress);
        var first = Key(VK_TAB);
        Assert.True(first is { Suppress: true, Activate: true, AppExpose: false, AltTab: true, MaskMenus: true });
        Assert.Equal(Origin, first.Foreground);
        session.Interaction.BeginSession(first.Foreground);
        session.Select(new IntPtr(8));
        Assert.False(Key(VK_TAB, false, injected: true).Suppress);
        Assert.True(Key(VK_TAB) is { Suppress: true, Activate: false });
        Assert.True(Key(VK_TAB, false) is { Suppress: true, Activate: false });
        Assert.True(Key(VK_LMENU, false) is { Suppress: false, Activate: false });
        Assert.True(session.Interaction.IsOpen);
        Assert.Equal(OverviewCloseRequest.None, session.TakeCloseRequest(busy: false, visible: true));

        Assert.False(Key(VK_LMENU).Suppress);
        var second = Key(VK_TAB);
        Assert.True(second is { Suppress: true, Activate: true, AppExpose: false, AltTab: true });
        Assert.Equal(default, second.Foreground);
        session.Interaction.RequestConfirmation();
        Assert.Equal(OverviewCloseRequest.None, session.TakeCloseRequest(busy: true, visible: true));
        Assert.Equal(OverviewCloseRequest.ConfirmSelection, session.TakeCloseRequest(busy: false, visible: true));
    }

    [Fact]
    public void EscapePassesToTheExistingDismissPathAndSupersedesAQueuedConfirmation()
    {
        var session = new OverviewSession();
        var input = new OverviewShortcutInput(session.Interaction, () => Origin, () => default);
        session.Interaction.BeginSession(Origin);
        session.Interaction.RequestConfirmation();
        var escape = input.Process(VK_ESCAPE, true, false, null, canUseAltTab: () => true);
        Assert.False(escape.Suppress);
        Assert.False(escape.Activate);
        session.Interaction.RequestDismiss();
        Assert.Equal(OverviewCloseRequest.Cancel, session.TakeCloseRequest(busy: false, visible: true));
    }

    [Fact]
    public void UnavailableOrDisabledAltTabPassesThroughWithoutChangingFunctionKeys()
    {
        var native = new OverviewHotkeys((_, _) => true, _ => true, () => true);
        Assert.True(native.TryInitialize("Win+F24", out var error), error);
        var input = new OverviewShortcutInput(new ScatterInteraction(), () => Origin, () => default);
        Assert.False(input.Process(VK_TAB, true, false, native, canUseAltTab: () => true).Suppress);
        Assert.True(native.TryApply("Alt+Tab", false, out error), error);
        Assert.False(input.Process(VK_TAB, true, false, native, canUseAltTab: () => true).Suppress);
        Assert.False(input.Process(VK_TAB, false, false, native).Suppress);
        Assert.False(input.Process(VK_LMENU, true, false, native).Suppress);
        Assert.False(input.Process(VK_TAB, true, false, native, canUseAltTab: () => false).Suppress);
        Assert.False(input.Process(VK_TAB, false, false, native).Suppress);
        Assert.False(input.Process(VK_LSHIFT, true, false, native).Suppress);
        Assert.False(input.Process(VK_TAB, true, false, native, canUseAltTab: () => false).Suppress);
        Assert.False(input.Process(VK_TAB, false, false, native).Suppress);
        input.Process(VK_LSHIFT, false, false, native);
        input.Process(VK_LMENU, false, false, native);
        Assert.True(native.TryApply("Win+F24", false, out error), error);
        Assert.False(input.Process(VK_LWIN, true, false, native).Suppress);
        Assert.True(input.Process(0x87, true, false, native, canUseAltTab: () => true)
            is { Suppress: true, Activate: true, AltTab: false });
    }

    [Fact]
    public void HookOnlyAltTabPreservesIndependentBindingAndRollsBackOnFailure()
    {
        var registered = new Dictionary<int, OverviewHotkey>();
        bool hookAvailable = false;
        var hotkeys = new OverviewHotkeys((id, key) =>
        {
            Assert.False(key.IsAltTab);
            return registered.TryAdd(id, key);
        }, registered.Remove, () => hookAvailable);
        Assert.True(hotkeys.TryInitialize("Win+W", "Ctrl+F24", out var error), error);
        Assert.False(hotkeys.TryApply("Alt+Tab", "Ctrl+F24", false, out error));
        Assert.Contains("keyboard handler is unavailable", error);
        Assert.Equal(2, registered.Count);
        Assert.True(hotkeys.MatchesNormal('W', 8));

        hookAvailable = true;
        Assert.True(hotkeys.TryApply("Alt+Tab", "Ctrl+F24", false, out error), error);
        Assert.Single(registered);
        Assert.True(hotkeys.MatchesAppExpose(0x87, 1));
        Assert.False(hotkeys.TryApply("Alt+Tab", "Alt+Tab", false, out error));
        Assert.True(hotkeys.MatchesAppExpose(0x87, 1));
        Assert.True(hotkeys.TryApply("Alt+Tab", null, false, out error), error);
        Assert.Empty(registered);
        Assert.Null(hotkeys.AppExpose);
        Assert.True(hotkeys.TryApply("Win+W", null, false, out error), error);
        Assert.True(hotkeys.MatchesAppExpose('W', 12));
        Assert.True(hotkeys.Release());
        Assert.Null(hotkeys.Normal);
        Assert.Empty(registered);
    }
}
