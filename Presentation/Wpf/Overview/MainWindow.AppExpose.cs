using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace WindowScatter
{
    public partial class MainWindow
    {
        private OverviewHotkeys? overviewHotkeys;
        private AppExposeRequest? appExposeRequest;
        partial void QueryShortcutRecording(ref bool recording);
        // Snapshot bits follow HotkeyParser: Ctrl=1, Alt=2, Shift=4, Win=8.
        partial void RecordOverviewShortcutKey(int virtualKey, int modifiers, bool isKeyDown);

        private void InitializeOverviewHotkeys(IntPtr handle)
        {
            if (!IsCoordinator) return;
            if (hookID == IntPtr.Zero)
            {
                Dispatcher.BeginInvoke(new Action(() => ReportShortcutError(
                    "Windows could not initialize the overview keyboard handler. Use the tray controls and restart MissionView.")));
                return;
            }
            overviewHotkeys = new OverviewHotkeys(
                (id, key) => RegisterHotKey(handle, id, key.NativeModifiers | 0x4000, (uint)key.Key),
                id => UnregisterHotKey(handle, id));
            if (!overviewHotkeys.TryInitialize(settings.Hotkey, settings.AppExposeHotkey, out string error) ||
                error.Length != 0)
                Dispatcher.BeginInvoke(new Action(() => ReportShortcutError(error)), DispatcherPriority.Background);
        }

        internal bool TryApplyOverviewHotkey(string hotkey, out string error)
            => TryApplyOverviewHotkeys(hotkey, settings.AppExposeHotkey, out error);

        internal bool TryApplyOverviewHotkeys(string hotkey, string? appExposeHotkey, out string error)
        {
            Dispatcher.VerifyAccess();
            if (!IsCoordinator) return coordinator.TryApplyOverviewHotkeys(hotkey, appExposeHotkey, out error);
            if (overviewHotkeys == null)
            {
                error = "The overview shortcuts are not initialized.";
                return false;
            }
            if (!overviewHotkeys.TryApply(hotkey, appExposeHotkey,
                preserveExistingShiftShortcut: false,
                out error))
            {
                System.Diagnostics.Trace.TraceError(error);
                return false;
            }
            shortcutInput.Reset();
            return true;
        }

        private void ReportShortcutError(string error)
        {
            if (isClosing) return;
            System.Diagnostics.Trace.TraceWarning(error);
            MessageBox.Show(this, error, "MissionView", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private async System.Threading.Tasks.Task ToggleAppExposeAsync(WindowActivationTarget foreground,
            WindowActivationTarget applicationWindow, string? captureError)
        {
            if (animationManager == null || isTransitioning || movingWindow) return;
            if (scatterInteraction.IsOpen || isScatterActive || IsVisible)
            {
                await ToggleScatterAsync();
                return;
            }
            if (!CanActivateScatter()) return;
            if (captureError != null)
            {
                ReportShortcutError($"App Expose could not identify the window under the pointer.\n{captureError}");
                return;
            }
            try
            {
                if (!ApplicationIdentityReader.IsEligiblePointedWindow(applicationWindow)) return;
                var request = AppExposeRequest.Capture(() => applicationWindow,
                    target => scatterInteraction.IsOwnWindow(target.Handle) || target.ProcessId == (uint)Environment.ProcessId,
                    ApplicationIdentityReader.Read, foreground);
                await StartScatterAsync(request);
            }
            catch (Exception ex) when (ex is ExternalException || ex is InvalidOperationException ||
                ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                System.Diagnostics.Trace.TraceWarning($"App Expose could not identify the window under the pointer: {ex}");
                MessageBox.Show(this, $"App Expose could not identify the window under the pointer.\n{ex.Message}",
                    "MissionView", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private Dictionary<IntPtr, List<WindowInfo>> EnumerateOverviewWindows()
        {
            appExposeRequest?.BeginEnumeration();
            return windowEnumerator.EnumerateByMonitor(monitors, appExposeRequest, showMinimizedWindows,
                views.Select(v => v.monitor).ToArray());
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr window, int id);
    }
}
