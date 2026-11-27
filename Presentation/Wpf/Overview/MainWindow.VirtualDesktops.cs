using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static WindowScatter.Win32Interop;

namespace WindowScatter;

public partial class MainWindow
{
    private readonly StackPanel virtualDesktopButtons = new() { Orientation = Orientation.Horizontal };
    private ScrollViewer? virtualDesktopStrip;
    private DesktopSnapshot? renderedDesktops;

    private bool CanChangeDesktop => scatterInteraction.IsOpen && !BlocksThumbnailInput &&
        !coordinator.isClosing && !isTransitioning && !isScatterActive && !AnyAnimating && !coordinator.movingWindow &&
        !services.Desktops.Pending && services.Desktops.Snapshot is not null;

    private void ConfigureVirtualDesktopStrip()
    {
        var strip = virtualDesktopStrip = new ScrollViewer
        {
            Content = virtualDesktopButtons, Visibility = Visibility.Collapsed, Focusable = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        AutomationProperties.SetName(strip, "Virtual desktops");
        Panel.SetZIndex(strip, 2);
        ChromeCanvas.Children.Add(strip);
        Canvas.SetTop(strip, 64);
    }

    private void LayoutVirtualDesktopStrip(double width)
    {
        var strip = virtualDesktopStrip;
        if (strip?.Visibility != Visibility.Visible) return;
        strip.MaxWidth = Math.Max(0, width - 24);
        strip.Measure(new Size(strip.MaxWidth, double.PositiveInfinity));
        Canvas.SetLeft(strip, Math.Max(0, (width - strip.DesiredSize.Width) / 2));
        double searchTop = Canvas.GetTop(SearchPanel);
        Canvas.SetTop(SearchPanel, Math.Max(searchTop, Canvas.GetTop(strip) + strip.DesiredSize.Height + 8));
    }

    private void RenderVirtualDesktopStrip()
    {
        var strip = virtualDesktopStrip!;
        var state = services.Desktops;
        var snapshot = state.Snapshot;
        if (!ReferenceEquals(snapshot, renderedDesktops))
        {
            renderedDesktops = snapshot;
            virtualDesktopButtons.Children.Clear();
            if (snapshot is not null)
            {
                for (int i = 0; i < snapshot.Ordered.Length; i++)
                {
                    Guid id = snapshot.Ordered[i];
                    bool current = id == snapshot.Active;
                    string label = $"Desktop {i + 1}";
                    var face = new StackPanel();
                    face.Children.Add(new TextBlock { Text = label, TextAlignment = TextAlignment.Center });
                    face.Children.Add(new TextBlock
                    {
                        Text = "Active", TextAlignment = TextAlignment.Center, FontSize = 11,
                        Visibility = current ? Visibility.Visible : Visibility.Hidden
                    });
                    var button = new Button
                    {
                        Content = face, Tag = id, MinWidth = 86, MinHeight = 46, Margin = new(3),
                        IsEnabled = !current
                    };
                    AutomationProperties.SetName(button, current ? $"{label}, Active" : label);
                    button.Click += async (_, _) => await coordinator.ChangeVirtualDesktopAsync(id);
                    virtualDesktopButtons.Children.Add(button);
                }
            }
        }
        strip.Visibility = snapshot is null ? Visibility.Collapsed : Visibility.Visible;
        strip.IsEnabled = !state.Pending;
        LayoutSearchPanel(chromeWindow.ActualWidth, chromeWindow.ActualHeight);
    }

    private (bool OverStrip, Guid? Target) VirtualDesktopAt(POINT cursor)
    {
        foreach (var view in views)
        {
            if (view.virtualDesktopStrip is not { IsVisible: true } strip ||
                !DragPreviewGeometry.Contains(view.DragMonitorBounds, cursor.X, cursor.Y)) continue;
            var point = strip.PointFromScreen(new Point(cursor.X, cursor.Y));
            if (!new Rect(strip.RenderSize).Contains(point)) continue;
            if (CanChangeDesktop)
                for (DependencyObject? hit = strip.InputHitTest(point) as DependencyObject;
                    hit is Visual; hit = VisualTreeHelper.GetParent(hit))
                    if (hit is Button { IsEnabled: true, Tag: Guid id }) return (true, id);
            return (true, null);
        }
        return default;
    }

    private void AddVirtualDesktopMoveMenu(ContextMenu menu, WindowThumb thumb)
    {
        if (!CanChangeDesktop || thumb.WasMinimized || services.Desktops.Snapshot is not { } snapshot) return;
        var move = new MenuItem { Header = "Move to desktop" };
        for (int i = 0; i < snapshot.Ordered.Length; i++)
        {
            Guid target = snapshot.Ordered[i];
            if (target == snapshot.Active) continue;
            var item = new MenuItem { Header = $"Desktop {i + 1}" };
            item.Click += async (_, _) =>
            {
                menu.IsOpen = false;
                if (ReferenceEquals(snapshot, services.Desktops.Snapshot))
                    await coordinator.ChangeVirtualDesktopAsync(target, thumb.Identity);
            };
            move.Items.Add(item);
        }
        if (move.Items.Count != 0) menu.Items.Add(move);
    }

    private async Task ChangeVirtualDesktopAsync(Guid target, WindowActivationTarget? window = null)
    {
        if (!CanChangeDesktop) return;
        if (window is { } selection && (windowActivation.Capture(selection.Handle) != selection ||
            !AllThumbs().Any(thumb => thumb.Identity == selection && !thumb.IsFiltered && !thumb.WasMinimized))) return;
        int generation = scatterInteraction.Generation;
        try
        {
            var task = services.Desktops.ChangeAsync(target, window);
            foreach (var view in views) view.RenderVirtualDesktopStrip();
            var result = await task;
            if (!scatterInteraction.IsOpen || generation != scatterInteraction.Generation) return;
            foreach (var view in views) view.RenderVirtualDesktopStrip();
            if (result == DesktopChange.Confirmed)
                CancelSessionImmediately();
            else if (result == DesktopChange.Unconfirmed)
                ReportMoveError(new InvalidOperationException(services.Desktops.Unavailable));
            else if (result == DesktopChange.Rejected)
                ReportMoveError(new InvalidOperationException("Windows rejected the desktop action."));
        }
        catch (Exception error)
        {
            if (generation != scatterInteraction.Generation || !scatterInteraction.IsOpen)
            {
                System.Diagnostics.Trace.TraceError($"Desktop action failed after Overview closed: {error}");
                return;
            }
            services.Desktops.Close();
            foreach (var view in views) view.RenderVirtualDesktopStrip();
            ReportMoveError(error);
        }
    }
}
