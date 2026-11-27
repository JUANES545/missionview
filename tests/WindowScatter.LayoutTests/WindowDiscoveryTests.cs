using WindowScatter;
using Xunit;
using static WindowScatter.Win32Interop;

public class WindowDiscoveryTests
{
    [Theory]
    [InlineData("z-order", true)]
    [InlineData("addition", false)]
    [InlineData("removal", false)]
    [InlineData("reused-hwnd", false)]
    [InlineData("minimized", false)]
    [InlineData("title", false)]
    [InlineData("geometry", false)]
    public void OverviewSnapshotIgnoresZOrderButDetectsSourceChanges(string change, bool same)
    {
        static WindowInfo Source(int handle) => new()
        {
            Handle = new(handle), Identity = new(new(handle), 1, 2), Title = "Document",
            OriginalRect = new() { Right = 640, Bottom = 480 }
        };
        var old = new[] { Source(10), Source(20) };
        var current = new List<WindowInfo> { Source(20), Source(10) };
        switch (change)
        {
            case "addition": current.Add(Source(30)); break;
            case "removal": current.RemoveAt(0); break;
            case "reused-hwnd": current[0].Identity = new(current[0].Handle, 9, 2); break;
            case "minimized": current[0].WasMinimized = true; break;
            case "title": current[0].Title = "Another document"; break;
            case "geometry": current[0].OriginalRect.Right++; break;
        }
        Assert.Equal(same, WindowInfo.SameOverviewSources(old, current));
    }

    [Theory]
    [InlineData(500, 1430, 1300, 2030)]
    [InlineData(-900, 300, 20, 900)]
    public void VisibleSliverBelongsToMonitorEvenWithCenterOutside(int left, int top, int right, int bottom)
    {
        var monitor = Monitor(1, 0, 0, 5120, 1440);
        var rect = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        Assert.Same(monitor, MonitorHelper.FindWindowMonitor(rect, [monitor]));
        var window = WindowEnumerator.CreateWindowInfo(new(10), "Test", false, rect, false, 0, monitor, [monitor]);
        Assert.NotNull(window);
        Assert.Equal((left, top, right, bottom),
            (window.OriginalRect.Left, window.OriginalRect.Top, window.OriginalRect.Right, window.OriginalRect.Bottom));
        var layout = Assert.Single(new WindowLayoutCalculator().CalculateLayouts([window], monitor.Width, monitor.Height));
        Assert.InRange(layout.X, 0, monitor.Width - layout.Width);
        Assert.InRange(layout.Y, 64, monitor.Height - layout.Height - 36);
    }

    [Fact]
    public void LargestVisibleIntersectionWinsNotCenterOrPointerAndOffscreenWindowsHaveNoMonitor()
    {
        var primary = Monitor(1, 0, 0, 1920, 1080);
        var secondary = Monitor(2, 1920, 0, 1920, 2160);
        var rect = new RECT { Left = 1000, Top = 900, Right = 2400, Bottom = 2300 };
        Assert.Same(secondary, MonitorHelper.FindWindowMonitor(rect, [primary, secondary]));
        Assert.Same(secondary, MonitorHelper.FindWindowMonitor(rect, [secondary, primary]));
        Assert.NotNull(WindowEnumerator.CreateWindowInfo(new(10), "Test", false, rect, false, 0,
            secondary, [primary, secondary]));
        Assert.Null(WindowEnumerator.CreateWindowInfo(new(10), "Test", false, rect, false, 0,
            primary, [primary, secondary]));
        Assert.Same(primary, MonitorHelper.FindWindowMonitor(
            new RECT { Left = 100, Top = 200, Right = 900, Bottom = 800 }, [secondary, primary]));
        Assert.Null(MonitorHelper.FindWindowMonitor(
            new RECT { Left = 100, Top = 3000, Right = 900, Bottom = 3600 }, [primary, secondary]));
    }

    [Theory]
    [InlineData(0u, 100)]
    [InlineData(0x40000u, -32000)]
    [InlineData(0x40080u, 100)]
    public void MinimizedWindowsStayExcludedEvenWhenAppWindowOrTheirRestoreLocationWouldBeVisible(uint style, int x)
    {
        var monitor = Monitor(1, 0, 0, 5120, 1440);
        var rect = new RECT { Left = x, Top = 100, Right = x + 800, Bottom = 700 };
        Assert.Null(WindowEnumerator.CreateWindowInfo(new(10), "Test", true, rect, false, style, monitor, [monitor]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void SecondaryNegativePortraitGeometryIsNormalizedOnceInPhysicalPixels(double dpi)
    {
        var primary = Monitor(1, 0, 0, 1920, 1080);
        var secondary = Monitor(2, -(int)(1200 * dpi), -(int)(400 * dpi), (int)(1200 * dpi), (int)(1600 * dpi));
        var rect = new RECT
        {
            Left = secondary.Left + (int)(100 * dpi), Top = secondary.Top + (int)(200 * dpi),
            Right = secondary.Left + (int)(900 * dpi), Bottom = secondary.Top + (int)(800 * dpi)
        };
        var window = WindowEnumerator.CreateWindowInfo(new(10), "Test", false, rect, true, 0,
            secondary, [primary, secondary]);
        Assert.NotNull(window);
        Assert.True(window.WasMaximized);
        Assert.Equal(((int)(100 * dpi), (int)(200 * dpi), (int)(900 * dpi), (int)(800 * dpi)),
            (window.OriginalRect.Left, window.OriginalRect.Top, window.OriginalRect.Right, window.OriginalRect.Bottom));
        Assert.Null(WindowEnumerator.CreateWindowInfo(new(10), "Test", false, rect, true, 0,
            primary, [primary, secondary]));
        var layout = Assert.Single(new WindowLayoutCalculator().CalculateLayouts([window], secondary.Width, secondary.Height, dpi));
        Assert.InRange(layout.X, 0, secondary.Width - layout.Width);
        Assert.InRange(layout.Y, 64 * dpi, secondary.Height - layout.Height - 36 * dpi);
    }

    [Theory]
    [InlineData(0x80u, false)]
    [InlineData(0x40080u, true)]
    [InlineData(0x108u, true)]
    public void ToolWindowAppWindowAndCallSurfacePolicyIsNotRelaxed(uint style, bool eligible)
    {
        var monitor = Monitor(1, 0, 0, 1920, 1080);
        var window = WindowEnumerator.CreateWindowInfo(new(10), "Test", false,
            new RECT { Left = 100, Top = 200, Right = 900, Bottom = 800 }, false, style, monitor, [monitor]);
        Assert.Equal(eligible, window != null);
    }

    [Fact]
    public void EqualIntersectionsHaveOneStableMonitorRegardlessOfEnumerationOrder()
    {
        var first = Monitor(1, 0, 0, 1920, 1080);
        var second = Monitor(2, 1920, 0, 1920, 1080);
        var rect = new RECT { Left = 1800, Top = 100, Right = 2040, Bottom = 800 };
        Assert.Same(first, MonitorHelper.FindWindowMonitor(rect, [first, second]));
        Assert.Same(first, MonitorHelper.FindWindowMonitor(rect, [second, first]));
    }

    private static MonitorHelper.MonitorBounds Monitor(int id, int x, int y, int width, int height) => new()
    {
        Handle = new IntPtr(id), Left = x, Top = y, Width = width, Height = height
    };
}
