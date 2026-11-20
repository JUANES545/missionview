using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace WindowScatter;

internal sealed class SettingsWindow : Window
{
    private readonly AppSettings source;
    private readonly Func<AppSettings, bool> apply;
    private readonly Func<bool> isAppExposeAvailable;
    private readonly Button shortcut = new();
    private readonly Button appExposeShortcut = new();
    private Button? activeRecorder;
    private readonly CheckBox hotCorners = new() { Content = "Enable hot corners" };
    private readonly ComboBox position = new();
    private readonly TextBox delay = new();
    private readonly CheckBox startup = new() { Content = "Start with Windows" };
    private readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock exposeHint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
    private readonly StackPanel updatePanel = new();
    private readonly TabControl categories = new();
    private readonly List<Grid> preferenceRows = [];
    private readonly List<(Button Button, TextBlock Detail)> contextualHelp = [];
    private readonly Button saveButton = new();
    private readonly Button cancelButton = new();
    private string hotkey;
    private string? appExposeHotkey;
    private bool shortcutWarning;
    internal bool IsRecordingShortcut { get; private set; }
    internal int RecordingGeneration { get; private set; }

    internal SettingsWindow(AppSettings source, Func<AppSettings, bool> apply, UIElement updates, Func<bool> isAppExposeAvailable)
    {
        this.source = source;
        this.apply = apply;
        this.isAppExposeAvailable = isAppExposeAvailable;
        hotkey = source.Hotkey;
        appExposeHotkey = source.AppExposeHotkey;
        Title = "MissionView Settings";
        Width = Math.Min(720, SystemParameters.WorkArea.Width);
        Height = Math.Min(480, SystemParameters.WorkArea.Height);
        MaxHeight = SystemParameters.WorkArea.Height;
        MinWidth = Math.Min(320, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(300, MaxHeight);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : SharedBrushes.Graphite;
        Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brush("#F3F5F4");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        hotCorners.Foreground = startup.Foreground = Foreground;
        UseLayoutRounding = true;
        ApplyControlStyles();
        var root = new DockPanel { Margin = new Thickness(20), LastChildFill = true };
        Content = root;
        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Assertive);
        footer.Children.Add(new ScrollViewer { Content = feedback, MaxHeight = 84,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = cancelButton;
        cancel.Content = "Cancel";
        cancel.IsCancel = true;
        cancel.MinWidth = 88;
        cancel.Click += (_, _) => Close();
        var save = saveButton;
        save.Content = "Save";
        save.MinWidth = 88;
        save.Margin = new Thickness(8, 8, 0, 0);
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
        KeyboardNavigation.SetTabIndex(footer, 100);
        KeyboardNavigation.SetTabIndex(cancel, 0);
        KeyboardNavigation.SetTabIndex(save, 1);
        var body = new DockPanel();
        var bodyScroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        root.Children.Add(bodyScroll);
        var brand = new Grid();
        brand.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        brand.ColumnDefinitions.Add(new ColumnDefinition());
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
        var heading = new TextBlock { Text = "MissionView settings", FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(heading, 1);
        brand.Children.Add(heading);
        brand.Margin = new Thickness(0, 0, 0, 20);
        DockPanel.SetDock(brand, Dock.Top);
        body.Children.Add(brand);
        categories.Background = Background;
        categories.Foreground = Foreground;
        categories.BorderThickness = new Thickness(0);
        categories.Padding = new Thickness(0);
        KeyboardNavigation.SetTabIndex(categories, 0);
        body.Children.Add(categories);
        var general = AddCategory("General");
        var shortcuts = AddCategory("Shortcuts");
        var corners = AddCategory("Hot corners");
        var about = AddCategory("Updates & About");
        startup.IsChecked = source.RunOnStartup;
        AddPreference(general, "Startup", startup, "Open MissionView when you sign in to Windows.");
        AddPreference(shortcuts, "Overview shortcut", shortcut, "Record a shortcut with Ctrl, Alt or Win. Press Escape to cancel recording. Changes apply when you save.");
        AddPreference(shortcuts, "App Exposé shortcut", appExposeShortcut, "Open windows from the application under the pointer. Record a separate shortcut with Ctrl, Alt or Win. Until changed, this follows the overview shortcut with Shift.");
        foreach (var recorder in new[] { shortcut, appExposeShortcut })
        {
            recorder.ContentTemplate = RecorderContentTemplate();
            recorder.Tag = "Record";
            recorder.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            recorder.Padding = new Thickness(10, 6, 10, 6);
            recorder.Click += (_, _) =>
            {
                if (ReferenceEquals(activeRecorder, recorder))
                {
                    StopRecording();
                    return;
                }
                StopRecording();
                activeRecorder = recorder;
                IsRecordingShortcut = true;
                recorder.Content = "Recording...";
                recorder.Tag = "Cancel";
                feedback.Text = "Use Ctrl, Alt or Win with a key. Escape cancels recording.";
                RefreshRecorderNames();
            };
            recorder.PreviewKeyDown += RecordShortcut;
            recorder.LostKeyboardFocus += (_, _) =>
            {
                if (ReferenceEquals(activeRecorder, recorder)) StopRecording();
            };
        }
        AutomationProperties.SetLiveSetting(exposeHint, AutomationLiveSetting.Polite);
        shortcuts.Children.Add(exposeHint);
        RefreshShortcutHint();
        hotCorners.IsChecked = source.EnableHotCorners;
        AddPreference(corners, "Activation", hotCorners, "Keep the pointer in the selected corner for the chosen delay to open the overview.");
        foreach (var corner in new[] { "TopLeft", "TopRight", "BottomLeft", "BottomRight" })
            position.Items.Add(new ComboBoxItem { Content = CornerLabel(corner), Tag = corner });
        position.SelectedIndex = Array.FindIndex(new[] { "TopLeft", "TopRight", "BottomLeft", "BottomRight" },
            value => value.Equals(source.HotCornerPosition, StringComparison.OrdinalIgnoreCase));
        AddPreference(corners, "Corner", position);
        delay.Text = source.HotCornerDelay.ToString(CultureInfo.CurrentCulture);
        delay.TextChanged += (_, _) => delay.Tag = null;
        AddPreference(corners, "Delay (milliseconds)", delay);
        void SetCornerEnabled()
        {
            position.IsEnabled = delay.IsEnabled = hotCorners.IsChecked == true;
        }
        hotCorners.Checked += (_, _) => SetCornerEnabled();
        hotCorners.Unchecked += (_, _) => SetCornerEnabled();
        SetCornerEnabled();
        var version = (typeof(SettingsWindow).Assembly.GetName().Version
            ?? throw new InvalidOperationException("The MissionView application version is missing.")).ToString(3);
        about.Children.Add(new TextBlock { Text = $"MissionView v{version}", FontSize = 18, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap });
        about.Children.Add(new TextBlock { Text = "Check for updates when you choose. MissionView does not check automatically.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        about.Children.Add(updatePanel);
        updatePanel.Children.Add(updates);
        about.Children.Add(new TextBlock { Text = "MIT license.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 20, 0, 0) });
        categories.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, categories)) return;
            if (IsRecordingShortcut) StopRecording();
            foreach (var help in contextualHelp) ((ToolTip)help.Button.ToolTip).IsOpen = false;
        };
        categories.SizeChanged += (_, _) => ReflowPreferences();
        SystemParameters.StaticPropertyChanged += RefreshSystemPalette;
        Closed += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= RefreshSystemPalette;
            foreach (var help in contextualHelp) ((ToolTip)help.Button.ToolTip).IsOpen = false;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                if (IsRecordingShortcut) StopRecording();
                else if (contextualHelp.Any(help => help.Detail.Visibility == Visibility.Visible || ((ToolTip)help.Button.ToolTip).IsOpen))
                {
                    foreach (var help in contextualHelp)
                    {
                        ((ToolTip)help.Button.ToolTip).IsOpen = false;
                        help.Detail.Visibility = Visibility.Collapsed;
                        AutomationProperties.SetItemStatus(help.Button, "Collapsed");
                    }
                }
                else Close();
                e.Handled = true;
            }
        };
        Loaded += (_, _) => startup.Focus();
    }

    internal void ShowUpdates() => categories.SelectedIndex = 3;

    private void RefreshSystemPalette(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.HighContrast)) return;
        Dispatcher.Invoke(() =>
        {
            Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : SharedBrushes.Graphite;
            Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brush("#F3F5F4");
            hotCorners.Foreground = startup.Foreground = Foreground;
            categories.Background = Background;
            categories.Foreground = Foreground;
            foreach (var row in preferenceRows)
                ((Label)row.Children[0]).Foreground = Foreground;
            saveButton.Background = SystemParameters.HighContrast ? SystemColors.ControlBrush : Brush("#7DDB9B");
            saveButton.Foreground = SystemParameters.HighContrast ? SystemColors.ControlTextBrush : SharedBrushes.Graphite;
            ApplyControlStyles();
        });
    }

    private StackPanel AddCategory(string title)
    {
        var form = new StackPanel { Margin = new Thickness(0, 16, 0, 4) };
        var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        categories.Items.Add(new TabItem { Header = title, Content = scroll });
        return form;
    }

    private void AddPreference(Panel form, string text, Control control, string? help = null)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new Label { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            Target = control, Foreground = Foreground, Padding = new Thickness(0, 8, 12, 8) };
        row.Children.Add(label);
        Grid.SetColumn(control, 1);
        control.MinHeight = 36;
        control.VerticalAlignment = VerticalAlignment.Center;
        if (control is not CheckBox)
        {
            AutomationProperties.SetName(control, text);
            AutomationProperties.SetAutomationId(control, text);
        }
        row.Children.Add(control);
        preferenceRows.Add(row);
        form.Children.Add(row);
        if (help != null)
        {
            AutomationProperties.SetHelpText(control, help);
            control.ToolTip = help;
            var detail = new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
            Grid.SetRow(detail, 2);
            Grid.SetColumnSpan(detail, 2);
            row.Children.Add(detail);
            var icon = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1), Child = new TextBlock { Text = "i", FontSize = 11,
                    FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            icon.SetBinding(Border.BorderBrushProperty, new Binding
                { Path = new PropertyPath("(0)", System.Windows.Documents.TextElement.ForegroundProperty),
                    RelativeSource = new RelativeSource(RelativeSourceMode.Self) });
            var info = new Button { Content = icon, MinWidth = 28, MinHeight = 28,
                Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
            AutomationProperties.SetName(info, $"{text} help");
            AutomationProperties.SetAutomationId(info, $"{text} help");
            AutomationProperties.SetHelpText(info, help);
            AutomationProperties.SetItemStatus(info, "Collapsed");
            var tooltip = new ToolTip { PlacementTarget = info, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                Content = new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 } };
            info.ToolTip = tooltip;
            info.GotKeyboardFocus += (_, _) =>
            {
                if (IsRecordingShortcut) StopRecording();
                if (detail.Visibility != Visibility.Visible) tooltip.IsOpen = true;
            };
            info.LostKeyboardFocus += (_, _) => tooltip.IsOpen = false;
            info.Click += (_, _) =>
            {
                if (IsRecordingShortcut) StopRecording();
                tooltip.IsOpen = false;
                detail.Visibility = detail.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                AutomationProperties.SetItemStatus(info, detail.Visibility == Visibility.Visible ? "Expanded" : "Collapsed");
                if (detail.Visibility == Visibility.Visible) detail.BringIntoView();
            };
            var caption = new DockPanel();
            DockPanel.SetDock(info, Dock.Right);
            caption.Children.Add(info);
            var captionText = (UIElement)label.Content;
            label.Content = null;
            caption.Children.Add(captionText);
            label.Content = caption;
            contextualHelp.Add((info, detail));
        }
        ReflowPreferences();
    }

    private void ReflowPreferences()
    {
        double width = categories.ActualWidth > 0 ? categories.ActualWidth : Width - 40;
        bool narrow = width < 520;
        exposeHint.Margin = new Thickness(narrow ? 0 : 210, -8, 0, 12);
        foreach (var row in preferenceRows)
        {
            row.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(210);
            row.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetRow(row.Children[1], narrow ? 1 : 0);
            Grid.SetColumn(row.Children[1], narrow ? 0 : 1);
        }
    }

    private void ApplyControlStyles()
    {
        SettingsControlStyles.Apply(Resources, SystemParameters.HighContrast);
        var button = new Style(typeof(Button));
        button.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 8, 12, 8)));
        button.Setters.Add(new Setter(Control.MinHeightProperty, 36.0));
        button.Setters.Add(new Setter(Control.BackgroundProperty, SystemParameters.HighContrast ? SystemColors.ControlBrush : Brush("#303B34")));
        button.Setters.Add(new Setter(Control.ForegroundProperty, Foreground));
        var border = new FrameworkElementFactory(typeof(Border), "Frame");
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter), "ButtonText");
        presenter.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        foreach (var property in new[] { UIElement.IsMouseOverProperty, Button.IsPressedProperty })
        {
            var trigger = new Trigger { Property = property, Value = true };
            trigger.Setters.Add(new Setter(Border.BackgroundProperty, SystemParameters.HighContrast ? SystemColors.HighlightBrush : Brush("#465B4C"), "Frame"));
            trigger.Setters.Add(new Setter(System.Windows.Documents.TextElement.ForegroundProperty,
                SystemParameters.HighContrast ? SystemColors.HighlightTextBrush : Brushes.White, "ButtonText"));
            template.Triggers.Add(trigger);
        }
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brushes.White, "Frame"));
        template.Triggers.Add(focus);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(System.Windows.Documents.TextElement.ForegroundProperty,
            SystemParameters.HighContrast ? SystemColors.GrayTextBrush : Brush("#BCC5BE"), "ButtonText"));
        disabled.Setters.Add(new Setter(Border.BackgroundProperty, SystemParameters.HighContrast ? SystemColors.ControlBrush : Brush("#303B34"), "Frame"));
        template.Triggers.Add(disabled);
        button.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        button.Setters.Add(new Setter(Control.TemplateProperty, template));
        Resources[typeof(Button)] = button;
        var tab = new Style(typeof(TabItem));
        tab.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 10, 10, 10)));
        tab.Setters.Add(new Setter(Control.ForegroundProperty, Foreground));
        var tabBorder = new FrameworkElementFactory(typeof(Border), "TabFrame");
        tabBorder.SetValue(Border.BackgroundProperty, Background);
        tabBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        tabBorder.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 2));
        tabBorder.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        var header = new FrameworkElementFactory(typeof(ContentPresenter));
        header.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        tabBorder.AppendChild(header);
        var tabTemplate = new ControlTemplate(typeof(TabItem)) { VisualTree = tabBorder };
        var selected = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BorderBrushProperty, SystemParameters.HighContrast ? SystemColors.HighlightBrush : Brush("#7DDB9B"), "TabFrame"));
        tabTemplate.Triggers.Add(selected);
        var tabHover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        tabHover.Setters.Add(new Setter(Border.BackgroundProperty, SystemParameters.HighContrast ? SystemColors.HighlightBrush : Brush("#465B4C"), "TabFrame"));
        tabHover.Setters.Add(new Setter(Control.ForegroundProperty, SystemParameters.HighContrast ? SystemColors.HighlightTextBrush : Brushes.White));
        tabTemplate.Triggers.Add(tabHover);
        var tabFocus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        tabFocus.Setters.Add(new Setter(Border.BackgroundProperty, SystemParameters.HighContrast ? SystemColors.HighlightBrush : Brush("#465B4C"), "TabFrame"));
        tabFocus.Setters.Add(new Setter(Control.ForegroundProperty, SystemParameters.HighContrast ? SystemColors.HighlightTextBrush : Brushes.White));
        tabTemplate.Triggers.Add(tabFocus);
        tab.Setters.Add(new Setter(Control.TemplateProperty, tabTemplate));
        Resources[typeof(TabItem)] = tab;
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private static DataTemplate RecorderContentTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(DockPanel));
        var action = new FrameworkElementFactory(typeof(TextBlock));
        action.SetValue(DockPanel.DockProperty, Dock.Right);
        action.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 0, 0));
        action.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        action.SetBinding(TextBlock.TextProperty, new Binding("Tag") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        panel.AppendChild(action);
        var chord = new FrameworkElementFactory(typeof(TextBlock));
        chord.SetBinding(TextBlock.TextProperty, new Binding());
        chord.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        panel.AppendChild(chord);
        return new DataTemplate { VisualTree = panel };
    }
    private sealed class DecorativeImage : Image
    {
        protected override AutomationPeer? OnCreateAutomationPeer() => null;
    }
    private static string CornerLabel(string value) => value switch
    {
        "TopLeft" => "Top left", "TopRight" => "Top right", "BottomLeft" => "Bottom left", _ => "Bottom right"
    };

    private void StopRecording()
    {
        RecordingGeneration++;
        IsRecordingShortcut = false;
        activeRecorder = null;
        shortcut.Tag = appExposeShortcut.Tag = "Record";
        shortcut.Content = hotkey;
        feedback.Text = "";
        RefreshShortcutHint();
    }

    private void RefreshShortcutHint()
    {
        shortcut.Content = hotkey;
        if (!OverviewShortcutPair.TryParse(hotkey, appExposeHotkey, out var pair, out string error))
        {
            appExposeShortcut.Content = appExposeHotkey ?? "Record shortcut...";
            exposeHint.Text = error;
            RefreshRecorderNames();
            return;
        }
        string effective = pair.AppExpose is { } app ? ShortcutText(app) : "Record shortcut...";
        appExposeShortcut.Content = appExposeHotkey ?? effective;
        bool changed = hotkey != source.Hotkey || appExposeHotkey != source.AppExposeHotkey;
        exposeHint.Text = pair.AppExpose == null
            ? "App Exposé unavailable. Record an independent shortcut."
            : changed
                ? appExposeHotkey == null ? "Follows Overview. Save to check availability." : "Save to check availability."
                : isAppExposeAvailable()
                    ? appExposeHotkey == null ? "Follows Overview" : ""
                    : "App Exposé is unavailable: Windows could not reserve both shortcuts. Choose another shortcut.";
        exposeHint.Visibility = exposeHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshRecorderNames();
    }

    private void RefreshRecorderNames()
    {
        string appValue = appExposeHotkey ?? (OverviewShortcutPair.TryParse(hotkey, null, out var pair, out _) &&
            pair.AppExpose is { } app ? ShortcutText(app) : "not configured");
        SetName(shortcut, "Overview shortcut", hotkey);
        SetName(appExposeShortcut, "App Exposé shortcut", appValue);
        void SetName(Button recorder, string label, string value)
            => AutomationProperties.SetName(recorder, $"{label}: {value}" +
                (ReferenceEquals(activeRecorder, recorder) ? $". Recording. {feedback.Text}" : ""));
    }

    private void ReportRecordingError(string error)
    {
        feedback.Text = error;
        RefreshRecorderNames();
    }

    private static string ShortcutText(OverviewHotkey value)
    {
        var key = KeyInterop.KeyFromVirtualKey(value.Key);
        string name = key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(CultureInfo.InvariantCulture),
            Key.Return => "Enter", Key.Next => "PageDown", Key.Prior => "PageUp", _ => key.ToString()
        };
        return ((value.Modifiers & 8) != 0 ? "Win+" : "") +
            ((value.Modifiers & 1) != 0 ? "Ctrl+" : "") +
            ((value.Modifiers & 2) != 0 ? "Alt+" : "") +
            ((value.Modifiers & 4) != 0 ? "Shift+" : "") + name;
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
            ReportRecordingError("Include Ctrl, Alt or Win in the shortcut.");
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
        bool appRecording = ReferenceEquals(activeRecorder, appExposeShortcut);
        string proposedNormal = appRecording ? hotkey : value;
        string? proposedApp = appRecording ? value : appExposeHotkey;
        if (!OverviewShortcutPair.TryParse(proposedNormal, proposedApp, out var pair, out string error))
        {
            ReportRecordingError(error);
            return;
        }
        bool unchanged = !appRecording && pair.Normal == OverviewHotkey.Parse(source.Hotkey);
        if (!appRecording && appExposeHotkey == null && (modifiers & ModifierKeys.Shift) != 0 && !unchanged)
        {
            ReportRecordingError("Choose a shortcut without Shift, or record an independent App Exposé shortcut first.");
            return;
        }
        if (appRecording) appExposeHotkey = value;
        else hotkey = unchanged ? source.Hotkey : value;
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
        bool unchanged = hotkey == source.Hotkey && appExposeHotkey == source.AppExposeHotkey && hotCorners.IsChecked == source.EnableHotCorners
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
            categories.SelectedIndex = 1;
            ReportRecordingError("Finish recording a shortcut or press Escape to cancel recording.");
            (activeRecorder ?? shortcut).Focus();
            return false;
        }
        if (!int.TryParse(delay.Text, out int milliseconds) || milliseconds < 0)
        {
            categories.SelectedIndex = 2;
            delay.Tag = "Invalid";
            feedback.Text = "Enter a delay of zero or more milliseconds.";
            delay.Focus();
            return false;
        }
        var candidate = source.Copy();
        candidate.Hotkey = hotkey;
        candidate.AppExposeHotkey = appExposeHotkey;
        if (!OverviewShortcutPair.TryParse(candidate.Hotkey, candidate.AppExposeHotkey, out _, out string shortcutError))
        {
            categories.SelectedIndex = 1;
            feedback.Text = shortcutError;
            shortcut.Focus();
            return false;
        }
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
        appExposeHotkey = source.AppExposeHotkey;
        StopRecording();
        hotCorners.IsChecked = source.EnableHotCorners;
        foreach (ComboBoxItem corner in position.Items)
            if (((string)corner.Tag).Equals(source.HotCornerPosition, StringComparison.OrdinalIgnoreCase))
                position.SelectedItem = corner;
        delay.Text = source.HotCornerDelay.ToString(CultureInfo.CurrentCulture);
        RefreshStartupPreference();
    }
}
