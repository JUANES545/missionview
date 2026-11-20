using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace WindowScatter;

public partial class MainWindow
{
    private UpdateController? updateController;
    private System.Windows.Forms.ToolStripMenuItem? updatesTrayItem;

    private UpdateController GetUpdateController()
    {
        if (updateController != null) return updateController;
        updateController = services.CreateUpdates(Close, Dispatcher);
        updateController.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(UpdateController.State) or nameof(UpdateController.HasInstallerStarted)) || updatesTrayItem == null) return;
            updatesTrayItem.Text = updateController.State.Status == UpdateStatus.Checking
                ? "Checking for updates..." : "Check for updates...";
            updatesTrayItem.Enabled = !updateController.State.IsBusy && !updateController.HasInstallerStarted;
        };
        return updateController;
    }

    private UIElement CreateUpdateControls() => new UpdatePanel(GetUpdateController(), ConfirmUpdateInstallation);

    private Task<bool> ConfirmUpdateInstallation(UpdateRelease release, CancellationToken cancellation)
    {
        return Task.FromResult(UpdateConsent.Confirm(release.Version.ToString(), cancellation,
            () => settingsWindow?.ResolveUnsavedChanges() ?? true,
            message => (settingsWindow != null
                ? MessageBox.Show(settingsWindow, message, "Install update", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                : MessageBox.Show(message, "Install update", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)) == MessageBoxResult.Yes));
    }

    private async Task RunUpdateAction()
    {
        try
        {
            OpenSettings();
            settingsWindow?.ShowUpdates();
            await GetUpdateController().CheckAsync();
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError($"Could not check for updates: {error}");
            MessageBox.Show($"MissionView could not check for updates.\n\n{error.Message}", "MissionView",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
