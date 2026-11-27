using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace WindowScatter;

internal static class UpdateInstaller
{
    internal static bool IsSupported() => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) &&
        RuntimeInformation.OSArchitecture == Architecture.X64 && Environment.Is64BitProcess;

    internal static bool IsInstalled()
    {
        if (!IsSupported() || Environment.ProcessPath is not { } executable) return false;
        try
        {
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var registration = user.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{E983258E-4F39-4514-96FD-BCC8D270BAAF}_is1");
            return registration?.GetValue("DisplayIcon", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string icon
                && MatchesRegisteredExecutable(executable, icon);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            Trace.TraceWarning($"Could not verify MissionView installer registration: {ex.Message}");
            return false;
        }
    }

    internal static bool MatchesRegisteredExecutable(string executable, string displayIcon) =>
        string.Equals(Path.GetFileName(executable), "MissionView.exe", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(executable, displayIcon, StringComparison.OrdinalIgnoreCase);

    internal static bool Start(string path) => Start(path, "");

    internal static bool StartAndRestart(string path)
    {
        if (!IsInstalled()) throw new UpdateException("Restarting to update requires a registered MissionView installation.");
        return Start(path, "/RESTARTMISSIONVIEW");
    }

    private static bool Start(string path, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(path)
        {
            Arguments = arguments,
            UseShellExecute = true,
            WorkingDirectory = AppContext.BaseDirectory
        });
        return process is not null;
    }
}
