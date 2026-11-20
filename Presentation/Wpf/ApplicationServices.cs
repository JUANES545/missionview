using System.Windows.Threading;

namespace WindowScatter;

// Process-scoped composition. HWND-bound renderers remain owned by their WPF surface.
internal sealed class ApplicationServices
{
    public AppSettings Settings { get; }
    public PreferencesService Preferences { get; }
    public OverviewSession Overview { get; } = new();
    public WindowActivation Activation { get; } = new();
    public DesktopToggle Desktop { get; } = new();
    public WindowTransfer Transfer { get; } = new();
    public WindowMoveOperation Move { get; } = new();
    public OverviewShortcutInput Shortcut { get; }

    internal ApplicationServices(JsonSettingsStore store)
    {
        Settings = store.Load();
        Preferences = new(Settings, store.Save);
        Shortcut = new(Overview.Interaction,
            () => Activation.Capture(Win32Interop.GetForegroundWindow()), Activation.CaptureUnderPointer);
    }

    internal UpdateController CreateUpdates(Action shutdown, Dispatcher dispatcher)
    {
        var version = typeof(ApplicationServices).Assembly.GetName().Version
            ?? throw new InvalidOperationException("The MissionView application version is missing.");
        if (!UpdateVersion.TryParse($"{version.Major}.{version.Minor}.{version.Build}", out var current))
            throw new InvalidOperationException("The MissionView application version is invalid.");
        return new(new UpdateService(), current, shutdown, UpdateInstaller.Start,
            new DispatcherSynchronizationContext(dispatcher), UpdateInstaller.IsSupported);
    }
}
