using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowScatter
{
    internal class WindowLayoutCalculator
    {
        internal const double SideInset = 14;
        internal const double BottomInset = 36;
        internal const double ToolbarHeight = 64;
        private readonly Dictionary<WindowActivationTarget, (PhysicalWindowRect Source, double X, double Y)> previous = new();
        private (double Width, double Height, double Dpi, string Size) previousContext;
        private double previousScale;

        internal void KeepRegisteredIdentities(IEnumerable<WindowActivationTarget> identities)
        {
            var registered = identities.ToHashSet();
            foreach (var identity in previous.Keys.Where(i => !registered.Contains(i)).ToArray()) previous.Remove(identity);
        }
        public List<WindowLayout> CalculateLayouts(List<WindowInfo> windows, double screenW, double screenH,
            double dpiScale = 1, string thumbnailSize = "Standard")
        {
            if (!double.IsFinite(screenW + screenH + dpiScale) || screenW <= 0 || screenH <= 0 || dpiScale <= 0)
                throw new ArgumentOutOfRangeException(nameof(screenW), "Screen dimensions and DPI must be finite and positive.");
            double maximumScale = thumbnailSize switch
            {
                "Compact" => .35,
                "Standard" => .5,
                "Large" => .75,
                _ => throw new ArgumentException("Unsupported thumbnail size.", nameof(thumbnailSize))
            };
            var context = (screenW, screenH, dpiScale, thumbnailSize);
            if (previousContext != context) previous.Clear();
            if (windows.Count == 0) { previous.Clear(); return new(); }
            if (windows.Any(w => Width(w) <= 0 || Height(w) <= 0))
                throw new ArgumentException("Window dimensions must be positive.", nameof(windows));

            double padding = Math.Min(16 * dpiScale, Math.Min(screenW, screenH) * .025);
            double side = SideInset * dpiScale + 1;
            double bottom = BottomInset * dpiScale + 1;
            double left = padding + side, top = ToolbarHeight * dpiScale + side;
            double availableW = screenW - padding - side - left;
            double availableH = screenH - padding - bottom - top;
            // Compact rows supply only a feasible fallback and lower retry bound.
            var ordered = SpatialOrder(windows);
            double bestScale = 0, bestShape = double.PositiveInfinity;
            int bestColumns = 0;
            for (int columns = 1; columns <= ordered.Count; columns++)
            {
                int fullRows = (ordered.Count - 1) / columns;
                int lastColumns = ordered.Count - fullRows * columns;
                double height = 0, widestFullRow = 0, lastRowWidth = 0;
                for (int i = 0; i < ordered.Count;)
                {
                    double rowWidth = 0, rowHeight = 0;
                    int end = i + Math.Min(columns, ordered.Count - i);
                    for (; i < end; i++)
                    {
                        rowWidth += Width(ordered[i]);
                        rowHeight = Math.Max(rowHeight, Height(ordered[i]));
                    }
                    height += rowHeight;
                    if (i == ordered.Count) lastRowWidth = rowWidth;
                    else widestFullRow = Math.Max(widestFullRow, rowWidth);
                }
                // Full rows have the same gaps; only the widest and the final row can constrain scale.
                double fullGaps = (columns - 1) * 2 * side, lastGaps = (lastColumns - 1) * 2 * side;
                double verticalGaps = fullRows * (side + bottom);
                double scale = Math.Min(maximumScale, (availableH - verticalGaps) / height);
                if (fullRows > 0) scale = Math.Min(scale, (availableW - fullGaps) / widestFullRow);
                scale = Math.Min(scale, (availableW - lastGaps) / lastRowWidth);
                if (scale <= 0) continue;
                double groupW = lastRowWidth * scale + lastGaps;
                if (fullRows > 0) groupW = Math.Max(groupW, widestFullRow * scale + fullGaps);
                double groupH = height * scale + verticalGaps;
                double shape = Math.Abs(Math.Log(groupW / groupH / (availableW / availableH)));
                if (scale > bestScale + 1e-10 || (Math.Abs(scale - bestScale) <= 1e-10 && shape < bestShape))
                {
                    bestScale = scale;
                    bestShape = shape;
                    bestColumns = columns;
                }
            }
            if (bestColumns == 0)
                throw new InvalidOperationException("The monitor cannot fit the overview controls and window labels.");

            var searchOrder = windows.OrderByDescending(w => Width(w) * Height(w))
                .ThenBy(w => w.OriginalRect.Left + Width(w) / 2)
                .ThenBy(w => w.OriginalRect.Top + Height(w) / 2)
                .ThenBy(w => w.Handle.ToInt64()).ToList();
            double searchScale = Math.Min(maximumScale,
                windows.Min(w => Math.Min(availableW / Width(w), availableH / Height(w))));
            var retained = windows.Where(w => w.Identity.Handle == w.Handle && w.Identity.ThreadId != 0 &&
                w.Identity.ProcessId != 0 && previous.TryGetValue(w.Identity, out var old) &&
                old.Source.Equals(w.OriginalRect)).ToDictionary(w => w.Identity, w => previous[w.Identity]);
            if (retained.Count > 0 && previousScale > 0 && previousScale <= searchScale)
            {
                var stable = TryPlace(searchOrder, previousScale, left, top, availableW, availableH, side, bottom, retained);
                if (stable != null) return Remember(stable, context, previousScale);
            }
            while (true)
            {
                var placed = TryPlace(searchOrder, searchScale, left, top, availableW, availableH, side, bottom);
                if (placed != null) return Remember(placed, context, searchScale);
                // Greedy search is incomplete; the compact row fit bounds retries.
                if (searchScale <= bestScale)
                    return Remember(PlaceGrid(ordered, bestScale, bestColumns, left, top, availableW, availableH, dpiScale),
                        context, bestScale);
                searchScale = Math.Max(bestScale, searchScale * .9);
            }

        }

        private List<WindowLayout> Remember(List<WindowLayout> layouts,
            (double Width, double Height, double Dpi, string Size) context, double scale)
        {
            previous.Clear();
            foreach (var layout in layouts)
                if (layout.Window.Identity.Handle == layout.Window.Handle && layout.Window.Identity.ThreadId != 0 &&
                    layout.Window.Identity.ProcessId != 0)
                    previous.Add(layout.Window.Identity, (layout.Window.OriginalRect, layout.X, layout.Y));
            previousContext = context;
            previousScale = scale;
            return layouts;
        }

        private static double Width(WindowInfo w) => (double)w.OriginalRect.Right - w.OriginalRect.Left;
        private static double Height(WindowInfo w) => (double)w.OriginalRect.Bottom - w.OriginalRect.Top;
        private static List<WindowInfo> SpatialOrder(List<WindowInfo> windows) => windows
            .OrderBy(w => w.OriginalRect.Top + Height(w) / 2)
            .ThenBy(w => w.OriginalRect.Left + Width(w) / 2)
            .ThenBy(w => w.Handle.ToInt64()).ToList();

        private static List<WindowLayout>? TryPlace(List<WindowInfo> windows, double scale,
            double left, double top, double availableW, double availableH, double side, double bottom,
            Dictionary<WindowActivationTarget, (PhysicalWindowRect Source, double X, double Y)>? retained = null)
        {
            var anchors = windows.Select(w => new WindowLayout
            {
                Window = w, OrigWidth = Width(w), OrigHeight = Height(w),
                Width = Width(w) * scale, Height = Height(w) * scale,
                X = Math.Clamp(w.OriginalRect.Left + Width(w) * (1 - scale) / 2,
                    left, Math.Max(left, left + availableW - Width(w) * scale)),
                Y = Math.Clamp(w.OriginalRect.Top + Height(w) * (1 - scale) / 2,
                    top, Math.Max(top, top + availableH - Height(w) * scale))
            }).ToList();
            var placed = new List<WindowLayout>();
            foreach (var a in anchors)
                if (retained != null && retained.TryGetValue(a.Window.Identity, out var old))
                {
                    if (!double.IsFinite(old.X + old.Y) || old.X < left || old.Y < top ||
                        old.X + a.Width > left + availableW + 1e-7 ||
                        old.Y + a.Height > top + availableH + 1e-7) return null;
                    a.X = old.X; a.Y = old.Y;
                    if (OverlapsAny(a.X, a.Y, a, placed, side, bottom)) return null;
                    placed.Add(a);
                }
            // Reserve independent anchors before moving colliding windows into free slots.
            foreach (var anchor in anchors)
                if (!placed.Contains(anchor) && !OverlapsAny(anchor.X, anchor.Y, anchor, anchors, side, bottom))
                    placed.Add(anchor);
            if (placed.Count == anchors.Count) return anchors;
            var originals = anchors.Select(a => (a.X, a.Y)).ToArray();
            var independent = placed.ToList();
            var grouped = TryPlaceGroups(anchors, placed, left, top, availableW, availableH, side, bottom);
            if (grouped != null) return grouped;
            bool keptGroups = placed.Count > independent.Count;
            if (TryPlaceIndividuals(anchors, placed, left, top, availableW, availableH, side, bottom)) return anchors;
            if (!keptGroups) return null;
            // A compact component must not make an otherwise feasible individual fit fail.
            for (int i = 0; i < anchors.Count; i++) (anchors[i].X, anchors[i].Y) = originals[i];
            return TryPlaceIndividuals(anchors, independent, left, top, availableW, availableH, side, bottom) ? anchors : null;
        }

        private static bool TryPlaceIndividuals(List<WindowLayout> anchors, List<WindowLayout> placed,
            double left, double top, double availableW, double availableH, double side, double bottom)
        {
            foreach (var layout in anchors)
            {
                if (placed.Contains(layout)) continue;
                double anchorX = layout.X, anchorY = layout.Y;
                double maxX = Math.Max(left, left + availableW - layout.Width);
                double maxY = Math.Max(top, top + availableH - layout.Height);
                var xs = new List<double> { anchorX, left, maxX };
                var ys = new List<double> { anchorY, top, maxY };
                foreach (var other in placed)
                {
                    xs.AddRange(new[] { other.X - layout.Width - 2 * side, other.X + other.Width + 2 * side });
                    ys.AddRange(new[] { other.Y - layout.Height - side - bottom, other.Y + other.Height + side + bottom });
                }
                double bestDistance = double.PositiveInfinity;
                bool found = false;
                var candidateXs = xs.Select(x => Math.Clamp(x, left, maxX)).Distinct().OrderBy(x => x).ToArray();
                var candidateYs = ys.Select(y => Math.Clamp(y, top, maxY)).Distinct().OrderBy(y => y).ToArray();
                foreach (double x in candidateXs)
                foreach (double y in candidateYs)
                {
                    double distance = (x - anchorX) * (x - anchorX) + (y - anchorY) * (y - anchorY);
                    if (distance >= bestDistance || OverlapsAny(x, y, layout, placed, side, bottom)) continue;
                    bestDistance = distance;
                    layout.X = x; layout.Y = y;
                    found = true;
                }
                if (!found) return false;
                placed.Add(layout);
            }
            return true;
        }

        private static List<WindowLayout>? TryPlaceGroups(List<WindowLayout> anchors, List<WindowLayout> independent,
            double left, double top, double availableW, double availableH, double side, double bottom)
        {
            var remaining = anchors.Except(independent).ToList();
            var placed = independent.ToList();
            var positions = new Dictionary<WindowLayout, (double X, double Y)>();
            while (remaining.Count > 0)
            {
                var group = new List<WindowLayout> { remaining[0] };
                remaining.RemoveAt(0);
                for (int i = 0; i < group.Count; i++)
                    foreach (var other in remaining.ToArray())
                        if (Overlaps(group[i].X, group[i].Y, group[i], other, side, bottom))
                        {
                            group.Add(other);
                            remaining.Remove(other);
                        }
                group = group.OrderBy(a => a.Window.OriginalRect.Top + a.OrigHeight / 2)
                    .ThenBy(a => a.Window.OriginalRect.Left + a.OrigWidth / 2)
                    .ThenBy(a => a.Window.Handle.ToInt64()).ToList();
                double bestDistance = double.PositiveInfinity;
                (Dictionary<WindowLayout, (double X, double Y)> Offsets, double X, double Y)? best = null;
                // Bounded compact row shapes; an unfittable envelope keeps the existing fallback.
                for (int columns = 1; columns <= group.Count; columns++)
                {
                    var rows = group.Chunk(columns).ToArray();
                    double groupW = rows.Max(row => row.Sum(a => a.Width) + (row.Length - 1) * 2 * side);
                    double groupH = rows.Sum(row => row.Max(a => a.Height)) + (rows.Length - 1) * (side + bottom);
                    if (groupW > availableW || groupH > availableH) continue;
                    var offsets = new Dictionary<WindowLayout, (double X, double Y)>();
                    double rowY = 0;
                    foreach (var row in rows)
                    {
                        double rowH = row.Max(a => a.Height);
                        double rowW = row.Sum(a => a.Width) + (row.Length - 1) * 2 * side;
                        double rowX = (groupW - rowW) / 2;
                        foreach (var a in row.OrderBy(a => a.Window.OriginalRect.Left + a.OrigWidth / 2)
                            .ThenBy(a => a.Window.Handle.ToInt64()))
                        {
                            offsets[a] = (rowX, rowY + (rowH - a.Height) / 2);
                            rowX += a.Width + 2 * side;
                        }
                        rowY += rowH + side + bottom;
                    }
                    double anchorX = group.Average(a => a.X - offsets[a].X);
                    double anchorY = group.Average(a => a.Y - offsets[a].Y);
                    double maxX = Math.Max(left, left + availableW - groupW);
                    double maxY = Math.Max(top, top + availableH - groupH);
                    var xs = new List<double> { anchorX, left, maxX };
                    var ys = new List<double> { anchorY, top, maxY };
                    foreach (var other in placed)
                    {
                        xs.AddRange(new[] { other.X - groupW - 2 * side, other.X + other.Width + 2 * side });
                        ys.AddRange(new[] { other.Y - groupH - side - bottom, other.Y + other.Height + side + bottom });
                    }
                    var envelope = new WindowLayout { Width = groupW, Height = groupH };
                    var candidateXs = xs.Select(x => Math.Clamp(x, left, maxX)).Distinct().OrderBy(x => x).ToArray();
                    var candidateYs = ys.Select(y => Math.Clamp(y, top, maxY)).Distinct().OrderBy(y => y).ToArray();
                    foreach (double x in candidateXs)
                    foreach (double y in candidateYs)
                    {
                        if (OverlapsAny(x, y, envelope, placed, side, bottom)) continue;
                        double distance = 0;
                        foreach (var a in group)
                        {
                            var offset = offsets[a];
                            distance += Math.Pow(x + offset.X - a.X, 2) + Math.Pow(y + offset.Y - a.Y, 2);
                        }
                        if (distance >= bestDistance) continue;
                        bestDistance = distance;
                        best = (offsets, x, y);
                    }
                }
                if (best is not { } placement) continue;
                foreach (var a in group)
                {
                    var offset = placement.Offsets[a];
                    var position = (X: placement.X + offset.X, Y: placement.Y + offset.Y);
                    positions[a] = position;
                    placed.Add(new WindowLayout { X = position.X, Y = position.Y, Width = a.Width, Height = a.Height });
                }
            }
            foreach (var (layout, position) in positions)
            {
                layout.X = position.X;
                layout.Y = position.Y;
                independent.Add(layout);
            }
            return independent.Count == anchors.Count ? anchors : null;
        }

        private static bool OverlapsAny(double x, double y, WindowLayout layout, List<WindowLayout> others,
            double side, double bottom)
        {
            foreach (var other in others)
                if (!ReferenceEquals(layout, other) && Overlaps(x, y, layout, other, side, bottom)) return true;
            return false;
        }

        private static bool Overlaps(double x, double y, WindowLayout layout, WindowLayout other, double side, double bottom)
            => x < other.X + other.Width + 2 * side - 1e-7 &&
               x + layout.Width + 2 * side > other.X + 1e-7 &&
               y < other.Y + other.Height + side + bottom - 1e-7 &&
               y + layout.Height + side + bottom > other.Y + 1e-7;

        internal static List<WindowLayout> PlaceGrid(List<WindowInfo> windows, double scale, int columns,
            double left, double top, double availableW, double availableH, double dpiScale = 1)
        {
            double horizontalGap = 2 * (SideInset * dpiScale + 1);
            double verticalGap = (SideInset + BottomInset) * dpiScale + 2;
            var rows = SpatialOrder(windows).Chunk(columns).ToArray();
            double groupH = rows.Sum(row => row.Max(Height) * scale) + (rows.Length - 1) * verticalGap;
            double y = top + (availableH - groupH) / 2;
            var result = new List<WindowLayout>();
            foreach (var row in rows)
            {
                double rowH = row.Max(Height) * scale;
                double rowW = row.Sum(Width) * scale + (row.Length - 1) * horizontalGap;
                double x = left + (availableW - rowW) / 2;
                foreach (var w in row.OrderBy(w => w.OriginalRect.Left + Width(w) / 2).ThenBy(w => w.Handle.ToInt64()))
                {
                    result.Add(new WindowLayout
                    {
                        Window = w, OrigWidth = Width(w), OrigHeight = Height(w),
                        Width = Width(w) * scale, Height = Height(w) * scale,
                        X = x, Y = y + (rowH - Height(w) * scale) / 2
                    });
                    x += Width(w) * scale + horizontalGap;
                }
                y += rowH + verticalGap;
            }
            return result;
        }
    }
}
