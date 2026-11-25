using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

internal static partial class Program
{
    private static bool mutationIssued;

    private readonly record struct WindowMove(nint Handle, uint OwnerPid, Guid Source);

    private static int Main(string[] args)
    {
        Guid target = Guid.Empty;
        var switching = args is ["--vm-only", "switch", "--allow-switch", _, _]
            && Guid.TryParseExact(args[4], "D", out target) && target != Guid.Empty;
        WindowMove? move = null;
        if (args is ["--vm-only", "move", "--allow-move", _, var handleText, var pidText, var sourceText, var targetText]
            && TryParseWindowHandle(handleText, out var handle)
            && uint.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out var ownerPid)
            && ownerPid != 0
            && Guid.TryParseExact(sourceText, "D", out var source) && source != Guid.Empty
            && Guid.TryParseExact(targetText, "D", out target) && target != Guid.Empty
            && source != target)
            move = new(handle, ownerPid, source);

        if (!(args is ["--vm-only", "check" or "list"]) && !switching && move is null)
        {
            Console.Error.WriteLine("Usage: --vm-only check | --vm-only list | --vm-only switch --allow-switch <shell-epoch> <desktop-guid>");
            Console.Error.WriteLine("       --vm-only move --allow-move <shell-epoch> <0xHWND> <owner-pid> <source-guid> <target-guid>");
            return 2;
        }

        try
        {
            RequireCandidate();
            using var shell = new ShellEpoch();
            shell.Verify();
            if (args[1] == "check")
            {
                Console.WriteLine("Candidate gate passed; no COM calls made.");
                return 0;
            }

            if ((switching || move.HasValue) && args[3] != shell.Token)
                throw new InvalidOperationException("Stale Explorer epoch. Stop and take a fresh read-only list.");

            var result = DesktopAbi.Run(shell, target, move);
            shell.Verify();
            Console.Write(result);
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException
            or IOException or UnauthorizedAccessException or System.Security.SecurityException
            or System.ComponentModel.Win32Exception or ArgumentException)
        {
            var status = mutationIssued ? "Operation outcome unconfirmed; do not retry." : "Probe stopped before any mutation.";
            var reason = ex is InvalidOperationException ? ex.Message : $"HRESULT 0x{ex.HResult:X8}";
            Console.Error.WriteLine($"{status} {reason}");
            return mutationIssued ? 4 : 3;
        }
    }

    private static bool TryParseWindowHandle(string text, out nint handle)
    {
        handle = 0;
        if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
            return false;

        // Win32 HWNDs use 32 significant bits; accept only zero- or sign-extended input.
        int low = unchecked((int)value);
        if (low == 0 || (value > uint.MaxValue && value != unchecked((ulong)(long)low)))
            return false;

        handle = low;
        return true;
    }

    private static void RequireCandidate()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new InvalidOperationException("Native Windows x64 is required.");

        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var windows = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        if (Environment.OSVersion.Version is not { Major: 10, Minor: 0, Build: 26200 }
            || windows?.GetValue("CurrentBuildNumber") is not "26200"
            || windows?.GetValue("UBR") is not 8117)
            throw new InvalidOperationException("Only Windows build 26200.8117 is a candidate.");

        var binding = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "VmBinding")?.Value;
        using var crypto = machine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        if (binding is not { Length: 64 } || crypto?.GetValue("MachineGuid") is not string machineId
            || !Guid.TryParse(machineId, out _)
            || !string.Equals(binding, Hash(machineId), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This build is not bound to this guest.");

        using var self = Process.GetCurrentProcess();
        if (!Environment.UserInteractive || self.SessionId <= 0
            || (uint)self.SessionId != WTSGetActiveConsoleSessionId())
            throw new InvalidOperationException("Use the guest's interactive console, not a service or RDP.");
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class ShellEpoch : IDisposable
    {
        private readonly nint window;
        private readonly uint pid;
        private readonly SafeProcessHandle process;
        public string Token { get; }
        public uint Pid => pid;

        public ShellEpoch()
        {
            window = GetShellWindow();
            if (window == 0 || GetWindowThreadProcessId(window, out pid) == 0)
                throw new InvalidOperationException("Explorer shell is unavailable.");

            process = OpenProcess(0x101000 /* SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION */, 0, pid);
            try
            {
                if (process.IsInvalid || GetProcessTimes(process, out var created, out _, out _, out _) == 0)
                    throw new InvalidOperationException("Could not identify the Explorer process generation.");

                using var owner = Process.GetProcessById((int)pid);
                using var self = Process.GetCurrentProcess();
                if (!owner.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)
                    || owner.SessionId != self.SessionId)
                    throw new InvalidOperationException("The shell is not Explorer in this session.");

                Verify();
                Token = Hash($"{pid:X8}:{created:X16}:{window:X16}");
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }

        public void Verify()
        {
            if (WaitForSingleObject(process, 0) != 258 /* WAIT_TIMEOUT */
                || GetShellWindow() != window
                || GetWindowThreadProcessId(window, out var currentPid) == 0 || currentPid != pid)
                throw new InvalidOperationException("Explorer stopped, restarted, or changed. Stop the probe.");
        }

        public void Dispose() => process.Dispose();
    }

    [LibraryImport("user32.dll")] private static partial nint GetShellWindow();
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(nint window, out uint pid);
    [LibraryImport("user32.dll")] private static partial nint GetAncestor(nint window, uint flags);
    [LibraryImport("kernel32.dll")] private static partial uint WTSGetActiveConsoleSessionId();
    [LibraryImport("kernel32.dll")] private static partial SafeProcessHandle OpenProcess(uint access, int inherit, uint pid);
    [LibraryImport("kernel32.dll")] private static partial uint WaitForSingleObject(SafeProcessHandle handle, uint timeout);
    [LibraryImport("kernel32.dll")] private static partial int GetProcessTimes(
        SafeProcessHandle handle, out long created, out long exited, out long kernel, out long user);

    private static unsafe partial class DesktopAbi
    {
        private static readonly Guid ShellClass = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
        private static readonly Guid ProviderId = new("6D5140C1-7436-11CE-8034-00AA006009FA");
        private static readonly Guid ManagerService = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
        private static readonly Guid ManagerId = new("53F5CA0B-158F-4124-900C-057158060B27");
        private static readonly Guid DesktopId = new("3F07F4BE-B107-441A-AF0F-39D82529072C");
        private static readonly Guid PublicManagerId = new("A5CD92FF-29BE-454C-8D04-D82879FB3F1B");
        private static readonly Guid ViewCollectionId = new("1841C6D7-4F9D-42C0-AF41-8747538F10E5");

        internal static string Run(ShellEpoch shell, Guid target, WindowMove? move)
        {
            nint provider = 0, manager = 0, array = 0, selected = 0;
            shell.Verify();
            Check(CoInitializeEx(0, 0), "CoInitializeEx (MTA)");
            try
            {
                shell.Verify();
                Check(CoCreateInstance(in ShellClass, 0, 4 /* CLSCTX_LOCAL_SERVER */, in ProviderId, out provider),
                    "CoCreateInstance", provider);
                QueryService(shell, provider, ManagerService, ManagerId, &manager);
                shell.Verify();
                Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(manager, 7))(manager, &array),
                    "GetDesktops", array);

                uint count = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(array, 3))(array, &count), "GetCount");
                if (count is 0 or > 256)
                    throw new InvalidOperationException("GetDesktops returned an invalid probe count.");

                var iid = DesktopId;
                var ids = new HashSet<Guid>();
                var listing = new StringBuilder().AppendLine($"shell-epoch {shell.Token}");
                for (uint index = 0; index < count; index++)
                {
                    nint desktop = 0;
                    try
                    {
                        shell.Verify();
                        Check(((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Slot(array, 4))
                            (array, index, &iid, &desktop), "GetAt", desktop);
                        shell.Verify();
                        var id = ReadId(desktop);
                        if (!ids.Add(id))
                            throw new InvalidOperationException("GetDesktops returned a duplicate ID.");
                        listing.AppendLine($"{index + 1} {id:D}");
                        if (id == target)
                        {
                            selected = desktop;
                            desktop = 0;
                        }
                    }
                    finally
                    {
                        Release(desktop);
                    }
                }

                shell.Verify();
                var active = ReadCurrent(shell, manager);
                if (!ids.Contains(active))
                    throw new InvalidOperationException("Active desktop is absent from the snapshot.");
                if (target == Guid.Empty)
                    return listing.AppendLine($"active {active:D}").ToString();
                if (selected == 0)
                    throw new InvalidOperationException("Target desktop is absent.");
                if (move is { } request)
                {
                    if (!ids.Contains(request.Source))
                        throw new InvalidOperationException("Source desktop is absent.");
                    MoveWindow(shell, provider, manager, selected, request, target);
                    return $"Confirmed window on desktop {target:D}.{Environment.NewLine}";
                }
                if (active == target)
                    return $"Already active {active:D}; no switch attempted.{Environment.NewLine}";

                shell.Verify();
                mutationIssued = true;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(manager, 9))(manager, selected),
                    "SwitchDesktop");
                if (ReadCurrent(shell, manager) != target)
                    throw new InvalidOperationException("Active desktop readback did not match the target.");
                return $"Confirmed active {target:D}.{Environment.NewLine}";
            }
            finally
            {
                Release(selected);
                Release(array);
                Release(manager);
                Release(provider);
                CoUninitialize();
            }
        }

        private static void MoveWindow(ShellEpoch shell, nint provider, nint manager,
            nint selected, WindowMove move, Guid target)
        {
            nint publicManager = 0, collection = 0, view = 0;
            try
            {
                if (move.OwnerPid == shell.Pid || move.OwnerPid == (uint)Environment.ProcessId)
                    throw new InvalidOperationException("Select a window from a separate test app, not Explorer or the probe.");
                var ownerThread = VerifyWindow(move);

                QueryService(shell, provider, PublicManagerId, PublicManagerId, &publicManager);
                QueryService(shell, provider, ViewCollectionId, ViewCollectionId, &collection);
                shell.Verify();
                Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(collection, 6))
                    (collection, move.Handle, &view), "GetViewForHwnd", view);

                int canMove = 0;
                shell.Verify();
                Check(((delegate* unmanaged[Stdcall]<nint, nint, int*, int>)Slot(manager, 5))
                    (manager, view, &canMove), "CanViewMoveDesktops");
                if (canMove == 0)
                    throw new InvalidOperationException("Explorer does not allow this window to move.");
                if (ReadWindowDesktop(shell, publicManager, move) != move.Source)
                    throw new InvalidOperationException("Window is not on the expected source desktop.");
                shell.Verify();
                if (VerifyWindow(move) != ownerThread)
                    throw new InvalidOperationException("Window thread changed before dispatch.");

                mutationIssued = true;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Slot(manager, 4))
                    (manager, view, selected), "MoveViewToDesktop");

                if (ReadWindowDesktop(shell, publicManager, move) != target || VerifyWindow(move) != ownerThread)
                    throw new InvalidOperationException("Window identity or destination readback did not match.");
            }
            finally
            {
                Release(view);
                Release(collection);
                Release(publicManager);
            }
        }

        private static uint VerifyWindow(WindowMove move)
        {
            var thread = GetWindowThreadProcessId(move.Handle, out var pid);
            if (thread == 0 || pid != move.OwnerPid ||
                unchecked((int)GetAncestor(move.Handle, 2 /* GA_ROOT */)) != (int)move.Handle)
                throw new InvalidOperationException("Expected top-level test window is no longer available.");
            return thread;
        }

        private static Guid ReadWindowDesktop(ShellEpoch shell, nint manager, WindowMove move)
        {
            shell.Verify();
            VerifyWindow(move);
            Guid id = default;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, int>)Slot(manager, 4))
                (manager, move.Handle, &id), "GetWindowDesktopId");
            return id;
        }

        private static void QueryService(ShellEpoch shell, nint provider, Guid service, Guid iid, nint* result)
        {
            shell.Verify();
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, nint*, int>)Slot(provider, 3))
                (provider, &service, &iid, result), $"QueryService {iid:D}", *result);
        }

        private static Guid ReadCurrent(ShellEpoch shell, nint manager)
        {
            nint current = 0;
            try
            {
                shell.Verify();
                Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(manager, 6))(manager, &current),
                    "GetCurrentDesktop", current);
                shell.Verify();
                return ReadId(current);
            }
            finally
            {
                Release(current);
            }
        }

        private static Guid ReadId(nint desktop)
        {
            Guid id = default;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, int>)Slot(desktop, 4))(desktop, &id), "GetId");
            if (id == Guid.Empty)
                throw new InvalidOperationException("GetId returned an empty desktop ID.");
            return id;
        }

        private static nint Slot(nint instance, int index) => (*(nint**)instance)[index];

        private static void Release(nint instance)
        {
            if (instance != 0)
                ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance);
        }

        private static void Check(int hr, string operation)
        {
            if (hr < 0)
                throw new InvalidOperationException($"{operation}: HRESULT 0x{hr:X8}.");
        }

        private static void Check(int hr, string operation, nint result)
        {
            Check(hr, operation);
            if (result == 0)
                throw new InvalidOperationException($"{operation} returned a null interface.");
        }

        [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint flags);
        [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
        [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(
            in Guid clsid, nint outer, uint context, in Guid iid, out nint result);
    }
}
