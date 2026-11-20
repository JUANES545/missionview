namespace WindowScatter;

internal readonly record struct DragPreviewFragment(PhysicalWindowRect Destination, PhysicalWindowRect Source);

// One gesture keeps its size and normalized grab point in desktop physical pixels.
internal sealed class DragPreviewGeometry
{
    private readonly int width, height;
    private readonly int grabX, grabY;

    internal DragPreviewGeometry(double thumbnailWidth, double thumbnailHeight,
        int originWidth, int originHeight, double grabX, double grabY)
    {
        if (!double.IsFinite(thumbnailWidth + thumbnailHeight + grabX + grabY) ||
            thumbnailWidth <= 0 || thumbnailHeight <= 0 || originWidth <= 0 || originHeight <= 0 ||
            grabX is < 0 or > 1 || grabY is < 0 or > 1)
            throw new ArgumentException("A drag preview needs positive geometry and a grab point inside the thumbnail.");
        double w = Math.Min(thumbnailWidth, originWidth * .7);
        double h = w * thumbnailHeight / thumbnailWidth;
        if (h > originHeight * .7) { w *= originHeight * .7 / h; h = originHeight * .7; }
        width = Math.Max(1, checked((int)Math.Round(w, MidpointRounding.AwayFromZero)));
        height = Math.Max(1, checked((int)Math.Round(h, MidpointRounding.AwayFromZero)));
        this.grabX = checked((int)Math.Round(width * grabX, MidpointRounding.AwayFromZero));
        this.grabY = checked((int)Math.Round(height * grabY, MidpointRounding.AwayFromZero));
    }

    internal PhysicalWindowRect At(int pointerX, int pointerY)
    {
        int left = checked(pointerX - grabX), top = checked(pointerY - grabY);
        return new() { Left = left, Top = top, Right = checked(left + width), Bottom = checked(top + height) };
    }

    internal static bool Contains(PhysicalWindowRect monitor, int x, int y) =>
        x >= monitor.Left && x < monitor.Right && y >= monitor.Top && y < monitor.Bottom;

    internal static DragPreviewFragment? Clip(PhysicalWindowRect image, PhysicalWindowRect monitor,
        int sourceWidth, int sourceHeight)
    {
        long w = (long)image.Right - image.Left, h = (long)image.Bottom - image.Top;
        if (w <= 0 || h <= 0 || sourceWidth <= 0 || sourceHeight <= 0 ||
            monitor.Right <= monitor.Left || monitor.Bottom <= monitor.Top)
            throw new ArgumentException("Drag source and destination rectangles must be positive.");
        int left = Math.Max(image.Left, monitor.Left), top = Math.Max(image.Top, monitor.Top);
        int right = Math.Min(image.Right, monitor.Right), bottom = Math.Min(image.Bottom, monitor.Bottom);
        if (left >= right || top >= bottom) return null;
        // Shared desktop edges map to the same source edge on both surfaces.
        static int Map(long offset, int source, long extent) => checked((int)((offset * source + extent / 2) / extent));
        var sourceRect = new PhysicalWindowRect
        {
            Left = Map((long)left - image.Left, sourceWidth, w),
            Top = Map((long)top - image.Top, sourceHeight, h),
            Right = Map((long)right - image.Left, sourceWidth, w),
            Bottom = Map((long)bottom - image.Top, sourceHeight, h)
        };
        if (sourceRect.Left == sourceRect.Right || sourceRect.Top == sourceRect.Bottom)
            throw new InvalidOperationException("The drag source is too small to represent the visible fragment.");
        return new(new() { Left = left - monitor.Left, Top = top - monitor.Top,
            Right = right - monitor.Left, Bottom = bottom - monitor.Top }, sourceRect);
    }
}
