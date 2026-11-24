using System.Diagnostics;
using static WindowScatter.Win32Interop;

namespace WindowScatter
{
    internal sealed class WindowActivation
    {
        private readonly Func<IntPtr, (uint Thread, uint Process)> identify;
        private readonly Action<IntPtr, int> show;
        private readonly Func<IntPtr, bool> activate;
        private readonly Func<IntPtr> foreground;
        private readonly Func<IntPtr, bool> isMinimized;

        public WindowActivation() : this(IdentifyWindow,
            (handle, command) => ShowWindow(handle, command), SetForegroundWindow, GetForegroundWindow, IsIconic) { }

        internal WindowActivation(Func<IntPtr, (uint Thread, uint Process)> identify,
            Action<IntPtr, int> show, Func<IntPtr, bool> activate, Func<IntPtr> foreground,
            Func<IntPtr, bool>? isMinimized = null)
        {
            this.identify = identify;
            this.show = show;
            this.activate = activate;
            this.foreground = foreground;
            this.isMinimized = isMinimized ?? (_ => true);
        }

        public WindowActivationTarget Capture(IntPtr handle)
        {
            var identity = handle == IntPtr.Zero ? default : identify(handle);
            return new WindowActivationTarget(handle, identity.Thread, identity.Process);
        }

        public WindowActivationTarget CaptureUnderPointer()
        {
            if (!GetCursorPos(out var point))
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                    "Windows could not read the pointer position.");
            return CapturePointerWindow(WindowFromPoint(point));
        }

        internal WindowActivationTarget CapturePointerWindow(IntPtr hit)
        {
            if (hit == IntPtr.Zero) return default;
            // Keep an owned dialog as the target; its owner's identity is resolved separately.
            IntPtr root = GetAncestor(hit, GA_ROOT);
            var target = Capture(root);
            if (target.Handle == IntPtr.Zero || target.ThreadId == 0 || target.ProcessId == 0)
                throw new InvalidOperationException("Point to an application window before opening App Expose.");
            if (Capture(root) != target || GetAncestor(hit, GA_ROOT) != root)
                throw new InvalidOperationException("The window under the pointer changed before it could be captured.");
            return ApplicationIdentityReader.IsEligiblePointedWindow(target) ? target : default;
        }

        public bool TryActivate(WindowActivationTarget target, bool restore, bool wasMaximized, bool ownedForeground,
            Func<bool>? stillValid = null)
        {
            if (target.Handle == IntPtr.Zero || target.ThreadId == 0 || target.ProcessId == 0 ||
                identify(target.Handle) != (target.ThreadId, target.ProcessId) || stillValid?.Invoke() == false)
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

            if (restore && isMinimized(target.Handle))
                show(target.Handle, wasMaximized ? SW_SHOWMAXIMIZED : SW_RESTORE);

            if (identify(target.Handle) != (target.ThreadId, target.ProcessId) || stillValid?.Invoke() == false)
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
            IReadOnlySet<IntPtr> surfaces, Action hide, Func<bool>? stillValid = null)
        {
            IntPtr active = foreground();
            bool ownedForeground = active != IntPtr.Zero && surfaces.Contains(active);
            hide();
            return TryActivate(target, restore, wasMaximized, ownedForeground, stillValid);
        }

        private static (uint Thread, uint Process) IdentifyWindow(IntPtr handle)
        {
            if (!IsWindow(handle)) return default;
            uint thread = GetWindowThreadProcessId(handle, out uint process);
            return (thread, process);
        }
    }
}
