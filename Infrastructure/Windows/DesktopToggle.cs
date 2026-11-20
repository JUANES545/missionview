using System.Runtime.InteropServices;

namespace WindowScatter
{
    internal sealed class DesktopToggle
    {
        public bool RestorePending { get; private set; }

        public bool CompleteOverviewExit(Action prepare, Action hide, Action toggle, Action queueCleanup)
        {
            try
            {
                prepare();
                hide();
                return TryToggle(toggle);
            }
            finally
            {
                queueCleanup();
            }
        }

        public bool TryToggle(Action toggle)
        {
            try
            {
                toggle();
                RestorePending = !RestorePending;
                return true;
            }
            catch (COMException ex)
            {
                System.Diagnostics.Trace.TraceError($"Shell ToggleDesktop failed: {ex}");
                return false;
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public static void ToggleShell()
        {
            Type shellType = Type.GetTypeFromCLSID(new Guid("13709620-C279-11CE-A49E-444553540000"), throwOnError: true)!;
            object shell = Activator.CreateInstance(shellType)
                ?? throw new COMException("Shell.Application could not be created.");
            try
            {
                ((dynamic)shell).ToggleDesktop();
            }
            finally
            {
                Marshal.ReleaseComObject(shell);
            }
        }
    }
}
