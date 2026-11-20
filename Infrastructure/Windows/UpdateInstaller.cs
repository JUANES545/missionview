using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindowScatter;

internal static class UpdateInstaller
{
    internal static bool IsSupported() => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) &&
        RuntimeInformation.OSArchitecture == Architecture.X64 && Environment.Is64BitProcess;

    internal static bool Start(string path)
    {
        using var process = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = AppContext.BaseDirectory
        });
        return process is not null;
    }
}
