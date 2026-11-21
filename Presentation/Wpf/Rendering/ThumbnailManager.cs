using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using static WindowScatter.Win32Interop;

namespace WindowScatter
{
    internal class ThumbnailManager
    {
        private readonly Canvas scatterCanvas;
        private readonly List<WindowThumb> windowThumbs;
        private readonly IntPtr ownerWindowHandle;
        private readonly Action<IntPtr> onWindowClicked;
        private readonly Action<IntPtr> onWindowHovered;
        private readonly Func<IntPtr, (int Result, IntPtr Handle)> register;
        public Action<WindowThumb>? ConfigureInput { get; set; }
        public IReadOnlyList<IntPtr> SourceOrder { get; set; } = Array.Empty<IntPtr>();

        public ThumbnailManager(Canvas scatterCanvas, List<WindowThumb> windowThumbs,
            IntPtr ownerWindowHandle, Action<IntPtr> onWindowClicked, Action<IntPtr> onWindowHovered,
            Func<IntPtr, (int Result, IntPtr Handle)>? register = null)
        {
            this.scatterCanvas = scatterCanvas;
            this.windowThumbs = windowThumbs;
            this.ownerWindowHandle = ownerWindowHandle;
            this.onWindowClicked = onWindowClicked;
            this.onWindowHovered = onWindowHovered;
            this.register = register ?? (source =>
            {
                int result = DwmRegisterThumbnail(ownerWindowHandle, source, out var thumbnail);
                return (result, thumbnail);
            });
        }

        /// <param name="dpiScale">Effective DPI scale of the target monitor (dpi/96).
        /// Layout coordinates are physical pixels (DWM space); WPF click targets
        /// live in DIPs, so geometry is divided by this scale (#10).</param>
        public void RegisterThumbnails(List<WindowLayout> layouts, WindowAnimationManager animationManager,
            double dpiScale = 1.0, bool settled = false)
        {
            if (dpiScale <= 0) dpiScale = 1.0;

            foreach (var layout in WindowStacking.BackToFront(layouts, l => l.Window.Handle, SourceOrder))
            {
                var win = layout.Window;
                bool wasMinimized = IsIconic(win.Handle);
                if (wasMinimized) continue;

                var (res, thumbHandle) = register(win.Handle);

                if (res != 0 || thumbHandle == IntPtr.Zero)
                {
                    System.Diagnostics.Trace.TraceWarning($"DWM could not register an overview thumbnail: 0x{res:X8}.");
                    continue;
                }

                PhysicalWindowRect currentRect = win.OriginalRect;
                double startX = currentRect.Left;
                double startY = currentRect.Top;
                double startW = currentRect.Right - currentRect.Left;
                double startH = currentRect.Bottom - currentRect.Top;

                double targetW = layout.Width / dpiScale;
                double targetH = layout.Height / dpiScale;
                double targetX = layout.X / dpiScale;
                double targetY = layout.Y / dpiScale;

                var clickBorder = new OverviewThumbnail(win.Title)
                {
                    Background = System.Windows.Media.Brushes.Transparent,
                    Width = targetW,
                    Height = targetH,
                    Cursor = Cursors.Hand,
                    IsHitTestVisible = false
                };

                Canvas.SetLeft(clickBorder, targetX);
                Canvas.SetTop(clickBorder, targetY);

                var highlightBorder = new Border
                {
                    Width = targetW + 10,
                    Height = targetH + 10,
                    BorderThickness = new System.Windows.Thickness(3),
                    BorderBrush = SharedBrushes.Mint,
                    CornerRadius = new System.Windows.CornerRadius(8),
                    Opacity = 0,
                    IsHitTestVisible = false,
                    Child = new Border
                    {
                        BorderBrush = Brushes.Black,
                        BorderThickness = new System.Windows.Thickness(2),
                        CornerRadius = new System.Windows.CornerRadius(5)
                    }
                };

                Canvas.SetLeft(highlightBorder, targetX - 5);
                Canvas.SetTop(highlightBorder, targetY - 5);

                var titleLabel = new TextBlock
                {
                    Text = TrimTitle(win.Title),
                    Width = targetW,
                    Height = 20,
                    ClipToBounds = true,
                    Foreground = Brushes.White,
                    Background = SharedBrushes.Graphite,
                    Padding = new Thickness(6, 0, 6, 0),
                    FontSize = 13,
                    FontFamily = new FontFamily("Segoe UI Variable, Segoe UI"),
                    FontWeight = System.Windows.FontWeights.Normal,
                    TextAlignment = System.Windows.TextAlignment.Center,
                    TextTrimming = System.Windows.TextTrimming.CharacterEllipsis,
                    Opacity = 0,
                    IsHitTestVisible = false
                };

                Canvas.SetLeft(titleLabel, targetX + (targetW - titleLabel.Width) / 2);
                Canvas.SetTop(titleLabel, targetY + targetH + 8);

                var thumb = new WindowThumb
                {
                    WindowHandle = win.Handle,
                    ThumbnailHandle = thumbHandle,
                    WasMinimized = wasMinimized,
                    WasMaximized = win.WasMaximized,
                    Identity = win.Identity,
                    ClickBorder = clickBorder,
                    HighlightBorder = highlightBorder,
                    TitleLabel = titleLabel,

                    StartX = startX,
                    StartY = startY,
                    StartWidth = startW,
                    StartHeight = startH,

                    TargetX = layout.X,
                    TargetY = layout.Y,
                    TargetWidth = layout.Width,
                    TargetHeight = layout.Height,

                    CurrentX = settled ? layout.X : startX,
                    CurrentY = settled ? layout.Y : startY,
                    CurrentWidth = settled ? layout.Width : startW,
                    CurrentHeight = settled ? layout.Height : startH
                };

                if (!animationManager.UpdateThumbnailPosition(thumb))
                {
                    int cleanup = DwmUnregisterThumbnail(thumbHandle);
                    System.Diagnostics.Trace.TraceWarning($"DWM rejected an overview thumbnail's initial bounds (cleanup 0x{cleanup:X8}).");
                    continue;
                }

                clickBorder.Tag = thumb.WindowHandle;
                if (ConfigureInput == null) clickBorder.MouseLeftButtonUp += ClickBorder_MouseDown;
                clickBorder.MouseMove += ClickBorder_MouseMove;

                scatterCanvas.Children.Add(highlightBorder);
                scatterCanvas.Children.Add(titleLabel);
                scatterCanvas.Children.Add(clickBorder);
                windowThumbs.Add(thumb);
                ConfigureInput?.Invoke(thumb);
            }
            var logicalOrder = layouts.Select((l, i) => (l.Window.Handle, i)).ToDictionary(x => x.Handle, x => x.i);
            windowThumbs.Sort((a, b) => logicalOrder[a.WindowHandle].CompareTo(logicalOrder[b.WindowHandle]));
            ApplyPalette(SystemParameters.HighContrast, SystemParameters.ClientAreaAnimation);
        }

        internal void ApplyPalette(bool highContrast, bool effects)
        {
            foreach (var thumb in windowThumbs)
            {
                if (thumb.HighlightBorder is { } frame)
                {
                    frame.BorderBrush = highContrast ? SystemColors.HighlightBrush : SharedBrushes.Mint;
                    if (frame.Child is Border inner)
                        inner.BorderBrush = highContrast ? SystemColors.WindowBrush : SharedBrushes.Graphite;
                    frame.Effect = !highContrast && effects
                        ? new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = .35, Color = Colors.Black }
                        : null;
                }
                if (thumb.TitleLabel is { } title)
                {
                    title.Foreground = highContrast ? SystemColors.WindowTextBrush : SharedBrushes.Text;
                    title.Background = highContrast ? SystemColors.WindowBrush : SharedBrushes.Graphite;
                    title.Effect = null;
                }
            }
        }

        /// <summary>
        /// Re-aligns WPF click/highlight borders with the live DWM thumbnail
        /// positions. Called on the UI thread once the scatter animation settles,
        /// so hit-testing matches what the user actually sees (#10). Must use the
        /// same DPI scale that was passed to <see cref="RegisterThumbnails"/>.
        /// </summary>
        public void SyncClickBordersToThumbs(double dpiScale)
        {
            if (dpiScale <= 0) dpiScale = 1.0;

            foreach (var thumb in windowThumbs)
            {
                if (thumb.ClickBorder == null) continue;

                double x = thumb.CurrentX / dpiScale;
                double y = thumb.CurrentY / dpiScale;
                double w = thumb.CurrentWidth / dpiScale;
                double h = thumb.CurrentHeight / dpiScale;

                thumb.ClickBorder.Width = w;
                thumb.ClickBorder.Height = h;
                Canvas.SetLeft(thumb.ClickBorder, x);
                Canvas.SetTop(thumb.ClickBorder, y);

                if (thumb.HighlightBorder != null)
                {
                    thumb.HighlightBorder.Width = w + 10;
                    thumb.HighlightBorder.Height = h + 10;
                    Canvas.SetLeft(thumb.HighlightBorder, x - 5);
                    Canvas.SetTop(thumb.HighlightBorder, y - 5);
                }

                if (thumb.TitleLabel != null)
                {
                    thumb.TitleLabel.Width = w;
                    Canvas.SetLeft(thumb.TitleLabel, x + (w - thumb.TitleLabel.Width) / 2);
                    Canvas.SetTop(thumb.TitleLabel, y + h + 8);
                }
            }
        }

        public void SetSelectedWindow(IntPtr windowHandle)
        {
            foreach (var thumb in windowThumbs)
            {
                bool isSelected = thumb.WindowHandle == windowHandle;

                if (thumb.HighlightBorder != null)
                    thumb.HighlightBorder.Opacity = isSelected ? 1 : 0;

                if (thumb.TitleLabel != null)
                    thumb.TitleLabel.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        public void SetAutomationSelection(IntPtr windowHandle)
        {
            foreach (var thumb in windowThumbs)
                if (thumb.ClickBorder is OverviewThumbnail target)
                    target.SetSelected(thumb.WindowHandle == windowHandle);
        }

        public void BeginTransition(Action startTransition)
        {
            foreach (var thumb in windowThumbs)
            {
                if (thumb.HighlightBorder != null)
                    thumb.HighlightBorder.Opacity = 0;
            }
            startTransition();
        }

        public void ShowLabels(bool show)
        {
            foreach (var thumb in windowThumbs)
            {
                if (thumb.TitleLabel != null)
                    thumb.TitleLabel.Opacity = show ? 1 : 0;
            }
        }

        public void SetPreviewAnimating(bool animating)
        {
            ShowLabels(!animating);
            foreach (var thumb in windowThumbs)
                if (thumb.ClickBorder != null) thumb.ClickBorder.IsHitTestVisible = !animating;
        }

        public void CleanupAllThumbnails()
        {
            foreach (var thumb in windowThumbs)
            {
                try
                {
                    if (thumb.ThumbnailHandle != IntPtr.Zero)
                        DwmUnregisterThumbnail(thumb.ThumbnailHandle);
                }
                catch { }
            }

            scatterCanvas.Children.Clear();
            windowThumbs.Clear();
        }

        public bool BringThumbnailToFront(IntPtr windowHandle, WindowAnimationManager animationManager)
        {
            var clickedThumb = windowThumbs.FirstOrDefault(t => t.WindowHandle == windowHandle);
            if (clickedThumb == null)
                return false;

            scatterCanvas.Children.Remove(clickedThumb.ClickBorder);
            scatterCanvas.Children.Remove(clickedThumb.HighlightBorder);
            scatterCanvas.Children.Remove(clickedThumb.TitleLabel);
            scatterCanvas.Children.Add(clickedThumb.HighlightBorder);
            scatterCanvas.Children.Add(clickedThumb.TitleLabel);
            scatterCanvas.Children.Add(clickedThumb.ClickBorder);

            // DWM thumbnail order follows registration order, not canvas Z-order.
            if (clickedThumb.ThumbnailHandle != IntPtr.Zero)
            {
                int result = DwmUnregisterThumbnail(clickedThumb.ThumbnailHandle);
                if (result < 0)
                {
                    System.Diagnostics.Trace.TraceError($"DWM could not unregister the foreground thumbnail: 0x{result:X8}.");
                    return false;
                }
                clickedThumb.ThumbnailHandle = IntPtr.Zero;
            }

            IntPtr newThumb;
            int registration = DwmRegisterThumbnail(ownerWindowHandle, clickedThumb.WindowHandle, out newThumb);
            if (registration >= 0 && newThumb != IntPtr.Zero)
            {
                clickedThumb.ThumbnailHandle = newThumb;
                if (animationManager.UpdateThumbnailPosition(clickedThumb)) return true;
                System.Diagnostics.Trace.TraceError("DWM rejected the raised thumbnail's destination.");
                return false;
            }
            System.Diagnostics.Trace.TraceError($"DWM could not raise the foreground thumbnail: 0x{registration:X8}.");
            return false;
        }

        public void RestoreSourceStacking(WindowAnimationManager animationManager)
        {
            foreach (var thumb in WindowStacking.BackToFront(windowThumbs, t => t.WindowHandle, SourceOrder))
                if (!BringThumbnailToFront(thumb.WindowHandle, animationManager))
                    throw new InvalidOperationException("DWM could not restore the source window stacking order.");
        }

        public void ReregisterAllThumbnails(WindowAnimationManager animationManager)
        {
            foreach (var thumb in windowThumbs)
            {
                if (thumb.ThumbnailHandle == IntPtr.Zero)
                {
                    IntPtr thumbHandle;
                    if (DwmRegisterThumbnail(ownerWindowHandle, thumb.WindowHandle, out thumbHandle) == 0)
                    {
                        thumb.ThumbnailHandle = thumbHandle;
                    }
                }

                animationManager.UpdateThumbnailPosition(thumb);
            }
        }

        private void ClickBorder_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var border = sender as Border;
            if (border?.Tag is IntPtr windowHandle)
            {
                onWindowClicked?.Invoke(windowHandle);
            }

            e.Handled = true;
        }

        private void ClickBorder_MouseMove(object sender, MouseEventArgs e)
        {
            var border = sender as Border;
            if (border?.Tag is IntPtr windowHandle)
            {
                onWindowHovered?.Invoke(windowHandle);
            }
        }

        private static string TrimTitle(string title)
        {
            title = title.Trim();
            return title.Length <= 80 ? title : title.Substring(0, 77) + "...";
        }
    }
}
