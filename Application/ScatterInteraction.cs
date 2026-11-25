namespace WindowScatter
{
    // Input policy is independent of WPF so key-repeat and deferred dismissal
    // can be verified without running the Windows overlay.
    internal sealed class ScatterInteraction
    {
        private bool activationKeyHeld;
        private bool dismissRequested;
        private bool confirmationRequested;
        private bool sessionOpen;
        private WindowActivationTarget previousWindow;
        private readonly HashSet<IntPtr> surfaces = new();
        public int Generation { get; private set; }
        public bool IsOpen => sessionOpen;
        public WindowActivationTarget PreviousWindow => previousWindow;
        public IReadOnlySet<IntPtr> Surfaces => surfaces;
        public void RegisterSurface(IntPtr handle) { if (handle != IntPtr.Zero) surfaces.Add(handle); }
        public void UnregisterSurface(IntPtr handle) => surfaces.Remove(handle);
        public bool IsOwnWindow(IntPtr handle) => handle != IntPtr.Zero && surfaces.Contains(handle);

        public void BeginSession(WindowActivationTarget previousWindow)
        {
            if (sessionOpen) return;
            Generation++;
            this.previousWindow = previousWindow;
            dismissRequested = false;
            confirmationRequested = false;
            sessionOpen = true;
        }

        public bool TryBeginClose(WindowActivationTarget? selection, out WindowActivationTarget target)
        {
            target = default;
            if (!sessionOpen) return false;
            sessionOpen = false;
            Generation++;
            dismissRequested = false;
            confirmationRequested = false;
            target = selection ?? previousWindow;
            previousWindow = default;
            return true;
        }

        public void InvalidateSession()
        {
            sessionOpen = false;
            previousWindow = default;
            Generation++;
            ClearDismissRequest();
        }

        public bool ActivationKeyDown(bool modifiersMatch)
        {
            bool activate = modifiersMatch && !activationKeyHeld;
            activationKeyHeld = true;
            return activate;
        }

        public void ActivationKeyUp() => activationKeyHeld = false;
        public void RequestDismiss()
        {
            confirmationRequested = false;
            dismissRequested = true;
        }

        public void RequestConfirmation()
        {
            dismissRequested = false;
            confirmationRequested = true;
        }

        public bool TakeConfirmationRequest(bool isBusy)
        {
            if (isBusy || !confirmationRequested) return false;
            confirmationRequested = false;
            return true;
        }

        public bool TakeDismissRequest(bool isBusy)
        {
            if (isBusy || !dismissRequested) return false;
            dismissRequested = false;
            return true;
        }

        public void ClearDismissRequest()
        {
            dismissRequested = false;
            confirmationRequested = false;
        }
    }
}
