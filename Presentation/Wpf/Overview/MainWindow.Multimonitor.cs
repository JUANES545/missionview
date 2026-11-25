using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using static WindowScatter.Win32Interop;

namespace WindowScatter
{
    public partial class MainWindow
    {
        private int enumeratedWindowCount;
        private readonly HashSet<ContextMenu> openMoveMenus = new();
        private string DisplayName => $"Display {coordinator.monitors.FindIndex(m => m.Handle == monitor.Handle) + 1}";
        private string IdleMoveStatus => coordinator.appExposeRequest?.UnidentifiedWindows > 0
            ? "Some windows could not be identified"
            : coordinator.appExposeRequest?.WindowsOverLimit > 0 ? "Some application windows exceed the 20-window display limit"
            : ViewThumbs.Count() < enumeratedWindowCount ? "Some previews unavailable" : "";
        private IEnumerable<MainWindow> OrderedViews() => views.OrderBy(v => v.monitor.Left).ThenBy(v => v.monitor.Top);
        private IEnumerable<WindowThumb> AllThumbs() => OrderedViews().SelectMany(v => v.ViewThumbs);
        private MainWindow? ViewFor(IntPtr handle) => views.FirstOrDefault(v => v.ViewThumbs.Any(t => t.WindowHandle == handle));

        private async System.Threading.Tasks.Task PrepareViewAsync(IReadOnlyList<IntPtr> sourceOrder, IntPtr desktopSource,
            List<WindowInfo> windows)
        {
            currentDpiScale = monitor.DpiScale;
            monitorPixelWidth = monitor.Width;
            monitorPixelHeight = monitor.Height;
            WindowState = WindowState.Normal;
            Width = monitor.Width / currentDpiScale;
            Height = monitor.Height / currentDpiScale;
            Left = monitor.Left / currentDpiScale;
            Top = monitor.Top / currentDpiScale;
            SearchPanel.Width = Math.Min(260, Math.Max(0, Width - 96) / 2);
            MoveStatus.Width = Math.Max(0, Width - 96 - SearchPanel.Width);
            EmptyMessage.Width = Math.Min(360, Math.Max(0, Width - 32));
            Canvas.SetLeft(EmptyMessage, (Width - EmptyMessage.Width) / 2);
            Canvas.SetTop(EmptyMessage, Height / 2 - 30);
            desktopCaptureManager.SetMonitorBounds(monitor.Left, monitor.Top, monitor.Width, monitor.Height);
            int generation = scatterInteraction.Generation;
            if (useDesktopCapture)
            {
                bool captured = desktopSource != IntPtr.Zero && await desktopCaptureManager.CaptureDesktopAsync(desktopSource);
                if (isClosing || !scatterInteraction.IsOpen || generation != scatterInteraction.Generation) return;
                if (!captured)
                {
                    useDesktopCapture = false;
                    wallpaperManager.SetWallpaperBackground();
                }
            }
            else wallpaperManager.SetWallpaperBackground();
            RebuildThumbnails(sourceOrder, windows, settled: false);
        }

        private void ShowView()
        {
            ShowActivated = false;
            Show();
            if (!SetWindowPos(new WindowInteropHelper(this).Handle, HWND_TOPMOST,
                monitor.Left, monitor.Top, monitor.Width, monitor.Height, SWP_SHOWWINDOW | SWP_NOACTIVATE))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not position an overview view.");
            chromeWindow.ShowAt(monitor.Left, monitor.Top, monitor.Width, monitor.Height, currentDpiScale, activate: false);
        }

        private void HideAllViews()
        {
            StopCloseObservation();
            CloseMoveMenus();
            foreach (var view in views.ToArray())
            {
                if (!view.chromeClosed) view.chromeWindow.Hide();
                view.Hide();
            }
            ResetFileDrag();
        }

        private void CleanupView()
        {
            InvalidateNavigation();
            SearchBox.Text = "";
            NoSearchResults.Visibility = Visibility.Collapsed;
            CleanupMinimizedStrip();
            animationManager?.StopAllAnimations();
            thumbnailManager?.CleanupAllThumbnails();
            desktopCaptureManager?.Cleanup();
            previewExpanded = false;
            dragPreview?.Dispose();
            dragPreview = null;
        }

        private void CloseAllViews(WindowActivationTarget? selection)
        {
            if (!session.TryBeginClose(selection, isClosing || isScatterActive || AnyAnimating || isTransitioning || movingWindow,
                out var target)) return;
            CloseMoveMenus();
            int generation = scatterInteraction.Generation;
            bool maximized = ViewFor(target.Handle)?.ViewThumbs.FirstOrDefault(t => t.WindowHandle == target.Handle)?.WasMaximized == true;
            var group = new OverviewReturnGroup(views.Count,
                () => !isClosing && generation == scatterInteraction.Generation,
                () => OnSwitchComplete(target, selection.HasValue, maximized));
            try
            {
                CancelDrag();
                var sourceOrder = WindowStacking.Capture();
                foreach (var view in views)
                {
                    if (view.minimizedStrip != null) view.minimizedStrip.Visibility = Visibility.Hidden;
                    view.thumbnailManager.BeginTransition(() =>
                    {
                        view.ResetPreview();
                        view.animationManager.StopAllAnimations();
                        view.thumbnailManager.SourceOrder = sourceOrder;
                        view.thumbnailManager.RestoreSourceStacking(view.animationManager);
                    });
                }
                for (int i = 0; i < views.Count; i++)
                {
                    var view = views[i];
                    int index = i;
                    if (selection.HasValue && view.windowThumbs.Any(t => t.WindowHandle == target.Handle))
                        view.windowSwitcher!.SwitchToWindow(target.Handle, _ => group.Returned(index));
                    else view.animationManager.StartReturnAnimation(() => group.Returned(index));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Coordinated overview return failed: {ex}");
                foreach (var view in views) view.animationManager.StopAllAnimations();
                group.Finish();
            }
        }

        private void ConfigureThumbnailInput(WindowThumb thumb)
        {
            var border = thumb.ClickBorder;
            ConfigureThumbnailAutomation(thumb);
            ConfigureCloseInput(thumb);
            border.MouseLeftButtonDown += (_, e) =>
            {
                if (BlocksThumbnailInput) { e.Handled = true; return; }
                if (e.OriginalSource is DependencyObject source && OverviewCloseInput.IsCloseSource(source, thumb.CloseButton)) return;
                if (thumb.IsFiltered || !scatterInteraction.IsOpen || AnyAnimating || isScatterActive ||
                    isTransitioning || coordinator.movingWindow) return;
                try
                {
                    SelectWindow(thumb.WindowHandle);
                    if (!scatterInteraction.IsOpen) return;
                    var cursor = ReadPointer();
                    uint dpi = GetDpiForWindow(chromeHandle);
                    coordinator.drag.Press(thumb.Identity, cursor.X, cursor.Y,
                        GetSystemMetricsForDpi(68, dpi), GetSystemMetricsForDpi(69, dpi));
                    coordinator.dragSource = this;
                    coordinator.dragThumb = thumb;
                    coordinator.dragCapture = border;
                    coordinator.dragGrabX = Math.Clamp((cursor.X - monitor.Left - thumb.CurrentX) / thumb.CurrentWidth, 0, 1);
                    coordinator.dragGrabY = Math.Clamp((cursor.Y - monitor.Top - thumb.CurrentY) / thumb.CurrentHeight, 0, 1);
                    if (!border.CaptureMouse())
                        throw new InvalidOperationException("Windows could not capture the drag.");
                }
                catch (Exception ex) { coordinator.CancelDrag(); ReportMoveError(ex); }
                e.Handled = true;
            };
            border.MouseMove += (_, e) =>
            {
                if (!ReferenceEquals(coordinator.dragCapture, border) || !coordinator.drag.IsPressed) return;
                if (e.LeftButton != MouseButtonState.Pressed) { coordinator.CancelDrag(); return; }
                try
                {
                    var cursor = ReadPointer();
                    bool wasDragging = coordinator.drag.IsDragging;
                    if (coordinator.drag.Move(cursor.X, cursor.Y))
                    {
                        if (!wasDragging) coordinator.StartDragPreview();
                        coordinator.UpdateDragPreview(cursor);
                    }
                }
                catch (Exception ex) { coordinator.CancelDrag(); ReportMoveError(ex); }
                e.Handled = true;
            };
            border.MouseLeftButtonUp += async (_, e) =>
            {
                if (!ReferenceEquals(coordinator.dragCapture, border) || !coordinator.drag.IsPressed) return;
                e.Handled = true;
                try
                {
                    var cursor = ReadPointer();
                    var result = coordinator.FinishDrag(cursor);
                    if (result.Dragged)
                    {
                        if (!ReferenceEquals(result.Destination, result.Source))
                            await coordinator.MoveWindowAsync(result.Target, result.Source, result.Destination!);
                    }
                    else OnWindowClicked(result.Target.Handle);
                }
                catch (Exception ex) { coordinator.CancelDrag(); ReportMoveError(ex); }
            };
            border.LostMouseCapture += (_, _) =>
            {
                if (ReferenceEquals(coordinator.dragCapture, border)) coordinator.CancelDrag();
            };
            border.ContextMenuOpening += (_, e) =>
            {
                if (thumb.IsFiltered || !scatterInteraction.IsOpen || isTransitioning || AnyAnimating ||
                    isScatterActive || coordinator.movingWindow)
                { e.Handled = true; return; }
                coordinator.SelectWindow(thumb.WindowHandle);
                border.ContextMenu = CreateMoveMenu(thumb, border);
            };
            border.ContextMenu = CreateMoveMenu(thumb, border);
        }

        private static POINT ReadPointer()
        {
            if (!Win32Interop.GetCursorPos(out var cursor))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not read the drag position.");
            return cursor;
        }

        private void ShowMoveMenu()
        {
            var view = ViewFor(selectedWindowHandle);
            var thumb = view?.ViewThumbs.FirstOrDefault(t => t.WindowHandle == selectedWindowHandle);
            if (thumb == null || thumb.IsFiltered || !scatterInteraction.IsOpen || isTransitioning || movingWindow || AnyAnimating) return;
            var menu = view!.CreateMoveMenu(thumb, thumb.ClickBorder);
            menu.IsOpen = true;
        }

        private ContextMenu CreateMoveMenu(WindowThumb thumb, FrameworkElement placementTarget)
        {
            int generation = scatterInteraction.Generation;
            var menu = new ContextMenu { PlacementTarget = placementTarget, Placement = PlacementMode.Bottom };
            IntPtr popup = IntPtr.Zero;
            menu.Opened += (_, _) =>
            {
                openMoveMenus.Add(menu);
                previewKey.Release();
                foreach (var view in views) view.ResetPreview();
                popup = (PresentationSource.FromVisual(menu) as HwndSource)?.Handle ?? IntPtr.Zero;
                scatterInteraction.RegisterSurface(popup);
            };
            menu.Closed += (_, _) =>
            {
                openMoveMenus.Remove(menu);
                scatterInteraction.UnregisterSurface(popup);
            };
            var closeItem = new MenuItem { Header = "Close window", IsEnabled = CanCloseWindow(thumb, generation) };
            closeItem.Click += (_, _) =>
            {
                menu.IsOpen = false;
                RequestWindowClose(thumb, generation);
            };
            foreach (var destination in OrderedViews().Where(_ => !thumb.WasMinimized && views.Count > 1))
            {
                var item = new MenuItem
                {
                    Header = $"Move to {destination.DisplayName} ({destination.monitor.Width} x {destination.monitor.Height})",
                    IsEnabled = !ReferenceEquals(destination, this) && destination.enumeratedWindowCount < 20,
                    ToolTip = destination.enumeratedWindowCount >= 20 ? "Display already shows 20 windows" : null
                };
                item.Click += async (_, _) =>
                {
                    menu.IsOpen = false;
                    if (generation == scatterInteraction.Generation && scatterInteraction.IsOpen)
                        await coordinator.MoveWindowAsync(thumb.Identity, this, destination);
                };
                menu.Items.Add(item);
            }
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            menu.Items.Add(closeItem);
            return menu;
        }

        private async System.Threading.Tasks.Task MoveWindowAsync(WindowActivationTarget target, MainWindow source, MainWindow destination)
        {
            if (movingWindow || isTransitioning || !scatterInteraction.IsOpen || isScatterActive || AnyAnimating) return;
            if (!views.Contains(source) || !views.Contains(destination))
            { ReportMoveError(new InvalidOperationException("The move destination is not visible in this overview.")); return; }
            if (ReferenceEquals(source, destination)) return;
            if (moveOperation.IsPending)
            { ReportMoveError(new InvalidOperationException("A previous native move is still pending. Wait for Windows to reply.")); return; }
            if (destination.enumeratedWindowCount >= 20)
            { ReportMoveError(new InvalidOperationException("The destination already shows 20 windows.")); return; }
            try { if (!EnsureTopology()) return; }
            catch (Exception ex) { AbortOverview(ex); return; }
            movingWindow = true;
            using var cancellation = new CancellationTokenSource();
            moveCancellation = cancellation;
            int generation = scatterInteraction.Generation;
            Exception? failure = null;
            try
            {
                CancelDrag();
                previewKey.Release();
                foreach (var view in views) { view.ResetPreview(); view.animationManager.StopAllAnimations(); }
                var topology = monitors.ToArray();
                var from = source.monitor;
                var to = destination.monitor;
                foreach (var view in views) view.MoveStatus.Text = "Moving window...";
                try
                {
                    await moveOperation.RunAsync(token => windowTransfer.Move(target, from, to, topology, token), cancellation.Token);
                }
                catch (TimeoutException)
                {
                    cancellation.Cancel();
                    CancelSessionImmediately();
                    ReportMoveError(new TimeoutException("Windows did not reply in time. Its pending request may still finish later."));
                    return;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
                catch (Exception ex) { failure = ex; }
                if (generation != scatterInteraction.Generation || !scatterInteraction.IsOpen || !EnsureTopology()) return;
                var zOrder = WindowStacking.Capture();
                var windows = EnumerateOverviewWindows();
                foreach (var view in views) view.RebuildThumbnails(zOrder, windows[view.monitor.Handle], settled: true);
                session.Select(IntPtr.Zero);
                if (IsSearching) RefreshSearchResults(target.Handle);
                else SelectWindow(AllThumbs().Any(t => t.WindowHandle == target.Handle) ? target.Handle :
                    AllThumbs().FirstOrDefault()?.WindowHandle ?? IntPtr.Zero);
                if (failure == null) destination.MoveStatus.Text = AllThumbs().Any(t => t.WindowHandle == target.Handle)
                    ? "Window moved" : "Window moved; preview unavailable";
            }
            catch (Exception ex) { AbortOverview(ex); return; }
            finally
            {
                if (ReferenceEquals(moveCancellation, cancellation)) moveCancellation = null;
                movingWindow = false;
            }
            if (failure != null) ReportMoveError(failure);
        }

        private void ReportMoveError(Exception error)
        {
            if (!IsCoordinator) { coordinator.ReportMoveError(error); return; }
            System.Diagnostics.Trace.TraceError($"Window transfer failed: {error}");
            if (reportingMoveError) return;
            bool wasMoving = movingWindow;
            reportingMoveError = movingWindow = true;
            CloseMoveMenus();
            var disabled = views.Where(v => !v.chromeClosed).ToArray();
            try
            {
                foreach (var view in disabled) view.chromeWindow.IsEnabled = false;
                MessageBox.Show(this, $"Windows could not confirm the requested move.\n{error.Message}",
                    "MissionView", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                foreach (var view in disabled.Where(v => !v.chromeClosed)) view.chromeWindow.IsEnabled = true;
                reportingMoveError = false;
                movingWindow = wasMoving;
            }
        }

        private void CloseMoveMenus()
        {
            foreach (var view in views.ToArray())
                foreach (var menu in view.openMoveMenus.ToArray()) menu.IsOpen = false;
        }

        private bool EnsureTopology()
        {
            if (MonitorHelper.SameTopology(monitors, MonitorHelper.GetMonitors())) return true;
            CancelForTopologyChange();
            return false;
        }

        private void QueueTopologyCancellation()
        {
            int generation = scatterInteraction.Generation;
            if (topologyCancellationGeneration == generation) return;
            topologyCancellationGeneration = generation;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (topologyCancellationGeneration == generation) topologyCancellationGeneration = -1;
                if (isClosing || generation != scatterInteraction.Generation) return;
                try
                {
                    if (!MonitorHelper.SameTopology(monitors, MonitorHelper.GetMonitors())) CancelForTopologyChange();
                }
                catch (Exception ex) { AbortOverview(ex); }
            }), DispatcherPriority.Send);
        }

        private void CancelForTopologyChange()
        {
            layoutCalculators.Clear();
            if (scatterInteraction.IsOpen) CancelSessionImmediately();
            else if (IsVisible)
            {
                scatterInteraction.InvalidateSession();
                isTransitioning = true;
                moveCancellation?.Cancel();
                CancelDrag();
                HideAllViews();
                QueueOverviewCleanup();
            }
            else return;
            trayIcon?.ShowBalloonTip(3000, "MissionView",
                "Display settings changed. Open the overview again.", System.Windows.Forms.ToolTipIcon.Info);
        }

        private void CancelSessionImmediately()
        {
            if (!scatterInteraction.TryBeginClose(null, out var previous)) return;
            isTransitioning = true;
            moveCancellation?.Cancel();
            CloseMoveMenus();
            CancelDrag();
            OnSwitchComplete(previous, restore: false, wasMaximized: false);
        }
    }
}
