using static WindowScatter.Win32Interop;

namespace WindowScatter
{
    internal static class WindowStacking
    {
        public static List<IntPtr> Capture()
        {
            var result = new List<IntPtr>();
            var visited = new HashSet<IntPtr>();
            for (IntPtr h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDNEXT))
            {
                if (!visited.Add(h) || result.Count >= 8192)
                    throw new InvalidOperationException("The native window stacking order changed during capture.");
                result.Add(h);
            }
            return result;
        }

        internal static IEnumerable<T> BackToFront<T>(IEnumerable<T> items, Func<T, IntPtr> handle,
            IReadOnlyList<IntPtr> frontToBack, IntPtr raised = default)
        {
            var rank = frontToBack.Select((h, i) => (h, i)).ToDictionary(x => x.h, x => x.i);
            return items.OrderBy(x => handle(x) == raised && raised != IntPtr.Zero ? 1 : 0)
                .ThenByDescending(x => rank.GetValueOrDefault(handle(x), int.MaxValue)).ToArray();
        }
    }
}
