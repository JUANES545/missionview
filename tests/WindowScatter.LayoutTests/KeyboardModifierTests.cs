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
