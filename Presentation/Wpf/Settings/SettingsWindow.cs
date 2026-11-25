using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace WindowScatter;

internal sealed class SettingsWindow : Window
{
    private readonly AppSettings source;
    private readonly Func<AppSettings, bool> apply;
    private readonly Func<bool> isAppExposeAvailable;
    private readonly Func<bool> confirmReset;
    private readonly Action<Uri> openProjectLink;
    private readonly Button shortcut = new();
    private readonly Button appExposeShortcut = new();
    private readonly TextBlock shortcutPreview = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
        Margin = new Thickness(12, 0, 12, 12), Tag = "ShortcutPreview" };
    private readonly TextBlock appExposePreview = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
        Margin = new Thickness(12, 0, 12, 12), Tag = "ShortcutPreview" };
    private Button? activeRecorder;
    private readonly CheckBox hotCorners = new() { Content = "Enable hot corners" };
    private readonly ComboBox position = new();
    private readonly ComboBox selectionBorder = new();
    private readonly ComboBox overviewMonitors = new();
    private readonly ComboBox thumbnailSize = new();
    private readonly Slider dimming = new() { Minimum = 0, Maximum = 100, TickFrequency = 5,
        SmallChange = 1, LargeChange = 10, IsMoveToPointEnabled = true };
    private readonly CheckBox minimized = new() { Content = "Show minimized windows in App Exposé" };
    private readonly CheckBox overviewMinimized = new() { Content = "Show minimized windows in Overview" };
    private readonly CheckBox reduceAnimations = new() { Content = "Reduce animations" };
    private readonly TextBox delay = new();
    private readonly CheckBox startup = new() { Content = "Start with Windows" };
    private readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock exposeHint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
    private readonly StackPanel updatePanel = new();
    private readonly TabControl categories = new();
    private readonly List<Grid> preferenceRows = [];
    private readonly List<(Button Button, TextBlock Detail)> contextualHelp = [];
    private string hotkey;
    private string? appExposeHotkey;
    private bool syncingControls;
    internal bool IsRecordingShortcut { get; private set; }
    internal int RecordingGeneration { get; private set; }

    internal SettingsWindow(AppSettings source, Func<AppSettings, bool> apply, UIElement updates, Func<bool> isAppExposeAvailable,
        Func<bool>? confirmReset = null, Action<Uri>? openProjectLink = null)
    {
        this.source = source;
        this.apply = apply;
        this.isAppExposeAvailable = isAppExposeAvailable;
        this.openProjectLink = openProjectLink ?? (address =>
        {
            using var process = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
        });
        this.confirmReset = confirmReset ?? (() => MessageBox.Show(this,
            "Reset all settings to their defaults?", "Reset settings",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes);
        hotkey = source.Hotkey;
        appExposeHotkey = source.AppExposeHotkey;
        Title = "MissionView";
        AutomationProperties.SetName(this, "MissionView settings");
        Width = Math.Min(720, SystemParameters.WorkArea.Width);
        Height = Math.Min(480, SystemParameters.WorkArea.Height);
        MaxHeight = SystemParameters.WorkArea.Height;
        MinWidth = Math.Min(320, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(300, MaxHeight);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : SharedBrushes.Graphite;
        Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : SharedBrushes.Text;
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
        FontSize = 14;
        hotCorners.Foreground = startup.Foreground = minimized.Foreground = reduceAnimations.Foreground = Foreground;
        UseLayoutRounding = true;
        ApplyControlStyles();
        var root = new DockPanel { Margin = new Thickness(20), LastChildFill = true };
        Content = root;
        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Assertive);
        var feedbackStyle = new Style(typeof(TextBlock));
        feedbackStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("FieldForeground")));
        feedbackStyle.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(12, 8, 12, 8)));
        feedbackStyle.Setters.Add(new Setter(TextBlock.BackgroundProperty, new DynamicResourceExtension("PreferenceSurface")));
        var errorFeedback = new Trigger { Property = TagProperty, Value = "Error" };
        errorFeedback.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("FieldError")));
        feedbackStyle.Triggers.Add(errorFeedback);
        var noFeedback = new Trigger { Property = TextBlock.TextProperty, Value = "" };
        noFeedback.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
        feedbackStyle.Triggers.Add(noFeedback);
        feedback.Style = feedbackStyle;
        footer.Children.Add(new ScrollViewer { Content = feedback, MaxHeight = 84,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        KeyboardNavigation.SetTabIndex(footer, 100);
        var body = new DockPanel { VerticalAlignment = VerticalAlignment.Top };
        var bodyScroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
            VerticalContentAlignment = VerticalAlignment.Top };
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
        var heading = new TextBlock { Text = "Settings", FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(heading, 1);
        brand.Children.Add(heading);
        brand.Margin = new Thickness(0, 0, 0, 20);
        DockPanel.SetDock(brand, Dock.Top);
        body.Children.Add(brand);
        categories.Background = Background;
        categories.Foreground = Foreground;
        categories.BorderThickness = new Thickness(0);
        categories.Padding = new Thickness(0);
        categories.VerticalContentAlignment = VerticalAlignment.Top;
        categories.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        KeyboardNavigation.SetTabIndex(categories, 0);
        body.Children.Add(categories);
        var general = AddCategory("General");
        var shortcuts = AddCategory("Shortcuts");
        var corners = AddCategory("Hot corners");
        var about = AddCategory("Updates & About");
        startup.IsChecked = source.RunOnStartup;
        AddPreference(general, "Startup", startup, "Open MissionView when you sign in to Windows.");
        foreach (var color in new[] { "White", "Blue", "Mint" })
            selectionBorder.Items.Add(new ComboBoxItem { Content = color, Tag = color });
        RestoreSelectionBorder(source);
        AddPreference(general, "Selection border", selectionBorder,
            "Choose the outline color for selected window previews in Overview and App Exposé. High contrast uses the Windows highlight color.");
        overviewMonitors.Items.Add(new ComboBoxItem { Content = "All displays", Tag = "All" });
        overviewMonitors.Items.Add(new ComboBoxItem { Content = "Current display", Tag = "Current" });
        SelectItem(overviewMonitors, source.OverviewMonitorScope);
        AddPreference(general, "Overview displays", overviewMonitors,
            "Show only the display under the pointer when the overview opens. Moving windows between displays is unavailable in this mode.");
        foreach (var size in new[] { "Compact", "Standard", "Large" })
            thumbnailSize.Items.Add(new ComboBoxItem { Content = size, Tag = size });
        SelectItem(thumbnailSize, source.ThumbnailSize);
        AddPreference(general, "Thumbnail size", thumbnailSize,
            "Choose the size of window previews. Previews shrink when needed to fit the display.");
        dimming.Value = source.BackgroundDimming;
        dimming.SetResourceReference(Control.ForegroundProperty, "FieldFocus");
        AddPreference(general, "Background dimming", dimming,
            "Dim the desktop behind window previews, from 0 to 100 percent. High contrast uses an opaque Windows background.");
        minimized.IsChecked = source.ShowMinimizedWindows;
        overviewMinimized.IsChecked = source.ShowMinimizedWindowsInOverview;
        AddPreference(general, "Minimized in Overview", overviewMinimized,
            "List minimized windows in a separate shelf. Select a window to restore it.");
        AddPreference(general, "Minimized windows", minimized,
            "Include minimized windows from the application under the pointer in the App Exposé shelf.");
        reduceAnimations.IsChecked = source.ReduceAnimations;
        AddPreference(general, "Animations", reduceAnimations,
            "Open, close and enlarge previews without animated movement. Windows reduced-motion settings also apply.");
        var reset = new Button { Content = "Reset settings" };
        reset.Click += (_, _) =>
        {
            if (this.confirmReset()) ApplyCompletedChange(new AppSettings(), reset: true);
        };
        AddPreference(general, "Reset settings", reset,
            "Restore the default preferences after confirmation.");
        AddPreference(shortcuts, "Overview shortcut", shortcut, "Record a shortcut with Ctrl, Alt or Win. Press Escape to cancel recording.");
        var useAltTab = new Button { Content = "Use Alt+Tab" };
        AddPreference(shortcuts, "Windows switcher", useAltTab,
            "Use left Alt+Tab for Overview instead of the Windows switcher while MissionView is running.");
        AutomationProperties.SetName(useAltTab, "Use Alt+Tab");
        useAltTab.Click += (_, _) =>
        {
            StopRecording();
            if (!OverviewShortcutPair.TryParse("Alt+Tab", appExposeHotkey, out _, out string error))
            {
                ReportRecordingError(error);
                return;
            }
            if (source.Hotkey != "Alt+Tab") Change(p => p.Hotkey = "Alt+Tab");
        };
        AddPreference(shortcuts, "App Exposé shortcut", appExposeShortcut, "Open windows from the application under the pointer. Record a separate shortcut with Ctrl, Alt or Win. By default this follows Overview with Shift, except when Overview uses Alt+Tab.");
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
                feedback.Tag = null;
                feedback.Text = "Use Ctrl, Alt or Win with a key. Escape cancels recording.";
                shortcutPreview.Visibility = appExposePreview.Visibility = Visibility.Collapsed;
                RefreshRecorderNames();
            };
            recorder.PreviewKeyDown += RecordShortcut;
            recorder.LostKeyboardFocus += (_, _) =>
            {
                if (ReferenceEquals(activeRecorder, recorder)) StopRecording();
            };
        }
        AutomationProperties.SetLiveSetting(exposeHint, AutomationLiveSetting.Polite);
        AutomationProperties.SetAutomationId(exposeHint, "App Exposé availability");
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
        AddPreference(corners, "Delay (milliseconds)", delay, "Enter zero or more milliseconds, then press Enter to apply.");
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
        about.Children.Add(updatePanel);
        updatePanel.Children.Add(updates);
        about.Children.Add(new TextBlock { Text = "MIT license.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 20, 0, 0) });
        var credits = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        AddProjectLink(credits, "Original", "miguelo96/WindowScatter", "https://github.com/miguelo96/WindowScatter");
        AddProjectLink(credits, "Based on", "RuoChen5251/WindowScatter", "https://github.com/RuoChen5251/WindowScatter");
        AddProjectLink(credits, "Repository", "JUANES545/missionview", "https://github.com/JUANES545/missionview");
        about.Children.Add(credits);
        categories.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, categories)) return;
            if (IsRecordingShortcut) StopRecording();
            CollapseContextualHelp();
        };
        categories.SizeChanged += (_, _) => ReflowPreferences();
        BindCompletedChanges();
        SystemParameters.StaticPropertyChanged += RefreshSystemPalette;
        Closed += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= RefreshSystemPalette;
            CollapseContextualHelp();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                if (IsRecordingShortcut) StopRecording();
                else if (contextualHelp.Any(help => help.Detail.Visibility == Visibility.Visible))
                    CollapseContextualHelp();
                else Close();
                e.Handled = true;
            }
        };
        Loaded += (_, _) => startup.Focus();
    }

    internal void ShowUpdates() => categories.SelectedIndex = 3;

    private void AddProjectLink(Panel parent, string label, string project, string url)
    {
        var address = new Uri(url);
        var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        line.Inlines.Add(new Run($"{label}: "));
        var link = new Hyperlink(new Run(project)) { NavigateUri = address };
        link.SetResourceReference(TextElement.ForegroundProperty, "LinkForeground");
        var focus = new Style(typeof(Control));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetResourceReference(Border.BorderBrushProperty, "FieldFocus");
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        focus.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Control)) { VisualTree = border }));
        link.FocusVisualStyle = focus;
        AutomationProperties.SetName(link, $"{label}: {project}");
        AutomationProperties.SetHelpText(link, address.AbsoluteUri);
        line.RequestBringIntoView += (_, e) =>
        {
            // Wrapped hyperlinks otherwise request only their first text rectangle.
            if (!ReferenceEquals(e.TargetObject, line) || e.TargetRect.IsEmpty) return;
            e.Handled = true;
            line.BringIntoView();
        };
        link.RequestNavigate += (_, e) =>
        {
            e.Handled = true;
            try { openProjectLink(address); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Trace.TraceError($"Could not open project link '{address}': {error}");
                feedback.Tag = "Error";
                feedback.Text = $"Could not open this link. {error.Message}";
                UIElementAutomationPeer.FromElement(feedback)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
        };
        line.Inlines.Add(link);
        parent.Children.Add(line);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyNativeCaption(SystemParameters.HighContrast);
    }

    internal bool ApplyNativeCaption(bool highContrast, Func<IntPtr, int, int, int>? setAttribute = null)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return false;
        setAttribute ??= SetCaptionAttribute;
        bool applied = true;
        Write(20, highContrast ? 0 : 1);
        Write(35, highContrast ? -1 : ColorRef(SharedBrushes.Graphite.Color));
        Write(36, highContrast ? -1 : ColorRef(SharedBrushes.Text.Color));
        return applied;

        void Write(int attribute, int value)
        {
            int result = setAttribute(handle, attribute, value);
            if (result < 0)
            {
                applied = false;
                Trace.TraceWarning($"Settings caption attribute {attribute} failed (HRESULT 0x{result:X8}); Windows keeps its native fallback.");
            }
        }
        static int ColorRef(Color color) => color.R | color.G << 8 | color.B << 16;
    }

    private static int SetCaptionAttribute(IntPtr handle, int attribute, int value)
        => DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void CollapseContextualHelp()
    {
        foreach (var help in contextualHelp)
        {
            help.Detail.Visibility = Visibility.Collapsed;
            AutomationProperties.SetItemStatus(help.Button, "Collapsed");
        }
    }

    private void RefreshSystemPalette(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.HighContrast)) return;
        Dispatcher.Invoke(() =>
        {
            Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : SharedBrushes.Graphite;
            Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : SharedBrushes.Text;
            hotCorners.Foreground = startup.Foreground = minimized.Foreground = reduceAnimations.Foreground = Foreground;
            overviewMinimized.Foreground = Foreground;
            categories.Background = Background;
            categories.Foreground = Foreground;
            foreach (var row in preferenceRows)
                ((Label)row.Children[0]).Foreground = Foreground;
            ApplyControlStyles();
            ApplyNativeCaption(SystemParameters.HighContrast);
        });
    }

    private StackPanel AddCategory(string title)
    {
        var form = new StackPanel { Margin = new Thickness(0, 16, 0, 4) };
        categories.Items.Add(new TabItem { Header = title, Content = form });
        return form;
    }

    private void AddPreference(Panel form, string text, Control control, string? help = null)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var captionText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        captionText.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (control == dimming)
        {
            var summary = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0) };
            summary.SetBinding(TextBlock.TextProperty, new Binding(nameof(Slider.Value)) { Source = dimming, StringFormat = "{0:0}% dimming" });
            summary.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
            captionText.Children.Add(summary);
        }
        var label = new Label { Content = captionText,
            Target = control, Foreground = Foreground, Padding = new Thickness(0, 8, 8, 8), Margin = new Thickness(12, 0, 0, 0) };
        row.Children.Add(label);
        Grid.SetColumn(control, 1);
        control.MinHeight = 36;
        control.Margin = new Thickness(12, 8, 12, 8);
        control.VerticalAlignment = VerticalAlignment.Center;
        if (control is not CheckBox)
        {
            AutomationProperties.SetName(control, text);
            AutomationProperties.SetAutomationId(control, text);
        }
        row.Children.Add(control);
        if (ReferenceEquals(control, shortcut) || ReferenceEquals(control, appExposeShortcut))
        {
            var preview = ReferenceEquals(control, shortcut) ? shortcutPreview : appExposePreview;
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(preview, 3);
            Grid.SetColumnSpan(preview, 2);
            preview.SetResourceReference(TextBlock.ForegroundProperty, "FieldForeground");
            AutomationProperties.SetLiveSetting(preview, AutomationLiveSetting.Polite);
            AutomationProperties.SetHelpText(preview, "Applies when you save.");
            row.Children.Add(preview);
        }
        preferenceRows.Add(row);
        form.Children.Add(row);
        if (help != null)
        {
            AutomationProperties.SetHelpText(control, help);
            var detail = new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed, Margin = new Thickness(12, 0, 12, 12) };
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
            info.Click += (_, _) =>
            {
                if (IsRecordingShortcut) StopRecording();
                detail.Visibility = detail.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                AutomationProperties.SetItemStatus(info, detail.Visibility == Visibility.Visible ? "Expanded" : "Collapsed");
                if (detail.Visibility == Visibility.Visible) detail.BringIntoView();
            };
            var caption = new DockPanel();
            DockPanel.SetDock(info, Dock.Right);
            caption.Children.Add(info);
            var title = (UIElement)label.Content;
            label.Content = null;
            caption.Children.Add(title);
            label.Content = caption;
            contextualHelp.Add((info, detail));
        }
        var glyph = new DecorativeGlyph { Text = text switch
            {
                "Startup" => "\uE7E8", "Selection border" => "\uE790", "Overview shortcut" => "\uE8A7", "App Exposé shortcut" => "\uE737",
                "Overview displays" => "\uE7F4", "Thumbnail size" => "\uE8A7", "Background dimming" => "\uE706",
                "Minimized windows" or "Minimized in Overview" => "\uE921", "Animations" => "\uE768", "Reset settings" => "\uE777",
                "Activation" => "\uE7C9", "Corner" => "\uE745", _ => "\uE916"
            },
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16,
            Width = 28, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var labelContent = (UIElement)label.Content;
        if (help == null)
        {
            label.Content = null;
            var captionPanel = new DockPanel();
            captionPanel.Children.Add(glyph);
            captionPanel.Children.Add(labelContent);
            label.Content = captionPanel;
        }
        else
        {
            ((DockPanel)labelContent).Children.Insert(1, glyph);
        }
        var surface = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), IsHitTestVisible = false };
        surface.SetResourceReference(Border.BackgroundProperty, "PreferenceSurface");
        surface.SetResourceReference(Border.BorderBrushProperty, "PreferenceEdge");
        Grid.SetColumnSpan(surface, 2);
        Grid.SetRowSpan(surface, row.RowDefinitions.Count);
        Panel.SetZIndex(surface, -1);
        row.Children.Add(surface);
        ReflowPreferences();
    }

    private void ReflowPreferences()
    {
        double width = categories.ActualWidth > 0 ? categories.ActualWidth : Width - 40;
        bool narrow = width < 520;
        exposeHint.Margin = new Thickness(narrow ? 12 : 252, 0, 12, 12);
        foreach (var row in preferenceRows)
        {
            row.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(240);
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
        button.Setters.Add(new Setter(Control.BackgroundProperty, SystemParameters.HighContrast ? SystemColors.ControlBrush : SharedBrushes.Raised));
        button.Setters.Add(new Setter(Control.ForegroundProperty, Foreground));
        var border = new FrameworkElementFactory(typeof(Border), "Frame");
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
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
        tab.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 10, 12, 10)));
        tab.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 12, 0)));
        tab.Setters.Add(new Setter(Control.ForegroundProperty, Foreground));
        var tabBorder = new FrameworkElementFactory(typeof(Border), "TabFrame");
        tabBorder.SetValue(Border.BackgroundProperty, Background);
        tabBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        tabBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(6, 6, 0, 0));
        tabBorder.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 2));
        tabBorder.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        var header = new FrameworkElementFactory(typeof(ContentPresenter));
        header.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        tabBorder.AppendChild(header);
        var tabTemplate = new ControlTemplate(typeof(TabItem)) { VisualTree = tabBorder };
        var selected = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, SystemParameters.HighContrast ? SystemColors.WindowBrush : SharedBrushes.Surface, "TabFrame"));
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
        var categoryTemplate = new ControlTemplate(typeof(TabControl));
        var categoryRoot = new FrameworkElementFactory(typeof(DockPanel));
        var headers = new FrameworkElementFactory(typeof(WrapPanel));
        headers.SetValue(Panel.IsItemsHostProperty, true);
        headers.SetValue(DockPanel.DockProperty, Dock.Top);
        categoryRoot.AppendChild(headers);
        var selectedContent = new FrameworkElementFactory(typeof(ContentPresenter), "PART_SelectedContentHost");
        selectedContent.SetValue(ContentPresenter.ContentSourceProperty, "SelectedContent");
        selectedContent.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
        categoryRoot.AppendChild(selectedContent);
        categoryTemplate.VisualTree = categoryRoot;
        categories.Template = categoryTemplate;
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
        var chord = new FrameworkElementFactory(typeof(ItemsControl));
        chord.SetBinding(ItemsControl.ItemsSourceProperty, new Binding { Converter = new KeycapConverter() });
        var keys = new FrameworkElementFactory(typeof(WrapPanel));
        chord.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(keys));
        var key = new FrameworkElementFactory(typeof(Border));
        key.SetResourceReference(Border.BackgroundProperty, "KeycapSurface");
        key.SetResourceReference(Border.BorderBrushProperty, "FieldBorder");
        key.SetValue(Border.BorderThicknessProperty, new Thickness(1, 1, 1, 2));
        key.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        key.SetValue(Border.PaddingProperty, new Thickness(6, 2, 6, 2));
        key.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 4, 2));
        var keyText = new FrameworkElementFactory(typeof(TextBlock));
        keyText.SetBinding(TextBlock.TextProperty, new Binding());
        key.AppendChild(keyText);
        chord.SetValue(ItemsControl.ItemTemplateProperty, new DataTemplate { VisualTree = key });
        panel.AppendChild(chord);
        return new DataTemplate { VisualTree = panel };
    }
    private sealed class KeycapConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is string text ? text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Array.Empty<string>();
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
    private sealed class DecorativeImage : Image
    {
        protected override AutomationPeer? OnCreateAutomationPeer() => null;
    }
    private sealed class DecorativeGlyph : TextBlock
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
        shortcutPreview.Text = appExposePreview.Text = "";
        shortcutPreview.Visibility = appExposePreview.Visibility = Visibility.Collapsed;
        if (!OverviewShortcutPair.TryGetPreviewWarning(hotkey, appExposeHotkey, out string warning, out string error) ||
            !OverviewShortcutPair.TryParse(hotkey, appExposeHotkey, out var pair, out error))
        {
            appExposeShortcut.Content = appExposeHotkey ?? "Record shortcut...";
            exposeHint.Text = error;
            exposeHint.Visibility = Visibility.Visible;
            RefreshRecorderNames();
            return;
        }
        string effective = pair.AppExpose is { } app ? ShortcutText(app) : "Record shortcut...";
        appExposeShortcut.Content = appExposeHotkey ?? effective;
        exposeHint.Text = pair.AppExpose == null
            ? "App Exposé unavailable. Record an independent shortcut."
            : isAppExposeAvailable()
                    ? appExposeHotkey == null ? "Follows Overview" : ""
                    : "App Exposé is unavailable: Windows could not reserve both shortcuts. Choose another shortcut.";
        exposeHint.Visibility = exposeHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var preview = !pair.Normal.IsAltTab && pair.AppExpose == new OverviewHotkey(8, 'W') ? appExposePreview : shortcutPreview;
        preview.Text = warning;
        preview.Visibility = Visibility.Visible;
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
        feedback.Tag = "Error";
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
        feedback.Tag = "Error";
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
        StopRecording();
        if (appRecording && source.AppExposeHotkey != value) Change(p => p.AppExposeHotkey = value);
        else if (!appRecording && !unchanged) Change(p => p.Hotkey = value);
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

    internal void RefreshStartupPreference()
    {
        bool wasSyncing = syncingControls;
        syncingControls = true;
        try { startup.IsChecked = source.RunOnStartup; }
        finally { syncingControls = wasSyncing; }
    }

    private void BindCompletedChanges()
    {
        void Check(CheckBox control, Action<AppSettings, bool> set)
        {
            control.Checked += (_, _) => Change(p => set(p, true));
            control.Unchecked += (_, _) => Change(p => set(p, false));
        }
        void Choose(ComboBox control, Action<AppSettings, string> set)
        {
            control.SelectionChanged += (_, _) =>
            {
                if (control.SelectedItem is ComboBoxItem { Tag: string value }) Change(p => set(p, value));
            };
        }
        Check(hotCorners, (p, value) => p.EnableHotCorners = value);
        Check(minimized, (p, value) => p.ShowMinimizedWindows = value);
        Check(overviewMinimized, (p, value) => p.ShowMinimizedWindowsInOverview = value);
        Check(reduceAnimations, (p, value) => p.ReduceAnimations = value);
        Check(startup, (p, value) => p.RunOnStartup = value);
        Choose(position, (p, value) => p.HotCornerPosition = value);
        Choose(selectionBorder, (p, value) => p.SelectionBorderColor = value);
        Choose(overviewMonitors, (p, value) => p.OverviewMonitorScope = value);
        Choose(thumbnailSize, (p, value) => p.ThumbnailSize = value);
        bool dragging = false;
        void CommitDimming()
        {
            if (dimming.Value != source.BackgroundDimming) Change(p => p.BackgroundDimming = dimming.Value);
        }
        dimming.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => dragging = true));
        dimming.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, e) =>
        {
            dragging = false;
            if (!e.Canceled) CommitDimming();
            else
            {
                syncingControls = true;
                try { dimming.Value = source.BackgroundDimming; }
                finally { syncingControls = false; }
            }
        }));
        dimming.ValueChanged += (_, _) => { if (!dragging) CommitDimming(); };
        delay.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            CommitDelay();
        };
    }

    internal void CommitDelay()
    {
        if (int.TryParse(delay.Text, out int milliseconds) && milliseconds >= 0)
        {
            if (milliseconds != source.HotCornerDelay) Change(p => p.HotCornerDelay = milliseconds);
            else { delay.Tag = null; feedback.Text = ""; }
        }
        else
        {
            categories.SelectedIndex = 2;
            delay.Tag = "Invalid";
            ReportShortcutWarning("Enter a delay of zero or more milliseconds.");
        }
    }

    private void Change(Action<AppSettings> edit)
    {
        if (syncingControls) return;
        var candidate = source.Copy();
        edit(candidate);
        ApplyCompletedChange(candidate);
    }

    private void ApplyCompletedChange(AppSettings candidate, bool reset = false)
    {
        syncingControls = true;
        try
        {
            feedback.Text = "";
            bool applied = apply(candidate);
            string message = feedback.Text;
            if (!applied || reset) RestorePreferences();
            else
            {
                hotkey = source.Hotkey;
                appExposeHotkey = source.AppExposeHotkey;
                RefreshShortcutHint();
            }
            if (!applied || message.Length != 0)
                ReportShortcutWarning(!applied ? "Settings were not applied. Previous preferences are shown." : message);
        }
        finally { syncingControls = false; }
    }

    private void RestorePreferences()
    {
        var preferences = source;
        hotkey = preferences.Hotkey;
        appExposeHotkey = preferences.AppExposeHotkey;
        StopRecording();
        hotCorners.IsChecked = preferences.EnableHotCorners;
        foreach (ComboBoxItem corner in position.Items)
            if (((string)corner.Tag).Equals(preferences.HotCornerPosition, StringComparison.OrdinalIgnoreCase))
                position.SelectedItem = corner;
        delay.Text = preferences.HotCornerDelay.ToString(CultureInfo.CurrentCulture);
        RestoreSelectionBorder(preferences);
        SelectItem(overviewMonitors, preferences.OverviewMonitorScope);
        SelectItem(thumbnailSize, preferences.ThumbnailSize);
        dimming.Value = preferences.BackgroundDimming;
        minimized.IsChecked = preferences.ShowMinimizedWindows;
        overviewMinimized.IsChecked = preferences.ShowMinimizedWindowsInOverview;
        reduceAnimations.IsChecked = preferences.ReduceAnimations;
        startup.IsChecked = preferences.RunOnStartup;
    }

    private void RestoreSelectionBorder(AppSettings preferences)
    {
        foreach (ComboBoxItem color in selectionBorder.Items)
            if (((string)color.Tag).Equals(preferences.SelectionBorderColor, StringComparison.OrdinalIgnoreCase))
                selectionBorder.SelectedItem = color;
    }

    private static void SelectItem(ComboBox control, string value)
    {
        foreach (ComboBoxItem item in control.Items)
            if (Equals(item.Tag, value)) control.SelectedItem = item;
    }
}
