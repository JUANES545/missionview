namespace WindowScatter;

// Immutable UI-thread snapshot. Geometry is desktop physical pixels except minimized content coordinates.
internal sealed record OverviewDisplaySnapshot(IntPtr Display, PreviewBounds Bounds,
    IReadOnlyList<NavigationTarget> Normal, IReadOnlyList<NavigationTarget> MinimizedContent,
    IReadOnlyList<NavigationTarget> MinimizedVisible)
{
    internal IEnumerable<NavigationTarget> Visible => OverviewNavigation.VisualOrder(Normal).Concat(MinimizedVisible);
}

internal readonly record struct OverviewSelection(IntPtr Handle, bool Keyboard, bool RevealMinimized);
internal enum OverviewCloseRequest { None, Cancel, ConfirmSelection }

// One instance per overview, shared by every display. Its caller serializes access on the UI thread.
internal sealed class OverviewSession
{
    public ScatterInteraction Interaction { get; } = new();
    public HeldPreviewState PreviewKey { get; } = new();
    public IntPtr Selection { get; private set; }
    public bool Opening { get; set; }
    public bool Transitioning { get; set; }
    public bool NavigationPending { get; private set; }
    private IReadOnlyList<WindowActivationTarget>? tabOrder;
    private int navigationVersion;

    public bool Select(IntPtr handle)
    {
        bool changed = Selection != handle;
        Selection = handle;
        return changed;
    }

    private static IEnumerable<OverviewDisplaySnapshot> Ordered(IReadOnlyList<OverviewDisplaySnapshot> displays) =>
        OverviewNavigation.VisualOrder(displays.Select(d => new NavigationTarget(new(d.Display, 1, 1), d.Bounds)))
            .Select(t => displays.Single(d => d.Display == t.Identity.Handle));

    public OverviewSelection SelectTab(IReadOnlyList<OverviewDisplaySnapshot> displays, int direction)
    {
        tabOrder ??= Ordered(displays).SelectMany(d => OverviewNavigation.VisualOrder(d.Normal).Concat(
            OverviewNavigation.VisualOrder(d.MinimizedContent))).Select(t => t.Identity).ToArray();
        return new(OverviewNavigation.SelectTab(tabOrder, Selection, direction), true, true);
    }

    public OverviewSelection SelectDirection(IReadOnlyList<OverviewDisplaySnapshot> displays, IntPtr activeDisplay, int dx, int dy)
    {
        var source = displays.FirstOrDefault(d => d.MinimizedContent.Any(t => t.Identity.Handle == Selection));
        if (source != null)
        {
            var next = OverviewNavigation.Select(source.MinimizedContent, Selection, dx, dy);
            if (next != Selection && next != IntPtr.Zero) return new(next, true, true);
            // A valid but manually scrolled-out selection is not a lost selection.
            if (!source.MinimizedVisible.Any(t => t.Identity.Handle == Selection)) return new(Selection, true, false);
        }
        var first = displays.First(d => d.Display == activeDisplay);
        var targets = first.Visible.Concat(Ordered(displays).Where(d => d != first).SelectMany(d => d.Visible)).ToArray();
        return new(OverviewNavigation.Select(targets, Selection, dx, dy), true, false);
    }

    public bool TryBeginClose(WindowActivationTarget? selection, bool busy, out WindowActivationTarget target)
    {
        target = default;
        if (busy || !Interaction.TryBeginClose(selection, out target)) return false;
        Transitioning = true;
        return true;
    }

    public OverviewCloseRequest TakeCloseRequest(bool busy, bool visible)
    {
        if (Interaction.TakeConfirmationRequest(busy) && visible)
            return Selection != IntPtr.Zero ? OverviewCloseRequest.ConfirmSelection : OverviewCloseRequest.Cancel;
        if (Interaction.TakeDismissRequest(busy) && visible) return OverviewCloseRequest.Cancel;
        return OverviewCloseRequest.None;
    }

    public void CancelNavigation()
    {
        navigationVersion++;
        NavigationPending = false;
    }

    public void InvalidateNavigation()
    {
        tabOrder = null;
        CancelNavigation();
    }

    // Effects only: return animations invoke their completion; this owns ordering and stale-session rejection.
    public void Navigate(IReadOnlyList<Action<Action>> returnPreviews, Func<bool> canContinue,
        Action selectNeighbour, Action completed)
    {
        if (NavigationPending) return;
        int generation = Interaction.Generation, version = ++navigationVersion;
        void SelectNeighbour()
        {
            NavigationPending = true;
            try { selectNeighbour(); }
            finally { NavigationPending = false; }
            completed();
        }
        if (returnPreviews.Count == 0) SelectNeighbour();
        else
        {
            NavigationPending = true;
            var group = new OverviewReturnGroup(returnPreviews.Count,
                () => NavigationPending && version == navigationVersion &&
                    generation == Interaction.Generation && Interaction.IsOpen && canContinue(), SelectNeighbour);
            for (int i = 0; i < returnPreviews.Count; i++)
            {
                int index = i;
                returnPreviews[i](() => group.Returned(index));
            }
        }
    }
}
