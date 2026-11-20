using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using static WindowScatter.Win32Interop;

namespace WindowScatter
{
    public sealed class OverviewDesktopButton : Button
    {
        // Space belongs to overview preview, including stale key routes to this control.
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Space) { e.Handled = true; return; }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.Key == Key.Space) { e.Handled = true; return; }
            base.OnKeyUp(e);
        }
    }

    // DWM thumbnails cover their destination HWND's WPF content. Chrome therefore
    // has its own owned surface above that HWND, never a thumbnail relationship.
    internal sealed class OverviewChromeWindow : Window
    {
        private readonly Window overviewHost;

        public OverviewChromeWindow(Window owner, UIElement content)
        {
            overviewHost = owner;
            Title = "MissionView controls";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            // Layered-window pixels with alpha 0 pass native mouse input through.
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            ShowInTaskbar = false;
            Topmost = true;
            Content = content;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            // MainWindow initializes its HWND before creating this surface's HWND.
            Owner = overviewHost;
            base.OnSourceInitialized(e);
            IntPtr handle = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW);
        }

        public void ShowAt(int left, int top, int width, int height, double dpiScale, bool activate = true)
        {
            bool opening = !IsVisible;
            Left = left / dpiScale;
            Top = top / dpiScale;
            Width = width / dpiScale;
            Height = height / dpiScale;
            if (opening) { ShowActivated = activate; Show(); }
            if (!SetWindowPos(new WindowInteropHelper(this).Handle, HWND_TOPMOST,
                left, top, width, height, SWP_SHOWWINDOW | (opening && activate ? 0 : SWP_NOACTIVATE)))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                    "Could not position the overview controls above the thumbnails.");
            if (opening && activate)
            {
                Activate();
                Focus();
            }
        }
    }
}
