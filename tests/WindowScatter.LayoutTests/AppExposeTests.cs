using System.ComponentModel;
using System.Runtime.InteropServices;
using WindowScatter;
using Xunit;
using static WindowScatter.Win32Interop;

public class AppExposeTests
{
    private static readonly ApplicationIdentity Chrome = ApplicationIdentity.Create(@"C:\Chrome\chrome.exe", null, null);
    private static WindowActivationTarget Target(int handle, uint pid = 10) => new(new(handle), 1, pid);

    [Fact]
    public void CaptureKeepsPreOverlayWindowAndApplicationAcrossLaterFocusChanges()
    {
        var foreground = Target(10);
        var captured = AppExposeRequest.Capture(() => foreground, _ => false, _ => Chrome);
        foreground = Target(99);
        Assert.Equal(Target(10), captured.Foreground);
        Assert.True(captured.Includes(Target(20, 200)));
        Assert.Equal(0, captured.UnidentifiedWindows);
    }

    [Fact]
    public void PointerTargetGroupsAcrossProcessesButCancelReturnsTheOriginalForeground()
    {
        var foreground = Target(10, 100);
        var pointed = Target(20, 200);
        var request = AppExposeRequest.Capture(() => pointed, _ => false,
            target => target.Handle == foreground.Handle
                ? ApplicationIdentity.Create(@"C:\Other\editor.exe", null, null) : Chrome, foreground);
        Assert.True(request.Includes(Target(21, 500)));
        Assert.False(request.Includes(foreground));
        var interaction = new ScatterInteraction();
        interaction.BeginSession(request.Foreground);
        interaction.RequestDismiss();
        Assert.True(interaction.TryBeginClose(null, out var restore));
        Assert.Equal(foreground, restore);
        Assert.NotEqual(pointed, restore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidOrOwnPointerTargetNeverUsesAValidForegroundAsFallback(bool own)
    {
        var foreground = Target(10);
        int identityReads = 0;
        Assert.Throws<InvalidOperationException>(() => AppExposeRequest.Capture(
            () => own ? Target(20) : default, _ => own,
            _ => { identityReads++; return Chrome; }, foreground));
        Assert.Equal(0, identityReads);
    }

    [Fact]
    public void ChromeAndElectronUseExecutableIdentityAcrossProcessesNotPidOrExecutableBasename()
    {
        var request = AppExposeRequest.Capture(() => Target(10), _ => false, target => target.Handle.ToInt64() switch
        {
            10 => Chrome,
            20 => ApplicationIdentity.Create(@"c:\chrome\CHROME.exe", null, null),
            30 => ApplicationIdentity.Create(@"C:\Unrelated\chrome.exe", null, null),
            _ => ApplicationIdentity.Create(@"C:\Electron\other.exe", null, null)
        });
        Assert.True(request.Includes(Target(20, 500)));
        Assert.False(request.Includes(Target(30, 10)));
        Assert.False(request.Includes(Target(40, 10)));
    }

    [Fact]
    public void ExplicitWindowAumidSeparatesWindowsInTheSameHostAndOverridesProcessAppId()
    {
        var one = ApplicationIdentity.Create(@"C:\Windows\ApplicationFrameHost.exe", "package!One", "host!Shared");
        var two = ApplicationIdentity.Create(@"C:\Windows\ApplicationFrameHost.exe", "package!Two", "host!Shared");
        Assert.False(one.Matches(two));
        Assert.True(one.Matches(ApplicationIdentity.Create(@"C:\App\app.exe", null, "package!One")));
        Assert.False(one.Matches(ApplicationIdentity.Create(@"C:\App\app.exe", null, null)));
        Assert.False(one.Matches(ApplicationIdentity.Create(@"C:\App\app.exe", "package!ONE", null)));
    }

    [Theory]
    [InlineData("ApplicationFrameHost.exe")]
    [InlineData("WWAHost.exe")]
    [InlineData("dllhost.exe")]
    [InlineData("rundll32.exe")]
    [InlineData("RuntimeBroker.exe")]
    [InlineData("svchost.exe")]
    [InlineData("msedgewebview2.exe")]
    [InlineData("electron.exe")]
    [InlineData("javaw.exe")]
    [InlineData("dotnet.exe")]
    public void SharedHostWithoutPerWindowIdentityNeverFallsBackToSharedProcessOrExe(string host)
        => Assert.Throws<InvalidOperationException>(() => ApplicationIdentity.Create(@"C:\Windows\" + host, null, "shared!Host"));

    [Fact]
    public void OwnOrMissingForegroundAndDeniedIdentityDoNotFallBackToAllApplications()
    {
        Assert.Throws<InvalidOperationException>(() => AppExposeRequest.Capture(() => Target(1), _ => true, _ => Chrome));
        Assert.Throws<InvalidOperationException>(() => AppExposeRequest.Capture(() => default, _ => false, _ => Chrome));
        Assert.Throws<Win32Exception>(() => AppExposeRequest.Capture(() => Target(1), _ => false, _ => throw new Win32Exception(5)));
        var request = AppExposeRequest.Capture(() => Target(1), _ => false,
            target => target.Handle == new IntPtr(1) ? Chrome : throw new COMException("denied"));
        Assert.False(request.Includes(Target(2)));
        Assert.Equal(1, request.UnidentifiedWindows);
        request.BeginEnumeration();
        Assert.Equal(0, request.UnidentifiedWindows);
        var denied = AppExposeRequest.Capture(() => Target(1), _ => false,
            target => target.Handle == new IntPtr(1) ? Chrome : throw new UnauthorizedAccessException());
        Assert.False(denied.Includes(Target(2)));
        Assert.Equal(1, denied.UnidentifiedWindows);
    }

    [Fact]
    public void FilterPrecedesCapacityAndKeepsSameAppMinimizedAndNormalWindowsAcrossMonitors()
    {
        var monitors = new[] { Monitor(1), Monitor(2) };
        var windows = Enumerable.Range(1, 25).Select(n => (new IntPtr(1), Info(n, false))).ToList();
        windows.Add((new(1), Info(30, true)));
        windows.Add((new(2), Info(31, false)));
        var request = AppExposeRequest.Capture(() => Target(30), _ => false, t =>
            t.Handle.ToInt64() >= 30 ? Chrome : ApplicationIdentity.Create(@"C:\Other.exe", null, null));
        var app = WindowEnumerator.GroupByMonitor(windows, monitors, request);
        Assert.True(Assert.Single(app[new(1)]).WasMinimized);
        Assert.Equal(new IntPtr(31), Assert.Single(app[new(2)]).Handle);
        var general = WindowEnumerator.GroupByMonitor(windows.Where(w => !w.Item2.WasMinimized), monitors);
        Assert.Equal(20, general[new(1)].Count);
        Assert.Equal(new IntPtr(1), general[new(1)][0].Handle);
    }

    [Fact]
    public void MinimizedMetadataUsesNormalWorkspacePlacementWithoutMutatingPlacement()
    {
        var monitor = Monitor(1);
        monitor.Left = -1920;
        monitor.WorkArea = new RECT { Left = -1920, Top = 40, Right = 0, Bottom = 1080 };
        var placement = new WINDOWPLACEMENT
        {
            flags = 2, showCmd = 2,
            rcNormalPosition = new RECT { Left = -1800, Top = 100, Right = -1000, Bottom = 700 }
        };
        var original = placement;
        var window = WindowEnumerator.CreateMinimizedWindowInfo(new(10), "Minimized", placement, 0, monitor)!;
        Assert.True(window.WasMinimized);
        Assert.True(window.WasMaximized);
        Assert.Equal((120, 140, 920, 740),
            (window.OriginalRect.Left, window.OriginalRect.Top, window.OriginalRect.Right, window.OriginalRect.Bottom));
        Assert.Equal(original, placement);
        Assert.Null(WindowEnumerator.CreateWindowInfo(new(10), "Minimized", true, placement.rcNormalPosition,
            false, 0, monitor, [monitor]));
    }

    [Fact]
    public void MixedApplicationSetPreservesCapacityOrderAndReportsOverflowInsteadOfDroppingItSilently()
    {
        var windows = Enumerable.Range(1, 25).Select(n => (new IntPtr(1), Info(n, n > 10)));
        var request = AppExposeRequest.Capture(() => Target(1), _ => false, _ => Chrome);
        var group = Assert.Single(WindowEnumerator.GroupByMonitor(windows, [Monitor(1)], request)).Value;
        Assert.Equal(20, group.Count);
        Assert.Equal(10, group.Count(w => w.WasMinimized));
        Assert.Equal(5, request.WindowsOverLimit);
        request.BeginEnumeration();
        Assert.Equal(0, request.WindowsOverLimit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelPreservesMinimizedStateAndConfirmationRestoresOnlyChosenWindow(bool confirm)
    {
        var previous = Target(1);
        var chosen = Target(2);
        var minimized = new HashSet<IntPtr> { chosen.Handle, new(3) };
        var before = minimized.ToArray();
        var restored = new List<IntPtr>();
        var interaction = new ScatterInteraction();
        interaction.BeginSession(previous);
        var activation = new WindowActivation(
            _ => (1, 10),
            (h, _) => { restored.Add(h); minimized.Remove(h); },
            h => { previous = new(h, 1, 10); return true; }, () => previous.Handle);
        Assert.True(interaction.TryBeginClose(confirm ? chosen : null, out var target));
        Assert.True(activation.TryActivate(target, restore: confirm, wasMaximized: false, ownedForeground: true));
        Assert.Equal(confirm ? [chosen.Handle] : Array.Empty<IntPtr>(), restored);
        Assert.Contains(new IntPtr(3), minimized);
        if (!confirm) Assert.Equal(before, minimized.ToArray());
    }

    [Fact]
    public void ConcurrentRestorationAndDestroyedIdentityDoNotApplyStaleMinimizedRestore()
    {
        int restored = 0;
        IntPtr active = new(99);
        var activation = new WindowActivation(_ => (1, 10), (_, _) => restored++,
            h => { active = h; return true; }, () => active, _ => false);
        Assert.True(activation.TryActivate(Target(2), true, true, true));
        Assert.Equal(0, restored);
        Assert.Equal(new IntPtr(2), active);
        var destroyed = new WindowActivation(_ => (0, 0), (_, _) => restored++,
            _ => throw new Exception("Stale target"), () => active, _ => true);
        Assert.False(destroyed.TryActivate(Target(2), true, true, true));
        Assert.Equal(0, restored);
    }

    private static WindowInfo Info(int id, bool minimized) =>
        new() { Handle = new(id), Identity = Target(id), WasMinimized = minimized };
    private static MonitorHelper.MonitorBounds Monitor(int id) =>
        new() { Handle = new(id), Width = 1920, Height = 1080 };
}
