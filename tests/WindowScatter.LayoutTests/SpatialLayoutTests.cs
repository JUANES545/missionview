using WindowScatter;
using Xunit;
using Xunit.Abstractions;
using static WindowScatter.Win32Interop;

public class SpatialLayoutTests(ITestOutputHelper output)
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
    public void StackedLargePreviewsStayTogetherBeforeIndependentObstacles()
    {
        var windows = new List<WindowInfo>
        {
            Win(1, 0, 0, 1706, 1440), Win(2, 10, 10, 1706, 1440),
            Win(3, 1706, 0, 1706, 1440), Win(4, 3840, 0, 1280, 1440)
        };
        var calculator = new WindowLayoutCalculator();
        var layouts = calculator.CalculateLayouts(windows, 5120, 1440);
        WriteGeometry(layouts);
        var first = layouts.Single(l => l.Window.Handle == new IntPtr(1));
        var second = layouts.Single(l => l.Window.Handle == new IntPtr(2));
        var obstacle = layouts.Single(l => l.Window.Handle == new IntPtr(3));
        var distant = layouts.Single(l => l.Window.Handle == new IntPtr(4));
        Assert.Equal((2132.5, 360d, 853d, 720d), (obstacle.X, obstacle.Y, obstacle.Width, obstacle.Height));
        Assert.Equal((4160d, 360d, 640d, 720d), (distant.X, distant.Y, distant.Width, distant.Height));
        Assert.All(layouts, l => Assert.Equal(.5, l.Width / l.OrigWidth));
        Assert.Equal(30, second.X - first.X - first.Width, 7);
        Assert.InRange(second.X + second.Width - first.X, 1736, 1736.000001);
        Assert.True(second.X + second.Width + 30 <= obstacle.X);
        Assert.InRange((first.X + second.X + second.Width) / 2, 853, 950);
        AssertSafe(layouts, 5120, 1440, 1);

        windows.Reverse();
        Assert.Equal(layouts.Select(l => (l.Window.Handle, l.X, l.Y, l.Width, l.Height)),
            calculator.CalculateLayouts(windows, 5120, 1440)
                .Select(l => (l.Window.Handle, l.X, l.Y, l.Width, l.Height)));
    }

    [Theory]
    [InlineData("Compact", .35, 1, 110000)]
    [InlineData("Standard", .5, 1.5, 250000)]
    [InlineData("Large", .75, 2, 800000)]
    public void ThreeStackedMixedPreviewsKeepACompactGroupAtRequestedScale(string size, double scale, double dpi,
        double maximumSquaredDisplacement)
    {
        var windows = new List<WindowInfo>
        {
            Win(1, 200, 300, 1200, 800), Win(2, 250, 320, 1000, 600),
            Win(3, 300, 340, 800, 700), Win(4, 3500, 300, 800, 800)
        };
        var calculator = new WindowLayoutCalculator();
        var layouts = calculator.CalculateLayouts(windows, 5120, 2160, dpi, size);
        WriteGeometry(layouts);
        var group = layouts.Where(l => l.Window.Handle != new IntPtr(4)).OrderBy(l => l.X).ToArray();
        var obstacle = layouts.Single(l => l.Window.Handle == new IntPtr(4));
        Assert.Equal((3900d, 700d), (obstacle.X + obstacle.Width / 2, obstacle.Y + obstacle.Height / 2));
        Assert.All(layouts, l =>
        {
            Assert.Equal(scale, l.Width / l.OrigWidth, 7);
            Assert.Equal(scale, l.Height / l.OrigHeight, 7);
        });
        AssertCompact(group, dpi);
        double displacement = group.Sum(l =>
            Math.Pow(l.X + l.Width / 2 - l.Window.OriginalRect.Left - l.OrigWidth / 2, 2) +
            Math.Pow(l.Y + l.Height / 2 - l.Window.OriginalRect.Top - l.OrigHeight / 2, 2));
        Assert.InRange(displacement, 0, maximumSquaredDisplacement);
        AssertSafe(layouts, 5120, 2160, dpi);
        windows = [windows[2], windows[0], windows[3], windows[1]];
        Assert.Equal(layouts.Select(l => (l.Window.Handle, l.X, l.Y, l.Width, l.Height)),
            calculator.CalculateLayouts(windows, 5120, 2160, dpi, size)
                .Select(l => (l.Window.Handle, l.X, l.Y, l.Width, l.Height)));
    }

    [Fact]
    public void UnfittableGroupKeepsIndividualFallbackAndIndependentAnchor()
    {
        var windows = new List<WindowInfo>
        {
            Win(1, 0, 0, 1706, 1440), Win(2, 10, 10, 1706, 1440),
            Win(3, 20, 20, 1706, 1440), Win(4, 1706, 0, 1706, 1440)
        };
        var layouts = new WindowLayoutCalculator().CalculateLayouts(windows, 5120, 1440);
        WriteGeometry(layouts);
        var obstacle = layouts.Single(l => l.Window.Handle == new IntPtr(4));
        Assert.Equal((2132.5, 360d, 853d, 720d), (obstacle.X, obstacle.Y, obstacle.Width, obstacle.Height));
        Assert.All(layouts, l => Assert.Equal(.5, l.Width / l.OrigWidth));
        Assert.Equal(new[] { (426.5, 360d), (3015.5, 370d), (3898.5, 380d) },
            layouts.Where(l => l.Window.Handle != new IntPtr(4)).Select(l => (l.X, l.Y)));
        AssertSafe(layouts, 5120, 1440, 1);
    }

    [Fact]
    public void ClosingOpeningAndAddingWindowsRetainsUnchangedVerifiedPositions()
    {
        var calculator = new WindowLayoutCalculator();
        var windows = new List<WindowInfo>
        {
            IdentifiedWin(1, 100, 200), IdentifiedWin(2, 110, 210), IdentifiedWin(3, 3500, 300)
        };
        var first = calculator.CalculateLayouts(windows, 5120, 1440);
        var survivor = first.Single(l => l.Window.Handle == new IntPtr(2));
        var closed = calculator.CalculateLayouts([windows[2], windows[1]], 5120, 1440);
        Assert.Equal((survivor.X, survivor.Y, survivor.Width, survivor.Height),
            Geometry(closed.Single(l => l.Window.Handle == new IntPtr(2))));
        var opened = calculator.CalculateLayouts([IdentifiedWin(4, 110, 210), windows[1], windows[2]], 5120, 1440);
        Assert.Equal(closed.OrderBy(l => l.Window.Handle.ToInt64()).Select(Geometry),
            opened.Where(l => l.Window.Handle != new IntPtr(4)).OrderBy(l => l.Window.Handle.ToInt64()).Select(Geometry));
        AssertSafe(opened, 5120, 1440, 1);
        Assert.All(opened, l => Assert.Equal(opened[0].Width / opened[0].OrigWidth, l.Height / l.OrigHeight, 7));
        Assert.Equal(opened.Select(Geometry), calculator.CalculateLayouts(
            [windows[2], windows[1], IdentifiedWin(4, 110, 210)], 5120, 1440).Select(Geometry));
    }

    [Theory]
    [InlineData("move")]
    [InlineData("resize")]
    [InlineData("process")]
    [InlineData("thread")]
    [InlineData("dpi")]
    [InlineData("dimensions")]
    [InlineData("size")]
    public void ChangedSourceIdentityOrLayoutContextDoesNotReuseOldPreviewPositions(string change)
    {
        var calculator = new WindowLayoutCalculator();
        calculator.CalculateLayouts([IdentifiedWin(1, 100, 200), IdentifiedWin(2, 110, 210)], 5120, 1440);
        var window = IdentifiedWin(2, change == "move" ? 2100 : 110, 210,
            change == "resize" ? 900 : 600);
        if (change == "process") window.Identity = window.Identity with { ProcessId = 42 };
        if (change == "thread") window.Identity = window.Identity with { ThreadId = 42 };
        double width = change == "dimensions" ? 2560 : 5120;
        double dpi = change == "dpi" ? 2 : 1;
        string size = change == "size" ? "Compact" : "Standard";
        var expected = new WindowLayoutCalculator().CalculateLayouts([window], width, 1440, dpi, size);
        Assert.Equal(expected.Select(Geometry), calculator.CalculateLayouts([window], width, 1440, dpi, size).Select(Geometry));
    }

    [Fact]
    public void IdenticalColdAndReopenedSnapshotsIgnoreEnumerationOrder()
    {
        var windows = new List<WindowInfo>
        {
            IdentifiedWin(1, 100, 200), IdentifiedWin(2, 110, 210),
            IdentifiedWin(3, 500, 800, 1000, 400), IdentifiedWin(4, 3500, 300)
        };
        var calculator = new WindowLayoutCalculator();
        var first = calculator.CalculateLayouts(windows, 5120, 1440);
        var expected = first.Select(l => (l.Window.Identity, Geometry(l))).ToArray();
        foreach (var order in new[] { windows.AsEnumerable().Reverse().ToList(),
            new List<WindowInfo> { windows[2], windows[0], windows[3], windows[1] } })
        {
            Assert.Equal(expected, new WindowLayoutCalculator().CalculateLayouts(order, 5120, 1440)
                .Select(l => (l.Window.Identity, Geometry(l))));
            Assert.Equal(expected, calculator.CalculateLayouts(order, 5120, 1440)
                .Select(l => (l.Window.Identity, Geometry(l))));
        }
        AssertSafe(first, 5120, 1440, 1);
    }

    [Fact]
    public void MovedWindowReanchorsWhileOtherVerifiedPositionsRemainAndUnsupportedIdentityIsForgotten()
    {
        var calculator = new WindowLayoutCalculator();
        var windows = new List<WindowInfo>
        { IdentifiedWin(1, 100, 200), IdentifiedWin(2, 110, 210), IdentifiedWin(3, 3500, 300) };
        var first = calculator.CalculateLayouts(windows, 5120, 1440);
        var moved = IdentifiedWin(1, 2000, 800);
        var next = calculator.CalculateLayouts([moved, windows[1], windows[2]], 5120, 1440);
        Assert.Equal(first.Where(l => l.Window.Handle != moved.Handle).Select(Geometry),
            next.Where(l => l.Window.Handle != moved.Handle).Select(Geometry));
        var reanchored = next.Single(l => l.Window.Handle == moved.Handle);
        Assert.Equal((2300d, 1000d), (reanchored.X + reanchored.Width / 2, reanchored.Y + reanchored.Height / 2));
        calculator.KeepRegisteredIdentities([moved.Identity, windows[2].Identity]);
        var rediscovered = calculator.CalculateLayouts([windows[1]], 5120, 1440);
        Assert.Equal(new WindowLayoutCalculator().CalculateLayouts([windows[1]], 5120, 1440).Select(Geometry),
            rediscovered.Select(Geometry));
        AssertSafe(next, 5120, 1440, 1);
    }

    [Fact]
    public void RetentionThatCannotFitFallsBackToASafeCommonScale()
    {
        var calculator = new WindowLayoutCalculator();
        calculator.CalculateLayouts([IdentifiedWin(1, 100, 200)], 1000, 800);
        var windows = Enumerable.Range(1, 10).Select(id => IdentifiedWin(id, 100, 200, 900, 600)).ToList();
        var layouts = calculator.CalculateLayouts(windows, 1000, 800);
        AssertSafe(layouts, 1000, 800, 1);
        Assert.All(layouts, l => Assert.Equal(layouts[0].Width / layouts[0].OrigWidth, l.Height / l.OrigHeight, 7));
        Assert.True(layouts[0].Width / layouts[0].OrigWidth < .5);
    }

    private static (double X, double Y, double Width, double Height) Geometry(WindowLayout layout) =>
        (layout.X, layout.Y, layout.Width, layout.Height);

    private static WindowInfo IdentifiedWin(int id, int x, int y, int w = 600, int h = 400)
    {
        var window = Win(id, x, y, w, h);
        window.Identity = new(window.Handle, (uint)id, 100);
        return window;
    }

    [Fact]
    public void LaterUnfittableComponentDoesNotDiscardAnEarlierCompactGroup()
    {
        // The right component needs fragmented slots; its envelope cannot fit beside the blocker.
        var windows = new List<WindowInfo>
        {
            Win(1, 0, -500, 2600, 1000), Win(2, 974, -490, 1706, 1000),
            Win(3, 1850, 0, 1800, 1440),
            Win(4, 3300, 0, 1700, 1440), Win(5, 3310, 10, 1700, 1440),
            Win(6, 3320, 20, 1700, 1440)
        };
        var calculator = new WindowLayoutCalculator();
        var layouts = calculator.CalculateLayouts(windows, 5120, 1440);
        WriteGeometry(layouts);
        Assert.All(layouts, l => Assert.Equal(.5, l.Width / l.OrigWidth, 7));
        AssertCompact(layouts.Where(l => l.Window.Handle.ToInt64() <= 2).ToArray(), 1);
        var blocker = layouts.Single(l => l.Window.Handle == new IntPtr(3));
        Assert.Equal((2300d, 360d, 900d, 720d), (blocker.X, blocker.Y, blocker.Width, blocker.Height));
        var nearRight = layouts.Single(l => l.Window.Handle == new IntPtr(4));
        Assert.Equal(3725, nearRight.X);
        AssertSafe(layouts, 5120, 1440, 1);
        windows.Reverse();
        Assert.Equal(layouts.Select(l => (l.Window.Handle, l.X, l.Y, l.Width, l.Height)),
            calculator.CalculateLayouts(windows, 5120, 1440).Select(l => (l.Window.Handle, l.X, l.Y, l.Width, l.Height)));
    }

    [Fact]
    public void BlockingCompactPlacementRetriesIndividualFitFromUnmodifiedAnchorsAtTheSameScale()
    {
        var windows = new List<WindowInfo>
        {
            Win(1, 0, -500, 2600, 1000), Win(2, 800, -490, 800, 1000),
            Win(3, 1730, 0, 1800, 1440),
            Win(4, 3300, 0, 1700, 1440), Win(5, 3310, 10, 1700, 1440),
            Win(6, 3320, 20, 1700, 1440)
        };
        var layouts = new WindowLayoutCalculator().CalculateLayouts(windows, 5120, 1440);
        WriteGeometry(layouts);
        Assert.All(layouts, l => Assert.Equal(.5, l.Width / l.OrigWidth, 7));
        var original = layouts.Single(l => l.Window.Handle == new IntPtr(1));
        Assert.Equal((650d, 79d, 1300d, 500d), Geometry(original));
        var blocker = layouts.Single(l => l.Window.Handle == new IntPtr(3));
        Assert.Equal((2180d, 360d, 900d, 720d), Geometry(blocker));
        var small = layouts.Single(l => l.Window.Handle == new IntPtr(2));
        Assert.Equal(79, small.Y);
        Assert.Equal(30, original.X - small.X - small.Width, 7);
        Assert.Equal(3725, layouts.Single(l => l.Window.Handle == new IntPtr(4)).X);
        AssertSafe(layouts, 5120, 1440, 1);
        windows.Reverse();
        Assert.Equal(layouts.Select(l => (l.Window.Handle, Geometry(l))),
            new WindowLayoutCalculator().CalculateLayouts(windows, 5120, 1440)
                .Select(l => (l.Window.Handle, Geometry(l))));
    }

    [Fact]
    public void CompactGroupFitsWhenCenteredAnchorBlocksIndividualCandidates()
    {
        var windows = Enumerable.Range(1, 4).Select(id => Win(id, 50, 100, 900, 600)).ToList();
        var layouts = new WindowLayoutCalculator().CalculateLayouts(windows, 1000, 800);
        Assert.All(layouts, l => Assert.Equal((450d, 300d), (l.Width, l.Height)));
        Assert.Equal(new[] { (35d, 79d), (515d, 79d), (35d, 431d), (515d, 431d) },
            layouts.Select(l => (l.X, l.Y)));
    }

    [Fact]
    public void GroupExactFitDoesNotInvertFractionalClampBounds()
    {
        var windows = new List<WindowInfo>
        {
            Win(1, 1455, 0, 293, 1713), Win(2, -7, 285, 1943, 1990), Win(3, 696, 2120, 723, 1707)
        };
        var layouts = new WindowLayoutCalculator().CalculateLayouts(windows, 631, 1830);
        AssertSafe(layouts, 631, 1830, 1);
        Assert.All(layouts, l => Assert.Equal(layouts[0].Width / layouts[0].OrigWidth,
            l.Width / l.OrigWidth, 7));
    }

    private static void AssertSafe(List<WindowLayout> layouts, double w, double h, double dpi)
    {
        foreach (var a in layouts)
        {
            Assert.InRange(a.X, 14 * dpi, w - a.Width - 14 * dpi);
            Assert.InRange(a.Y, (64 + 14) * dpi, h - a.Height - 36 * dpi);
            foreach (var b in layouts.Where(b => b.Window.Handle.ToInt64() > a.Window.Handle.ToInt64()))
                Assert.True(a.X + a.Width + 2 * (14 * dpi + 1) <= b.X + 1e-7 ||
                    b.X + b.Width + 2 * (14 * dpi + 1) <= a.X + 1e-7 ||
                    a.Y + a.Height + 50 * dpi + 2 <= b.Y + 1e-7 ||
                    b.Y + b.Height + 50 * dpi + 2 <= a.Y + 1e-7);
        }
    }

    private static void AssertCompact(WindowLayout[] group, double dpi)
    {
        var connected = new HashSet<WindowLayout> { group[0] };
        bool added;
        do
        {
            added = false;
            foreach (var a in group.Where(a => !connected.Contains(a)))
                if (connected.Any(b =>
                    ((Math.Abs(a.X - b.X - b.Width - 2 * (14 * dpi + 1)) < 1e-7 ||
                      Math.Abs(b.X - a.X - a.Width - 2 * (14 * dpi + 1)) < 1e-7) &&
                     a.Y < b.Y + b.Height && b.Y < a.Y + a.Height) ||
                    ((Math.Abs(a.Y - b.Y - b.Height - 50 * dpi - 2) < 1e-7 ||
                      Math.Abs(b.Y - a.Y - a.Height - 50 * dpi - 2) < 1e-7) &&
                     a.X < b.X + b.Width && b.X < a.X + a.Width)))
                {
                    connected.Add(a);
                    added = true;
                }
        } while (added);
        Assert.Equal(group.Length, connected.Count);
    }

    private void WriteGeometry(List<WindowLayout> layouts)
    {
        foreach (var l in layouts)
            output.WriteLine(FormattableString.Invariant(
                $"Window {l.Window.Handle}: ({l.X}, {l.Y}, {l.Width}, {l.Height})"));
    }

    private static WindowInfo Win(int id, int x, int y, int w = 600, int h = 400) => new()
    {
        Handle = new IntPtr(id), OriginalRect = new PhysicalWindowRect { Left = x, Top = y, Right = x + w, Bottom = y + h }
    };
}
