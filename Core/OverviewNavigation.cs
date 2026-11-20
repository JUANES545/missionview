namespace WindowScatter
{
    internal readonly record struct NavigationTarget(WindowActivationTarget Identity, PreviewBounds Bounds)
    {
        public bool IsValid => Identity.Handle != IntPtr.Zero && Identity.ThreadId != 0 && Identity.ProcessId != 0 &&
            Bounds.Width > 0 && Bounds.Height > 0 &&
            double.IsFinite(Bounds.X + Bounds.Y + Bounds.Width + Bounds.Height);
    }

    internal static class OverviewNavigation
    {
        internal static IntPtr Select(IReadOnlyList<NavigationTarget> targets, IntPtr selected, int dx, int dy)
        {
            var valid = targets.Where(t => t.IsValid).ToArray();
            if (valid.Length == 0) return IntPtr.Zero;
            var current = valid.FirstOrDefault(t => t.Identity.Handle == selected);
            if (!current.IsValid) return valid[0].Identity.Handle;
            var origin = current.Bounds;
            var candidates = valid.Where(t => t.Identity != current.Identity).Select(t =>
            {
                var b = t.Bounds;
                double gap = dx > 0 ? b.X - origin.X - origin.Width : dx < 0 ? origin.X - b.X - b.Width :
                    dy > 0 ? b.Y - origin.Y - origin.Height : origin.Y - b.Y - b.Height;
                double overlap = dx != 0
                    ? Math.Min(origin.Y + origin.Height, b.Y + b.Height) - Math.Max(origin.Y, b.Y)
                    : Math.Min(origin.X + origin.Width, b.X + b.Width) - Math.Max(origin.X, b.X);
                double offset = dx != 0 ? Math.Abs(b.Y + b.Height / 2 - origin.Y - origin.Height / 2) :
                    Math.Abs(b.X + b.Width / 2 - origin.X - origin.Width / 2);
                double perpendicularGap = Math.Max(0, -overlap);
                return new { Target = t, Gap = gap, Beam = overlap > 0, Offset = offset,
                    Distance = gap * gap + perpendicularGap * perpendicularGap };
            }).Where(c => c.Gap >= 0)
                .OrderBy(c => c.Beam ? 0 : 1)
                .ThenBy(c => c.Beam ? c.Gap : c.Distance)
                .ThenBy(c => c.Gap).ThenBy(c => c.Offset)
                .ThenBy(c => c.Target.Identity.Handle.ToInt64())
                .ThenBy(c => c.Target.Identity.ProcessId).ThenBy(c => c.Target.Identity.ThreadId)
                .FirstOrDefault();
            return candidates?.Target.Identity.Handle ?? selected;
        }

        internal static IReadOnlyList<NavigationTarget> VisualOrder(IEnumerable<NavigationTarget> targets)
        {
            var remaining = targets.Where(t => t.IsValid).OrderBy(t => t.Bounds.Y).ThenBy(t => t.Bounds.X)
                .ThenBy(t => t.Identity.Handle.ToInt64()).ThenBy(t => t.Identity.ProcessId)
                .ThenBy(t => t.Identity.ThreadId).ToList();
            var ordered = new List<NavigationTarget>();
            while (remaining.Count != 0)
            {
                double top = remaining[0].Bounds.Y, bottom = top + remaining[0].Bounds.Height;
                var row = new List<NavigationTarget>();
                foreach (var target in remaining)
                {
                    if (target.Bounds.Y >= bottom) break;
                    if (target.Bounds.Y + target.Bounds.Height <= top) continue;
                    row.Add(target);
                    // A shared vertical band avoids chaining separate rows through a tall tile.
                    top = Math.Max(top, target.Bounds.Y);
                    bottom = Math.Min(bottom, target.Bounds.Y + target.Bounds.Height);
                }
                ordered.AddRange(row.OrderBy(t => t.Bounds.X).ThenBy(t => t.Bounds.Y)
                    .ThenBy(t => t.Identity.Handle.ToInt64()).ThenBy(t => t.Identity.ProcessId)
                    .ThenBy(t => t.Identity.ThreadId));
                foreach (var target in row) remaining.Remove(target);
            }
            return ordered;
        }

        internal static IntPtr SelectTab(IReadOnlyList<WindowActivationTarget> order, IntPtr selected, int direction)
        {
            if (order.Count == 0) return IntPtr.Zero;
            int current = -1;
            for (int i = 0; i < order.Count; i++)
                if (order[i].Handle == selected) { current = i; break; }
            if (current < 0) return order[direction > 0 ? 0 : order.Count - 1].Handle;
            return order[(current + direction + order.Count) % order.Count].Handle;
        }
    }
}
