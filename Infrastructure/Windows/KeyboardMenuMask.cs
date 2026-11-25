using System.Runtime.InteropServices;
using static WindowScatter.Win32Interop;

namespace WindowScatter
{
    internal static class KeyboardMenuMask
    {
        internal delegate uint SendInputEvents(uint count, INPUT[] inputs, int size);
        internal static bool IsDragShortcut(OverviewShortcutEvent input, OverviewHotkey? normal) =>
            input.Suppress && input.Activate && !input.AppExpose && input.MaskMenus &&
            (input.Modifiers & OverviewHotkey.Win) != 0 && normal?.Modifiers == input.Modifiers;

        public static bool TrySend(SendInputEvents send)
        {
            // An unassigned key masks Win/Alt menus without releasing held modifiers.
            // https://www.autohotkey.com/docs/v2/lib/A_MenuMaskKey.htm
            const ushort maskKey = 0xE8;
            var inputs = new[]
            {
                new INPUT { type = 1, data = new INPUTUNION { keyboard = new KEYBDINPUT { virtualKey = maskKey } } },
                new INPUT { type = 1, data = new INPUTUNION { keyboard = new KEYBDINPUT { virtualKey = maskKey, flags = KEYEVENTF_KEYUP } } }
            };
            int size = Marshal.SizeOf<INPUT>();
            uint sent = send((uint)inputs.Length, inputs, size);
            if (sent == inputs.Length) return true;

            System.Diagnostics.Trace.TraceError($"Menu mask SendInput inserted {sent}/2 events; error {Marshal.GetLastWin32Error()}.");
            if (sent == 1 && send(1, new[] { inputs[1] }, size) != 1)
                System.Diagnostics.Trace.TraceError($"Menu mask key release failed: {Marshal.GetLastWin32Error()}.");
            return false;
        }
    }
}
