using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using static WindowScatter.Win32Interop;

namespace WindowScatter
{
    internal class WindowEnumerator
    {
        private const int MAX_WINDOWS = 20;

        private readonly Func<IntPtr, bool> isOwnWindow;

        public WindowEnumerator(IntPtr ownerWindowHandle, IntPtr chromeWindowHandle)
        {
            isOwnWindow = h => h == ownerWindowHandle || h == chromeWindowHandle;
        }

        public WindowEnumerator(Func<IntPtr, bool> isOwnWindow) => this.isOwnWindow = isOwnWindow;

        public List<WindowInfo> EnumerateVisibleWindows(MonitorHelper.MonitorBounds targetMonitor) =>
            EnumerateVisibleWindows(targetMonitor, MonitorHelper.GetMonitors());

        public List<WindowInfo> EnumerateVisibleWindows(MonitorHelper.MonitorBounds targetMonitor,
            IReadOnlyList<MonitorHelper.MonitorBounds> monitors) =>
            EnumerateByMonitor(monitors).GetValueOrDefault(targetMonitor.Handle) ?? new();

        public Dictionary<IntPtr, List<WindowInfo>> EnumerateByMonitor(IReadOnlyList<MonitorHelper.MonitorBounds> monitors)
        {
            var windows = new List<(IntPtr Monitor, WindowInfo Window)>();

#if ENUMERATION_DIAGNOSTICS
            var diagnostics = new WindowEnumerationDiagnostics();
#endif
            bool enumerationSucceeded = EnumWindows((h, l) =>
            {
                try
                {
                    if (isOwnWindow(h) || !IsWindowVisible(h))
                        return true;
                    uint thread = GetWindowThreadProcessId(h, out uint process);
                    if (thread == 0 || process == 0) return true;
                    bool minimized = IsIconic(h);
                    if (minimized) return true;

                    int extendedStyle = GetWindowLong(h, GWL_EXSTYLE);
                    if (extendedStyle == 0 && Marshal.GetLastWin32Error() != 0)
                    {
                        System.Diagnostics.Trace.TraceWarning("Could not read a window's extended styles during enumeration.");
                        return true;
                    }
                    if (!WindowSurfacePolicy.IsEligible(unchecked((uint)extendedStyle)))
                        return true;

                    // Suspended UWP apps and other ghost windows report visible
                    // but are cloaked by DWM - never actually on screen (#9).
                    if (IsCloaked(h))
                        return true;

                    int len = GetWindowTextLength(h);
                    if (len == 0) return true;

                    var sb = new StringBuilder(len + 1);
                    GetWindowText(h, sb, sb.Capacity);
                    string title = sb.ToString();

                    if (string.IsNullOrWhiteSpace(title) || IsExcludedWindow(title))
                        return true;

                    var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                    if (!GetWindowPlacement(h, ref placement))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "GetWindowPlacement failed.");
                    RECT rect;
                    if (!GetWindowRect(h, out rect))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "GetWindowRect failed.");
                    int frameResult = DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out var dwmRect, Marshal.SizeOf<RECT>());
                    if (frameResult >= 0)
                        rect = dwmRect;
                    else
                        System.Diagnostics.Trace.TraceWarning($"Frame bounds unavailable for HWND 0x{h.ToInt64():X}: 0x{frameResult:X8}; using GetWindowRect.");
                    var source = MonitorHelper.FindWindowMonitor(rect, monitors);
                    if (source == null) return true;
                    var window = CreateWindowInfo(h, title, minimized, rect, placement.showCmd == SW_SHOWMAXIMIZED,
                        unchecked((uint)extendedStyle), source, monitors);
                    if (window != null && GetWindowThreadProcessId(h, out uint currentProcess) == thread && currentProcess == process)
                    {
                        window.Identity = new(h, thread, process);
                        windows.Add((source.Handle, window));
                    }
                }
                catch (Exception ex)
                {
                    int error = ex is System.ComponentModel.Win32Exception native ? native.NativeErrorCode : ex.HResult;
                    System.Diagnostics.Trace.TraceWarning($"Window metadata failed for HWND 0x{h.ToInt64():X}: {ex.GetType().Name}, code 0x{error:X8}, {ex.Message}");
                }
#if ENUMERATION_DIAGNOSTICS
                finally { diagnostics.Capture(h); }
#endif

                return true;
            }, IntPtr.Zero);
            if (!enumerationSucceeded)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate windows.");

            var grouped = GroupByMonitor(windows, monitors);

#if ENUMERATION_DIAGNOSTICS
            diagnostics.Write(System.IO.Path.Combine(AppContext.BaseDirectory, WindowEnumerationDiagnostics.FileName),
                grouped.Values.SelectMany(w => w).Select(w => w.Handle).ToList(), enumerationSucceeded);
#endif
            return grouped;
        }

        internal static Dictionary<IntPtr, List<WindowInfo>> GroupByMonitor(
            IEnumerable<(IntPtr Monitor, WindowInfo Window)> windows,
            IReadOnlyList<MonitorHelper.MonitorBounds> monitors)
        {
            var grouped = monitors.ToDictionary(m => m.Handle, _ => new List<WindowInfo>());
            foreach (var item in windows)
                if (grouped.TryGetValue(item.Monitor, out var group) && group.Count < MAX_WINDOWS)
                    group.Add(item.Window);
            return grouped;
        }

        internal static WindowInfo? CreateWindowInfo(IntPtr handle, string title, bool minimized,
            RECT rect, bool maximized, uint style, MonitorHelper.MonitorBounds target,
            IReadOnlyList<MonitorHelper.MonitorBounds> monitors)
        {
            if (minimized || !WindowSurfacePolicy.IsEligible(style)) return null;
            var source = MonitorHelper.FindWindowMonitor(rect, monitors);
            if (source == null || source.Handle != target.Handle) return null;
            if ((long)rect.Right - rect.Left < 100 || (long)rect.Bottom - rect.Top < 100) return null;
            return new WindowInfo
            {
                Handle = handle, Title = title, WasMaximized = maximized,
                OriginalRect = new RECT
                {
                    Left = rect.Left - target.Left, Right = rect.Right - target.Left,
                    Top = rect.Top - target.Top, Bottom = rect.Bottom - target.Top
                }
            };
        }

        public List<WindowInfo> EnumerateVisibleWindows()
        {
            return EnumerateVisibleWindows(MonitorHelper.GetPrimaryMonitor());
        }

        private bool IsExcludedWindow(string title)
        {
            var lower = title.ToLower();
            return lower.Contains("window scatter") ||
                    lower.Contains("program manager") ||
                    lower.Contains("microsoft text input") ||
                    lower.Contains("windows input") ||
                    lower.Contains("nvidia geforce") ||
                    title == "Default IME" ||
                    title == "MSCTFIME UI" ||
                    title == "GDI+ Window";
        }

        private static bool IsCloaked(IntPtr h)
        {
            int result = DwmGetWindowAttributeInt(h, DWMWA_CLOAKED, out int cloaked, sizeof(int));
            if (result < 0) throw new COMException("Could not read window cloaking state.", result);
            return cloaked != 0;
        }
    }
}
