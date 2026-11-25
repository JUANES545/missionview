using WindowScatter;
using Xunit;
using static WindowScatter.Win32Interop;

public class KeyboardModifierTests
{
    [Theory]
    [InlineData(0xA0, 0xA1, 0x04)]
    [InlineData(0xA2, 0xA3, 0x01)]
    [InlineData(0xA4, 0xA5, 0x02)]
    [InlineData(VK_LWIN, VK_RWIN, 0x08)]
    public void EitherSideAndPartialReleaseKeepModifierState(int left, int right, int modifier)
    {
        var state = new KeyboardModifierState();
        foreach (var (first, second) in new[] { (left, right), (right, left) })
        {
            state.Update(first, true);
            Assert.True(state.Matches(modifier));
            state.Update(second, true);
            state.Update(first, false);
            Assert.True(state.Matches(modifier));
            state.Update(second, false);
            Assert.True(state.Matches(0));
        }
    }

    [Theory]
    [InlineData(0xA0)]
    [InlineData(0xA1)]
    [InlineData(0xA2)]
    [InlineData(0xA3)]
    [InlineData(0xA4)]
    [InlineData(0xA5)]
    public void ExtraModifierPreventsWinHotkeyMatch(int extraKey)
    {
        var state = new KeyboardModifierState();
        state.Update(VK_LWIN, true);
        Assert.True(state.Matches(0x08));
        state.Update(extraKey, true);
        Assert.False(state.Matches(0x08));
        state.Update(extraKey, false);
        Assert.True(state.Matches(0x08));
    }

    [Fact]
    public void MenuMaskDoesNotReleaseHeldModifiers()
    {
        var state = new KeyboardModifierState();
        foreach (int key in new[] { VK_LWIN, VK_LCONTROL, VK_LMENU, VK_LSHIFT })
            state.Update(key, true);
        Assert.True(KeyboardMenuMask.TrySend((count, inputs, size) =>
        {
            Assert.Equal(2u, count);
            Assert.Equal(IntPtr.Size == 8 ? 40 : 28, size);
            Assert.All(inputs, input =>
            {
                Assert.Equal(1u, input.type);
                Assert.Equal(0xE8, input.data.keyboard.virtualKey);
                state.Update(input.data.keyboard.virtualKey, (input.data.keyboard.flags & KEYEVENTF_KEYUP) == 0);
            });
            Assert.Equal(0u, inputs[0].data.keyboard.flags);
            Assert.Equal(KEYEVENTF_KEYUP, inputs[1].data.keyboard.flags);
            return count;
        }));
        Assert.True(state.Matches(0x0F));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void MaskFailureIsReportedAndPartialSendReleasesOnlyTheMask(uint sent)
    {
        int calls = 0;
        Assert.False(KeyboardMenuMask.TrySend((count, inputs, size) =>
        {
            calls++;
            if (calls == 1) return sent;
            Assert.Equal(1u, count);
            var input = Assert.Single(inputs);
            Assert.Equal(0xE8, input.data.keyboard.virtualKey);
            Assert.Equal(KEYEVENTF_KEYUP, input.data.keyboard.flags);
            return 0;
        }));
        Assert.Equal(sent == 1 ? 2 : 1, calls);
    }

    public static IEnumerable<object[]> WinOverviewShortcuts =>
        Enumerable.Range('A', 26).Select(key => new object[] { $"Win+{(char)key}", key })
            .Concat(Enumerable.Range(1, 24).Select(number => new object[] { $"Win+F{number}", 0x70 + number - 1 }))
            .Concat(new object[][] { ["Win+1", (int)'1'],
                ["Win+Ctrl+Left", 0x25], ["Win+Alt+PageDown", 0x22], ["Win+Shift+Space", 0x20] });

    [Theory]
    [MemberData(nameof(WinOverviewShortcuts))]
    public void FileDragMasksOnlyTheRecognizedWinOverviewPress(string chord, int key)
    {
        var hotkeys = new OverviewHotkeys((_, _) => false, _ => true, () => true);
        if (chord == "Win+L")
        {
            Assert.False(hotkeys.TryInitialize(chord, out var reserved));
            Assert.Contains("system security", reserved);
            Assert.Null(hotkeys.Normal);
        }
        else Assert.True(hotkeys.TryInitialize(chord, out var error), error);

        var interaction = new ScatterInteraction();
        var source = new OverviewShortcutInput(interaction, () => default, () => default);
        Assert.False(KeyboardMenuMask.IsDragShortcut(source.Process(VK_LWIN, true, false, hotkeys), hotkeys.Normal));

        var binding = hotkeys.Normal;
        foreach (var (modifier, virtualKey) in new[] { (1, VK_LCONTROL), (2, VK_LMENU), (4, VK_LSHIFT) })
            if ((binding?.Modifiers & modifier) == modifier) source.Process(virtualKey, true, false, hotkeys);

        int otherKey = key == 0x70 ? 0x71 : 0x70;
        var unmatched = source.Process(otherKey, true, false, hotkeys);
        Assert.False(unmatched.Suppress);
        Assert.False(KeyboardMenuMask.IsDragShortcut(unmatched, binding));
        Assert.False(KeyboardMenuMask.IsDragShortcut(source.Process(key, true, false, hotkeys, injected: true), binding));
        var open = source.Process(key, true, false, hotkeys);
        Assert.Equal(binding.HasValue, KeyboardMenuMask.IsDragShortcut(open, binding));
        Assert.False(KeyboardMenuMask.IsDragShortcut(source.Process(key, true, false, hotkeys), binding));
        Assert.False(KeyboardMenuMask.IsDragShortcut(source.Process(key, false, false, hotkeys), binding));
        interaction.BeginSession(default);
        var confirm = source.Process(key, true, false, hotkeys);
        Assert.Equal(binding.HasValue, KeyboardMenuMask.IsDragShortcut(confirm, binding));
        Assert.False(KeyboardMenuMask.IsDragShortcut(confirm with { AppExpose = true }, binding));
        Assert.False(KeyboardMenuMask.IsDragShortcut(confirm with { Modifiers = confirm.Modifiers ^ 1 }, binding));
    }

    [Fact]
    public void FileDragDoesNotMaskUnrecognizedOrNonWinShortcuts()
    {
        var hotkeys = new OverviewHotkeys((_, _) => false, _ => true);
        Assert.False(hotkeys.TryInitialize("Win+A", out _));
        var source = new OverviewShortcutInput(new ScatterInteraction(), () => default, () => default);
        source.Process(VK_LWIN, true, false, hotkeys);
        Assert.False(KeyboardMenuMask.IsDragShortcut(source.Process('A', true, false, hotkeys), hotkeys.Normal));
        hotkeys = new OverviewHotkeys((_, _) => true, _ => true);
        Assert.True(hotkeys.TryInitialize("Ctrl+F11", out _));
        source.Process(VK_LWIN, false, false, hotkeys);
        source.Process(VK_LCONTROL, true, false, hotkeys);
        var control = source.Process(0x7A, true, false, hotkeys);
        Assert.True(control.Activate);
        Assert.False(KeyboardMenuMask.IsDragShortcut(control, hotkeys.Normal));
    }

    [Fact]
    public void RepeatedAndNonModifierKeysDoNotChangeModifiers()
    {
        var state = new KeyboardModifierState();
        state.Update(VK_LWIN, true);
        state.Update(VK_LWIN, true);
        state.Update(0x7B, true);
        Assert.True(state.Matches(0x08));
        state.Update(VK_LWIN, false);
        Assert.True(state.Matches(0));
    }
}
