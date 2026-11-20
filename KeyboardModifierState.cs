using static WindowScatter.Win32Interop;

namespace WindowScatter
{
    internal sealed class KeyboardModifierState
    {
        private readonly HashSet<int> pressedKeys = new();

        public void Update(int virtualKey, bool isPressed)
        {
            if (virtualKey != VK_LWIN && virtualKey != VK_RWIN &&
                virtualKey != VK_CONTROL && virtualKey != VK_LCONTROL && virtualKey != VK_RCONTROL &&
                virtualKey != VK_MENU && virtualKey != VK_LMENU && virtualKey != VK_RMENU &&
                virtualKey != VK_SHIFT && virtualKey != VK_LSHIFT && virtualKey != VK_RSHIFT)
                return;

            if (isPressed)
                pressedKeys.Add(virtualKey);
            else
                pressedKeys.Remove(virtualKey);
        }

        public bool Matches(int modifiers)
        {
            int current = 0;
            if (pressedKeys.Contains(VK_LWIN) || pressedKeys.Contains(VK_RWIN)) current |= 0x08;
            if (pressedKeys.Contains(VK_CONTROL) || pressedKeys.Contains(VK_LCONTROL) || pressedKeys.Contains(VK_RCONTROL)) current |= 0x01;
            if (pressedKeys.Contains(VK_MENU) || pressedKeys.Contains(VK_LMENU) || pressedKeys.Contains(VK_RMENU)) current |= 0x02;
            if (pressedKeys.Contains(VK_SHIFT) || pressedKeys.Contains(VK_LSHIFT) || pressedKeys.Contains(VK_RSHIFT)) current |= 0x04;
            return current == modifiers;
        }
    }
}
