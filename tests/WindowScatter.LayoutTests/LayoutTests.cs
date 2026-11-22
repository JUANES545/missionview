using WindowScatter;
using Xunit;
using static WindowScatter.Win32Interop;

public class LayoutTests
{
    private static WindowInfo Win(int id, int x, int y, int w, int h) => new()
    {
        Handle = new IntPtr(id), Title = $"Window {id}", WasMaximized = false,
        OriginalRect = new PhysicalWindowRect { Left = x, Top = y, Right = x + w, Bottom = y + h }
    };

    private static List<WindowLayout> Calculate(List<WindowInfo> windows, double w = 1920, double h = 1080)
        => new WindowLayoutCalculator().CalculateLayouts(windows, w, h);

    [Theory]
    [InlineData("Compact", .35, 1.5)]
    [InlineData("Standard", .5, 2)]
    [InlineData("Large", .75, 1.25)]
    public void RequestedSizesChangeActualLayoutAndKeepChromeWithinMixedDpiDisplays(string size, double cap, double dpi)
    {
        var calculator = new WindowLayoutCalculator();
        var window = Win(1, 100, 100, 600, 400);
        var layout = Assert.Single(calculator.CalculateLayouts([window], 1920, 1080, dpi, size));
        Assert.Equal(600 * cap, layout.Width, 8);
        Assert.Equal(400 * cap, layout.Height, 8);
        VerifyChrome([layout], 1920, 1080, dpi);
        var dense = calculator.CalculateLayouts(Enumerable.Range(1, 20).Select(i => Win(i, 0, 0, 1600, 900)).ToList(),
            960, 600, dpi, size);
        Assert.All(dense, item =>
        {
            Assert.InRange(item.Width / 1600, double.Epsilon, cap);
            Assert.Equal(item.Width / 1600, item.Height / 900, 8);
        });
        VerifyChrome(dense, 960, 600, dpi);
        Assert.Throws<ArgumentException>(() => calculator.CalculateLayouts([window], 1920, 1080, dpi, "Huge"));
    }

    private static void VerifyGeometry(List<WindowLayout> layouts, double w, double h)
    {
        double? scale = null;
        foreach (var l in layouts)
        {
            Assert.True(double.IsFinite(l.X + l.Y + l.Width + l.Height));
            Assert.True(l.Width > 0 && l.Height > 0);
            Assert.InRange(l.X, 0, w - l.Width + 1e-7);
            Assert.InRange(l.Y, 0, h - l.Height + 1e-7);
            double sx = l.Width / (l.Window.OriginalRect.Right - l.Window.OriginalRect.Left);
            double sy = l.Height / (l.Window.OriginalRect.Bottom - l.Window.OriginalRect.Top);
            Assert.InRange(sx, double.Epsilon, .5);
            Assert.Equal(sx, sy, 8);
            if (scale.HasValue) Assert.Equal(scale.Value, sx, 8);
            scale = sx;
        }
        for (int i = 0; i < layouts.Count; i++)
        for (int j = i + 1; j < layouts.Count; j++)
        {
            var a = layouts[i]; var b = layouts[j];
            double area = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X))
                * Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
            Assert.True(area <= 1e-6,
                $"Excessive overlap: {area}");
        }
    }

    [Fact]
    public void MixedSizesKeepOneScaleAndAspectRatios()
        => VerifyGeometry(Calculate([Win(1, 0, 0, 1800, 900), Win(2, 900, 200, 300, 600),
            Win(3, 300, 400, 700, 200)]), 1920, 1080);

    [Fact]
    public void DenseStackFitsWithoutExcessiveOverlap()
        => VerifyGeometry(Calculate(Enumerable.Range(1, 20).Select(i => Win(i, 0, 0, 1920, 1080)).ToList()), 1920, 1080);

    [Fact]
    public void RepeatedCallsAndEnumerationOrderDoNotChangeLayout()
    {
        var windows = Enumerable.Range(1, 8).Select(i => Win(i, i * 90, i * 40, 900, 600)).ToList();
        var calculator = new WindowLayoutCalculator();
        var first = calculator.CalculateLayouts(windows, 1920, 1080).ToDictionary(l => l.Window.Handle);
        windows.Reverse();
        var second = calculator.CalculateLayouts(windows, 1920, 1080);
        foreach (var l in second)
        {
            var a = first[l.Window.Handle];
            Assert.Equal((a.X, a.Y, a.Width, a.Height), (l.X, l.Y, l.Width, l.Height));
        }
    }

    [Fact]
    public void SeparatedWindowsRetainTheirOriginalCenters()
    {
        var layouts = Calculate([Win(1, 50, 100, 400, 300), Win(2, 1300, 650, 400, 300)]);
        var left = layouts.Single(l => l.Window.Handle == new IntPtr(1));
        var right = layouts.Single(l => l.Window.Handle == new IntPtr(2));
        Assert.Equal(250, left.X + left.Width / 2, 6);
        Assert.Equal(250, left.Y + left.Height / 2, 6);
        Assert.Equal(1500, right.X + right.Width / 2, 6);
        Assert.Equal(800, right.Y + right.Height / 2, 6);
    }

    [Fact]
    public void NearbyWindowsMoveApartBeforeReducingScale()
    {
        var layouts = Calculate([Win(1, 100, 100, 800, 600), Win(2, 490, 100, 800, 600)]);
        var left = layouts.Single(l => l.Window.Handle == new IntPtr(1));
        var right = layouts.Single(l => l.Window.Handle == new IntPtr(2));
        Assert.True(right.X >= left.X + left.Width + 28);
        Assert.Equal(280, left.X, 7);
        Assert.Equal(710, right.X, 7);
        Assert.Equal(250, right.Y, 7);
        Assert.Equal(400, right.Width);
        VerifyGeometry(layouts, 1920, 1080);
    }

    [Fact]
    public void EmptyInputReturnsEmpty() => Assert.Empty(Calculate([]));

    [Fact]
    public void GridFallbackReservesChromeForMixedSizes()
    {
        var windows = Enumerable.Range(1, 12).Select(i => Win(i, i * 10, i * 20,
            i % 2 == 0 ? 1920 : 120, i % 3 == 0 ? 1080 : 200)).ToList();
        var layouts = WindowLayoutCalculator.PlaceGrid(windows, .08, 4, 30, 79, 900, 400);
        VerifyGeometry(layouts, 960, 520);
        VerifyChrome(layouts, 960, 520, 1);
    }

    [Theory]
    [InlineData(5120, 1440, 1, 25)]
    [InlineData(1920, 1080, 1.5, 40)]
    [InlineData(1080, 1920, 2, 30)]
    [InlineData(320, 240, 1, 20)]
    [InlineData(5120, 1440, 2, 80)]
    public void CompleteChromeEnvelopesFitWithoutIntersections(double w, double h, double dpi, int count)
    {
        var windows = Enumerable.Range(1, count).Select(i => Win(i, 0, 0,
            i % 3 == 0 ? 120 : 1600, i % 2 == 0 ? 1100 : 240)).ToList();
        var layouts = new WindowLayoutCalculator().CalculateLayouts(windows, w, h, dpi);
        VerifyGeometry(layouts, w, h);
        VerifyChrome(layouts, w, h, dpi);
    }

    private static void VerifyChrome(List<WindowLayout> layouts, double w, double h, double dpi)
    {
        for (int i = 0; i < layouts.Count; i++)
        {
            var a = layouts[i];
            Assert.True(a.X - 14 * dpi >= -1e-6);
            Assert.True(a.Y - 14 * dpi >= 64 * dpi - 1e-6);
            Assert.True(a.X + a.Width + 14 * dpi <= w + 1e-6);
            Assert.True(a.Y + a.Height + 36 * dpi <= h + 1e-6);
            foreach (var b in layouts.Skip(i + 1))
                Assert.True(a.X + a.Width + 28 * dpi <= b.X + 1e-6 ||
                    b.X + b.Width + 28 * dpi <= a.X + 1e-6 ||
                    a.Y + a.Height + 50 * dpi <= b.Y + 1e-6 ||
                    b.Y + b.Height + 50 * dpi <= a.Y + 1e-6);
        }
    }

    [Fact]
    public void MovingAndResizingSameHandleUpdatesNextLayout()
    {
        var calculator = new WindowLayoutCalculator();
        var first = calculator.CalculateLayouts([Win(1, 0, 0, 400, 300)], 1920, 1080).Single();
        var second = calculator.CalculateLayouts([Win(1, 900, 500, 800, 600)], 1920, 1080).Single();
        Assert.Equal(400, second.Width, 6);
        Assert.Equal(300, second.Height, 6);
        Assert.Equal(1300, second.X + second.Width / 2, 6);
        Assert.Equal(800, second.Y + second.Height / 2, 6);
        Assert.NotEqual(first.X, second.X);
    }

    [Fact]
    public void SingleWindowStaysCenteredOnItsOriginalPosition()
    {
        var layout = Calculate([Win(1, 600, 300, 400, 200)]).Single();
        Assert.Equal(800, layout.X + layout.Width / 2, 6);
        Assert.Equal(400, layout.Y + layout.Height / 2, 6);
    }

    [Fact]
    public void ExactFitOnFractionalPaddingDoesNotInvertClampBounds()
        => VerifyGeometry(Calculate([Win(1, -380, 108, 1058, 1816)], 3720, 643), 3720, 643);

    [Fact]
    public void RandomizedDesktopGeometryPreservesLayoutInvariants()
    {
        var random = new Random(12345);
        for (int sample = 0; sample < 100; sample++)
        {
            int w = random.Next(320, 4000), h = random.Next(240, 2200);
            var windows = Enumerable.Range(1, random.Next(1, 21)).Select(i => Win(i,
                random.Next(-1000, w + 1000), random.Next(-500, h + 500),
                random.Next(100, 3000), random.Next(100, 2000))).ToList();
            List<WindowLayout> layouts;
            try { layouts = Calculate(windows, w, h); }
            catch (Exception e)
            {
                throw new Exception($"Sample {sample}, screen {w}x{h}: " + string.Join("; ", windows.Select(v =>
                    $"{v.OriginalRect.Left},{v.OriginalRect.Top},{v.OriginalRect.Right - v.OriginalRect.Left},{v.OriginalRect.Bottom - v.OriginalRect.Top}")), e);
            }
            Assert.Equal(windows.Count, layouts.Count);
            Assert.Equal(windows.Count, layouts.Select(l => l.Window.Handle).Distinct().Count());
            VerifyGeometry(layouts, w, h);
            VerifyChrome(layouts, w, h, 1);
        }
    }

    [Theory]
    [InlineData(320, 240)]
    [InlineData(1080, 1920)]
    [InlineData(3840, 2160)]
    public void OffscreenAndMixedWindowsFitDifferentScreens(double w, double h)
        => VerifyGeometry(Calculate([Win(1, -1000, -500, 4000, 2000), Win(2, 3000, 2000, 100, 200),
            Win(3, 100, 100, 300, 900)], w, h), w, h);
}
