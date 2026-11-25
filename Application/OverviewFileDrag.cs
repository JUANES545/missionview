namespace WindowScatter;

internal sealed class OverviewFileDrag
{
    internal sealed record Hover(IntPtr Display, WindowActivationTarget Target);

    public Hover? Current { get; private set; }
    public bool Cancelled { get; private set; }

    public void Observe(IntPtr display, WindowActivationTarget? target)
    {
        if (Cancelled) return;
        if (target is not { } window)
        {
            if (Current?.Display == display) Current = null;
        }
        else if (Current?.Display != display || Current.Target != window) Current = new(display, window);
    }

    public void Cancel()
    {
        Cancelled = true;
        Current = null;
    }

    public WindowActivationTarget? Confirm(Hover? atShortcut, bool primaryHeld, IntPtr display, WindowActivationTarget? hit) =>
        !Cancelled && primaryHeld && atShortcut != null && ReferenceEquals(Current, atShortcut) &&
        atShortcut.Display == display && atShortcut.Target == hit ? hit : null;
}
