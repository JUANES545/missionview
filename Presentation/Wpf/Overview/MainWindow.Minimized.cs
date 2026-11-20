using System.Windows;
using System.Windows.Controls;

namespace WindowScatter
{
    public partial class MainWindow
    {
        private MinimizedWindowStrip? minimizedStrip;
        private IEnumerable<WindowThumb> ViewThumbs => windowThumbs.Concat(minimizedStrip?.Thumbs ?? []);
        private double OverviewContentHeight => monitorPixelHeight - (minimizedStrip?.Geometry.ReservedHeight ?? 0) * currentDpiScale;

        private void RebuildMinimizedStrip(List<WindowInfo> windows, bool settled)
        {
            CleanupMinimizedStrip();
            var minimized = windows.Where(w => w.WasMinimized).ToList();
            if (minimized.Count == 0) return;
            minimizedStrip = new MinimizedWindowStrip(minimized, monitor.Width / currentDpiScale,
                monitor.Height / currentDpiScale, currentDpiScale, OnMinimizedClicked, OnWindowHovered,
                selected: handle =>
                {
                    if (!scatterInteraction.IsOpen || AnyAnimating || isScatterActive || isTransitioning ||
                        coordinator.movingWindow || coordinator.navigationPending || coordinator.drag.IsPressed)
                        throw new InvalidOperationException("Window selection is unavailable during an overview transition.");
                    coordinator.SelectWindow(handle);
                })
            { Visibility = settled ? Visibility.Visible : Visibility.Hidden };
            Panel.SetZIndex(minimizedStrip, 2);
            ChromeCanvas.Children.Add(minimizedStrip);
        }

        private void OnMinimizedClicked(IntPtr handle)
        {
            if (!scatterInteraction.IsOpen || AnyAnimating || isScatterActive || isTransitioning || coordinator.movingWindow) return;
            coordinator.SelectWindow(handle);
            OnWindowClicked(handle);
        }

        private void CleanupMinimizedStrip()
        {
            if (minimizedStrip != null) ChromeCanvas.Children.Remove(minimizedStrip);
            minimizedStrip = null;
        }
    }
}
