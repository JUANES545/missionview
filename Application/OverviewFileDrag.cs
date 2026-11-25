namespace WindowScatter;

internal sealed class OverviewFileDrag
{
    internal sealed record Hover(WindowActivationTarget Target);

    public Hover? Current { get; private set; }
    public bool Cancelled { get; private set; }

    public void Observe(WindowActivationTarget? target)
    {
        if (Cancelled || !target.HasValue) Current = null;
        else if (Current?.Target != target) Current = new(target.Value);
    }

    public void Cancel()
    {
        Cancelled = true;
        Current = null;
    }

    public WindowActivationTarget? Confirm(Hover? atShortcut, bool primaryHeld, WindowActivationTarget? hit) =>
        !Cancelled && primaryHeld && atShortcut != null && ReferenceEquals(Current, atShortcut) &&
        atShortcut.Target == hit ? hit : null;
}
