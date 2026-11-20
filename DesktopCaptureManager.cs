using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using static WindowScatter.Win32Interop;

namespace WindowScatter
{
    internal class DesktopCaptureManager
    {
        private readonly Image backgroundImage;
        private readonly Window owner;
        private IntPtr desktopThumbnail = IntPtr.Zero;
        private IntPtr desktopSource = IntPtr.Zero;
        private int generation;

        private RECT currentMonitorBounds;

        public DesktopCaptureManager(Image backgroundImage, Window owner)
        {
            this.backgroundImage = backgroundImage;
            this.owner = owner;
        }

        public void SetMonitorBounds(int left, int top, int width, int height)
        {
            currentMonitorBounds = new RECT { Left = left, Top = top, Right = left + width, Bottom = top + height };
        }

        public async Task<bool> CaptureDesktopAsync(IntPtr sharedSource = default)
        {
            int current = ++generation;
            var helper = new WindowInteropHelper(owner);
            IntPtr ownerHwnd = helper.Handle;

            if (ownerHwnd == IntPtr.Zero) return false;

            if (desktopThumbnail != IntPtr.Zero)
            {
                CleanupCore();
                await Task.Delay(10);
                if (current != generation) return false;
            }

            IntPtr desktopHandle = sharedSource != IntPtr.Zero ? sharedSource : GetDesktopWindowHandle();
            if (desktopHandle == IntPtr.Zero) return false;

            int result = DwmRegisterThumbnail(ownerHwnd, desktopHandle, out desktopThumbnail);
            if (result != 0 || desktopThumbnail == IntPtr.Zero) return false;

            desktopSource = desktopHandle;
            if (!UpdateDesktopThumbnail())
            {
                Cleanup();
                return false;
            }
            DwmFlush();
            await Task.Delay(50);
            if (current != generation) return false;

            SIZE sourceSize;
            int sizeResult = DwmQueryThumbnailSourceSize(desktopThumbnail, out sourceSize);
            if (sizeResult != 0 || sourceSize.cx == 0 || sourceSize.cy == 0)
            {
                Cleanup();
                return false;
            }

            return true;
        }

        public bool UpdateDesktopThumbnail(byte opacity = 255)
        {
            if (desktopThumbnail == IntPtr.Zero || desktopSource == IntPtr.Zero) return false;

            if (!GetWindowRect(desktopSource, out RECT sourceBounds))
            {
                System.Diagnostics.Trace.TraceError($"GetWindowRect for desktop source failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            // Both rectangles are physical pixels; rcSource is local to WorkerW/Progman.
            return DesktopThumbnail.TryUpdate(desktopThumbnail, currentMonitorBounds, sourceBounds,
                opacity, DwmUpdateThumbnailProperties);
        }

        public void Cleanup()
        {
            generation++;
            CleanupCore();
        }

        private void CleanupCore()
        {
            if (desktopThumbnail != IntPtr.Zero)
            {
                DwmUnregisterThumbnail(desktopThumbnail);
                desktopThumbnail = IntPtr.Zero;
            }
            desktopSource = IntPtr.Zero;
        }

        public static IntPtr GetDesktopWindowHandle()
        {
            IntPtr workerW = IntPtr.Zero;
            IntPtr progman = FindWindow("Progman", null);

            if (progman == IntPtr.Zero) return IntPtr.Zero;

            SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out IntPtr result);

            EnumWindows((hwnd, lParam) =>
            {
                IntPtr shellViewWin = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (shellViewWin != IntPtr.Zero)
                {
                    workerW = FindWindowEx(IntPtr.Zero, hwnd, "WorkerW", null);
                }
                return true;
            }, IntPtr.Zero);

            return workerW != IntPtr.Zero ? workerW : progman;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
    }
}
