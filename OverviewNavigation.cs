namespace WindowScatter
{
    internal readonly record struct NavigationTarget(WindowActivationTarget Identity, double X, double Y);

    internal static class OverviewNavigation
    {
        internal static IntPtr Select(IReadOnlyList<NavigationTarget> targets, IntPtr selected, int dx, int dy)
        {
            if (targets.Count == 0) return IntPtr.Zero;
            int index = -1;
            for (int i = 0; i < targets.Count; i++)
                if (targets[i].Identity.Handle == selected) { index = i; break; }

            var current = targets[index < 0 ? 0 : index];
            var candidates = targets
                .Where(t => t.Identity.Handle != current.Identity.Handle)
                .Select(t => new { Target = t, X = t.X - current.X, Y = t.Y - current.Y })
                .Where(c => (dx == 0 || Math.Sign(c.X) == dx) && (dy == 0 || Math.Sign(c.Y) == dy))
                .OrderBy(c => c.X * c.X + c.Y * c.Y)
                .ThenBy(c => dx != 0 ? Math.Abs(c.Y) : Math.Abs(c.X))
                .ThenBy(c => c.Target.Identity.Handle.ToInt64())
                .ThenBy(c => c.Target.Identity.ProcessId)
                .ThenBy(c => c.Target.Identity.ThreadId);
            var candidate = candidates.FirstOrDefault();
            if (candidate != null) return candidate.Target.Identity.Handle;

            int direction = dx + dy > 0 ? 1 : -1;
            if (index < 0) index = direction > 0 ? -1 : 0;
            return targets[(index + direction + targets.Count) % targets.Count].Identity.Handle;
        }
    }
}
