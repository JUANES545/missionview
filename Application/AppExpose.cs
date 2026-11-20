namespace WindowScatter
{
    internal sealed record ApplicationIdentity(string ExecutablePath, string? AppUserModelId)
    {
        public bool Matches(ApplicationIdentity other) =>
            AppUserModelId != null || other.AppUserModelId != null
                ? AppUserModelId != null && other.AppUserModelId != null &&
                  StringComparer.Ordinal.Equals(AppUserModelId, other.AppUserModelId)
                : StringComparer.OrdinalIgnoreCase.Equals(ExecutablePath, other.ExecutablePath);

        internal static ApplicationIdentity Create(string executablePath, string? windowAppId, string? processAppId)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
                throw new InvalidOperationException("The application's executable identity is unavailable.");
            string executable = executablePath.Replace('/', '\\').Split('\\')[^1];
            bool sharedHost = executable.ToLowerInvariant() is
                "applicationframehost.exe" or "wwahost.exe" or "dllhost.exe" or "rundll32.exe" or
                "runtimebroker.exe" or "svchost.exe" or "msedgewebview2.exe" or "electron.exe" or
                "java.exe" or "javaw.exe" or "python.exe" or "pythonw.exe" or "dotnet.exe" or
                "wscript.exe" or "cscript.exe" or "mshta.exe" or "powershell.exe" or "pwsh.exe";
            string? appId = string.IsNullOrWhiteSpace(windowAppId) ? null : windowAppId;
            if (sharedHost && appId == null)
                throw new InvalidOperationException("This shared application host does not expose a per-window application identity.");
            appId ??= string.IsNullOrWhiteSpace(processAppId) ? null : processAppId;
            return new(executablePath, appId);
        }
    }

    internal sealed class AppExposeRequest
    {
        public WindowActivationTarget Foreground { get; }
        public ApplicationIdentity Application { get; }
        public int UnidentifiedWindows { get; private set; }
        public int WindowsOverLimit { get; private set; }
        private readonly Func<WindowActivationTarget, ApplicationIdentity> identify;

        private AppExposeRequest(WindowActivationTarget foreground, ApplicationIdentity application,
            Func<WindowActivationTarget, ApplicationIdentity> identify)
        {
            Foreground = foreground;
            Application = application;
            this.identify = identify;
        }

        public static AppExposeRequest Capture(Func<WindowActivationTarget> captureWindow,
            Func<WindowActivationTarget, bool> isOwnWindow, Func<WindowActivationTarget, ApplicationIdentity> identify,
            WindowActivationTarget? previousForeground = null)
        {
            var window = captureWindow();
            if (window.Handle == IntPtr.Zero || window.ThreadId == 0 || window.ProcessId == 0)
                throw new InvalidOperationException("Point to an application window before opening App Expose.");
            if (isOwnWindow(window))
                throw new InvalidOperationException("Point to another application's window before opening App Expose.");
            return new(previousForeground ?? window, identify(window), identify);
        }

        public void BeginEnumeration() { UnidentifiedWindows = 0; WindowsOverLimit = 0; }
        public void RecordUnavailableWindow() => UnidentifiedWindows++;
        public void RecordWindowOverLimit() => WindowsOverLimit++;

        public bool Includes(WindowActivationTarget window)
        {
            try { return Application.Matches(identify(window)); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException ||
                ex is InvalidOperationException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                RecordUnavailableWindow();
                System.Diagnostics.Trace.TraceWarning(
                    $"App Expose skipped HWND 0x{window.Handle.ToInt64():X}: {ex.GetType().Name}, code 0x{ex.HResult:X8}.");
                return false;
            }
        }
    }
}
