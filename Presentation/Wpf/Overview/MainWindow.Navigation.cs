using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WindowScatter
{
    public partial class MainWindow
    {
        private bool navigationPending => session.NavigationPending;

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is MenuItem) return;
            if (Keyboard.FocusedElement is OverviewCloseButton && e.Key is Key.Return or Key.Space) return;
            bool escape = e.Key == Key.Escape || (e.Key == Key.System && e.SystemKey == Key.Escape);
            if (coordinator.drag.IsPressed)
            {
                if (escape && !e.IsRepeat) coordinator.CancelDrag();
                e.Handled = true;
                return;
            }
            if (SearchBox.IsKeyboardFocused && (composingSearchText || e.Key is Key.ImeProcessed or Key.DeadCharProcessed))
                return;
            if (escape && IsSearching && scatterInteraction.IsOpen && !isTransitioning)
            {
                if (!e.IsRepeat) coordinator.UpdateSearch("");
                e.Handled = true;
                return;
            }
            if (scatterInteraction.IsOpen && !isScatterActive && !AnyAnimating && !isTransitioning && !coordinator.movingWindow)
            {
                if (OverviewMoveShortcut.Matches(e.Key, e.SystemKey, Keyboard.Modifiers))
                {
                    bool moveKey = (e.Key == Key.System ? e.SystemKey : e.Key) == Key.M;
                    if (!moveKey || views.Count > 1) coordinator.ShowMoveMenu();
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Space && !IsSearching)
                {
                    if (previewKey.Press(e.IsRepeat) && !coordinator.navigationPending) coordinator.activeView?.ExpandPreview();
                    e.Handled = true;
                    return;
                }
                bool editing = IsSearching && SearchBox.IsKeyboardFocused &&
                    (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0 &&
                    e.Key is Key.Left or Key.Right or Key.Up or Key.Down;
                if (!editing && coordinator.Navigate(this, e.Key))
                {
                    e.Handled = true;
                    return;
                }
            }
            else if (SearchBox.IsKeyboardFocused &&
                (e.Key is Key.Tab or Key.Return || (e.Key == Key.Space && !IsSearching)))
            {
                e.Handled = true;
                return;
            }
            if (escape && !e.IsRepeat)
            {
                e.Handled = true;
                RequestDismissScatter();
            }
        }

        private void Window_KeyUp(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is OverviewCloseButton && e.Key == Key.Space) return;
            if (e.Key != Key.Space) return;
            if (!previewKey.IsHeld && (IsSearching || composingSearchText)) return;
            if (previewKey.Release() && scatterInteraction.IsOpen && !isTransitioning && !coordinator.navigationPending)
                foreach (var view in views.Where(v => v.previewExpanded || v.animationManager.IsPreviewAnimating))
                    view.AnimatePreview(IntPtr.Zero, default);
            e.Handled = true;
        }

        private void OnWindowHovered(IntPtr windowHandle)
        {
            if (scatterInteraction.IsOpen && !AnyAnimating && !isTransitioning && !coordinator.drag.IsPressed &&
                !coordinator.movingWindow && !coordinator.navigationPending &&
                (!IsSearching || ViewThumbs.Any(t => t.WindowHandle == windowHandle && !t.IsFiltered)))
            {
                if (!Win32Interop.GetCursorPos(out var cursor))
                {
                    System.Diagnostics.Trace.TraceWarning("Could not read the pointer position for thumbnail selection.");
                    return;
                }
                if (Mouse.MiddleButton == MouseButtonState.Pressed ||
                    cursor.X == coordinator.lastHoverPosition.X && cursor.Y == coordinator.lastHoverPosition.Y) return;
                coordinator.lastHoverPosition = cursor;
                SelectWindow(windowHandle);
            }
        }

        private IReadOnlyList<NavigationTarget> NormalNavigationTargets() => windowThumbs
            .Where(t => !t.IsFiltered).Select(t =>
            new NavigationTarget(t.Identity, new(monitor.Left + t.TargetX, monitor.Top + t.TargetY,
                t.TargetWidth, t.TargetHeight))).Where(t => t.IsValid).ToArray();

        private IReadOnlyList<OverviewDisplaySnapshot> NavigationSnapshot() => views.Select(v =>
            new OverviewDisplaySnapshot(v.monitor.Handle, new(v.monitor.Left, v.monitor.Top, v.monitor.Width, v.monitor.Height),
                v.NormalNavigationTargets(), v.minimizedStrip?.NavigationTargets(visibleOnly: false) ?? [],
                (v.minimizedStrip?.NavigationTargets(visibleOnly: true) ?? []).Select(t => t with
                { Bounds = t.Bounds with { X = t.Bounds.X + v.monitor.Left, Y = t.Bounds.Y + v.monitor.Top } }).ToArray())).ToArray();

        private void ApplyNavigationSelection(OverviewSelection selection) =>
            SelectWindow(selection.Handle, selection.Keyboard, selection.RevealMinimized);

        private void SelectNextWindow(int direction)
        {
            ApplyNavigationSelection(session.SelectTab(NavigationSnapshot(), direction));
        }

        private void SelectWindowInDirection(int dx, int dy)
        {
            ApplyNavigationSelection(session.SelectDirection(NavigationSnapshot(), (activeView ?? this).monitor.Handle, dx, dy));
        }

        private void CancelNavigation()
        {
            session.CancelNavigation();
        }

        private void InvalidateNavigation()
        {
            session.InvalidateNavigation();
        }

        private bool Navigate(MainWindow sender, Key key)
        {
            if (key == Key.Return)
            {
                if (selectedWindowHandle != IntPtr.Zero) OnWindowClicked(selectedWindowHandle);
                return true;
            }
            if (key is not (Key.Tab or Key.Left or Key.Right or Key.Up or Key.Down)) return false;
            if (navigationPending) return true;
            int direction = (Keyboard.Modifiers & ModifierKeys.Shift) == 0 ? 1 : -1;
            void SelectNeighbour()
            {
                if (key == Key.Tab) SelectNextWindow(direction);
                else SelectWindowInDirection(key == Key.Left ? -1 : key == Key.Right ? 1 : 0,
                    key == Key.Up ? -1 : key == Key.Down ? 1 : 0);
            }
            void CompleteNavigation()
            {
                if (Win32Interop.GetCursorPos(out var cursor)) lastHoverPosition = cursor;
                else System.Diagnostics.Trace.TraceWarning("Could not read the pointer position after keyboard navigation.");
                var view = activeView ?? sender;
                if (view.chromeWindow.IsVisible && !view.SearchBox.IsKeyboardFocused) view.FocusOverviewInput();
                if (previewKey.IsHeld) activeView?.ExpandPreview();
            }
            var expanded = views.Where(v => v.previewExpanded || v.animationManager.IsPreviewAnimating).ToArray();
            session.Navigate(expanded.Select(v => new Action<Action>(
                returned => v.AnimatePreview(IntPtr.Zero, default, returned))).ToArray(),
                () => !isTransitioning && !movingWindow && !drag.IsPressed, SelectNeighbour, CompleteNavigation);
            return true;
        }

        private void SelectWindow(IntPtr windowHandle, bool keyboard = false, bool revealMinimized = false)
        {
            if (!IsCoordinator) { coordinator.SelectWindow(windowHandle, keyboard, revealMinimized); return; }
            if (windowHandle != IntPtr.Zero && ViewFor(windowHandle) == null)
                throw new InvalidOperationException("The selected window is no longer available in this overview.");
            bool changed = session.Select(windowHandle);
            foreach (var view in views) view.thumbnailManager?.SetAutomationSelection(windowHandle);
            if (!changed)
            {
                foreach (var view in views)
                    view.minimizedStrip?.SetSelected(windowHandle, reveal: revealMinimized && view == activeView, keyboardFocus: keyboard);
                return;
            }
            activeView = ViewFor(windowHandle) ?? activeView ?? this;
            foreach (var view in views)
            {
                view.minimizedStrip?.SetSelected(windowHandle, reveal: revealMinimized && view == activeView, keyboardFocus: keyboard);
                if (appExposeRequest != null) view.MoveStatus.Text = view.IdleMoveStatus;
                if (view != activeView && (view.previewExpanded || view.animationManager.IsPreviewAnimating))
                    view.AnimatePreview(IntPtr.Zero, default);
                view.thumbnailManager?.SetSelectedWindow(isScatterActive || view.animationManager.IsPreviewAnimating
                    ? IntPtr.Zero : windowHandle);
            }
            RestorePendingCloseStatus();
            if (previewKey.IsHeld && !navigationPending) activeView.ExpandPreview();
        }

        private void ConfigureThumbnailAutomation(WindowThumb thumb)
        {
            var target = (OverviewThumbnail)thumb.ClickBorder;
            int generation = scatterInteraction.Generation;
            target.ConfigureAutomation(
                () => scatterInteraction.IsOpen && generation == scatterInteraction.Generation &&
                    !isClosing && !isScatterActive && !AnyAnimating && !isTransitioning &&
                    !coordinator.movingWindow && !coordinator.navigationPending && !coordinator.drag.IsPressed &&
                    windowThumbs.Contains(thumb),
                selected => coordinator.SelectWindow(selected ? thumb.WindowHandle : IntPtr.Zero),
                () => OnWindowClicked(thumb.WindowHandle));
            target.SetSelected(thumb.WindowHandle == selectedWindowHandle);
        }

        private void ExpandPreview()
        {
            if (minimizedStrip?.Thumbs.Any(t => t.WindowHandle == selectedWindowHandle) == true)
            {
                ResetPreview(releaseKey: false);
                MoveStatus.Text = "Minimized. Press Enter to restore; live preview is not shown.";
                return;
            }
            var thumb = windowThumbs.FirstOrDefault(t => t.WindowHandle == selectedWindowHandle);
            if (thumb == null) return;
            animationManager.StopAllAnimations();
            var bounds = OverviewPreview.Fit(thumb.StartWidth, thumb.StartHeight, monitorPixelWidth,
                OverviewContentHeight, Math.Min(WindowLayoutCalculator.ToolbarHeight * currentDpiScale,
                    monitorPixelHeight * .4), currentDpiScale);
            if (!thumbnailManager.BringThumbnailToFront(thumb.WindowHandle, animationManager))
            {
                AbortOverview(new InvalidOperationException("DWM could not raise the live preview."));
                return;
            }
            previewExpanded = true;
            AnimatePreview(thumb.WindowHandle, new(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        }

        private void AnimatePreview(IntPtr selected, PreviewBounds bounds, Action? returned = null)
        {
            int generation = scatterInteraction.Generation;
            thumbnailManager.SetPreviewAnimating(true);
            thumbnailManager.BeginTransition(() => animationManager.StartPreviewAnimation(selected, bounds, success =>
            {
                if (isClosing || !scatterInteraction.IsOpen || generation != scatterInteraction.Generation) return;
                if (!success)
                {
                    AbortOverview(new InvalidOperationException("DWM could not animate the live preview."));
                    return;
                }
                if (isClosing || isTransitioning || !IsVisible || !scatterInteraction.IsOpen) return;
                previewExpanded = selected != IntPtr.Zero && previewKey.IsHeld && ReferenceEquals(this, coordinator.activeView);
                if (selected == IntPtr.Zero)
                {
                    try { thumbnailManager.RestoreSourceStacking(animationManager); }
                    catch (Exception ex) { AbortOverview(ex); return; }
                }
                thumbnailManager.CompleteTransition(currentDpiScale, selectedWindowHandle);
                animationManager.StartKeepAlive();
                returned?.Invoke();
            }));
        }

        private void ResetPreview(bool releaseKey = true, bool restoreSourceStacking = false)
        {
            CancelNavigation();
            if (releaseKey) previewKey.Release();
            if (!previewExpanded && !animationManager.IsPreviewAnimating) return;
            animationManager.StopAllAnimations();
            previewExpanded = false;
            foreach (var thumb in windowThumbs)
            {
                thumb.CurrentX = thumb.TargetX;
                thumb.CurrentY = thumb.TargetY;
                thumb.CurrentWidth = thumb.TargetWidth;
                thumb.CurrentHeight = thumb.TargetHeight;
                if (!animationManager.UpdateThumbnailPosition(thumb))
                    System.Diagnostics.Trace.TraceError("DWM could not restore a thumbnail to its overview slot.");
            }
            if (restoreSourceStacking) thumbnailManager.RestoreSourceStacking(animationManager);
            if (!isTransitioning && scatterInteraction.IsOpen && Visibility == Visibility.Visible)
            {
                thumbnailManager.CompleteTransition(currentDpiScale, selectedWindowHandle);
                animationManager.StartKeepAlive();
            }
            else thumbnailManager.SyncClickBordersToThumbs(currentDpiScale);
        }
    }
}
