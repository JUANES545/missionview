using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using static WindowScatter.Win32Interop;

namespace WindowScatter
{
    internal sealed record NativeWindowMetadata
    {
        public long Hwnd { get; init; }
        public string ClassName { get; init; } = "";
        public int ClassError { get; init; }
        public bool ClassTruncated { get; init; }
        public uint ProcessId { get; init; }
        public uint ThreadId { get; init; }
        public long Owner { get; init; }
        public long RootOwner { get; init; }
        public long RootOwnerLastActivePopup { get; init; }
        public uint Style { get; init; }
        public int StyleError { get; init; }
        public uint ExtendedStyle { get; init; }
        public int ExtendedStyleError { get; init; }
        public bool IsWindow { get; init; }
        public bool Visible { get; init; }
        public bool Minimized { get; init; }
        public int CloakedHResult { get; init; }
        public int Cloaked { get; init; }
        public bool RectAvailable { get; init; }
        public int RectError { get; init; }
        public int[] Rect { get; init; } = [];
        public int FrameHResult { get; init; }
        public int[] Frame { get; init; } = [];
    }

    internal sealed class WindowEnumerationDiagnostics
    {
        internal const int MaxWindows = 512;
        internal const string FileName = "enumeration-diagnostic.json";
        private readonly List<NativeWindowMetadata> windows = new();
        private int enumeratedWindows;

        public void Capture(IntPtr hwnd)
        {
            if (windows.Count >= MaxWindows)
            {
                enumeratedWindows++;
                return;
            }
            var className = new StringBuilder(128);
            int classLength = GetClassName(hwnd, className, className.Capacity);
            int classError = classLength == 0 ? Marshal.GetLastWin32Error() : 0;
            uint thread = GetWindowThreadProcessId(hwnd, out uint process);
            IntPtr rootOwner = GetAncestor(hwnd, GA_ROOTOWNER);
            int style = GetWindowLong(hwnd, GWL_STYLE);
            int styleError = style == 0 ? Marshal.GetLastWin32Error() : 0;
            int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            int extendedStyleError = extendedStyle == 0 ? Marshal.GetLastWin32Error() : 0;
            int cloakedResult = DwmGetWindowAttributeInt(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int));
            bool rectAvailable = GetWindowRect(hwnd, out var rect);
            int rectError = rectAvailable ? 0 : Marshal.GetLastWin32Error();
            int frameResult = DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<RECT>());
            Record(new NativeWindowMetadata
            {
                Hwnd = hwnd.ToInt64(), ClassName = className.ToString(), ClassError = classError,
                ClassTruncated = classLength == className.Capacity - 1,
                ProcessId = process, ThreadId = thread,
                Owner = GetWindow(hwnd, GW_OWNER).ToInt64(), RootOwner = rootOwner.ToInt64(),
                RootOwnerLastActivePopup = rootOwner == IntPtr.Zero ? 0 : GetLastActivePopup(rootOwner).ToInt64(),
                Style = unchecked((uint)style), StyleError = styleError,
                ExtendedStyle = unchecked((uint)extendedStyle), ExtendedStyleError = extendedStyleError,
                IsWindow = Win32Interop.IsWindow(hwnd), Visible = IsWindowVisible(hwnd), Minimized = IsIconic(hwnd),
                CloakedHResult = cloakedResult, Cloaked = cloaked,
                RectAvailable = rectAvailable, RectError = rectError,
                Rect = [rect.Left, rect.Top, rect.Right, rect.Bottom],
                FrameHResult = frameResult, Frame = [frame.Left, frame.Top, frame.Right, frame.Bottom]
            });
        }

        internal void Record(NativeWindowMetadata window)
        {
            enumeratedWindows++;
            if (windows.Count < MaxWindows) windows.Add(window);
        }

        public bool Write(string path, IReadOnlyCollection<IntPtr> included, bool enumerationSucceeded)
        {
            var handles = included.Select(h => h.ToInt64()).ToHashSet();
            var snapshot = new
            {
                Schema = 1, CapturedUtc = DateTime.UtcNow, EnumerationSucceeded = enumerationSucceeded,
                EnumeratedWindows = enumeratedWindows, Truncated = enumeratedWindows > windows.Count,
                IncludedHandles = handles.OrderBy(h => h).ToArray(),
                // This is a public Win32 relationship, not an authoritative taskbar representative.
                RepresentativeBasis = "GetAncestor(GA_ROOTOWNER)/GetLastActivePopup; diagnostic only",
                Windows = windows.Select(w => new { Metadata = w, IncludedInOverview = handles.Contains(w.Hwnd) })
            };
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(snapshot), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                System.Diagnostics.Trace.TraceError(
                    $"Enumeration diagnostics could not be written ({ex.GetType().Name}, 0x{ex.HResult:X8}).");
                return false;
            }
        }
    }
}
