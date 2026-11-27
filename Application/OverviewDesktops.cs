namespace WindowScatter;

internal sealed record DesktopSnapshot(Guid[] Ordered, Guid Active, string Epoch);
internal sealed record DesktopRead(DesktopSnapshot? Snapshot, string? Unavailable);
internal enum DesktopChange { Confirmed, Rejected, Unconfirmed }

internal interface IOverviewDesktops
{
    Task<DesktopRead> ReadAsync();
    Task<DesktopChange> SwitchAsync(DesktopSnapshot snapshot, Guid target);
    // Revalidate the snapshot epoch, window identity and source desktop before dispatch; confirm by readback.
    Task<DesktopChange> MoveAsync(DesktopSnapshot snapshot, WindowActivationTarget window, Guid target);
}

internal sealed class UnavailableOverviewDesktops : IOverviewDesktops
{
    internal const string Reason = "Unavailable on this Windows build. Use Windows+Tab to manage desktops.";
    public Task<DesktopRead> ReadAsync() => Task.FromResult(new DesktopRead(null, Reason));
    public Task<DesktopChange> SwitchAsync(DesktopSnapshot snapshot, Guid target) => Task.FromResult(DesktopChange.Rejected);
    public Task<DesktopChange> MoveAsync(DesktopSnapshot snapshot, WindowActivationTarget window, Guid target) =>
        Task.FromResult(DesktopChange.Rejected);
}

// Shared by the overview surfaces. Provider implementations must bound native work off the dispatcher.
internal sealed class OverviewDesktopState(IOverviewDesktops provider)
{
    private int generation;
    internal DesktopSnapshot? Snapshot { get; private set; }
    internal string? Unavailable { get; private set; }
    internal bool Pending { get; private set; }

    internal void Close()
    {
        generation++;
        Snapshot = null;
        Unavailable = null;
    }

    internal async Task OpenAsync()
    {
        Close();
        int version = generation;
        DesktopRead result;
        try { result = await provider.ReadAsync(); }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError($"Could not read the desktop list: {error}");
            if (version == generation) Unavailable = "Windows did not return the desktop list.";
            return;
        }
        if (version != generation) return;
        var snapshot = result.Snapshot;
        if (snapshot is not null && snapshot.Active != Guid.Empty && snapshot.Ordered.Contains(snapshot.Active)
            && snapshot.Ordered.All(id => id != Guid.Empty) && snapshot.Ordered.Distinct().Count() == snapshot.Ordered.Length
            && !string.IsNullOrWhiteSpace(snapshot.Epoch))
            Snapshot = snapshot with { Ordered = (Guid[])snapshot.Ordered.Clone() };
        else
            Unavailable = result.Unavailable ?? "Windows did not return a valid desktop list.";
    }

    internal async Task<DesktopChange?> ChangeAsync(Guid target, WindowActivationTarget? window)
    {
        if (Pending || Snapshot is not { } snapshot || target == snapshot.Active || !snapshot.Ordered.Contains(target))
            return null;
        Pending = true;
        int version = generation;
        try
        {
            var result = window is { } selection
                ? await provider.MoveAsync(snapshot, selection, target)
                : await provider.SwitchAsync(snapshot, target);
            if (version != generation) return null;
            if (result == DesktopChange.Unconfirmed)
            {
                Snapshot = null;
                Unavailable = "Windows did not confirm the desktop action. Reopen Overview to refresh.";
            }
            else if (result == DesktopChange.Confirmed && window is null)
                Snapshot = snapshot with { Active = target };
            return result;
        }
        finally { Pending = false; }
    }
}
