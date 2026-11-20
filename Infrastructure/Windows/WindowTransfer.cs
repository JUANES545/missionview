using System.ComponentModel;
using System.Runtime.InteropServices;
using static WindowScatter.Win32Interop;
using static WindowScatter.MonitorHelper;

namespace WindowScatter
{
    internal readonly record struct WindowMoveSnapshot(WindowActivationTarget Identity, RECT Outer,
        RECT Frame, WINDOWPLACEMENT Placement, uint Style, bool Available);

    internal sealed class WindowTransfer
    {
        private readonly Func<IntPtr, WindowMoveSnapshot> read;
        private readonly Action<IntPtr, RECT> position;
        private readonly Action<IntPtr, WINDOWPLACEMENT> placement;

        public WindowTransfer() : this(ReadNative, PositionNative, PlaceNative) { }
        internal WindowTransfer(Func<IntPtr, WindowMoveSnapshot> read, Action<IntPtr, RECT> position,
            Action<IntPtr, WINDOWPLACEMENT> placement)
        { this.read = read; this.position = position; this.placement = placement; }

        public void Move(WindowActivationTarget target, MonitorBounds source, MonitorBounds destination,
            IReadOnlyList<MonitorBounds> topology, CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            if (source.Handle == destination.Handle) return;
            if (!topology.Any(m => m.Handle == source.Handle) || !topology.Any(m => m.Handle == destination.Handle))
                throw new InvalidOperationException("The display is no longer connected.");
            var before = read(target.Handle);
            ValidateIdentity(target, before);
            if (FindWindowMonitor(before.Outer, topology)?.Handle != source.Handle)
                throw new InvalidOperationException("The window changed displays before the move.");
            bool maximized = before.Placement.showCmd == SW_SHOWMAXIMIZED;
            RECT desired;
            if (maximized)
            {
                var p = before.Placement;
                var normal = FromWorkspace(p.rcNormalPosition, source, before.Style);
                p.rcNormalPosition = ToWorkspace(Map(normal, source.WorkArea, destination.WorkArea),
                    destination, before.Style);
                p.ptMaxPosition = new System.Drawing.Point(
                    destination.WorkArea.Left - WorkspaceOffsetX(destination, before.Style),
                    destination.WorkArea.Top - WorkspaceOffsetY(destination, before.Style));
                // SW_SHOWNA preserves the existing state without activating/restoring.
                p.showCmd = 8;
                p.flags = 0;
                cancellation.ThrowIfCancellationRequested();
                placement(target.Handle, p);
                cancellation.ThrowIfCancellationRequested();
                var placed = read(target.Handle);
                ValidateIdentity(target, placed);
                if (placed.Placement.showCmd != SW_SHOWMAXIMIZED)
                    throw new InvalidOperationException("Windows did not preserve the maximized window state.");
                desired = destination.WorkArea;
            }
            else desired = Map(before.Outer, source.WorkArea, destination.WorkArea);
            cancellation.ThrowIfCancellationRequested();
            position(target.Handle, desired);
            cancellation.ThrowIfCancellationRequested();
            var after = read(target.Handle);
            ValidateIdentity(target, after);
            if (FindWindowMonitor(after.Frame, topology)?.Handle != destination.Handle ||
                (after.Placement.showCmd == SW_SHOWMAXIMIZED) != maximized ||
                !Contains(destination.WorkArea, after.Frame))
                throw new InvalidOperationException("Windows did not confirm the requested display, bounds and window state.");
            if (maximized && !Contains(destination.WorkArea, FromWorkspace(after.Placement.rcNormalPosition, destination, after.Style)))
                throw new InvalidOperationException("Windows did not preserve a usable restored position on the destination display.");
        }

        private static void ValidateIdentity(WindowActivationTarget target, WindowMoveSnapshot snapshot)
        {
            if (target.ThreadId == 0 || target.ProcessId == 0 || snapshot.Identity != target || !snapshot.Available)
                throw new InvalidOperationException("The window is no longer available for moving.");
        }

        internal static RECT Map(RECT frame, RECT from, RECT to)
        {
            int width = Math.Min(frame.Right - frame.Left, to.Right - to.Left);
            int height = Math.Min(frame.Bottom - frame.Top, to.Bottom - to.Top);
            if (width <= 0 || height <= 0 || from.Right <= from.Left || from.Bottom <= from.Top)
                throw new InvalidOperationException("The window or display has no usable bounds.");
            double cx = ((double)frame.Left + frame.Right - 2d * from.Left) / (2d * (from.Right - from.Left));
            double cy = ((double)frame.Top + frame.Bottom - 2d * from.Top) / (2d * (from.Bottom - from.Top));
            int left = (int)Math.Clamp(Math.Round(to.Left + cx * (to.Right - to.Left) - width / 2d),
                to.Left, to.Right - width);
            int top = (int)Math.Clamp(Math.Round(to.Top + cy * (to.Bottom - to.Top) - height / 2d),
                to.Top, to.Bottom - height);
            return new RECT { Left = left, Top = top, Right = left + width, Bottom = top + height };
        }

        private static int WorkspaceOffsetX(MonitorBounds m, uint style) =>
            (style & WS_EX_TOOLWINDOW) == 0 ? m.WorkArea.Left - m.Left : 0;
        private static int WorkspaceOffsetY(MonitorBounds m, uint style) =>
            (style & WS_EX_TOOLWINDOW) == 0 ? m.WorkArea.Top - m.Top : 0;
        internal static RECT FromWorkspace(RECT r, MonitorBounds m, uint style) =>
            Offset(r, WorkspaceOffsetX(m, style), WorkspaceOffsetY(m, style));
        internal static RECT ToWorkspace(RECT r, MonitorBounds m, uint style) =>
            Offset(r, -WorkspaceOffsetX(m, style), -WorkspaceOffsetY(m, style));
        private static RECT Offset(RECT r, int x, int y) => new()
        { Left = r.Left + x, Right = r.Right + x, Top = r.Top + y, Bottom = r.Bottom + y };
        private static bool Contains(RECT area, RECT r) => r.Right > r.Left && r.Bottom > r.Top &&
            r.Left >= area.Left - 1 && r.Top >= area.Top - 1 && r.Right <= area.Right + 1 && r.Bottom <= area.Bottom + 1;

        private static WindowMoveSnapshot ReadNative(IntPtr handle)
        {
            uint thread = GetWindowThreadProcessId(handle, out uint process);
            var p = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (thread == 0 || process == 0 || !GetWindowPlacement(handle, ref p) || !GetWindowRect(handle, out var r))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the window before/after moving.");
            uint style = unchecked((uint)GetWindowLong(handle, GWL_EXSTYLE));
            if (style == 0 && Marshal.GetLastWin32Error() != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the window styles.");
            int cloakResult = DwmGetWindowAttributeInt(handle, DWMWA_CLOAKED, out int cloak, sizeof(int));
            if (cloakResult < 0) throw new COMException("Could not read the window cloaking state.", cloakResult);
            int result = DwmGetWindowAttribute(handle, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<RECT>());
            if (result < 0)
            {
                System.Diagnostics.Trace.TraceWarning($"DWM frame bounds unavailable during move: 0x{result:X8}.");
                frame = r;
            }
            return new(new(handle, thread, process), r, frame, p, style,
                IsWindowVisible(handle) && !IsIconic(handle) && cloak == 0 && WindowSurfacePolicy.IsEligible(style));
        }

        private static void PositionNative(IntPtr handle, RECT bounds)
        {
            if (!SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Right - bounds.Left,
                bounds.Bottom - bounds.Top, SWP_NOACTIVATE | SWP_NOZORDER))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected the window move.");
        }

        private static void PlaceNative(IntPtr handle, WINDOWPLACEMENT value)
        {
            if (!SetWindowPlacement(handle, ref value))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected the restored window placement.");
        }
    }
}
