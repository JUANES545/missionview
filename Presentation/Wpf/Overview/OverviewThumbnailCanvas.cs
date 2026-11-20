using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
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
            .Where(item => item.IsSelected).Select(item => ProviderFromPeer(CreatePeerForElement(item))).ToArray();
        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Selection ? this : base.GetPattern(patternInterface);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
        protected override string GetNameCore() => "Windows";
        protected override List<AutomationPeer> GetChildrenCore() => owner.Children.OfType<OverviewThumbnail>()
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
        private bool Available => owner.IsEnabled && owner.available();
        public bool IsSelected => owner.IsSelected;
        public IRawElementProviderSimple? SelectionContainer =>
            VisualTreeHelper.GetParent(owner) is OverviewThumbnailCanvas canvas
                ? ProviderFromPeer(CreatePeerForElement(canvas)) : null;
        protected override bool IsEnabledCore() => base.IsEnabledCore() && owner.available();
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        protected override List<AutomationPeer>? GetChildrenCore() => null;
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
