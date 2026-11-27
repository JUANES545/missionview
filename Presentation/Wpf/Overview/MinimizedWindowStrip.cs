using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Markup;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media.Effects;

namespace WindowScatter
{
    internal sealed class MinimizedWindowStrip : Border
    {
        public IReadOnlyList<WindowThumb> Thumbs { get; }
        internal MinimizedShelfGeometry Geometry { get; }
        private readonly ScrollViewer scroll;
        private readonly WrapPanel wrap;
        private readonly double scale, left, top;
        private readonly Action<IntPtr> select;
        private readonly Func<bool> effectsEnabled;
        private readonly Func<string>? selectionBorderColor;
        private readonly List<MinimizedWindowButton> buttons = [];

        public MinimizedWindowStrip(IReadOnlyList<WindowInfo> windows, double width, double displayHeight, double scale,
            Action<IntPtr> clicked, Action<IntPtr> hovered, Func<IntPtr, ImageSource?>? readIcon = null,
            Action<IntPtr>? selected = null, Func<bool>? effectsEnabled = null, Func<string>? selectionBorderColor = null)
        {
            if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
            select = selected ?? (handle => SetSelected(handle));
            this.effectsEnabled = effectsEnabled ?? (() => true);
            this.selectionBorderColor = selectionBorderColor;
            this.scale = scale;
            Geometry = MinimizedShelfGeometry.Calculate(width, displayHeight, windows.Count, SystemParameters.VerticalScrollBarWidth);
            left = (width - Geometry.Width) / 2;
            top = Geometry.Top;
            Width = Geometry.Width;
            Height = Geometry.Height;
            Canvas.SetLeft(this, left);
            Canvas.SetTop(this, top);
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            SetResourceReference(BackgroundProperty, "Shelf");
            CornerRadius = new CornerRadius(20);
            if (windows.Count == 0) Visibility = Visibility.Collapsed;
            var dock = new DockPanel { Margin = new Thickness(12, 10, 12, 10) };
            var header = new TextBlock { Text = $"Minimized ({windows.Count})", FontFamily = new("Segoe UI"), TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 12, Height = 16, LineHeight = 16, Margin = new Thickness(0, 0, 0, 6) };
            header.SetResourceReference(TextBlock.ForegroundProperty, "Secondary");
            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);
            wrap = new WrapPanel
            {
                Width = Geometry.Columns * (Geometry.ItemWidth + 8),
                Margin = new Thickness(0, 0, -8, -8)
            };
            scroll = new ScrollViewer
            {
                Content = wrap, VerticalScrollBarVisibility = Geometry.Scrolls ? ScrollBarVisibility.Visible : ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false,
                Focusable = false, Padding = new Thickness(0), Height = Geometry.ViewportHeight
            };
            dock.Children.Add(scroll);
            Child = dock;
            var template = (ControlTemplate)XamlReader.Parse(ItemTemplate.Replace(
                "ASSEMBLY", typeof(MinimizedWindowButton).Assembly.GetName().Name));
            var itemStyle = new Style(typeof(MinimizedWindowButton));
            itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Text")));
            var thumbs = new List<WindowThumb>();
            foreach (var window in windows)
            {
                var title = new TextBlock
                {
                    Text = window.Title, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 14,
                    FontFamily = new("Segoe UI"), LineHeight = 20, VerticalAlignment = VerticalAlignment.Center
                };
                var content = new DockPanel();
                var source = (readIcon ?? ReadWindowIcon)(window.Handle);
                FrameworkElement icon = source != null
                    ? new Image { Source = source, Width = 24, Height = 24 }
                    : new System.Windows.Shapes.Path { Width = 24, Height = 24, Stretch = Stretch.Uniform,
                        StrokeThickness = 1.5, Data = System.Windows.Media.Geometry.Parse("M2,3 L22,3 22,21 2,21 Z M2,8 L22,8") };
                if (icon is System.Windows.Shapes.Path glyph) glyph.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
                    new Binding(nameof(Control.Foreground)) { RelativeSource = new(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
                icon.Margin = new Thickness(0, 0, 10, 0);
                icon.IsHitTestVisible = false;
                DockPanel.SetDock(icon, Dock.Left); content.Children.Add(icon);
                content.Children.Add(title);
                var button = new MinimizedWindowButton(this, window.Handle)
                {
                    Content = content, Template = template, Style = itemStyle, IsTabStop = false,
                    Focusable = true,
                    ToolTip = $"{window.Title}\nMinimized. Press Enter to restore. Preview and moving are unavailable." +
                        (source == null ? "\nApplication icon unavailable." : "")
                };
                var tooltip = new ToolTip { Content = button.ToolTip, PlacementTarget = button };
                button.ToolTip = tooltip;
                var tile = new Border
                {
                    Width = Geometry.ItemWidth, Height = 46, Margin = new Thickness(0, 0, 8, 8),
                    Child = button, Cursor = Cursors.Hand
                };
                AutomationProperties.SetName(button, $"{window.Title}, minimized");
                AutomationProperties.SetHelpText(button, "Press Enter to restore. Preview and moving are unavailable while minimized.");
                var thumb = new WindowThumb
                {
                    Title = window.Title,
                    ApplicationName = window.ApplicationName,
                    WindowHandle = window.Handle, Identity = window.Identity, WasMinimized = true,
                    WasMaximized = window.WasMaximized, ClickBorder = tile, HighlightBorder = tile, TitleLabel = title, MinimizedButton = button,
                    TargetWidth = tile.Width * scale, TargetHeight = tile.Height * scale
                };
                tile.MouseEnter += (_, _) => hovered(window.Handle);
                button.Click += (_, e) =>
                {
                    if (tile.Visibility == Visibility.Visible) clicked(window.Handle);
                    e.Handled = true;
                };
                buttons.Add(button);
                thumbs.Add(thumb);
                wrap.Children.Add(tile);
            }
            Thumbs = thumbs;
            scroll.ScrollChanged += (_, _) => UpdateGeometry();
            SizeChanged += (_, _) => UpdateGeometry();
            ApplyPalette(SystemParameters.HighContrast);
            Loaded += (_, _) => { ApplyPalette(SystemParameters.HighContrast); SystemParameters.StaticPropertyChanged += SystemPropertyChanged; };
            Unloaded += (_, _) => SystemParameters.StaticPropertyChanged -= SystemPropertyChanged;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var result = base.ArrangeOverride(finalSize);
            UpdateGeometry();
            return result;
        }

        private void UpdateGeometry()
        {
            foreach (var thumb in Thumbs)
            {
                Point point = thumb.ClickBorder.TranslatePoint(new Point(), this);
                thumb.TargetX = (left + point.X) * scale;
                thumb.TargetY = (top + point.Y) * scale;
            }
        }

        internal IReadOnlyList<NavigationTarget> NavigationTargets(bool visibleOnly)
        {
            if (Visibility != Visibility.Visible) return [];
            var targets = new List<NavigationTarget>();
            var viewport = scroll.Template?.FindName("PART_ScrollContentPresenter", scroll) as ScrollContentPresenter;
            foreach (var thumb in Thumbs)
            {
                var tile = thumb.ClickBorder;
                if (tile.Visibility != Visibility.Visible) continue;
                var bounds = new Rect(tile.TranslatePoint(new Point(), wrap), tile.RenderSize);
                if (visibleOnly)
                {
                    if (viewport == null) continue;
                    bounds = new Rect(tile.TranslatePoint(new Point(), viewport), tile.RenderSize);
                    bounds.Intersect(new Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight));
                    if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) continue;
                    bounds.Location = viewport.TranslatePoint(bounds.Location, this);
                    bounds.Offset(left, top);
                }
                var target = new NavigationTarget(thumb.Identity,
                    new(bounds.X * scale, bounds.Y * scale, bounds.Width * scale, bounds.Height * scale));
                if (target.IsValid) targets.Add(target);
            }
            return targets;
        }

        internal void ApplyTitleFilter(string foldedQuery)
        {
            bool changed = false;
            for (int i = 0; i < Thumbs.Count; i++)
            {
                var thumb = Thumbs[i];
                bool matches = thumb.MatchesTitle(foldedQuery);
                var visibility = matches ? Visibility.Visible : Visibility.Hidden;
                if (thumb.ClickBorder.Visibility == visibility) continue;
                thumb.ClickBorder.Visibility = visibility;
                thumb.RefreshSelectionOutline();
                buttons[i].IsEnabled = matches;
                buttons[i].RefreshKeyboardToolTip();
                changed = true;
            }
            if (changed) UIElementAutomationPeer.FromElement(this)?.ResetChildrenCache();
        }

        public void SetSelected(IntPtr handle, bool reveal = false, bool keyboardFocus = false)
        {
            for (int i = 0; i < Thumbs.Count; i++)
            {
                var thumb = Thumbs[i];
                bool selected = thumb.WindowHandle == handle;
                thumb.SetSelectionOutline(selected);
                if (thumb.CloseButton != null) thumb.CloseButton.Selected = selected;
                buttons[i].IsSelected = selected;
                buttons[i].KeyboardSelected = selected && keyboardFocus;
                if (selected && reveal)
                {
                    var bounds = new Rect(thumb.ClickBorder.TranslatePoint(new Point(), wrap), thumb.ClickBorder.RenderSize);
                    double offset = scroll.VerticalOffset;
                    if (bounds.Top < offset) scroll.ScrollToVerticalOffset(bounds.Top);
                    else if (bounds.Bottom > offset + scroll.ViewportHeight)
                        scroll.ScrollToVerticalOffset(bounds.Bottom - scroll.ViewportHeight);
                }
            }
            scroll.UpdateLayout();
            UpdateGeometry();
        }

        internal void SetSelectionOutlinesVisible(bool visible)
        {
            foreach (var thumb in Thumbs) thumb.SetOutlineTransition(!visible);
        }

        internal bool CanKeepWindows(IReadOnlyList<WindowInfo> windows) =>
            windows.Count == Thumbs.Count && Thumbs.All(thumb => windows.Any(window =>
                window.WasMinimized && window.Identity == thumb.Identity && window.Handle == thumb.WindowHandle &&
                window.Title == thumb.Title && window.ApplicationName == thumb.ApplicationName &&
                window.WasMaximized == thumb.WasMaximized));

        private void SystemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation))
                ApplyPalette(SystemParameters.HighContrast);
        }

        internal void ApplyPalette(bool highContrast)
        {
            bool effects = !highContrast && SystemParameters.ClientAreaAnimation && effectsEnabled();
            Resources["Shelf"] = highContrast ? SystemColors.WindowBrush : effects ? SharedBrushes.Dock : SharedBrushes.Graphite;
            Effect = effects ? new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Opacity = .24, Color = Colors.Black } : null;
            Resources["Item"] = highContrast ? SystemColors.WindowBrush : SharedBrushes.Raised;
            Resources["Hover"] = highContrast ? SystemColors.WindowBrush : Brush("#465B4C");
            Resources["Text"] = highContrast ? SystemColors.WindowTextBrush : SharedBrushes.Text;
            Resources["Secondary"] = highContrast ? SystemColors.WindowTextBrush : SharedBrushes.Secondary;
            Resources["Selected"] = highContrast ? SystemColors.HighlightBrush : Brush("#465B4C");
            Resources["SelectedText"] = highContrast ? SystemColors.HighlightTextBrush : SharedBrushes.Text;
            Resources["Outline"] = highContrast ? SystemColors.HighlightTextBrush : Brushes.White;
            Resources["Focus"] = highContrast ? SystemColors.WindowTextBrush : Brushes.White;
            var selectionBorder = highContrast ? SystemColors.HighlightBrush :
                SharedBrushes.SelectionBorder(selectionBorderColor?.Invoke() ?? "White");
            foreach (var thumb in Thumbs) thumb.CloseButton?.ApplyPalette(highContrast, selectionBorder);
        }

        private static Brush Brush(string value) => (Brush)new BrushConverter().ConvertFromInvariantString(value)!;

        protected override AutomationPeer OnCreateAutomationPeer() => new ShelfPeer(this);
        internal void SelectFromAutomation(IntPtr handle) => select(handle);

        private sealed class ShelfPeer(MinimizedWindowStrip owner) : FrameworkElementAutomationPeer(owner), ISelectionProvider
        {
            public bool CanSelectMultiple => false;
            public bool IsSelectionRequired => false;
            public IRawElementProviderSimple[] GetSelection() => owner.buttons.Where(b => !b.IsFiltered && b.IsSelected)
                .Select(b => ProviderFromPeer(CreatePeerForElement(b))).ToArray();
            public override object? GetPattern(PatternInterface patternInterface) =>
                patternInterface == PatternInterface.Selection ? this :
                patternInterface == PatternInterface.Scroll ? CreatePeerForElement(owner.scroll).GetPattern(patternInterface) :
                base.GetPattern(patternInterface);
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
            protected override string GetNameCore() => $"Minimized ({owner.Thumbs.Count})";
            protected override List<AutomationPeer> GetChildrenCore() => owner.Thumbs
                .Select((thumb, index) => (thumb, button: owner.buttons[index]))
                .Where(item => !item.thumb.IsFiltered)
                .SelectMany(item => item.thumb.CloseButton is { } close
                    ? new[] { CreatePeerForElement(item.button), CreatePeerForElement(close) }
                    : new[] { CreatePeerForElement(item.button) }).ToList();
        }

        private const string ItemTemplate = """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:local="clr-namespace:WindowScatter;assembly=ASSEMBLY"
                TargetType="{x:Type local:MinimizedWindowButton}">
              <Grid SnapsToDevicePixels="True" Background="Transparent">
                <Border x:Name="Surface" Background="{DynamicResource Item}" CornerRadius="10"/>
                <Border x:Name="Frame" Margin="2" Background="Transparent" CornerRadius="8"
                    BorderThickness="2" BorderBrush="Transparent">
                  <Grid Margin="6,0">
                    <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="16"/></Grid.ColumnDefinitions>
                    <ContentPresenter VerticalAlignment="Center"/>
                    <Path x:Name="Check" Grid.Column="1" Data="M2,8 L6,12 13,3" Stroke="{TemplateBinding Foreground}"
                        StrokeThickness="2" Width="16" Height="16" VerticalAlignment="Center" Visibility="Hidden"
                        IsHitTestVisible="False"/>
                  </Grid>
                </Border>
                <Rectangle x:Name="Focus" Margin="0.5" Stroke="{DynamicResource Focus}" StrokeThickness="1"
                    RadiusX="10" RadiusY="10" StrokeDashArray="1,2" IsHitTestVisible="False" Visibility="Hidden"/>
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Background" Value="{DynamicResource Hover}"/></Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="Surface" Property="Background" Value="{DynamicResource Hover}"/></Trigger>
                <Trigger Property="IsSelected" Value="True">
                  <Setter TargetName="Surface" Property="Background" Value="{DynamicResource Selected}"/>
                  <Setter Property="Foreground" Value="{DynamicResource SelectedText}"/>
                  <Setter TargetName="Check" Property="Visibility" Value="Visible"/>
                </Trigger>
                <MultiTrigger>
                  <MultiTrigger.Conditions>
                    <Condition Property="IsSelected" Value="True"/>
                    <Condition Property="SelectionOutlineVisible" Value="True"/>
                  </MultiTrigger.Conditions>
                  <Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource Outline}"/>
                </MultiTrigger>
                <Trigger Property="KeyboardSelected" Value="True"><Setter TargetName="Focus" Property="Visibility" Value="Visible"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Focus" Property="Visibility" Value="Visible"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """;

        private static ImageSource? ReadWindowIcon(IntPtr window)
        {
            // Borrow only the icon handle; never ask an application to restore for a preview.
            IntPtr icon = IntPtr.Zero;
            if (SendMessageTimeout(window, 0x007F, new IntPtr(2), IntPtr.Zero, 2, 50, out var result) != IntPtr.Zero)
                icon = result;
            if (icon == IntPtr.Zero) icon = GetClassLongPtr(window, -34); // GCLP_HICONSM
            if (icon == IntPtr.Zero) icon = GetClassLongPtr(window, -14); // GCLP_HICON
            try
            {
                if (icon == IntPtr.Zero)
                {
                    System.Diagnostics.Trace.TraceWarning("A minimized window did not provide an application icon.");
                    return null;
                }
                var source = Imaging.CreateBitmapSourceFromHIcon(icon,
                    Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is ExternalException)
            {
                System.Diagnostics.Trace.TraceWarning($"Could not read a minimized window icon: {ex.GetType().Name}.");
                return null;
            }

        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam,
            IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
        private static extern IntPtr GetClassLongPtr(IntPtr window, int index);
    }

    public sealed class MinimizedWindowButton : Button
    {
        private readonly MinimizedWindowStrip shelf;
        private readonly IntPtr handle;
        public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(nameof(IsSelected),
            typeof(bool), typeof(MinimizedWindowButton), new FrameworkPropertyMetadata(false, SelectionChanged));
        public static readonly DependencyProperty KeyboardSelectedProperty = DependencyProperty.Register(nameof(KeyboardSelected),
            typeof(bool), typeof(MinimizedWindowButton),
            new FrameworkPropertyMetadata(false, (sender, _) => ((MinimizedWindowButton)sender).RefreshKeyboardToolTip()));
        public static readonly DependencyProperty SelectionOutlineVisibleProperty = DependencyProperty.Register(nameof(SelectionOutlineVisible),
            typeof(bool), typeof(MinimizedWindowButton), new FrameworkPropertyMetadata(true));
        public bool IsSelected { get => (bool)GetValue(IsSelectedProperty); internal set => SetValue(IsSelectedProperty, value); }
        public bool KeyboardSelected { get => (bool)GetValue(KeyboardSelectedProperty); internal set => SetValue(KeyboardSelectedProperty, value); }
        public bool SelectionOutlineVisible { get => (bool)GetValue(SelectionOutlineVisibleProperty); internal set => SetValue(SelectionOutlineVisibleProperty, value); }
        internal bool IsFiltered => Parent is Border { Visibility: Visibility.Hidden } or
            Grid { Parent: Border { Visibility: Visibility.Hidden } };

        internal MinimizedWindowButton(MinimizedWindowStrip shelf, IntPtr handle)
        {
            this.shelf = shelf;
            this.handle = handle;
            FocusVisualStyle = null; // The template draws a system-colored focus cue inside the allocated bounds.
            GotKeyboardFocus += (_, _) => RefreshKeyboardToolTip();
            LostKeyboardFocus += (_, _) => RefreshKeyboardToolTip();
            Unloaded += (_, _) => { if (ToolTip is ToolTip tooltip) tooltip.IsOpen = false; };
        }

        internal void RefreshKeyboardToolTip()
        {
            if (ToolTip is ToolTip tooltip)
                tooltip.IsOpen = (KeyboardSelected || IsKeyboardFocused) && IsVisible && PresentationSource.FromVisual(this) != null;
        }

        private static void SelectionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (UIElementAutomationPeer.FromElement((MinimizedWindowButton)sender) is { } peer)
            {
                peer.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, e.OldValue, e.NewValue);
                if ((bool)e.NewValue) peer.RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementSelected);
            }
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new ItemPeer(this);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Space) { e.Handled = true; return; }
            base.OnKeyDown(e);
        }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.Key == Key.Space) { e.Handled = true; return; }
            base.OnKeyUp(e);
        }

        private sealed class ItemPeer(MinimizedWindowButton owner) : ButtonAutomationPeer(owner), ISelectionItemProvider
        {
            public bool IsSelected => owner.IsSelected;
            public IRawElementProviderSimple SelectionContainer => ProviderFromPeer(CreatePeerForElement(owner.shelf));
            public void Select() => owner.Dispatcher.Invoke(() =>
            {
                EnsureNotFiltered();
                owner.shelf.SelectFromAutomation(owner.handle);
            });
            public void AddToSelection() => Select();
            public void RemoveFromSelection()
            {
                owner.Dispatcher.Invoke(() =>
                {
                    EnsureNotFiltered();
                    if (IsSelected) owner.shelf.SelectFromAutomation(IntPtr.Zero);
                });
            }
            private void EnsureNotFiltered()
            {
                if (owner.IsFiltered) throw new ElementNotEnabledException();
            }
            public override object? GetPattern(PatternInterface patternInterface) =>
                patternInterface == PatternInterface.SelectionItem ? this : base.GetPattern(patternInterface);
            protected override List<AutomationPeer>? GetChildrenCore() => null;
        }
    }
}
