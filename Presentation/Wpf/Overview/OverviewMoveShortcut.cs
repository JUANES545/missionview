using System.Windows.Input;

namespace WindowScatter
{
    internal static class OverviewMoveShortcut
    {
        internal static bool Matches(Key key, Key systemKey, ModifierKeys modifiers)
        {
            if (key == Key.System) key = systemKey;
            return key == Key.Apps ||
                (key == Key.F10 && (modifiers & ModifierKeys.Shift) != 0) ||
                (key == Key.M && modifiers == ModifierKeys.Alt);
        }
    }
}
