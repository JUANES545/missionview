using WindowScatter;
using Xunit;

public class DragPreviewGeometryTests
{
    [Theory]
    [InlineData(950, 150, 50)]
    [InlineData(999, 101, 99)]
    [InlineData(1000, 100, 100)]
    [InlineData(1050, 50, 150)]
    public void OneImageCrossesBeforePointerAndHasComplementarySourceCrops(int pointer, int leftWidth, int rightWidth)
    {
        var image = new DragPreviewGeometry(200, 100, 1000, 800, .5, .5).At(pointer, 400);
        var left = DragPreviewGeometry.Clip(image, Rect(0, 0, 1000, 800), 800, 400)!.Value;
        var right = DragPreviewGeometry.Clip(image, Rect(1000, 0, 200, 800), 800, 400)!.Value;
        Assert.Equal(200, image.Right - image.Left);
        Assert.Equal(leftWidth, left.Destination.Right - left.Destination.Left);
        Assert.Equal(rightWidth, right.Destination.Right - right.Destination.Left);
        Assert.Equal(0, left.Source.Left);
        Assert.Equal(left.Source.Right, right.Source.Left);
        Assert.Equal(800, right.Source.Right);
        Assert.Equal(leftWidth * 4, left.Source.Right);
    }

    [Fact]
    public void NegativeVerticalAndFourWayIntersectionsUseOneSourceWithoutGapsOrDuplicateFullImages()
    {
        var image = new DragPreviewGeometry(200, 100, 1000, 800, .5, .5).At(-20, -10);
        var monitors = new[] { Rect(-1000, -800, 1000, 800), Rect(0, -800, 1000, 800),
            Rect(-1000, 0, 1000, 800), Rect(0, 0, 1000, 800) };
        var fragments = monitors.Select(m => DragPreviewGeometry.Clip(image, m, 802, 402)!.Value).ToArray();
        Assert.Equal(20000, fragments.Sum(f =>
            (f.Destination.Right - f.Destination.Left) * (f.Destination.Bottom - f.Destination.Top)));
        Assert.Equal(fragments[0].Source.Right, fragments[1].Source.Left);
        Assert.Equal(fragments[2].Source.Right, fragments[3].Source.Left);
        Assert.Equal(fragments[0].Source.Bottom, fragments[2].Source.Top);
        Assert.Equal(fragments[1].Source.Bottom, fragments[3].Source.Top);
        Assert.Equal(802 * 402, fragments.Sum(f =>
            (f.Source.Right - f.Source.Left) * (f.Source.Bottom - f.Source.Top)));
    }

    [Fact]
    public void ReturningToOriginRestoresFullCropAndGapsHaveNoInventedSurface()
    {
        var geometry = new DragPreviewGeometry(201, 101, 1000, 800, .25, .75);
        var origin = Rect(-1000, 0, 1000, 800);
        var other = Rect(50, 100, 500, 500);
        var start = geometry.At(-500, 300);
        Assert.Equal(201, start.Right - start.Left);
        Assert.Equal(101, start.Bottom - start.Top);
        var crop = DragPreviewGeometry.Clip(start, origin, 1001, 501)!.Value.Source;
        Assert.Equal(Rect(0, 0, 1001, 501), crop);
        var middle = geometry.At(20, 200);
        var left = DragPreviewGeometry.Clip(middle, origin, 1001, 501)!.Value;
        var right = DragPreviewGeometry.Clip(middle, other, 1001, 501)!.Value;
        Assert.True(left.Source.Right < right.Source.Left); // The physical gap has no display.
        Assert.False(DragPreviewGeometry.Contains(origin, 20, 200));
        Assert.False(DragPreviewGeometry.Contains(other, 20, 200));
        Assert.Equal(start, geometry.At(-500, 300));
        Assert.Null(DragPreviewGeometry.Clip(start, other, 1001, 501));
        Assert.False(DragPreviewGeometry.Contains(origin, 0, 300));
        Assert.True(DragPreviewGeometry.Contains(other, 50, 300));
    }

    [Fact]
    public void SizeUsesOriginLimitOnceAndGrabPointDoesNotDriftWithIntegerRounding()
    {
        var geometry = new DragPreviewGeometry(2000, 1000, 1000, 800, .2, .8);
        var before = geometry.At(999, 300);
        var after = geometry.At(1000, 300);
        Assert.Equal(700, before.Right - before.Left);
        Assert.Equal(350, before.Bottom - before.Top);
        Assert.Equal(before.Left + 1, after.Left);
        Assert.Equal(before.Right + 1, after.Right);
        Assert.Equal(999, before.Left + 140);
        Assert.Equal(300, before.Top + 280);
    }

    [Fact]
    public void OddSizedGrabCrossingDesktopZeroMovesExactlyOnePhysicalPixel()
    {
        var geometry = new DragPreviewGeometry(201, 101, 1000, 800, .5, .5);
        var a = geometry.At(100, 50);
        var b = geometry.At(101, 51);
        Assert.Equal(a.Left + 1, b.Left);
        Assert.Equal(a.Top + 1, b.Top);
    }

    [Fact]
    public void InvalidOrUnrepresentableSourceIsAnExplicitError()
    {
        Assert.Throws<ArgumentException>(() => new DragPreviewGeometry(0, 100, 1000, 800, .5, .5));
        Assert.Throws<ArgumentException>(() => new DragPreviewGeometry(100, 100, 1000, 800, double.NaN, .5));
        Assert.Throws<ArgumentException>(() => DragPreviewGeometry.Clip(Rect(0, 0, 20, 20), Rect(0, 0, 10, 10), 0, 10));
        Assert.Throws<InvalidOperationException>(() =>
            DragPreviewGeometry.Clip(Rect(0, 0, 100, 100), Rect(0, 0, 1, 1), 1, 1));
    }

    private static PhysicalWindowRect Rect(int x, int y, int width, int height) =>
        new() { Left = x, Top = y, Right = x + width, Bottom = y + height };
}
