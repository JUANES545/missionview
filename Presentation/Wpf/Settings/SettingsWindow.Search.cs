using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using System.Runtime.InteropServices;

namespace WindowScatter;

internal sealed partial class SettingsWindow
{
    private const double CaptionBand = 44;
    private const double CaptionButtonReserve = 144;
    private const double CompactSearchWidth = 72;
    private const int DwmCaptionButtonBounds = 5;
    private readonly Grid searchArea = new();
    private readonly TextBox searchBox = new() { Padding = new Thickness(28, 6, 54, 6) };
    private readonly ScrollViewer filterScroll = new()
    {
        Name = "FilteredSettings", Visibility = Visibility.Collapsed, Focusable = false,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private readonly StackPanel filteredRows = new() { Margin = new Thickness(20, 16, 20, 20) };
    private readonly TextBlock searchStatus = new()
    {
        Text = "No matching settings", TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed
    };
    private readonly List<FilterRow> filterRows = [];
    private IInputElement? focusBeforeSearch;
    private double pageOffsetBeforeSearch;
    private object? categoryBeforeSearch;

    private Grid CreateTitleBar()
    {
        var chrome = new WindowChrome { UseAeroCaptionButtons = true };
        double MinimumCaptionHeight() => Math.Max(CaptionBand, SystemParameters.WindowCaptionHeight + chrome.ResizeBorderThickness.Top);
        Thickness SearchMargin()
        {
            double inset = Math.Max(5, Math.Ceiling(chrome.ResizeBorderThickness.Top) + 1);
            return new Thickness(4, inset, 4, inset);
        }
        void SetChromeHeight(double height)
        {
            chrome.GlassFrameThickness = new Thickness(0, height, 0, 0);
            chrome.CaptionHeight = Math.Max(0, height - chrome.ResizeBorderThickness.Top);
        }
        double bandHeight = MinimumCaptionHeight();
        SetChromeHeight(bandHeight);
        WindowChrome.SetWindowChrome(this, chrome);

        var leading = new ColumnDefinition { Width = new GridLength(CaptionButtonReserve) };
        var trailing = new ColumnDefinition { Width = new GridLength(CaptionButtonReserve) };
        searchArea.ColumnDefinitions.Add(leading);
        searchArea.ColumnDefinitions.Add(new ColumnDefinition());
        searchArea.ColumnDefinitions.Add(trailing);
        var captionRow = new RowDefinition { Height = GridLength.Auto, MinHeight = bandHeight };
        searchArea.RowDefinitions.Add(captionRow);
        DockPanel.SetDock(searchArea, Dock.Top);
        KeyboardNavigation.SetTabIndex(searchArea, 0);

        AddCaptionSurface(row: 0, columns: 2);

        void AddCaptionSurface(int row, int columns)
        {
            var surface = new Border { IsHitTestVisible = false };
            surface.SetResourceReference(Border.BackgroundProperty, "SettingsSurface");
            Grid.SetRow(surface, row);
            Grid.SetColumnSpan(surface, columns);
            searchArea.Children.Add(surface);
        }

        var brand = new DockPanel { Margin = new Thickness(12, 0, 6, 0), IsHitTestVisible = false };
        using (var stream = typeof(SettingsWindow).Assembly.GetManifestResourceStream("MissionView.TrayIcon.ico"))
        {
            if (stream != null)
                brand.Children.Add(new DecorativeImage
                {
                    Source = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad),
                    Width = 18, Height = 18, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center
                });
        }
        brand.Children.Add(new TextBlock
        {
            Text = Title, FontSize = 14, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
        });
        searchArea.Children.Add(brand);

        var field = AddSearch(SearchMargin());

        void SetCaptionLanes()
        {
            double buttons = CaptionButtonReserve;
            double height = MinimumCaptionHeight();
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero && DwmGetCaptionButtonBounds(handle, DwmCaptionButtonBounds, out var bounds, Marshal.SizeOf<CaptionButtonBounds>()) == 0 &&
                bounds.Right > bounds.Left)
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                buttons = Math.Max(buttons, Math.Ceiling((bounds.Right - bounds.Left) / dpi.DpiScaleX) + 8);
                height = Math.Max(height, Math.Ceiling(bounds.Bottom / dpi.DpiScaleY));
            }
            leading.Width = trailing.Width = new GridLength(buttons);
            MinWidth = Math.Min(SystemParameters.WorkArea.Width, Math.Max(376, 2 * buttons + CompactSearchWidth + 8));
            captionRow.MinHeight = height;
            field.Margin = SearchMargin();
            SetChromeHeight(Math.Max(height, captionRow.ActualHeight));
        }
        Loaded += (_, _) => SetCaptionLanes();
        StateChanged += (_, _) => Dispatcher.InvokeAsync(SetCaptionLanes, DispatcherPriority.Loaded);
        DpiChanged += (_, _) => Dispatcher.InvokeAsync(SetCaptionLanes, DispatcherPriority.Loaded);
        field.SizeChanged += (_, e) => SetChromeHeight(Math.Max(captionRow.MinHeight, e.NewSize.Height + field.Margin.Top + field.Margin.Bottom));
        return searchArea;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CaptionButtonBounds
    {
        internal int Left, Top, Right, Bottom;
    }

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetCaptionButtonBounds(IntPtr handle, int attribute, out CaptionButtonBounds bounds, int size);

    private Grid AddSearch(Thickness margin)
    {
        var field = new Grid { MaxWidth = 430, Margin = margin, ClipToBounds = true };
        Grid.SetColumn(field, 1);
        WindowChrome.SetIsHitTestVisibleInChrome(field, true);
        WindowChrome.SetIsHitTestVisibleInChrome(searchBox, true);
        field.Children.Add(searchBox);
        var placeholder = new DecorativeGlyph
        {
            Text = "Search settings", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(28, 0, 54, 0),
            IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis
        };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
        field.Children.Add(placeholder);
        var icon = new DecorativeGlyph
        {
            Text = "\uE721", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14,
            Margin = new Thickness(9, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
        field.Children.Add(icon);
        var hint = new DecorativeGlyph
        {
            Text = "Ctrl+K", FontSize = 12, Margin = new Thickness(0, 0, 9, 0), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
        field.Children.Add(hint);
        field.SizeChanged += (_, e) =>
        {
            bool showHint = e.NewSize.Width >= 208;
            bool showIcon = e.NewSize.Width >= 148;
            hint.Visibility = showHint ? Visibility.Visible : Visibility.Collapsed;
            icon.Visibility = showIcon ? Visibility.Visible : Visibility.Collapsed;
            int inset = e.NewSize.Width >= 72 ? 10 : 4;
            searchBox.Padding = new Thickness(showIcon ? 28 : inset, 6, showHint ? 54 : inset, 6);
            placeholder.Margin = new Thickness(searchBox.Padding.Left, 0, searchBox.Padding.Right, 0);
        };
        searchArea.Children.Add(field);
        AutomationProperties.SetName(searchBox, "Search settings");
        AutomationProperties.SetAcceleratorKey(searchBox, "Ctrl+K");
        AutomationProperties.SetHelpText(searchBox, "Type to filter settings. Press Tab to reach matching settings or Escape to clear search.");
        searchBox.GotKeyboardFocus += (_, e) =>
        {
            if (filterScroll.Visibility != Visibility.Visible && e.OldFocus != searchBox)
                focusBeforeSearch = e.OldFocus;
        };
        searchBox.TextChanged += (_, _) =>
        {
            placeholder.Visibility = searchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            FilterSettings();
        };
        KeyboardNavigation.SetTabNavigation(searchArea, KeyboardNavigationMode.Local);
        return field;
    }

    private Grid CreateSearchBody()
    {
        var body = new Grid { Children = { categories, filterScroll } };
        KeyboardNavigation.SetTabIndex(body, 1);
        KeyboardNavigation.SetTabNavigation(body, KeyboardNavigationMode.Local);
        filterScroll.SetResourceReference(BackgroundProperty, "SettingsSurface");
        filterScroll.BorderThickness = new Thickness(0);
        filterScroll.Content = filteredRows;
        AutomationProperties.SetName(filterScroll, "Matching settings");
        AutomationProperties.SetLiveSetting(searchStatus, AutomationLiveSetting.Polite);
        searchStatus.SetResourceReference(TextBlock.ForegroundProperty, "FieldForeground");
        filteredRows.Children.Add(searchStatus);
        filterScroll.ScrollChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, filterScroll) && e.ViewportWidthChange != 0) ReflowPreferences();
        };
        return body;
    }

    internal bool HandleSearchKey(Key key, ModifierKeys modifiers)
    {
        if (IsRecordingShortcut) return false;
        if (key == Key.K && modifiers == ModifierKeys.Control)
        {
            searchBox.Focus();
            searchBox.SelectAll();
            return true;
        }
        if (key == Key.Escape && (searchBox.Text.Length != 0 || searchArea.IsKeyboardFocusWithin))
        {
            searchBox.Clear();
            IInputElement? previous = focusBeforeSearch;
            if (previous is null || !ReferenceEquals(Keyboard.Focus(previous), previous))
                (categories.SelectedItem as TabItem)?.Focus();
            return true;
        }
        return false;
    }

    private void RegisterSearchRow(Panel owner, FrameworkElement row, string label, string keywords) =>
        filterRows.Add(new FilterRow(owner, row, $"{label} {keywords}"));

    private void FilterSettings()
    {
        var query = searchBox.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool filtering = filterScroll.Visibility == Visibility.Visible;
        if (query.Length == 0 && !filtering) return;
        if (!filtering)
        {
            categoryBeforeSearch = categories.SelectedItem;
            pageOffsetBeforeSearch = PageScroll?.VerticalOffset ?? 0;
            if (categories.IsKeyboardFocusWithin)
            {
                focusBeforeSearch = Keyboard.FocusedElement;
                searchBox.Focus();
            }
            categories.Visibility = Visibility.Collapsed;
            filterScroll.Visibility = Visibility.Visible;
        }
        var comparer = System.Globalization.CultureInfo.InvariantCulture.CompareInfo;
        int nextIndex = 0;
        foreach (FilterRow result in filterRows)
        {
            bool matches = query.Length != 0 && result.Row.Visibility == Visibility.Visible &&
                query.All(term => comparer.IndexOf(result.Text, term,
                    System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0);
            int originalIndex = result.Owner.Children.IndexOf(result.Row);
            if (matches)
            {
                if (originalIndex >= 0)
                {
                    // Keep the original position even when neighboring rows also match.
                    result.Owner.Children[originalIndex] = result.Slot;
                    filteredRows.Children.Insert(nextIndex, result.Row);
                }
                nextIndex++;
            }
            else if (originalIndex < 0)
            {
                if (result.Row.IsKeyboardFocusWithin) searchBox.Focus();
                filteredRows.Children.Remove(result.Row);
                result.Owner.Children[result.Owner.Children.IndexOf(result.Slot)] = result.Row;
            }
        }
        if (query.Length == 0)
        {
            AutomationProperties.SetItemStatus(searchBox, "");
            searchStatus.Visibility = Visibility.Collapsed;
            filterScroll.Visibility = Visibility.Collapsed;
            if (categoryBeforeSearch is { } tab) categories.SelectedItem = tab;
            categories.Visibility = Visibility.Visible;
            PageScroll?.ScrollToVerticalOffset(pageOffsetBeforeSearch);
        }
        else
        {
            searchStatus.Visibility = nextIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetItemStatus(searchBox, nextIndex == 1 ? "1 matching setting" : $"{nextIndex} matching settings");
            if (nextIndex == 0)
                UIElementAutomationPeer.CreatePeerForElement(searchStatus)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            filterScroll.ScrollToTop();
        }
        ReflowPreferences();
    }

    private sealed record FilterRow(Panel Owner, FrameworkElement Row, string Text)
    {
        internal Border Slot { get; } = new() { Visibility = Visibility.Collapsed };
    }
}
