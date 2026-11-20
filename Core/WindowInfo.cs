using System;

namespace WindowScatter
{
    internal class WindowInfo
    {
        public IntPtr Handle;
        public string Title = string.Empty;
        public PhysicalWindowRect OriginalRect;
        public bool WasMaximized;
        public bool WasMinimized;
        public WindowActivationTarget Identity;
    }

    // Opaque native identity: no ownership of or calls through the HWND.
    internal readonly record struct WindowActivationTarget(IntPtr Handle, uint ThreadId, uint ProcessId);

    // Integer physical pixels, relative to the owning display for discovered/layout windows.
    internal struct PhysicalWindowRect
    {
        public int Left, Top, Right, Bottom;
    }

    // Animation coordinates remain physical doubles; WPF converts explicitly at its boundary.
    public class WindowPreviewState
    {
        public IntPtr WindowHandle { get; set; }
        public bool WasMinimized { get; set; }
        internal bool WasMaximized { get; set; }
        internal WindowActivationTarget Identity { get; set; }
        public double StartX { get; set; }
        public double StartY { get; set; }
        public double StartWidth { get; set; }
        public double StartHeight { get; set; }

        public double TargetX { get; set; }
        public double TargetY { get; set; }
        public double TargetWidth { get; set; }
        public double TargetHeight { get; set; }

        public double CurrentX { get; set; }
        public double CurrentY { get; set; }
        public double CurrentWidth { get; set; }
        public double CurrentHeight { get; set; }
    }

    internal class WindowLayout
    {
        public WindowInfo Window = null!;
        public double Width;
        public double Height;
        public double X;
        public double Y;
        public double OrigWidth;
        public double OrigHeight;
    }
}
