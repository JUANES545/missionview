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
        private string IdleMoveStatus => windowThumbs.Count < enumeratedWindowCount ? "Some previews unavailable" : "";
        private IEnumerable<MainWindow> OrderedViews() => views.OrderBy(v => v.monitor.Left).ThenBy(v => v.monitor.Top);
        private IEnumerable<WindowThumb> AllThumbs() => OrderedViews().SelectMany(v => v.windowThumbs);
        private MainWindow? ViewFor(IntPtr handle) => views.FirstOrDefault(v => v.windowThumbs.Any(t => t.WindowHandle == handle));

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
            DropHint.Width = Math.Max(0, Width - 16);
            DropHint.Height = Math.Max(0, Height - 80);
            MoveStatus.Width = Math.Max(0, Width - 88);
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

        private void RebuildThumbnails(IReadOnlyList<IntPtr> sourceOrder, List<WindowInfo> windows, bool settled)
        {
            animationManager.StopAllAnimations();
            thumbnailManager.CleanupAllThumbnails();
            enumeratedWindowCount = windows.Count;
            var layouts = new WindowLayoutCalculator().CalculateLayouts(windows, monitor.Width, monitor.Height, currentDpiScale);
            thumbnailManager.SourceOrder = sourceOrder;
            thumbnailManager.RegisterThumbnails(layouts, animationManager, currentDpiScale, settled);
            windowSwitcher = new WindowSwitcher(thumbnailManager, animationManager, windowThumbs, layouts);
            ChromeCanvas.Visibility = Visibility.Visible;
            EmptyMessage.Text = windows.Count == 0 ? "No windows on this display.\nDrop a window here." :
                "Window previews unavailable.\nDrop a window here.";
            EmptyMessage.Visibility = windowThumbs.Count == 0 ? Visibility.Visible : Visibility.Hidden;
            MoveStatus.Text = IdleMoveStatus;
            if (!settled) return;
            thumbnailManager.SyncClickBordersToThumbs(currentDpiScale);
            thumbnailManager.SetPreviewAnimating(false);
            animationManager.StartKeepAlive();
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
            CloseMoveMenus();
            foreach (var view in views.ToArray())
            {
                if (!view.chromeClosed) view.chromeWindow.Hide();
                view.Hide();
            }
        }

        private void CleanupView()
        {
            animationManager?.StopAllAnimations();
            thumbnailManager?.CleanupAllThumbnails();
            desktopCaptureManager?.Cleanup();
            previewExpanded = false;
            if (dragThumbnail != IntPtr.Zero)
            {
                int result = DwmUnregisterThumbnail(dragThumbnail);
                if (result < 0) System.Diagnostics.Trace.TraceError($"DWM drag cleanup failed: 0x{result:X8}.");
                dragThumbnail = IntPtr.Zero;
            }
        }

        private void CloseAllViews(WindowActivationTarget? selection)
        {
            if (isClosing || isScatterActive || AnyAnimating || isTransitioning || movingWindow) return;
            if (!scatterInteraction.TryBeginClose(selection, out var target)) return;
            isTransitioning = true;
            CloseMoveMenus();
            int generation = scatterInteraction.Generation;
            bool maximized = ViewFor(target.Handle)?.windowThumbs.FirstOrDefault(t => t.WindowHandle == target.Handle)?.WasMaximized == true;
            var group = new OverviewReturnGroup(views.Count,
                () => !isClosing && generation == scatterInteraction.Generation,
                () => OnSwitchComplete(target, selection.HasValue, maximized));
            try
            {
                CancelDrag();
                var sourceOrder = WindowStacking.Capture();
                foreach (var view in views)
                {
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

        private bool Navigate(MainWindow sender, Key key)
        {
            if (key == Key.Tab)
            {
                var stops = new List<object>();
                foreach (var view in OrderedViews())
                {
                    stops.AddRange(view.windowThumbs);
                    stops.Add(view.DesktopButton);
                }
                object? current = stops.OfType<Button>().FirstOrDefault(b => b.IsKeyboardFocusWithin);
                current ??= stops.OfType<WindowThumb>().FirstOrDefault(t => t.WindowHandle == selectedWindowHandle);
                int direction = (Keyboard.Modifiers & ModifierKeys.Shift) == 0 ? 1 : -1;
                int index = current == null ? direction > 0 ? -1 : 0 : stops.IndexOf(current);
                object next = stops[(index + direction + stops.Count) % stops.Count];
                if (next is Button button) button.Focus();
                else if (next is WindowThumb thumb)
                {
                    SelectWindow(thumb.WindowHandle);
                    activeView!.chromeWindow.Activate();
                    activeView.chromeWindow.Focus();
                }
                return true;
            }
            if (key is Key.Left or Key.Right or Key.Up or Key.Down)
            {
                SelectWindowInDirection(key == Key.Left ? -1 : key == Key.Right ? 1 : 0,
                    key == Key.Up ? -1 : key == Key.Down ? 1 : 0);
                (activeView ?? sender).chromeWindow.Activate();
                (activeView ?? sender).chromeWindow.Focus();
                return true;
            }
            if (key == Key.Return)
            {
                if (selectedWindowHandle != IntPtr.Zero) OnWindowClicked(selectedWindowHandle);
                return true;
            }
            return false;
        }

        private void ConfigureThumbnailInput(WindowThumb thumb)
        {
            var border = thumb.ClickBorder;
            AutomationProperties.SetName(border, thumb.TitleLabel.Text);
            border.MouseLeftButtonDown += (_, e) =>
            {
                if (!scatterInteraction.IsOpen || AnyAnimating || isScatterActive || isTransitioning || coordinator.movingWindow) return;
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
                    var destination = views.FirstOrDefault(v => cursor.X >= v.monitor.Left && cursor.X < v.monitor.Right &&
                        cursor.Y >= v.monitor.Top && cursor.Y < v.monitor.Bottom);
                    var source = coordinator.dragSource!;
                    var result = coordinator.drag.Release();
                    coordinator.CancelDrag();
                    if (result.Dragged)
                    {
                        if (destination != null && !ReferenceEquals(destination, source))
                            await coordinator.MoveWindowAsync(result.Target, source, destination);
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
                if (!scatterInteraction.IsOpen || isTransitioning || AnyAnimating || isScatterActive || coordinator.movingWindow)
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

        private void StartDragPreview()
        {
            previewKey.Release();
            foreach (var view in views)
            {
                view.ResetPreview();
                view.animationManager.StopAllAnimations();
                view.thumbnailManager.BeginTransition(() => { });
                int result = DwmRegisterThumbnail(new WindowInteropHelper(view).Handle, drag.Target.Handle, out view.dragThumbnail);
                if (result < 0 || view.dragThumbnail == IntPtr.Zero)
                    throw new COMException("DWM could not create the drag preview.", result < 0 ? result : unchecked((int)0x80004005));
                var props = new DWM_THUMBNAIL_PROPERTIES { dwFlags = DWM_TNP_VISIBLE, fVisible = false };
                int update = DwmUpdateThumbnailProperties(view.dragThumbnail, ref props);
                if (update < 0) throw new COMException("DWM could not initialize the drag preview.", update);
            }
            if (dragThumb != null && !dragSource!.animationManager.UpdateThumbnailPosition(dragThumb, 72))
                throw new InvalidOperationException("DWM could not mark the dragged thumbnail.");
        }

        private void UpdateDragPreview(POINT cursor)
        {
            foreach (var view in views)
            {
                bool inside = cursor.X >= view.monitor.Left && cursor.X < view.monitor.Right &&
                    cursor.Y >= view.monitor.Top && cursor.Y < view.monitor.Bottom;
                bool valid = inside && !ReferenceEquals(view, dragSource) && view.enumeratedWindowCount < 20;
                view.DropHint.Visibility = valid ? Visibility.Visible : Visibility.Hidden;
                view.MoveStatus.Text = inside ? ReferenceEquals(view, dragSource) ? "Move to another display; Esc cancels" :
                    valid ? "Release to move here" : "Display already shows 20 windows" : view.IdleMoveStatus;
                double width = Math.Min(dragThumb!.TargetWidth, view.monitor.Width * .7);
                double height = width * dragThumb.TargetHeight / dragThumb.TargetWidth;
                if (height > view.monitor.Height * .7) { width *= view.monitor.Height * .7 / height; height = view.monitor.Height * .7; }
                int left = (int)Math.Clamp(cursor.X - view.monitor.Left - width * dragGrabX, 0, view.monitor.Width - width);
                int top = (int)Math.Clamp(cursor.Y - view.monitor.Top - height * dragGrabY, 0, view.monitor.Height - height);
                var props = new DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
                    fVisible = inside, opacity = 230, fSourceClientAreaOnly = true,
                    rcDestination = new RECT { Left = left, Top = top, Right = left + (int)width, Bottom = top + (int)height }
                };
                int result = DwmUpdateThumbnailProperties(view.dragThumbnail, ref props);
                if (result < 0) throw new COMException("DWM could not move the drag preview.", result);
            }
        }

        private void CancelDrag()
        {
            drag.Cancel();
            var capture = dragCapture;
            dragCapture = null;
            if (capture?.IsMouseCaptured == true) capture.ReleaseMouseCapture();
            foreach (var view in views.ToArray())
            {
                bool hadGhost = view.dragThumbnail != IntPtr.Zero;
                if (hadGhost)
                {
                    int result = DwmUnregisterThumbnail(view.dragThumbnail);
                    if (result < 0) System.Diagnostics.Trace.TraceError($"DWM drag cleanup failed: 0x{result:X8}.");
                    view.dragThumbnail = IntPtr.Zero;
                }
                view.DropHint.Visibility = Visibility.Hidden;
                if (view.monitor != null) view.MoveStatus.Text = view.IdleMoveStatus;
                if (hadGhost && scatterInteraction.IsOpen && !isTransitioning && !isClosing && !movingWindow)
                {
                    if (dragThumb != null && ReferenceEquals(view, dragSource) && !view.animationManager.UpdateThumbnailPosition(dragThumb))
                        System.Diagnostics.Trace.TraceError("DWM could not restore the source thumbnail after dragging.");
                    view.thumbnailManager.SetSelectedWindow(selectedWindowHandle);
                    view.animationManager.StartKeepAlive();
                }
            }
            dragSource = null;
            dragThumb = null;
        }

        private void ShowMoveMenu()
        {
            var view = ViewFor(selectedWindowHandle);
            var thumb = view?.windowThumbs.FirstOrDefault(t => t.WindowHandle == selectedWindowHandle);
            if (thumb == null || !scatterInteraction.IsOpen || isTransitioning || movingWindow || AnyAnimating) return;
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
            foreach (var destination in OrderedViews())
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
            return menu;
        }

        private async System.Threading.Tasks.Task MoveWindowAsync(WindowActivationTarget target, MainWindow source, MainWindow destination)
        {
            if (movingWindow || isTransitioning || !scatterInteraction.IsOpen || isScatterActive || AnyAnimating) return;
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
                var windows = windowEnumerator.EnumerateByMonitor(monitors);
                foreach (var view in views) view.RebuildThumbnails(zOrder, windows[view.monitor.Handle], settled: true);
                selection = IntPtr.Zero;
                SelectWindow(AllThumbs().Any(t => t.WindowHandle == target.Handle) ? target.Handle :
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
