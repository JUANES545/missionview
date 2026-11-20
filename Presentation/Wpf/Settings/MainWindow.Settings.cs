using System.Windows;
using System.Windows.Interop;

namespace WindowScatter;

public partial class MainWindow
{
    private SettingsWindow? settingsWindow;
    private System.Windows.Forms.ToolStripMenuItem? overviewTrayItem;
    internal bool IsRecordingShortcut => settingsWindow?.IsRecordingShortcut == true;
    partial void QueryShortcutRecording(ref bool recording) => recording = IsRecordingShortcut;
    partial void RecordOverviewShortcutKey(int virtualKey, int modifiers, bool isKeyDown)
    {
        var recorder = settingsWindow;
        if (recorder?.IsRecordingShortcut != true) return;
        int generation = recorder.RecordingGeneration;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ReferenceEquals(settingsWindow, recorder) && recorder.RecordingGeneration == generation)
                recorder.RecordVirtualKey(virtualKey, modifiers, isKeyDown);
        }));
    }

    private void OpenSettings()
    {
        if (scatterInteraction.IsOpen) CancelSessionImmediately();
        if (settingsWindow != null)
        {
            if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
            settingsWindow.Activate();
            return;
        }
        settingsWindow = new SettingsWindow(settings, ApplyPreferences, CreateUpdateControls(),
            () => overviewHotkeys?.AppExpose != null);
        var handle = new WindowInteropHelper(settingsWindow).EnsureHandle();
        scatterInteraction.RegisterSurface(handle);
        settingsWindow.Closed += (_, _) =>
        {
            scatterInteraction.UnregisterSurface(handle);
            settingsWindow = null;
        };
        settingsWindow.Show();
    }

    private bool ApplyPreferences(AppSettings candidate)
    {
        var previous = settings.Copy();
        bool hotkeyChanged = candidate.Hotkey != previous.Hotkey || candidate.AppExposeHotkey != previous.AppExposeHotkey;
        bool startupChanged = candidate.RunOnStartup != settings.RunOnStartup;
        bool cornersChanged = candidate.EnableHotCorners != previous.EnableHotCorners
            || candidate.HotCornerPosition != previous.HotCornerPosition || candidate.HotCornerDelay != previous.HotCornerDelay;
        bool startupAttempted = false, cornersAttempted = false;
        Action restoreStartup;
        try { restoreStartup = startupChanged ? StartupRegistration.Capture() : () => { }; }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            ReportSettingsError(error);
            return false;
        }
        bool applied = services.Preferences.TryApply(candidate, () =>
        {
            if (startupChanged)
            {
                startupAttempted = true;
                StartupRegistration.Apply(settings.RunOnStartup);
            }
            if (cornersChanged)
            {
                cornersAttempted = true;
                RefreshHotCorners();
            }
        }, () =>
        {
            var failures = new List<Exception>();
            try { if (startupAttempted) restoreStartup(); }
            catch (Exception error) { failures.Add(error); }
            try { if (hotkeyChanged) ApplyConfiguredHotkey(previous); }
            catch (Exception error) { failures.Add(error); }
            try { if (cornersAttempted) RefreshHotCorners(); }
            catch (Exception error) { failures.Add(error); }
            if (failures.Count > 0) throw new AggregateException("Windows could not restore all previous preferences.", failures);
        }, ReportSettingsError, hotkeyChanged ? () => ApplyConfiguredHotkey(candidate) : null);
        RefreshTrayPreferences();
        return applied;
    }

    private void ApplyConfiguredHotkey(AppSettings candidate)
    {
        if (!TryApplyOverviewHotkeys(candidate.Hotkey, candidate.AppExposeHotkey, out string error))
            throw new InvalidOperationException(error);
        if (!string.IsNullOrEmpty(error))
        {
            System.Diagnostics.Trace.TraceWarning(error);
            settingsWindow?.ReportShortcutWarning(error);
        }
    }

    private void RefreshHotCorners()
    {
        hotCornerManager.Stop();
        hotCornerManager.Start();
    }

    private void RefreshTrayPreferences()
    {
        if (overviewTrayItem != null) overviewTrayItem.ShortcutKeyDisplayString = settings.Hotkey;
        settingsWindow?.RefreshStartupPreference();
    }

    private static void ReportSettingsError(Exception error)
    {
        System.Diagnostics.Trace.TraceError($"Could not apply MissionView settings: {error}");
        MessageBox.Show($"MissionView could not apply your settings.\n\n{error.Message}",
            "MissionView Settings", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
