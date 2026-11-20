namespace WindowScatter
{
    internal static class WindowSurfacePolicy
    {
        public static bool IsEligible(uint extendedStyle)
            => (extendedStyle & Win32Interop.WS_EX_APPWINDOW) != 0 ||
               (extendedStyle & Win32Interop.WS_EX_TOOLWINDOW) == 0;
    }
}
