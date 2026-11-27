using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace WindowScatter;

public partial class MainWindow
{
    private UpdateController? updateController;
    private DispatcherTimer? automaticUpdateTimer;
    private System.Windows.Forms.ToolStripMenuItem? updatesTrayItem;

    private UpdateController GetUpdateController()
    {
        if (updateController != null) return updateController;
        updateController = services.CreateUpdates(Close, Dispatcher);
        updateController.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(UpdateController.State) or nameof(UpdateController.HasInstallerStarted)) || updatesTrayItem == null) return;
            updatesTrayItem.Text = updateController.State.Status switch
            {
                UpdateStatus.Checking => "Checking for updates...",
                UpdateStatus.ReadyToInstall when updateController.IsInstalled => "Update ready...",
                _ => "Check for updates..."
            };
            updatesTrayItem.Enabled = !updateController.State.IsBusy && !updateController.HasInstallerStarted;
        };
        return updateController;
    }

    private UIElement CreateUpdateControls()
    {
        var updater = GetUpdateController();
        bool installed = updater.IsInstalled;
        return new UpdatePanel(updater, (release, token) => ConfirmUpdateInstallation(release, token, installed),
            settings, ApplyPreferences);
    }

    private void ApplyAutomaticUpdatesPreference()
    {
        if (!settings.InstallUpdatesAutomatically || !UpdateInstaller.IsInstalled())
        {
            automaticUpdateTimer?.Stop();
            updateController?.CancelBackground();
            return;
        }

        if (automaticUpdateTimer is null)
        {
            automaticUpdateTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            automaticUpdateTimer.Tick += StageAutomaticUpdate;
        }
        automaticUpdateTimer.Interval = TimeSpan.FromSeconds(5);
        automaticUpdateTimer.Start();
    }

    private async void StageAutomaticUpdate(object? sender, EventArgs e)
    {
        if (sender is not DispatcherTimer timer || isClosing || !settings.InstallUpdatesAutomatically) return;
        timer.Interval = TimeSpan.FromDays(1);
        try { await GetUpdateController().StageInBackgroundAsync(); }
        catch (Exception error) { System.Diagnostics.Trace.TraceError($"Could not stage a background update: {error}"); }
    }

    private Task<bool> ConfirmUpdateInstallation(UpdateRelease release, CancellationToken cancellation, bool restart)
    {
        return Task.FromResult(UpdateConsent.Confirm(release.Version.ToString(), cancellation,
            () => settingsWindow?.IsRecordingShortcut != true,
            message => (settingsWindow != null
                ? MessageBox.Show(settingsWindow, message, "Install update", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                : MessageBox.Show(message, "Install update", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)) == MessageBoxResult.Yes,
            restart));
    }

    private async Task RunUpdateAction()
    {
        try
        {
            OpenSettings();
            settingsWindow?.ShowUpdates();
            var updater = GetUpdateController();
            if (updater.State.Status != UpdateStatus.ReadyToInstall || !updater.IsInstalled)
                await updater.CheckAsync();
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError($"Could not check for updates: {error}");
            MessageBox.Show($"MissionView could not check for updates.\n\n{error.Message}", "MissionView",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
