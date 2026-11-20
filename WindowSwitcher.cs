using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static WindowScatter.Win32Interop;

namespace WindowScatter
{
    internal class WindowSwitcher
    {
        private readonly ThumbnailManager thumbnailManager;
        private readonly WindowAnimationManager animationManager;
        private readonly List<WindowThumb> windowThumbs;
        private readonly List<WindowLayout> cachedLayouts;

        public WindowSwitcher(ThumbnailManager thumbnailManager, WindowAnimationManager animationManager,
            List<WindowThumb> windowThumbs, List<WindowLayout> cachedLayouts)
        {
            this.thumbnailManager = thumbnailManager;
            this.animationManager = animationManager;
            this.windowThumbs = windowThumbs;
            this.cachedLayouts = cachedLayouts;
        }

        public void SwitchToWindow(IntPtr windowHandle, Action<bool> onSwitchComplete)
        {
            // DWM thumbnail stacking follows registration order rather than the WPF
            // canvas Z-order. Re-register only the clicked thumbnail so it stays on
            // top while it returns to its real window. This is intentionally kept
            // separate from the old all-thumbnail resync below, which added a large
            // idle-time stall to the dismiss path.
            if (!thumbnailManager.BringThumbnailToFront(windowHandle, animationManager))
                System.Diagnostics.Trace.TraceWarning("The selected thumbnail could not be raised for the return animation.");

            var layout = cachedLayouts?.FirstOrDefault(l => l.Window.Handle == windowHandle);
            bool wasMaximized = layout != null && layout.Window.WasMaximized;

            foreach (var thumb in windowThumbs)
            {
                var originalLayout = cachedLayouts?.FirstOrDefault(l => l.Window.Handle == thumb.WindowHandle);

                if (originalLayout != null)
                {
                    var origRect = originalLayout.Window.OriginalRect;
                    thumb.StartX = origRect.Left;
                    thumb.StartY = origRect.Top;
                    thumb.StartWidth = origRect.Right - origRect.Left;
                    thumb.StartHeight = origRect.Bottom - origRect.Top;
                }
                else
                {
                    RECT rect;
                    if (GetWindowRect(thumb.WindowHandle, out rect))
                    {
                        thumb.StartX = rect.Left;
                        thumb.StartY = rect.Top;
                        thumb.StartWidth = rect.Right - rect.Left;
                        thumb.StartHeight = rect.Bottom - rect.Top;
                    }
                }
            }

            try
            {
                animationManager.StartReturnAnimation(async () =>
                {
                    // Short grace: the animation loop's trailing DwmFlush
                    // already presented the final positions, so this only
                    // needs to cover 1-2 presents, not 100ms.
                    await Task.Delay(30);

                    onSwitchComplete(wasMaximized);
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Scatter selection animation failed: {ex}");
                onSwitchComplete(wasMaximized);
            }
        }
    }
}
