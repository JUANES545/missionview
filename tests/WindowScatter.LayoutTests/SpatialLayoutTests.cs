using WindowScatter;
using Xunit;
using static WindowScatter.Win32Interop;

public class SpatialLayoutTests
{
    [Theory]
    [InlineData(5120, 1440, 1)]
    [InlineData(2560, 1440, 1.5)]
    [InlineData(1080, 1920, 2)]
    public void SeparatedInBoundsAnchorsStayAtRealWindowCenters(double w, double h, double dpi)
    {
        var windows = new List<WindowInfo>
        {
            Win(1, 100, 200), Win(2, (int)w - 700, (int)h - 500)
        };
        var layouts = new WindowLayoutCalculator().CalculateLayouts(windows, w, h, dpi);
        Assert.All(layouts, l => Assert.Equal(300, l.Width));
        foreach (var layout in layouts)
        {
            Assert.Equal((layout.Window.OriginalRect.Left + layout.Window.OriginalRect.Right) / 2d,
                layout.X + layout.Width / 2, 7);
            Assert.Equal((layout.Window.OriginalRect.Top + layout.Window.OriginalRect.Bottom) / 2d,
                layout.Y + layout.Height / 2, 7);
        }
    }

    [Fact]
    public void IncompleteFallbackRowUsesOnlyNecessaryGapsAndIsCentered()
    {
        var windows = Enumerable.Range(1, 5).Select(i => Win(i, i * 100, i * 100)).ToList();
        var layouts = WindowLayoutCalculator.PlaceGrid(windows, .5, 3, 30, 79, 1800, 900);
        var firstRow = layouts.Take(3).ToArray();
        var lastRow = layouts.Skip(3).ToArray();
        Assert.Equal(30, firstRow[1].X - firstRow[0].X - firstRow[0].Width, 6);
        Assert.Equal(30, lastRow[1].X - lastRow[0].X - lastRow[0].Width, 6);
        Assert.Equal(930, (lastRow[0].X + lastRow[1].X + lastRow[1].Width) / 2, 6);
        Assert.Equal(52, lastRow[0].Y - firstRow[0].Y - firstRow[0].Height, 6);
    }

    [Fact]
    public void MovingValidNormalizedGeometryMovesAnchorsByTheSameAmount()
    {
        var calculator = new WindowLayoutCalculator();
        var first = calculator.CalculateLayouts([Win(1, 100, 300), Win(2, 1800, 300)], 5120, 1440, 1.5);
        var translated = calculator.CalculateLayouts([Win(2, 2000, 400), Win(1, 300, 400)], 5120, 1440, 1.5);
        Assert.Equal(first.Select(l => (l.Window.Handle, X: l.X + 200, Y: l.Y + 100, l.Width, l.Height)),
            translated.Select(l => (l.Window.Handle, l.X, l.Y, l.Width, l.Height)));
    }

    [Fact]
    public void ResolvingCollisionProtectsIndependentThirdAnchorWithoutShrinkingEverything()
    {
        var windows = new List<WindowInfo>
        {
            Win(1, 100, 100, 800, 600), Win(2, 490, 100, 800, 600), Win(3, 1075, 100, 200, 600)
        };
        var layouts = new WindowLayoutCalculator().CalculateLayouts(windows, 1920, 1080);
        var third = layouts.Single(l => l.Window.Handle == new IntPtr(3));
        Assert.Equal((1125d, 250d, 100d, 300d), (third.X, third.Y, third.Width, third.Height));
        Assert.All(layouts, l => Assert.Equal(.5, l.Width / l.OrigWidth));
        var moved = layouts.Single(l => l.Window.Handle == new IntPtr(2));
        Assert.InRange(Math.Sqrt(Math.Pow(moved.X - 690, 2) + Math.Pow(moved.Y - 250, 2)), 1, 352.000001);
    }

    [Fact]
    public void CompactFallbackFitsWhenCenteredAnchorBlocksAllLocalCandidates()
    {
        var windows = Enumerable.Range(1, 4).Select(id => Win(id, 50, 100, 900, 600)).ToList();
        var layouts = new WindowLayoutCalculator().CalculateLayouts(windows, 1000, 800);
        Assert.All(layouts, l => Assert.Equal((450d, 300d), (l.Width, l.Height)));
        Assert.Equal(new[] { (35d, 87d), (515d, 87d), (35d, 439d), (515d, 439d) },
            layouts.Select(l => (l.X, l.Y)));
    }

    private static WindowInfo Win(int id, int x, int y, int w = 600, int h = 400) => new()
    {
        Handle = new IntPtr(id), OriginalRect = new PhysicalWindowRect { Left = x, Top = y, Right = x + w, Bottom = y + h }
    };
}
