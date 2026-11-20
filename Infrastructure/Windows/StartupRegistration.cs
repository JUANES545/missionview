using System.IO;

namespace WindowScatter
{
    internal static class StartupRegistration
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "WindowScatter";

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        internal static Action Capture()
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
            object? previous = key?.GetValue(RunValueName, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames);
            var kind = previous == null ? Microsoft.Win32.RegistryValueKind.String : key!.GetValueKind(RunValueName);
            return () =>
            {
                using var restoreKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                    ?? throw new IOException("Windows could not restore the startup registry key.");
                if (previous == null) restoreKey.DeleteValue(RunValueName, throwOnMissingValue: false);
                else restoreKey.SetValue(RunValueName, previous, kind);
            };
        }

        /// <summary>
        /// Makes the registry match <see cref="AppSettings.RunOnStartup"/> so the app (including
        /// the plain .exe release) survives restarts (#15).
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        internal static void Apply(bool enabled)
        {
            string exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Windows did not provide the executable path.");
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new IOException("Windows could not open the startup registry key.");
            if (enabled)
                key.SetValue(RunValueName, "\"" + exePath + "\"");
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }
}
