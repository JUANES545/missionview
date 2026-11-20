namespace WindowScatter
{
    internal sealed class HeldPreviewState
    {
        public bool IsHeld { get; private set; }
        public bool Press(bool isRepeat)
        {
            if (isRepeat || IsHeld) return false;
            IsHeld = true;
            return true;
        }
        public bool Release()
        {
            bool held = IsHeld;
            IsHeld = false;
            return held;
        }
    }

    internal readonly record struct PreviewBounds(double X, double Y, double Width, double Height)
    {
        public static PreviewBounds Interpolate(PreviewBounds start, PreviewBounds end, double progress)
        {
            if (progress >= 1) return end;
            if (progress <= 0) return start;
            double t = 1 - Math.Pow(1 - progress, 3);
            return new(start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t,
                start.Width + (end.Width - start.Width) * t, start.Height + (end.Height - start.Height) * t);
        }
    }

    internal static class OverviewPreview
    {
        public static (double X, double Y, double Width, double Height) Fit(
            double sourceWidth, double sourceHeight, double monitorWidth, double monitorHeight, double topInset = 0,
            double dpiScale = 1)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0 || monitorWidth <= 0 || monitorHeight <= 0 ||
                topInset < 0 || topInset >= monitorHeight ||
                dpiScale <= 0 ||
                !double.IsFinite(sourceWidth + sourceHeight + monitorWidth + monitorHeight + topInset + dpiScale))
                throw new ArgumentOutOfRangeException(nameof(sourceWidth), "Preview geometry must be finite and positive.");

            double side = WindowLayoutCalculator.SideInset * dpiScale + 1;
            double bottom = WindowLayoutCalculator.BottomInset * dpiScale + 1;
            double availableWidth = monitorWidth - 2 * side;
            double availableHeight = monitorHeight - topInset - side - bottom;
            if (availableWidth <= 0 || availableHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(monitorHeight), "The monitor cannot fit preview controls.");
            double scale = Math.Min(1, Math.Min(availableWidth * .9 / sourceWidth, availableHeight * .8 / sourceHeight));
            double width = sourceWidth * scale;
            double height = sourceHeight * scale;
            return ((monitorWidth - width) / 2, topInset + side + (availableHeight - height) / 2, width, height);
        }

        public static IntPtr InitialSelection(IntPtr previous, IEnumerable<IntPtr> windows)
            => windows.Contains(previous) ? previous : IntPtr.Zero;
    }
}
