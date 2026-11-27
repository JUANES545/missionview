using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace WindowScatter;

internal sealed class UpdatePanel : StackPanel
{
    internal const string AutomaticUpdatesKey = "Install updates automatically";
    private readonly UpdateController controller;
    private readonly Func<UpdateRelease, CancellationToken, Task<bool>> confirm;
    private readonly AppSettings? settings;
    private readonly CheckBox? automaticUpdates;
    private readonly bool installed;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly ProgressBar progress = new() { Height = 4, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Button action = new() { HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 10, 16, 10), MinHeight = 40 };
    private readonly Button cancel = new() { Content = "Cancel update", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 8, 0, 0) };
    internal StackPanel ActionRow { get; } = new();
    internal StackPanel? AutomaticRow { get; }

    internal UpdatePanel(UpdateController controller, Func<UpdateRelease, CancellationToken, Task<bool>> confirm,
        AppSettings? settings = null, Func<AppSettings, bool>? apply = null)
    {
        this.controller = controller;
        this.confirm = confirm;
        this.settings = settings;
        installed = controller.IsInstalled;
        if (installed && settings is not null && apply is not null)
        {
            const string help = "Download and verify updates in the background. Install when you select Restart to update.";
            automaticUpdates = new CheckBox
            {
                Content = AutomaticUpdatesKey, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center, MinHeight = 32
            };
            automaticUpdates.SetResourceReference(Control.ForegroundProperty, "FieldForeground");
            AutomationProperties.SetAutomationId(automaticUpdates, AutomaticUpdatesKey);
            AutomationProperties.SetName(automaticUpdates, AutomaticUpdatesKey);
            AutomationProperties.SetHelpText(automaticUpdates, help);
            automaticUpdates.Click += (_, _) =>
            {
                var candidate = settings.Copy();
                candidate.InstallUpdatesAutomatically = automaticUpdates.IsChecked == true;
                apply(candidate);
                Refresh();
            };
            var description = new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            description.SetResourceReference(TextBlock.ForegroundProperty, "FieldForeground");
            AutomaticRow = new StackPanel { Children = { automaticUpdates, description } };
            AutomaticRow.Loaded += (_, _) => RefreshAutomaticPreference();
            Children.Add(AutomaticRow);
        }
        ActionRow.Children.Add(status);
        ActionRow.Children.Add(progress);
        ActionRow.Children.Add(action);
        ActionRow.Children.Add(cancel);
        Children.Add(ActionRow);
        action.SetResourceReference(Control.BackgroundProperty, "FieldSelection");
        action.SetResourceReference(Control.ForegroundProperty, "FieldSelectionText");
        progress.SetResourceReference(Control.ForegroundProperty, "FieldFocus");
        progress.SetResourceReference(Control.BackgroundProperty, "PreferenceSurface");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(progress, "Update progress");
        action.Click += async (_, _) => await ExecuteAction();
        cancel.Click += (_, _) => controller.Cancel();
        ActionRow.Loaded += (_, _) => { controller.PropertyChanged += StateChanged; Refresh(); };
        ActionRow.Unloaded += (_, _) => controller.PropertyChanged -= StateChanged;
        Refresh();
    }

    private void StateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UpdateController.State) or nameof(UpdateController.HasInstallerStarted)) Refresh();
    }

    private void Refresh()
    {
        var state = controller.State;
        RefreshAutomaticPreference();
        status.Text = state.Status switch
        {
            UpdateStatus.Idle => "",
            UpdateStatus.Checking => "Checking...",
            UpdateStatus.Available when state.AvailableVersion is { } version => $"Version {version} available.",
            UpdateStatus.ReadyToInstall when installed && state.AvailableVersion is { } ready => $"Version {ready} is ready. {state.Message}",
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
            UpdateStatus.ReadyToInstall => installed ? "Restart to update" : "Install update...",
            _ => "Check for updates"
        };
        action.IsEnabled = !state.IsBusy && !controller.HasInstallerStarted;
        cancel.Visibility = !controller.HasInstallerStarted && state.IsBusy && state.Status is UpdateStatus.Checking or UpdateStatus.Downloading or UpdateStatus.Verifying
            ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void RefreshAutomaticPreference()
    {
        if (automaticUpdates is not null && settings is not null)
            automaticUpdates.IsChecked = settings.InstallUpdatesAutomatically;
    }

    private async Task ExecuteAction()
    {
        if (controller.HasInstallerStarted || controller.State.IsBusy) return;
        try
        {
            switch (controller.State.Status)
            {
                case UpdateStatus.Available: await controller.DownloadAsync(); break;
                case UpdateStatus.ReadyToInstall: await controller.InstallAsync(confirm, restart: installed); break;
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
