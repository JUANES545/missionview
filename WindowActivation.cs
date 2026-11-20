using System.Diagnostics;
using static WindowScatter.Win32Interop;

namespace WindowScatter
{
    internal readonly record struct WindowActivationTarget(IntPtr Handle, uint ThreadId, uint ProcessId);

    internal sealed class WindowActivation
    {
        private readonly Func<IntPtr, (uint Thread, uint Process)> identify;
        private readonly Action<IntPtr, int> show;
        private readonly Func<IntPtr, bool> activate;
        private readonly Func<IntPtr> foreground;

        public WindowActivation() : this(IdentifyWindow,
            (handle, command) => ShowWindow(handle, command), SetForegroundWindow, GetForegroundWindow) { }

        internal WindowActivation(Func<IntPtr, (uint Thread, uint Process)> identify,
            Action<IntPtr, int> show, Func<IntPtr, bool> activate, Func<IntPtr> foreground)
        {
            this.identify = identify;
            this.show = show;
            this.activate = activate;
            this.foreground = foreground;
        }

        public WindowActivationTarget Capture(IntPtr handle)
        {
            var identity = handle == IntPtr.Zero ? default : identify(handle);
            return new WindowActivationTarget(handle, identity.Thread, identity.Process);
        }

        public bool TryActivate(WindowActivationTarget target, bool restore, bool wasMaximized, bool ownedForeground)
        {
            if (target.Handle == IntPtr.Zero || target.ThreadId == 0 || target.ProcessId == 0 ||
                identify(target.Handle) != (target.ThreadId, target.ProcessId))
            {
                Trace.TraceWarning("Scatter target no longer identifies a valid window.");
                return false;
            }

            if (foreground() == target.Handle) return true;
            if (!ownedForeground)
            {
                Trace.TraceWarning("Scatter did not reactivate its target because another window took focus.");
                return false;
            }

            if (restore)
                show(target.Handle, wasMaximized ? SW_SHOWMAXIMIZED : SW_RESTORE);

            if (identify(target.Handle) != (target.ThreadId, target.ProcessId))
            {
                Trace.TraceWarning("Scatter target changed while restoring it.");
                return false;
            }

            bool accepted = activate(target.Handle);
            bool active = foreground() == target.Handle;
            if (!active)
                Trace.TraceWarning($"Windows did not foreground the scatter target (SetForegroundWindow returned {accepted}).");
            return active;
        }

        public bool CloseOverviewAndActivate(WindowActivationTarget target, bool restore, bool wasMaximized,
            IntPtr host, IntPtr chrome, Action hide)
            => CloseOverviewAndActivate(target, restore, wasMaximized, new HashSet<IntPtr> { host, chrome }, hide);

        public bool CloseOverviewAndActivate(WindowActivationTarget target, bool restore, bool wasMaximized,
            IReadOnlySet<IntPtr> surfaces, Action hide)
        {
            IntPtr active = foreground();
            bool ownedForeground = active != IntPtr.Zero && surfaces.Contains(active);
            hide();
            return TryActivate(target, restore, wasMaximized, ownedForeground);
        }

        private static (uint Thread, uint Process) IdentifyWindow(IntPtr handle)
        {
            if (!IsWindow(handle)) return default;
            uint thread = GetWindowThreadProcessId(handle, out uint process);
            return (thread, process);
        }
    }
}
