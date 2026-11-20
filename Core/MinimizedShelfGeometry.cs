namespace WindowScatter
{
    internal readonly record struct MinimizedShelfGeometry(
        double Width, double Height, double Top, double ItemWidth, int Columns, int Rows, bool Scrolls, double Gutter)
    {
        public double ReservedHeight => Height == 0 ? 0 : Height + 24;
        public double ViewportHeight => Rows == 0 ? 0 : Rows * 54 - 8;

        internal static MinimizedShelfGeometry Calculate(double displayWidth, double displayHeight, int count,
            double scrollbarWidth)
        {
            if (count < 0 || !double.IsFinite(displayWidth + displayHeight + scrollbarWidth) ||
                displayWidth <= 0 || displayHeight <= 0 || scrollbarWidth < 0)
                throw new ArgumentOutOfRangeException(nameof(displayWidth), "Shelf dimensions must be finite and positive.");
            if (count == 0) return default;
            if (112 > displayHeight * .35)
                throw new InvalidOperationException("This display is too short to fit the minimized shelf and overview. Increase its available height or reduce display scaling.");
            double available = displayWidth - 32;
            int maxRows = 166 <= displayHeight * .35 ? 2 : 1;
            int columns = Math.Min(count, Math.Clamp((int)((available - 24 + 8) / 248), 1, 4));
            bool scrolls = count > columns * maxRows;
            double gutter = scrolls ? scrollbarWidth : 0;
            if (scrolls) columns = Math.Min(count, Math.Clamp((int)((available - 24 - gutter + 8) / 248), 1, 4));
            double itemWidth = Math.Min(240, (available - 24 - gutter - (columns - 1) * 8) / columns);
            // Preserve the icon, padding and selection mark rather than clipping an interactive target.
            if (itemWidth < 74)
                throw new InvalidOperationException("This display is too narrow to fit the minimized window controls. Increase its available width or reduce display scaling.");
            int rows = Math.Min(maxRows, (count + columns - 1) / columns);
            double width = 24 + columns * itemWidth + (columns - 1) * 8 + gutter;
            double height = 88 + (rows - 1) * 54;
            return new(width, height, displayHeight - height - 12, itemWidth, columns, rows, scrolls, gutter);
        }
    }
}
