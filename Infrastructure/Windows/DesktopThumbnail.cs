using static WindowScatter.Win32Interop;

namespace WindowScatter
{
    internal static class DesktopThumbnail
    {
        internal delegate int UpdateProperties(IntPtr thumbnail, ref DWM_THUMBNAIL_PROPERTIES properties);

        public static bool TryUpdate(IntPtr thumbnail, RECT monitorBounds, RECT sourceBounds,
            byte opacity, UpdateProperties update)
        {
            if (monitorBounds.Right <= monitorBounds.Left || monitorBounds.Bottom <= monitorBounds.Top ||
                monitorBounds.Left < sourceBounds.Left || monitorBounds.Top < sourceBounds.Top ||
                monitorBounds.Right > sourceBounds.Right || monitorBounds.Bottom > sourceBounds.Bottom)
            {
                System.Diagnostics.Trace.TraceWarning("Desktop thumbnail source does not cover the target monitor.");
                return false;
            }

            var properties = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_RECTSOURCE | DWM_TNP_OPACITY |
                    DWM_TNP_VISIBLE | DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new RECT
                {
                    Right = monitorBounds.Right - monitorBounds.Left,
                    Bottom = monitorBounds.Bottom - monitorBounds.Top
                },
                rcSource = new RECT
                {
                    Left = monitorBounds.Left - sourceBounds.Left,
                    Top = monitorBounds.Top - sourceBounds.Top,
                    Right = monitorBounds.Right - sourceBounds.Left,
                    Bottom = monitorBounds.Bottom - sourceBounds.Top
                },
                opacity = opacity,
                fVisible = true,
                fSourceClientAreaOnly = false
            };
            int result = update(thumbnail, ref properties);
            if (result < 0)
            {
                System.Diagnostics.Trace.TraceError($"DwmUpdateThumbnailProperties failed: 0x{result:X8}");
                return false;
            }
            return true;
        }
    }
}
