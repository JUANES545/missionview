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
            EnumerateByMonitor(monitors, showMinimizedWindows: false).GetValueOrDefault(targetMonitor.Handle) ?? new();

        public Dictionary<IntPtr, List<WindowInfo>> EnumerateByMonitor(IReadOnlyList<MonitorHelper.MonitorBounds> monitors,
            AppExposeRequest? request = null, bool showMinimizedWindows = true,
            IReadOnlyList<MonitorHelper.MonitorBounds>? visibleMonitors = null, bool showMinimizedInOverview = false)
        {
            visibleMonitors ??= monitors;
            bool includeMinimized = request == null ? showMinimizedInOverview : showMinimizedWindows;
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
                    if (minimized && !includeMinimized) return true;

                    int extendedStyle = GetWindowLong(h, GWL_EXSTYLE);
                    if (extendedStyle == 0 && Marshal.GetLastWin32Error() != 0)
                    {
                        System.Diagnostics.Trace.TraceWarning("Could not read a window's extended styles during enumeration.");
                        request?.RecordUnavailableWindow();
                        return true;
                    }
                    if (!WindowSurfacePolicy.IsEligible(unchecked((uint)extendedStyle)))
                        return true;

                    // Suspended UWP apps and other ghost windows report visible
                    // but are cloaked by DWM - never actually on screen (#9).
                    if (IsCloaked(h))
                        return true;
                    if (minimized && request == null && !CurrentVirtualDesktop.Contains(h)) return true;

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
                    WindowInfo? window;
                    MonitorHelper.MonitorBounds? source;
                    if (minimized)
                    {
                        IntPtr monitorHandle = MonitorFromWindow(h, 2); // Last normal placement, not the iconic (-32000) rectangle.
                        source = monitors.FirstOrDefault(m => m.Handle == monitorHandle);
                        if (source == null) return true;
                        window = CreateMinimizedWindowInfo(h, title, placement, unchecked((uint)extendedStyle), source);
                    }
                    else
                    {
                        if (!GetWindowRect(h, out var rect))
                            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "GetWindowRect failed.");
                        int frameResult = DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out var dwmRect, Marshal.SizeOf<RECT>());
                        if (frameResult >= 0)
                            rect = dwmRect;
                        else
                            System.Diagnostics.Trace.TraceWarning($"Frame bounds unavailable for HWND 0x{h.ToInt64():X}: 0x{frameResult:X8}; using GetWindowRect.");
                        source = MonitorHelper.FindWindowMonitor(rect, monitors);
                        if (source == null) return true;
                        window = CreateWindowInfo(h, title, false, rect, placement.showCmd == SW_SHOWMAXIMIZED,
                            unchecked((uint)extendedStyle), source, monitors);
                    }
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
                    request?.RecordUnavailableWindow();
                }
#if ENUMERATION_DIAGNOSTICS
                finally { diagnostics.Capture(h); }
#endif

                return true;
            }, IntPtr.Zero);
            if (!enumerationSucceeded)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate windows.");

            var grouped = GroupByMonitor(windows, visibleMonitors, request, showMinimizedWindows, showMinimizedInOverview);
            var applicationNames = new Dictionary<uint, string?>();
            foreach (var window in grouped.Values.SelectMany(group => group))
            {
                uint process = window.Identity.ProcessId;
                if (GetWindowThreadProcessId(window.Handle, out uint liveProcess) != window.Identity.ThreadId ||
                    liveProcess != process) continue;
                if (!applicationNames.TryGetValue(process, out string? name))
                    applicationNames[process] = name = ApplicationIdentityReader.ReadApplicationName(process);
                window.ApplicationName = name;
            }

#if ENUMERATION_DIAGNOSTICS
            diagnostics.Write(System.IO.Path.Combine(AppContext.BaseDirectory, WindowEnumerationDiagnostics.FileName),
                grouped.Values.SelectMany(w => w).Select(w => w.Handle).ToList(), enumerationSucceeded);
#endif
            return grouped;
        }

        internal static Dictionary<IntPtr, List<WindowInfo>> GroupByMonitor(
            IEnumerable<(IntPtr Monitor, WindowInfo Window)> windows,
            IReadOnlyList<MonitorHelper.MonitorBounds> monitors, AppExposeRequest? request = null,
            bool showMinimizedWindows = true, bool showMinimizedInOverview = false)
        {
            bool includeMinimized = request == null ? showMinimizedInOverview : showMinimizedWindows;
            var grouped = monitors.ToDictionary(m => m.Handle, _ => new List<WindowInfo>());
            foreach (var item in windows)
            {
                if (grouped.TryGetValue(item.Monitor, out var group))
                {
                    if (item.Window.WasMinimized && !includeMinimized) continue;
                    if (request != null && !request.Includes(item.Window.Identity)) continue;
                    if (group.Count < MAX_WINDOWS) group.Add(item.Window);
                    else request?.RecordWindowOverLimit();
                }
            }
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
                OriginalRect = new PhysicalWindowRect
                {
                    Left = rect.Left - target.Left, Right = rect.Right - target.Left,
                    Top = rect.Top - target.Top, Bottom = rect.Bottom - target.Top
                }
            };
        }

        internal static WindowInfo? CreateMinimizedWindowInfo(IntPtr handle, string title,
            WINDOWPLACEMENT placement, uint style, MonitorHelper.MonitorBounds monitor)
        {
            if (!WindowSurfacePolicy.IsEligible(style)) return null;
            var rect = WindowTransfer.FromWorkspace(placement.rcNormalPosition, monitor, style);
            return new WindowInfo
            {
                Handle = handle, Title = title, WasMinimized = true,
                WasMaximized = (placement.flags & 2) != 0,
                OriginalRect = new PhysicalWindowRect
                {
                    Left = rect.Left - monitor.Left, Right = rect.Right - monitor.Left,
                    Top = rect.Top - monitor.Top, Bottom = rect.Bottom - monitor.Top
                }
            };
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

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
