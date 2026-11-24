using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace WindowScatter;

public sealed class OverviewThumbnailCanvas : Canvas
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ThumbnailListPeer(this);

    private sealed class ThumbnailListPeer(OverviewThumbnailCanvas owner) : FrameworkElementAutomationPeer(owner), ISelectionProvider
    {
        public bool CanSelectMultiple => false;
        public bool IsSelectionRequired => false;
        public IRawElementProviderSimple[] GetSelection() => owner.Children.OfType<OverviewThumbnail>()
            .Where(item => item.Visibility == Visibility.Visible && item.IsSelected)
            .Select(item => ProviderFromPeer(CreatePeerForElement(item))).ToArray();
        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Selection ? this : base.GetPattern(patternInterface);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
        protected override string GetNameCore() => "Windows";
        protected override List<AutomationPeer> GetChildrenCore() => owner.Children.OfType<OverviewThumbnail>()
            .Where(item => item.Visibility == Visibility.Visible)
            .Select(item => CreatePeerForElement(item)).ToList();
    }
}

// Keep Border's pointer/keyboard behavior: Button would also consume the overview's Space/drag input.
internal sealed class OverviewThumbnail : Border
{
    private Func<bool> available = () => false;
    private Action<bool> select = _ => throw new ElementNotEnabledException();
    private Action invoke = () => throw new ElementNotEnabledException();
    internal bool IsSelected { get; private set; }

    internal OverviewThumbnail(string title)
    {
        AutomationProperties.SetName(this, title);
        AutomationProperties.SetHelpText(this, "Press Enter to activate. Hold Space to preview.");
    }

    internal void ConfigureAutomation(Func<bool> available, Action<bool> select, Action invoke)
    {
        this.available = available;
        this.select = select;
        this.invoke = invoke;
    }

    internal void SetSelected(bool selected)
    {
        if (IsSelected == selected) return;
        bool previous = IsSelected;
        IsSelected = selected;
        if (UIElementAutomationPeer.FromElement(this) is { } peer)
        {
            peer.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, previous, selected);
            peer.RaiseAutomationEvent(selected ? AutomationEvents.SelectionItemPatternOnElementSelected
                : AutomationEvents.SelectionItemPatternOnElementRemovedFromSelection);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ThumbnailPeer(this);

    private sealed class ThumbnailPeer(OverviewThumbnail owner) : FrameworkElementAutomationPeer(owner),
        IInvokeProvider, ISelectionItemProvider
    {
        private bool Available => owner.Visibility == Visibility.Visible && owner.IsEnabled && owner.available();
        public bool IsSelected => owner.IsSelected;
        public IRawElementProviderSimple? SelectionContainer =>
            VisualTreeHelper.GetParent(owner) is OverviewThumbnailCanvas canvas
                ? ProviderFromPeer(CreatePeerForElement(canvas)) : null;
        protected override bool IsEnabledCore() => Available;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        protected override List<AutomationPeer>? GetChildrenCore() =>
            owner.Visibility == Visibility.Visible && owner.Child is Grid grid ? grid.Children.OfType<OverviewCloseButton>()
                .Select(button => CreatePeerForElement(button)).ToList() : null;
        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface is PatternInterface.Invoke or PatternInterface.SelectionItem ? this : base.GetPattern(patternInterface);

        private void EnsureAvailable()
        {
            if (!Available) throw new ElementNotEnabledException();
        }

        public void Invoke() => owner.Dispatcher.Invoke(() =>
        {
            EnsureAvailable();
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!Available)
                {
                    System.Diagnostics.Trace.TraceInformation("Queued thumbnail invocation canceled because the overview target is no longer available.");
                    return;
                }
                owner.invoke();
                RaiseAutomationEvent(AutomationEvents.InvokePatternOnInvoked);
            }));
        });

        public void Select() => owner.Dispatcher.Invoke(() => { EnsureAvailable(); owner.select(true); });
        public void AddToSelection() => Select();
        public void RemoveFromSelection() => owner.Dispatcher.Invoke(() =>
        {
            EnsureAvailable();
            if (IsSelected) owner.select(false);
        });
    }
}

internal sealed class OverviewCloseButton : Button
{
    internal static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(
        nameof(Selected), typeof(bool), typeof(OverviewCloseButton), new PropertyMetadata(false));
    public bool Selected { get => (bool)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }
    private readonly Func<bool> available;
    private readonly Action close;

    internal OverviewCloseButton(string title, Func<bool> available, Action close)
    {
        this.available = available;
        this.close = close;
        Width = Height = 28;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(3);
        Cursor = Cursors.Hand;
        IsTabStop = false;
        Focusable = true;
        ToolTip = $"Close {title}";
        AutomationProperties.SetName(this, $"Close {title}");
        AutomationProperties.SetHelpText(this, "Request normal window closure. The application may ask you to save.");
        Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
              <Grid Background="Transparent" SnapsToDevicePixels="True">
                <Ellipse x:Name="Circle" Fill="{TemplateBinding Background}"
                    Stroke="{TemplateBinding BorderBrush}" StrokeThickness="1.5"/>
                <Path Data="M9,9 L19,19 M19,9 L9,19" Stroke="{TemplateBinding Foreground}"
                    StrokeThickness="1.75" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
                    IsHitTestVisible="False"/>
                <Ellipse x:Name="Focus" Margin="2" Stroke="{TemplateBinding Foreground}"
                    StrokeThickness="1" StrokeDashArray="1,2" Visibility="Hidden" IsHitTestVisible="False"/>
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                  <Setter TargetName="Circle" Property="StrokeThickness" Value="2.5"/>
                  <Setter TargetName="Circle" Property="Stroke" Value="{DynamicResource CloseHover}"/>
                </Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="Circle" Property="Opacity" Value=".7"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Focus" Property="Visibility" Value="Visible"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value=".5"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        var style = new Style(typeof(OverviewCloseButton));
        style.Setters.Add(new Setter(OpacityProperty, 0d));
        style.Setters.Add(new Setter(IsHitTestVisibleProperty, false));
        var hidden = new Trigger { Property = VisibilityProperty, Value = Visibility.Hidden };
        hidden.Setters.Add(new Setter(IsEnabledProperty, false));
        style.Triggers.Add(hidden);
        foreach (var property in new[] { nameof(Selected), nameof(IsKeyboardFocused) })
        {
            var trigger = new Trigger { Property = property == nameof(Selected) ? SelectedProperty : IsKeyboardFocusedProperty, Value = true };
            trigger.Setters.Add(new Setter(OpacityProperty, 1d));
            trigger.Setters.Add(new Setter(IsHitTestVisibleProperty, true));
            style.Triggers.Add(trigger);
        }
        var hover = new DataTrigger
        {
            Binding = new Binding(nameof(IsMouseOver)) { RelativeSource = new(RelativeSourceMode.FindAncestor, typeof(Border), 1) },
            Value = true
        };
        hover.Setters.Add(new Setter(OpacityProperty, 1d));
        hover.Setters.Add(new Setter(IsHitTestVisibleProperty, true));
        style.Triggers.Add(hover);
        Style = style;
        ApplyPalette(SystemParameters.HighContrast);
    }

    internal void ApplyPalette(bool highContrast)
    {
        Background = highContrast ? SystemColors.WindowBrush : SharedBrushes.Text;
        Foreground = highContrast ? SystemColors.WindowTextBrush : SharedBrushes.Graphite;
        BorderBrush = highContrast ? SystemColors.HighlightBrush : SharedBrushes.Graphite;
        Resources["CloseHover"] = highContrast ? SystemColors.HighlightBrush : SharedBrushes.Mint;
    }

    protected override void OnClick()
    {
        // Do not raise a bubbling Click: minimized tiles otherwise restore/activate the source.
        if (IsEnabled && available()) close();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ClosePeer(this);

    private sealed class ClosePeer(OverviewCloseButton owner) : ButtonAutomationPeer(owner), IInvokeProvider
    {
        protected override bool IsEnabledCore() => base.IsEnabledCore() && owner.available();
        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);
        public void Invoke() => owner.Dispatcher.Invoke(() =>
        {
            if (!owner.IsEnabled || !owner.available()) throw new ElementNotEnabledException();
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!owner.IsEnabled || !owner.available())
                {
                    System.Diagnostics.Trace.TraceInformation("Queued close invocation canceled because the overview target is no longer available.");
                    return;
                }
                owner.OnClick();
                RaiseAutomationEvent(AutomationEvents.InvokePatternOnInvoked);
            }));
        });
    }
}

internal static class OverviewCloseInput
{
    internal static void Attach(WindowThumb thumb, string title, Func<bool> available, Action close)
    {
        bool CanInvoke() => !thumb.IsFiltered && thumb.CloseButton?.IsEnabled != false && available();
        var border = thumb.ClickBorder;
        var previous = border.Child;
        border.Child = null;
        var grid = new Grid();
        if (previous is FrameworkElement content)
        {
            // The minimized shelf uses real content, so reserve room rather than covering its title/icon.
            content.Margin = new Thickness(34, 0, 0, 0);
            grid.Children.Add(content);
        }
        var button = new OverviewCloseButton(title, CanInvoke, close)
        {
            Visibility = border.IsHitTestVisible ? Visibility.Visible : Visibility.Hidden
        };
        grid.Children.Add(button);
        border.Child = grid;
        thumb.CloseButton = button;
        bool middlePressed = false;
        border.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                middlePressed = CanInvoke();
                e.Handled = true;
            }
            else middlePressed = false;
        };
        border.PreviewMouseUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            e.Handled = true;
            bool pressed = middlePressed;
            middlePressed = false;
            if (pressed && CanInvoke()) close();
        };
        border.MouseLeave += (_, _) => middlePressed = false;
        border.IsVisibleChanged += (_, _) => middlePressed = false;
        border.IsHitTestVisibleChanged += (_, _) => middlePressed = false;
        button.IsEnabledChanged += (_, _) => middlePressed = false;
    }

    internal static bool IsCloseSource(DependencyObject source, OverviewCloseButton? button)
    {
        for (var current = source; current != null; current = current is Visual
            ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, button)) return true;
        return false;
    }
}
