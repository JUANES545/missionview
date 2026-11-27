using System.ComponentModel;
using System.Runtime.InteropServices;
using static WindowScatter.Win32Interop;

namespace WindowScatter;

internal sealed class WindowClose
{
    private readonly Func<IntPtr, WindowActivationTarget> identify;
    private readonly Func<IntPtr, (bool Queued, int Error)> post;
    private readonly Func<IntPtr, CloseWindowSurface> readSurface;
    private readonly Func<IntPtr, IntPtr> lastPopup;
    private readonly Func<IntPtr> foreground;
    private readonly HashSet<WindowActivationTarget> requested = [];
    private WindowActivationTarget? latestRequest;

    internal WindowClose() : this(new WindowActivation().Capture, handle =>
    {
        bool queued = PostMessage(handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        return (queued, queued ? 0 : Marshal.GetLastWin32Error());
    }, ReadSurface, GetLastActivePopup, GetForegroundWindow) { }

    internal WindowClose(Func<IntPtr, WindowActivationTarget> identify,
        Func<IntPtr, (bool Queued, int Error)> post,
        Func<IntPtr, CloseWindowSurface>? readSurface = null,
        Func<IntPtr, IntPtr>? lastPopup = null, Func<IntPtr>? foreground = null)
    {
        this.identify = identify;
        this.post = post;
        this.readSurface = readSurface ?? (handle => new(identify(handle), IntPtr.Zero, true, true));
        this.lastPopup = lastPopup ?? (_ => IntPtr.Zero);
        this.foreground = foreground ?? (() => IntPtr.Zero);
    }

    internal bool IsPending(WindowActivationTarget target) => requested.Contains(target);
    internal bool HasPending => requested.Count != 0;
    internal WindowActivationTarget? LatestRequest => latestRequest;

    internal bool IsReturnTargetValid(WindowActivationTarget target, WindowActivationTarget original)
    {
        if (original.Handle == IntPtr.Zero || original.ThreadId == 0 || original.ProcessId == 0 ||
            identify(original.Handle) != original) return false;
        var source = readSurface(original.Handle);
        if (source.Identity != original) return false;
        return target == original ? source.Visible && identify(target.Handle) == target :
            IsOwnedAttention(target.Handle, source) == target;
    }

    internal WindowActivationTarget? ResolveReturnTarget()
    {
        var attention = FindAttentionTarget(includeRequestedForeground: true);
        if (attention.HasValue) return attention;
        return latestRequest is { } target && requested.Contains(target) && identify(target.Handle) == target &&
            readSurface(target.Handle) is { Visible: true } surface && surface.Identity == target
            ? target : null;
    }

    internal WindowActivationTarget? FindAttentionTarget(bool includeRequestedForeground = false)
    {
        if (latestRequest is not { } target || !requested.Contains(target) || identify(target.Handle) != target) return null;
        var source = readSurface(target.Handle);
        if (source.Identity != target) return null;
        IntPtr active = foreground();
        if (active == target.Handle)
            return includeRequestedForeground && source.Visible && readSurface(target.Handle) == source && foreground() == active
                ? target : null;
        if (active != IntPtr.Zero && IsOwnedAttention(active, source) is { } focused && foreground() == active)
            return focused;
        if (source.Enabled) return null;
        IntPtr popup = lastPopup(target.Handle);
        if (popup == IntPtr.Zero || popup == target.Handle) return null;
        var owned = IsOwnedAttention(popup, source);
        return owned.HasValue && lastPopup(target.Handle) == popup && readSurface(target.Handle) == source
            ? owned : null;
    }

    private WindowActivationTarget? IsOwnedAttention(IntPtr handle, CloseWindowSurface source)
    {
        var chain = new List<CloseWindowSurface>();
        IntPtr current = handle;
        for (int depth = 0; depth < 32; depth++)
        {
            var surface = readSurface(current);
            if (surface.Identity.Handle != current || surface.Identity.ThreadId == 0 ||
                surface.Identity.ProcessId != source.Identity.ProcessId ||
                chain.Any(node => node.Identity.Handle == current)) return null;
            if (depth == 0 && (!surface.Visible || !surface.Enabled)) return null;
            chain.Add(surface);
            if (surface.Owner == source.Identity.Handle)
                return readSurface(source.Identity.Handle) == source &&
                    chain.All(node => readSurface(node.Identity.Handle) == node) ? chain[0].Identity : null;
            if (surface.Owner == IntPtr.Zero) return null;
            current = surface.Owner;
        }
        return null;
    }

    internal void Request(WindowActivationTarget target)
    {
        if (target.Handle == IntPtr.Zero || target.ThreadId == 0 || target.ProcessId == 0 ||
            identify(target.Handle) != target)
            throw new InvalidOperationException("The preview no longer identifies the original window.");
        if (!requested.Add(target))
            throw new InvalidOperationException("A close request for this window is still unconfirmed. Press Escape to check the application.");
        try
        {
            var result = post(target.Handle);
            if (!result.Queued)
                throw new Win32Exception(result.Error, result.Error == 5
                    ? "Windows blocked the close request. The application may be running with higher privileges."
                    : "Windows could not queue the close request.");
            latestRequest = target;
        }
        catch
        {
            requested.Remove(target);
            throw;
        }
    }

    // A queued WM_CLOSE does not say whether the application accepted, canceled or delayed it.
    internal bool IsClosed(WindowActivationTarget target) => identify(target.Handle) != target;

    internal IReadOnlyList<WindowActivationTarget> ConfirmClosed()
    {
        var closed = requested.Where(IsClosed).ToArray();
        foreach (var target in closed) requested.Remove(target);
        return closed;
    }

    internal void EndSession()
    {
        requested.Clear();
        latestRequest = null;
    }

    private static CloseWindowSurface ReadSurface(IntPtr handle)
    {
        var identity = new WindowActivation().Capture(handle);
        bool visible = IsWindowVisible(handle);
        if (visible)
        {
            int result = DwmGetWindowAttributeInt(handle, DWMWA_CLOAKED, out int cloaked, sizeof(int));
            if (result < 0)
                System.Diagnostics.Trace.TraceWarning($"Could not verify close attention window visibility: 0x{result:X8}.");
            visible = result >= 0 && cloaked == 0;
        }
        return new(identity, GetWindow(handle, GW_OWNER), visible, IsWindowEnabled(handle));
    }
}

internal readonly record struct CloseWindowSurface(WindowActivationTarget Identity, IntPtr Owner, bool Visible, bool Enabled);
