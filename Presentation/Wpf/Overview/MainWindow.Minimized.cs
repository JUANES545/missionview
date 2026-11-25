using System.Windows;
using System.Windows.Controls;
using static WindowScatter.Win32Interop;

namespace WindowScatter
{
    public partial class MainWindow
    {
        private MinimizedWindowStrip? minimizedStrip;
        private IEnumerable<WindowThumb> ViewThumbs => windowThumbs.Concat(minimizedStrip?.Thumbs ?? []);
        private double OverviewContentHeight => monitorPixelHeight - (minimizedStrip?.Geometry.ReservedHeight ?? 0) * currentDpiScale;
        private bool IncludeMinimizedWindows => coordinator.showMinimizedWindows;

        private static bool IsOverviewShelfTargetAvailable(IntPtr handle) =>
            IsWindowVisible(handle) && IsWindowEnabled(handle) &&
            DwmGetWindowAttributeInt(handle, DWMWA_CLOAKED, out int cloaked, sizeof(int)) >= 0 &&
            cloaked == 0 && CurrentVirtualDesktop.Contains(handle);

        private void RebuildThumbnails(IReadOnlyList<IntPtr> sourceOrder, List<WindowInfo> windows, bool settled)
        {
            windows = windows.Where(w => IncludeMinimizedWindows || !w.WasMinimized).ToList();
            InvalidateNavigation();
            animationManager.StopAllAnimations();
            thumbnailManager.CleanupAllThumbnails();
            enumeratedWindowCount = windows.Count;
            RebuildMinimizedStrip(windows, settled);
            RefreshOverviewPresentation(SystemParameters.HighContrast, SystemParameters.ClientAreaAnimation);
            var layouts = LayoutCalculatorForDisplay().CalculateLayouts(windows.Where(w => !w.WasMinimized).ToList(),
                monitor.Width, OverviewContentHeight, currentDpiScale, settings.ThumbnailSize);
            thumbnailManager.SourceOrder = sourceOrder;
            string foldedQuery = OverviewTitleSearch.Fold(coordinator.searchQuery);
            thumbnailManager.RegisterThumbnails(layouts, animationManager, currentDpiScale, settled, foldedQuery);
            minimizedStrip?.ApplyTitleFilter(foldedQuery);
            LayoutCalculatorForDisplay().KeepRegisteredIdentities(windowThumbs.Select(t => t.Identity));
            windowSwitcher = new WindowSwitcher(thumbnailManager, animationManager, windowThumbs, layouts);
            if (coordinator.selectedWindowHandle != IntPtr.Zero && coordinator.ViewFor(coordinator.selectedWindowHandle) == null)
                coordinator.SelectWindow(IntPtr.Zero);
            ChromeCanvas.Visibility = Visibility.Visible;
            string destinationHint = views.Count > 1 ? "\nDrop a window here." : "";
            EmptyMessage.Text = windows.Count == 0 ? coordinator.appExposeRequest != null
                ? "No windows from this application on this display." + destinationHint
                : "No windows on this display." + destinationHint :
                "Window previews unavailable." + destinationHint;
            EmptyMessage.Visibility = !ViewThumbs.Any() ? Visibility.Visible : Visibility.Hidden;
            MoveStatus.Text = IdleMoveStatus;
            if (!settled) return;
            thumbnailManager.CompleteTransition(currentDpiScale, selectedWindowHandle);
            animationManager.StartKeepAlive();
        }

        private void RebuildMinimizedStrip(List<WindowInfo> windows, bool settled)
        {
            CleanupMinimizedStrip();
            if (!IncludeMinimizedWindows) return;
            var minimized = windows.Where(w => w.WasMinimized).ToList();
            if (minimized.Count == 0) return;
            minimizedStrip = new MinimizedWindowStrip(minimized, monitor.Width / currentDpiScale,
                monitor.Height / currentDpiScale, currentDpiScale, OnMinimizedClicked, OnWindowHovered,
                effectsEnabled: () => !settings.ReduceAnimations, selected: handle =>
                {
                    if (!scatterInteraction.IsOpen || AnyAnimating || isScatterActive || isTransitioning ||
                        coordinator.movingWindow || coordinator.navigationPending || coordinator.drag.IsPressed)
                        throw new InvalidOperationException("Window selection is unavailable during an overview transition.");
                    coordinator.SelectWindow(handle);
                }, selectionBorderColor: () => settings.SelectionBorderColor)
            { Visibility = settled ? Visibility.Visible : Visibility.Hidden };
            foreach (var thumb in minimizedStrip.Thumbs) ConfigureCloseInput(thumb);
            minimizedStrip.ApplyPalette(SystemParameters.HighContrast);
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
            if (minimizedStrip != null)
            {
                minimizedStrip.IsEnabled = false;
                minimizedStrip.SetSelected(IntPtr.Zero);
                ChromeCanvas.Children.Remove(minimizedStrip);
            }
            minimizedStrip = null;
            InvalidateNavigation();
        }
    }
}
