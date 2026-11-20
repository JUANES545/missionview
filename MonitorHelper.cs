using System;
using System.Runtime.InteropServices;
using static WindowScatter.Win32Interop;

namespace WindowScatter
{
    internal class MonitorHelper
    {
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref RECT bounds, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        public class MonitorBounds
        {
            public IntPtr Handle { get; set; }
            public int Left { get; set; }
            public int Top { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public bool IsPrimary { get; set; }
            public RECT WorkArea { get; set; }
            public double DpiScale { get; set; } = 1;

            public int Right => Left + Width;
            public int Bottom => Top + Height;

            public override string ToString()
            {
                return $"Monitor: {Width}x{Height} at ({Left},{Top}) {(IsPrimary ? "[PRIMARY]" : "")}";
            }
        }

        public static MonitorBounds GetMonitorFromCursor()
        {
            POINT cursorPos;
            if (!GetCursorPos(out cursorPos))
            {
                return GetPrimaryMonitor();
            }

            return GetMonitorFromPoint(cursorPos.X, cursorPos.Y);
        }

        public static MonitorBounds GetMonitorFromPoint(int x, int y)
        {
            POINT pt = new POINT { X = x, Y = y };
            IntPtr hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            return GetMonitorBounds(hMonitor);
        }

        public static MonitorBounds GetPrimaryMonitor()
        {
            POINT point = new POINT { X = 0, Y = 0 };
            IntPtr hMonitor = MonitorFromPoint(point, MONITOR_DEFAULTTOPRIMARY);
            return GetMonitorBounds(hMonitor);
        }

        /// <summary>
        /// Effective DPI scale (dpi/96) for the monitor containing the given bounds'
        /// center. Used to convert physical pixels (Win32/DWM space) to WPF DIPs.
        /// Returns 1.0 when the DPI cannot be determined (e.g. older Windows).
        /// </summary>
        public static double GetDpiScaleForMonitor(MonitorBounds monitor)
        {
            return GetDpiScaleAtPoint(
                monitor.Left + monitor.Width / 2,
                monitor.Top + monitor.Height / 2);
        }

        public static double GetDpiScaleAtPoint(int x, int y)
        {
            try
            {
                POINT pt = new POINT { X = x, Y = y };
                IntPtr hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
                if (hMonitor != IntPtr.Zero)
                {
                    uint dpiX, dpiY;
                    if (GetDpiForMonitor(hMonitor, 0, out dpiX, out dpiY) == 0 && dpiX > 0)
                        return dpiX / 96.0;
                }
            }
            catch { }

            return 1.0;
        }

        private static MonitorBounds GetMonitorBounds(IntPtr hMonitor)
        {
            MONITORINFO info = new MONITORINFO();
            info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));

            if (GetMonitorInfo(hMonitor, ref info))
            {
                return new MonitorBounds
                {
                    Handle = hMonitor,
                    Left = info.rcMonitor.Left,
                    Top = info.rcMonitor.Top,
                    Width = info.rcMonitor.Right - info.rcMonitor.Left,
                    Height = info.rcMonitor.Bottom - info.rcMonitor.Top,
                    IsPrimary = (info.dwFlags & 0x00000001) != 0,
                    WorkArea = info.rcWork,
                    DpiScale = GetDpiScaleAtPoint(info.rcMonitor.Left +
                        (info.rcMonitor.Right - info.rcMonitor.Left) / 2,
                        info.rcMonitor.Top + (info.rcMonitor.Bottom - info.rcMonitor.Top) / 2)
                };
            }

            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                "Could not read monitor geometry.");
        }

        public static List<MonitorBounds> GetMonitors()
        {
            var handles = new List<IntPtr>();
            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr h, IntPtr dc, ref RECT rect, IntPtr data) => { handles.Add(h); return true; }, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate monitors.");
            if (handles.Count == 0) throw new InvalidOperationException("No display monitors were reported.");
            return handles.Select(GetMonitorBounds).ToList();
        }

        public static MonitorBounds? FindWindowMonitor(RECT rect, IReadOnlyList<MonitorBounds> monitors)
        {
            MonitorBounds? best = null;
            long bestArea = 0;
            foreach (var monitor in monitors.OrderBy(m => m.Left).ThenBy(m => m.Top).ThenBy(m => m.Handle.ToInt64()))
            {
                long width = Math.Max(0L, (long)Math.Min(rect.Right, monitor.Right) - Math.Max(rect.Left, monitor.Left));
                long height = Math.Max(0L, (long)Math.Min(rect.Bottom, monitor.Bottom) - Math.Max(rect.Top, monitor.Top));
                long area = width * height;
                if (area > bestArea) { bestArea = area; best = monitor; }
            }
            return best;
        }

        public static bool SameTopology(IReadOnlyList<MonitorBounds> before, IReadOnlyList<MonitorBounds> after) =>
            before.Count == after.Count && before.All(a => after.Any(b => a.Handle == b.Handle &&
                a.Left == b.Left && a.Top == b.Top && a.Width == b.Width && a.Height == b.Height &&
                a.WorkArea.Equals(b.WorkArea) && Math.Abs(a.DpiScale - b.DpiScale) < .001));

    }
}
