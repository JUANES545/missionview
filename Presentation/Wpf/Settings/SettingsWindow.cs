using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WindowScatter;

internal sealed class SettingsWindow : Window
{
    private readonly AppSettings source;
    private readonly Func<AppSettings, bool> apply;
    private readonly Func<bool> isAppExposeAvailable;
    private readonly Button shortcut = new();
    private readonly CheckBox hotCorners = new() { Content = "Enable hot corners" };
    private readonly ComboBox position = new();
    private readonly TextBox delay = new();
    private readonly CheckBox startup = new() { Content = "Start with Windows" };
    private readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock exposeHint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
    private readonly StackPanel updatePanel = new();
    private string hotkey;
    private bool shortcutWarning;
    internal bool IsRecordingShortcut { get; private set; }
    internal int RecordingGeneration { get; private set; }

    internal SettingsWindow(AppSettings source, Func<AppSettings, bool> apply, UIElement updates, Func<bool> isAppExposeAvailable)
    {
        this.source = source;
        this.apply = apply;
        this.isAppExposeAvailable = isAppExposeAvailable;
        hotkey = source.Hotkey;
        Title = "MissionView Settings";
        Width = Math.Min(460, SystemParameters.WorkArea.Width);
        Height = Math.Min(660, SystemParameters.WorkArea.Height);
        MaxHeight = SystemParameters.WorkArea.Height;
        MinWidth = Math.Min(360, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(400, MaxHeight);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : SharedBrushes.Graphite;
        Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brush("#F3F5F4");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        hotCorners.Foreground = startup.Foreground = Foreground;
        UseLayoutRounding = true;
        var root = new DockPanel { Margin = new Thickness(24), LastChildFill = true };
        Content = root;
        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Assertive);
        footer.Children.Add(feedback);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88, Padding = new Thickness(12, 6, 12, 6) };
        cancel.Click += (_, _) => Close();
        var save = new Button { Content = "Save", MinWidth = 88, Margin = new Thickness(8, 8, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        if (!SystemParameters.HighContrast)
        {
            save.Background = Brush("#7DDB9B");
            save.Foreground = SharedBrushes.Graphite;
        }
        cancel.Margin = new Thickness(0, 8, 0, 0);
        save.Click += (_, _) => Save();
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        footer.Children.Add(actions);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(scroll);
        var form = new StackPanel();
        KeyboardNavigation.SetTabNavigation(form, KeyboardNavigationMode.Continue);
        scroll.Content = form;
        var brand = new StackPanel { Orientation = Orientation.Horizontal };
        using (var stream = typeof(SettingsWindow).Assembly.GetManifestResourceStream("MissionView.TrayIcon.ico"))
        {
            if (stream != null)
            {
                var bitmap = System.Windows.Media.Imaging.BitmapFrame.Create(stream,
                    System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                var image = new DecorativeImage { Source = bitmap, Width = 28, Height = 28, IsHitTestVisible = false, Margin = new Thickness(0, 0, 10, 0) };
                brand.Children.Add(image);
            }
        }
        brand.Children.Add(new TextBlock { Text = "MissionView", FontSize = 22, FontWeight = FontWeights.SemiBold });
        form.Children.Add(brand);
        form.Children.Add(new TextBlock { Text = "Settings", FontSize = 18, Margin = new Thickness(0, 20, 0, 12) });
        AddLabel(form, "Overview shortcut", shortcut);
        shortcut.Content = hotkey;
        shortcut.HorizontalContentAlignment = HorizontalAlignment.Left;
        shortcut.Padding = new Thickness(10, 6, 10, 6);
        shortcut.Click += (_, _) =>
        {
            RecordingGeneration++;
            IsRecordingShortcut = true;
            shortcut.Content = "Press a shortcut...";
            feedback.Text = "Use Ctrl, Alt or Win with a key. Escape cancels recording.";
        };
        shortcut.PreviewKeyDown += RecordShortcut;
        shortcut.LostKeyboardFocus += (_, _) => StopRecording();
        form.Children.Add(new TextBlock { Text = "Click to record. Changes take effect when you save.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) });
        AutomationProperties.SetLiveSetting(exposeHint, AutomationLiveSetting.Polite);
        form.Children.Add(exposeHint);
        RefreshShortcutHint();
        hotCorners.IsChecked = source.EnableHotCorners;
        hotCorners.Margin = new Thickness(0, 0, 0, 8);
        form.Children.Add(hotCorners);
        foreach (var corner in new[] { "TopLeft", "TopRight", "BottomLeft", "BottomRight" })
            position.Items.Add(new ComboBoxItem { Content = CornerLabel(corner), Tag = corner });
        position.SelectedIndex = Array.FindIndex(new[] { "TopLeft", "TopRight", "BottomLeft", "BottomRight" },
            value => value.Equals(source.HotCornerPosition, StringComparison.OrdinalIgnoreCase));
        AddLabel(form, "Corner", position);
        delay.Text = source.HotCornerDelay.ToString(CultureInfo.CurrentCulture);
        AddLabel(form, "Delay (milliseconds)", delay);
        void SetCornerEnabled()
        {
            position.IsEnabled = delay.IsEnabled = hotCorners.IsChecked == true;
        }
        hotCorners.Checked += (_, _) => SetCornerEnabled();
        hotCorners.Unchecked += (_, _) => SetCornerEnabled();
        SetCornerEnabled();
        startup.IsChecked = source.RunOnStartup;
        startup.Margin = new Thickness(0, 12, 0, 16);
        form.Children.Add(startup);
        form.Children.Add(new TextBlock { Text = "Updates", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 8) });
        form.Children.Add(updatePanel);
        updatePanel.Children.Add(updates);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                if (IsRecordingShortcut) StopRecording();
                else Close();
                e.Handled = true;
            }
        };
        Loaded += (_, _) => shortcut.Focus();
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private sealed class DecorativeImage : Image
    {
        protected override AutomationPeer? OnCreateAutomationPeer() => null;
    }
    private static string CornerLabel(string value) => value switch
    {
        "TopLeft" => "Top left", "TopRight" => "Top right", "BottomLeft" => "Bottom left", _ => "Bottom right"
    };

    private void AddLabel(Panel form, string text, Control control)
    {
        form.Children.Add(new Label { Content = text, Target = control, Foreground = Foreground, Padding = new Thickness(0), Margin = new Thickness(0, 4, 0, 4) });
        AutomationProperties.SetName(control, text);
        control.MinHeight = 30;
        control.Margin = new Thickness(0, 0, 0, 8);
        form.Children.Add(control);
    }

    private void StopRecording()
    {
        RecordingGeneration++;
        IsRecordingShortcut = false;
        shortcut.Content = hotkey;
        feedback.Text = "";
        RefreshShortcutHint();
    }

    private void RefreshShortcutHint()
    {
        exposeHint.Text = hotkey != source.Hotkey
            ? $"App Exposé requested: Shift with {hotkey}. Save to check availability."
            : (HotkeyParser.Parse(hotkey).ModifierKeys & 4) != 0
                ? "App Exposé is unavailable: this shortcut already includes Shift. Choose a shortcut without Shift."
                : isAppExposeAvailable()
                    ? $"App Exposé shortcut: hold Shift with {hotkey}."
                    : "App Exposé is unavailable: Windows could not reserve both shortcuts. Choose another shortcut.";
    }

    internal void ReportShortcutWarning(string warning)
    {
        shortcutWarning = true;
        feedback.Text = warning;
        UIElementAutomationPeer.FromElement(feedback)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void RecordShortcut(object sender, KeyEventArgs e)
    {
        if (!IsRecordingShortcut) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab) { StopRecording(); return; }
        e.Handled = true;
        if (key == Key.Escape) { StopRecording(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;
        RecordShortcut(key, Keyboard.Modifiers);
    }

    internal void RecordShortcut(Key key, ModifierKeys modifiers)
    {
        if (!IsRecordingShortcut) return;
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
        {
            feedback.Text = "Include Ctrl, Alt or Win in the shortcut.";
            return;
        }
        string keyName = key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(CultureInfo.InvariantCulture),
            Key.Return => "Enter", Key.Next => "PageDown", Key.Prior => "PageUp", _ => key.ToString()
        };
        string value = ((modifiers & ModifierKeys.Windows) != 0 ? "Win+" : "") +
            ((modifiers & ModifierKeys.Control) != 0 ? "Ctrl+" : "") +
            ((modifiers & ModifierKeys.Alt) != 0 ? "Alt+" : "") +
            ((modifiers & ModifierKeys.Shift) != 0 ? "Shift+" : "") + keyName;
        if (HotkeyParser.Parse(value).VirtualKeyCode == 0)
        {
            feedback.Text = "Use a letter, number, F1-F12 or a navigation key.";
            return;
        }
        var proposed = HotkeyParser.Parse(value);
        var original = HotkeyParser.Parse(source.Hotkey);
        bool unchanged = proposed.VirtualKeyCode == original.VirtualKeyCode && proposed.ModifierKeys == original.ModifierKeys;
        if ((modifiers & ModifierKeys.Shift) != 0 && !unchanged)
        {
            feedback.Text = "Choose a shortcut without Shift. Shift is used for App Exposé.";
            return;
        }
        hotkey = unchanged ? source.Hotkey : value;
        StopRecording();
    }

    internal void RecordVirtualKey(int virtualKey, int modifiers, bool isKeyDown)
    {
        if (!IsRecordingShortcut || !isKeyDown) return;
        if (virtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5) return;
        if (virtualKey is 0x1B or 0x09) { StopRecording(); return; }
        var keys = ModifierKeys.None;
        if ((modifiers & 1) != 0) keys |= ModifierKeys.Control;
        if ((modifiers & 2) != 0) keys |= ModifierKeys.Alt;
        if ((modifiers & 4) != 0) keys |= ModifierKeys.Shift;
        if ((modifiers & 8) != 0) keys |= ModifierKeys.Windows;
        RecordShortcut(KeyInterop.KeyFromVirtualKey(virtualKey), keys);
    }

    internal bool ResolveUnsavedChanges()
    {
        bool unchanged = hotkey == source.Hotkey && hotCorners.IsChecked == source.EnableHotCorners
            && ((string)((ComboBoxItem)position.SelectedItem).Tag).Equals(source.HotCornerPosition, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(delay.Text, out int milliseconds) && milliseconds == source.HotCornerDelay
            && startup.IsChecked == source.RunOnStartup;
        if (unchanged && !IsRecordingShortcut) return true;
        var choice = MessageBox.Show(this, "Save your settings before installing the update?\n\nChoose No to discard these changes.",
            "Unsaved settings", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (choice == MessageBoxResult.Yes) return Save(closeOnSuccess: false);
        if (choice != MessageBoxResult.No) return false;
        RestorePreferences();
        return true;
    }

    internal void RefreshStartupPreference() => startup.IsChecked = source.RunOnStartup;

    private bool Save(bool closeOnSuccess = true)
    {
        if (IsRecordingShortcut)
        {
            feedback.Text = "Finish recording a shortcut or press Escape to cancel recording.";
            shortcut.Focus();
            return false;
        }
        if (!int.TryParse(delay.Text, out int milliseconds) || milliseconds < 0)
        {
            feedback.Text = "Enter a delay of zero or more milliseconds.";
            delay.Focus();
            return false;
        }
        var candidate = source.Copy();
        candidate.Hotkey = hotkey;
        candidate.EnableHotCorners = hotCorners.IsChecked == true;
        candidate.HotCornerPosition = (string)((ComboBoxItem)position.SelectedItem).Tag;
        candidate.HotCornerDelay = milliseconds;
        candidate.RunOnStartup = startup.IsChecked == true;
        shortcutWarning = false;
        feedback.Text = "";
        if (apply(candidate))
        {
            RefreshShortcutHint();
            if (closeOnSuccess && !shortcutWarning) Close();
            return true;
        }
        else
        {
            RestorePreferences();
            feedback.Text = "Settings were not applied. Previous preferences are shown; review the error before trying again.";
            return false;
        }
    }

    private void RestorePreferences()
    {
        hotkey = source.Hotkey;
        StopRecording();
        hotCorners.IsChecked = source.EnableHotCorners;
        foreach (ComboBoxItem corner in position.Items)
            if (((string)corner.Tag).Equals(source.HotCornerPosition, StringComparison.OrdinalIgnoreCase))
                position.SelectedItem = corner;
        delay.Text = source.HotCornerDelay.ToString(CultureInfo.CurrentCulture);
        RefreshStartupPreference();
    }
}
