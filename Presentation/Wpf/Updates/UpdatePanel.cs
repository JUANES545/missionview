using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace WindowScatter;

internal sealed class UpdatePanel : StackPanel
{
    private readonly UpdateController controller;
    private readonly Func<UpdateRelease, CancellationToken, Task<bool>> confirm;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly ProgressBar progress = new() { Height = 4, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Button action = new() { HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 10, 16, 10), MinHeight = 40 };
    private readonly Button cancel = new() { Content = "Cancel update", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 8, 0, 0) };

    internal UpdatePanel(UpdateController controller, Func<UpdateRelease, CancellationToken, Task<bool>> confirm)
    {
        this.controller = controller;
        this.confirm = confirm;
        Children.Add(status);
        Children.Add(progress);
        Children.Add(action);
        Children.Add(cancel);
        action.SetResourceReference(Control.BackgroundProperty, "FieldSelection");
        action.SetResourceReference(Control.ForegroundProperty, "FieldSelectionText");
        progress.SetResourceReference(Control.ForegroundProperty, "FieldFocus");
        progress.SetResourceReference(Control.BackgroundProperty, "PreferenceSurface");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(progress, "Update progress");
        action.Click += async (_, _) => await ExecuteAction();
        cancel.Click += (_, _) => controller.Cancel();
        Loaded += (_, _) => { controller.PropertyChanged += StateChanged; Refresh(); };
        Unloaded += (_, _) => controller.PropertyChanged -= StateChanged;
        Refresh();
    }

    private void StateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UpdateController.State) or nameof(UpdateController.HasInstallerStarted)) Refresh();
    }

    private void Refresh()
    {
        var state = controller.State;
        status.Text = state.Status switch
        {
            UpdateStatus.Idle => "",
            UpdateStatus.Checking => "Checking...",
            UpdateStatus.Available when state.AvailableVersion is { } version => $"Version {version} available.",
            _ => state.Message
        };
        status.Visibility = status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        status.SetResourceReference(TextBlock.ForegroundProperty, state.Status switch
        {
            UpdateStatus.Error => "FieldError",
            UpdateStatus.UpToDate => "FieldFocus",
            _ => "FieldForeground"
        });
        progress.Visibility = state.IsBusy && (!state.IsIndeterminate || SystemParameters.ClientAreaAnimation)
            ? Visibility.Visible : Visibility.Collapsed;
        progress.IsIndeterminate = state.IsIndeterminate && SystemParameters.ClientAreaAnimation;
        progress.Value = Math.Clamp(state.Progress ?? 0, 0, 1);
        action.Content = state.Status switch
        {
            UpdateStatus.Available => "Download update",
            UpdateStatus.ReadyToInstall => "Install update...",
            _ => "Check for updates"
        };
        action.IsEnabled = !state.IsBusy && !controller.HasInstallerStarted;
        cancel.Visibility = !controller.HasInstallerStarted && state.IsBusy && state.Status is UpdateStatus.Checking or UpdateStatus.Downloading or UpdateStatus.Verifying
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ExecuteAction()
    {
        if (controller.HasInstallerStarted || controller.State.IsBusy) return;
        try
        {
            switch (controller.State.Status)
            {
                case UpdateStatus.Available: await controller.DownloadAsync(); break;
                case UpdateStatus.ReadyToInstall: await controller.InstallAsync(confirm); break;
                default: await controller.CheckAsync(); break;
            }
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError($"Could not complete update action: {error}");
            status.Text = $"The update action failed. {error.Message}";
            status.Visibility = Visibility.Visible;
            status.SetResourceReference(TextBlock.ForegroundProperty, "FieldError");
            UIElementAutomationPeer.FromElement(status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
