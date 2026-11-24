namespace WindowScatter;

public partial class MainWindow
{
    private readonly Dictionary<IntPtr, WindowLayoutCalculator> layoutCalculators = new();
    private List<MonitorHelper.MonitorBounds> layoutTopology = new();
    private IntPtr[] layoutDisplays = [];
    private string? layoutScope;
    private ApplicationIdentity? layoutApplication;
    private bool layoutIncludesMinimized;

    private WindowLayoutCalculator LayoutCalculatorForDisplay()
    {
        if (!coordinator.layoutCalculators.TryGetValue(monitor.Handle, out var calculator))
            coordinator.layoutCalculators.Add(monitor.Handle, calculator = new());
        return calculator;
    }

    private void RefreshOverviewPresentation(bool highContrast, bool systemAnimation)
    {
        bool motion = settings.AllowsMotion(systemAnimation);
        animationManager?.SetMotionEnabled(motion);
        thumbnailManager?.ApplyPalette(highContrast, motion);
        DimOverlay.Background = highContrast ? System.Windows.SystemColors.WindowBrush
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(
                (byte)Math.Round(settings.BackgroundDimming * 255 / 100), 32, 37, 34));
    }

    private void InitializeOverviewViews(IReadOnlyList<MonitorHelper.MonitorBounds> topology, IntPtr pointerMonitor)
    {
        // Keep the full topology for ownership and hotplug; only visible displays receive views.
        monitors = topology.OrderBy(m => m.Left).ThenBy(m => m.Top).ToList();
        var visible = MonitorHelper.OverviewMonitors(monitors, settings.OverviewMonitorScope, pointerMonitor);
        var displays = visible.Select(m => m.Handle).OrderBy(h => h.ToInt64()).ToArray();
        var application = appExposeRequest?.Application;
        if (!MonitorHelper.SameTopology(layoutTopology, monitors) || !layoutDisplays.SequenceEqual(displays) ||
            layoutScope != settings.OverviewMonitorScope || layoutIncludesMinimized != settings.ShowMinimizedWindows ||
            (layoutApplication == null ? application != null : application == null || !layoutApplication.Matches(application)))
            layoutCalculators.Clear();
        layoutTopology = monitors.Select(m => new MonitorHelper.MonitorBounds
        {
            Handle = m.Handle, Left = m.Left, Top = m.Top, Width = m.Width, Height = m.Height,
            DpiScale = m.DpiScale, WorkArea = m.WorkArea
        }).ToList();
        layoutDisplays = displays;
        layoutScope = settings.OverviewMonitorScope;
        layoutApplication = application;
        layoutIncludesMinimized = settings.ShowMinimizedWindows;
        monitor = visible.FirstOrDefault(m => m.Handle == pointerMonitor)
            ?? throw new InvalidOperationException("The pointer display is no longer available. Open the overview again.");
        showMinimizedWindows = settings.ShowMinimizedWindows;
        foreach (var other in visible.Where(m => m.Handle != monitor.Handle))
        {
            var view = new MainWindow(this) { monitor = other };
            views.Add(view);
            view.InitializeRendering();
        }
    }
}
