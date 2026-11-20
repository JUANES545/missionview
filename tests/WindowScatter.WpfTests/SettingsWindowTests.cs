using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WindowScatter;
using Xunit;

public sealed class SettingsWindowTests
{
    [Fact]
    public void SettingsExposeOnlyWorkingPreferencesWithNamesAndPreserveUnexposedRenderer()
        => RunSta(() =>
        {
            var source = new AppSettings { Hotkey = "Win+F12", EnableHotCorners = false, Renderer = "legacy", AnimationSpeed = 0.63 };
            AppSettings? saved = null;
            var window = new SettingsWindow(source, value => { saved = value; return true; }, new TextBlock { Text = "Updates unavailable" }, () => true);
            try
            {
                Assert.False(window.IsVisible);
                Assert.Null(UIElementAutomationPeer.CreatePeerForElement(Descendants(window).OfType<Image>().Single()));
                var controls = Descendants(window).OfType<Control>().ToArray();
                Assert.Single(controls.OfType<ComboBox>());
                Assert.Equal(new[] { "Overview shortcut: Win+F12", "App Exposé shortcut: Win+Shift+F12", "Corner", "Delay (milliseconds)" },
                    controls.Where(c => !string.IsNullOrEmpty(AutomationProperties.GetName(c)) &&
                        !AutomationProperties.GetAutomationId(c).EndsWith(" help")).Select(AutomationProperties.GetName));
                Assert.Equal("Win+F12", Button(window, "Win+F12").Content);
                Assert.False(controls.OfType<ComboBox>().Single().IsEnabled);
                TextBox(window, "Delay (milliseconds)").Text = "250";
                Button(window, "Save").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.NotNull(saved);
                Assert.Equal(0.63, saved.AnimationSpeed);
                Assert.Equal(250, saved.HotCornerDelay);
                Assert.Equal("legacy", saved.Renderer);
                Assert.Equal("Win+F12", saved.Hotkey);
                Assert.False(saved.EnableHotCorners);
                Assert.Equal(0.63, source.AnimationSpeed);
            }
            finally { window.Close(); }
        });

    [Theory]
    [InlineData("-1")]
    [InlineData("bad")]
    [InlineData("2147483648")]
    public void InvalidInputNeverAppliesAndKeepsWindowOpen(string delay)
        => RunSta(() =>
        {
            int calls = 0;
            var window = new SettingsWindow(new AppSettings(), _ => { calls++; return true; }, new TextBlock(), () => true);
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                TextBox(window, "Delay (milliseconds)").Text = delay;
                Button(window, "Save").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(0, calls);
                Assert.False(closed);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text.StartsWith("Enter "));
            }
            finally { window.Close(); }
        });

    [Fact]
    public void FailedApplyStaysOpenAndCancelDoesNotApply()
        => RunSta(() =>
        {
            int calls = 0;
            var source = new AppSettings { Hotkey = "Win+Shift+F12" };
            var window = new SettingsWindow(source, _ => { calls++; return false; }, new TextBlock(), () => false);
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                TextBox(window, "Delay (milliseconds)").Text = "750";
                Button(window, "Save").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(1, calls);
                Assert.False(closed);
                Assert.Equal("Win+Shift+F12", source.Hotkey);
                Assert.Equal(source.HotCornerDelay.ToString(), TextBox(window, "Delay (milliseconds)").Text);
                Button(window, "Cancel").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.True(closed);
                Assert.Equal(1, calls);
            }
            finally { window.Close(); }
        });

    [Fact]
    public void RecorderRejectsUnsupportedAndNewShiftCombinationsAndSavesSupportedShortcut()
        => RunSta(() =>
        {
            AppSettings? saved = null;
            var window = new SettingsWindow(new AppSettings { Hotkey = "Win+F12" },
                value => { saved = value; return true; }, new TextBlock(), () => true);
            try
            {
                var button = Button(window, "Win+F12");
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.True(window.IsRecordingShortcut);
                window.RecordShortcut(Key.F9, ModifierKeys.None);
                Assert.True(window.IsRecordingShortcut);
                window.RecordShortcut(Key.F9, ModifierKeys.Windows | ModifierKeys.Shift);
                Assert.True(window.IsRecordingShortcut);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains("without Shift"));
                window.RecordShortcut(Key.OemPlus, ModifierKeys.Control);
                Assert.True(window.IsRecordingShortcut);
                window.RecordShortcut(Key.F9, ModifierKeys.Control | ModifierKeys.Alt);
                Assert.False(window.IsRecordingShortcut);
                Assert.Equal("Ctrl+Alt+F9", button.Content);
                var hint = Descendants(window).OfType<TextBlock>().Single(text => AutomationProperties.GetLiveSetting(text) == AutomationLiveSetting.Polite);
                Assert.Contains("Save", hint.Text);
                Assert.Equal("Ctrl+Alt+Shift+F9", Descendants(window).OfType<Button>()
                    .Single(b => AutomationProperties.GetAutomationId(b) == "App Exposé shortcut").Content);
                Button(window, "Save").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal("Ctrl+Alt+F9", saved?.Hotkey);
            }
            finally { window.Close(); }
        });

    [Fact]
    public void RecorderCanRetainLegacyShiftShortcutWithoutReinterpretingIt()
        => RunSta(() =>
        {
            var window = new SettingsWindow(new AppSettings { Hotkey = "Win+Shift+F12" }, _ => true, new TextBlock(), () => false);
            try
            {
                var button = Button(window, "Win+Shift+F12");
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                window.RecordShortcut(Key.F12, ModifierKeys.Windows | ModifierKeys.Shift);
                Assert.False(window.IsRecordingShortcut);
                Assert.Equal("Win+Shift+F12", button.Content);
                var hint = Descendants(window).OfType<TextBlock>().Single(text => AutomationProperties.GetLiveSetting(text) == AutomationLiveSetting.Polite);
                Assert.Contains("unavailable", hint.Text);
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                window.RecordShortcut(Key.F9, ModifierKeys.Control);
                Assert.Equal("Ctrl+Shift+F9", Descendants(window).OfType<Button>()
                    .Single(b => AutomationProperties.GetAutomationId(b) == "App Exposé shortcut").Content);
                Assert.DoesNotContain("unavailable", hint.Text);
            }
            finally { window.Close(); }
        });

    [Fact]
    public void ReservedShortcutRawBridgeUsesHeritageModifierBitsAndIgnoresDuplicateDelivery()
        => RunSta(() =>
        {
            var window = new SettingsWindow(new AppSettings { Hotkey = "Win+F12" }, _ => true, new TextBlock(), () => true);
            try
            {
                var button = Button(window, "Win+F12");
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                int generation = window.RecordingGeneration;
                window.RecordVirtualKey(0x5B, 8, true);
                window.RecordVirtualKey(0x7B, 8, false);
                Assert.True(window.IsRecordingShortcut);
                window.RecordVirtualKey(0x7B, 8, true);
                Assert.False(window.IsRecordingShortcut);
                Assert.True(window.RecordingGeneration > generation);
                Assert.Equal("Win+F12", button.Content);
                window.RecordShortcut(Key.F9, ModifierKeys.Control);
                Assert.Equal("Win+F12", button.Content);
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                generation = window.RecordingGeneration;
                window.RecordVirtualKey(0x1B, 0, true);
                Assert.False(window.IsRecordingShortcut);
                Assert.True(window.RecordingGeneration > generation);
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                window.RecordVirtualKey(0x78, 3, true);
                Assert.Equal("Ctrl+Alt+F9", button.Content);
            }
            finally { window.Close(); }
        });

    [Fact]
    public void TrayStartupPreferenceRefreshDoesNotDiscardOtherPendingFields()
        => RunSta(() =>
        {
            var source = new AppSettings { RunOnStartup = false };
            var window = new SettingsWindow(source, _ => true, new TextBlock(), () => true);
            try
            {
                TextBox(window, "Delay (milliseconds)").Text = "725";
                source.RunOnStartup = true;
                window.RefreshStartupPreference();
                Assert.True(Descendants(window).OfType<CheckBox>().Single(c => Equals(c.Content, "Start with Windows")).IsChecked);
                Assert.Equal("725", TextBox(window, "Delay (milliseconds)").Text);
            }
            finally { window.Close(); }
        });

    [Fact]
    public void HintUsesActualReservationAndKeepsNewShortcutPendingUntilSaved()
        => RunSta(() =>
        {
            bool allowReservation = false;
            var hotkeys = new OverviewHotkeys((_, _) => allowReservation, _ => true);
            Assert.True(hotkeys.TryInitialize("Win+W", out string warning));
            Assert.NotEmpty(warning);
            var source = new AppSettings { Hotkey = "Win+W" };
            var window = new SettingsWindow(source, candidate =>
            {
                Assert.True(hotkeys.TryApply(candidate.Hotkey, false, out string error), error);
                source.CopyFrom(candidate);
                return true;
            }, new TextBlock(), () => hotkeys.AppExpose != null);
            try
            {
                var hint = Descendants(window).OfType<TextBlock>().Single(text => AutomationProperties.GetLiveSetting(text) == AutomationLiveSetting.Polite);
                Assert.Contains("unavailable", hint.Text);
                Assert.Contains("reserve", hint.Text);
                Button(window, "Win+W").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                window.RecordShortcut(Key.F9, ModifierKeys.Control | ModifierKeys.Alt);
                Assert.Contains("Follows Overview", hint.Text);
                Assert.Contains("Save", hint.Text);
                Assert.DoesNotContain("hold Shift", hint.Text);
                allowReservation = true;
                Button(window, "Save").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.NotNull(hotkeys.AppExpose);
                Assert.Equal("Follows Overview", hint.Text);
                Assert.Equal("Ctrl+Alt+Shift+F9", Descendants(window).OfType<Button>()
                    .Single(b => AutomationProperties.GetAutomationId(b) == "App Exposé shortcut").Content);
            }
            finally { window.Close(); }
        });

    [Fact]
    public void SuccessfulApplyWarningStaysAccessibleWithoutFailingOrClosingSettings()
        => RunSta(() =>
        {
            var source = new AppSettings { Hotkey = "Win+W" };
            int calls = 0;
            SettingsWindow? window = null;
            window = new SettingsWindow(source, candidate =>
            {
                calls++;
                source.CopyFrom(candidate);
                window!.ReportShortcutWarning("The general overview shortcut is preserved; App Exposé is unavailable.");
                return true;
            }, new TextBlock(), () => false);
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                TextBox(window, "Delay (milliseconds)").Text = "725";
                Button(window, "Save").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(1, calls);
                Assert.Equal(725, source.HotCornerDelay);
                Assert.False(closed);
                var warning = Descendants(window).OfType<TextBlock>().Single(text => text.Text.StartsWith("The general"));
                Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(warning));
                Assert.DoesNotContain(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains("not applied"));
            }
            finally { window.Close(); }
        });

    private static Button Button(Window window, string content) => Descendants(window).OfType<Button>().Single(b => Equals(b.Content, content));
    private static TextBox TextBox(Window window, string name) => Descendants(window).OfType<TextBox>().Single(b => AutomationProperties.GetName(b) == name);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
    }
}
