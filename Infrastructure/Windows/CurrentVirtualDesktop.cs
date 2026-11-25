using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindowScatter;

internal static class CurrentVirtualDesktop
{
    public static bool Contains(IntPtr window)
    {
        try
        {
            ((IVirtualDesktopManager)new VirtualDesktopManager()).IsWindowOnCurrentVirtualDesktop(window, out bool current);
            return current;
        }
        catch (COMException error)
        {
            Trace.TraceWarning($"Could not verify the current virtual desktop: 0x{error.HResult:X8}.");
            return false;
        }
    }

    [ComImport, Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")]
    private class VirtualDesktopManager { }

    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        void IsWindowOnCurrentVirtualDesktop(IntPtr window, [MarshalAs(UnmanagedType.Bool)] out bool current);
    }
}
