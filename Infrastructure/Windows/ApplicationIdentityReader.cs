using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static WindowScatter.Win32Interop;

namespace WindowScatter
{
    internal static class ApplicationIdentityReader
    {
        public static bool IsEligiblePointedWindow(WindowActivationTarget target)
        {
            Validate(target);
            if (GetAncestor(target.Handle, GA_ROOT) != target.Handle ||
                !IsWindowVisible(target.Handle) || IsIconic(target.Handle))
                throw new InvalidOperationException("The window under the pointer is no longer available.");
            int style = GetWindowLong(target.Handle, GWL_EXSTYLE);
            if (style == 0 && Marshal.GetLastWin32Error() != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The pointed window's styles could not be read.");
            if (!WindowSurfacePolicy.IsEligible(unchecked((uint)style)))
            {
                Validate(target);
                return false;
            }
            var className = new StringBuilder(256);
            if (GetClassName(target.Handle, className, className.Capacity) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The pointed window's class could not be read.");
            if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "tooltips_class32" or "#32768")
            {
                Validate(target);
                return false;
            }
            int result = DwmGetWindowAttributeInt(target.Handle, DWMWA_CLOAKED, out int cloaked, sizeof(int));
            if (result < 0) throw new COMException("The pointed window's visibility could not be read.", result);
            int titleLength = GetWindowTextLength(target.Handle);
            if (titleLength == 0 && Marshal.GetLastWin32Error() != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The pointed window's title could not be read.");
            Validate(target);
            return cloaked == 0 && titleLength != 0;
        }

        public static ApplicationIdentity Read(WindowActivationTarget target)
        {
            Validate(target);
            // An owned dialog may inherit its owner's explicit shell identity, but
            // a cross-process owner must never turn a shared host into that app.
            IntPtr identityWindow = target.Handle;
            IntPtr owner = GetAncestor(target.Handle, GA_ROOTOWNER);
            if (owner != IntPtr.Zero && GetWindowThreadProcessId(owner, out uint ownerProcess) != 0 &&
                ownerProcess == target.ProcessId)
                identityWindow = owner;
            string? windowAppId = ReadWindowAppId(target.Handle);
            if (windowAppId == null && identityWindow != target.Handle)
                windowAppId = ReadWindowAppId(identityWindow);
            using var process = OpenProcess(0x1000, false, target.ProcessId); // QUERY_LIMITED_INFORMATION
            if (process.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The active application's identity could not be read.");
            if (ReadExecutablePath(process) is not { } path)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The application's executable could not be identified.");
            string? processAppId = windowAppId == null ? ReadProcessAppId(process) : null;
            var identity = ApplicationIdentity.Create(path, windowAppId, processAppId);
            Validate(target);
            return identity;
        }

        private static string? ReadExecutablePath(SafeProcessHandle process)
        {
            var path = new StringBuilder(32768);
            uint length = (uint)path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString() : null;
        }

        internal static string? ReadApplicationName(uint processId)
        {
            using var process = OpenProcess(0x1000, false, processId);
            if (process.IsInvalid || ReadExecutablePath(process) is not { } path ||
                path.StartsWith(@"\\", StringComparison.Ordinal) || ApplicationIdentity.IsSharedHost(path)) return null;
            try
            {
                string? name = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or Win32Exception)
            {
                return null; // Version metadata is optional; the title remains searchable.
            }
        }

        private static void Validate(WindowActivationTarget target)
        {
            if (!IsWindow(target.Handle) || GetWindowThreadProcessId(target.Handle, out uint process) != target.ThreadId ||
                process != target.ProcessId || target.ThreadId == 0 || target.ProcessId == 0)
                throw new InvalidOperationException("The application window changed before its identity could be read.");
        }

        private static string? ReadProcessAppId(SafeProcessHandle process)
        {
            uint length = 0;
            int result = GetApplicationUserModelId(process, ref length, null);
            if (result == 15703) return null; // APPMODEL_ERROR_NO_APPLICATION
            if (result != 122) throw new Win32Exception(result, "The packaged application identity could not be read.");
            var value = new StringBuilder((int)length);
            result = GetApplicationUserModelId(process, ref length, value);
            if (result != 0) throw new Win32Exception(result, "The packaged application identity could not be read.");
            return value.ToString();
        }

        private static string? ReadWindowAppId(IntPtr window)
        {
            Guid iid = typeof(IPropertyStore).GUID;
            int result = SHGetPropertyStoreForWindow(window, ref iid, out var store);
            Marshal.ThrowExceptionForHR(result);
            try
            {
                var key = new PropertyKey { Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
                Marshal.ThrowExceptionForHR(store.GetValue(ref key, out var value));
                try
                {
                    if (value.Type == 0) return null; // VT_EMPTY: no explicit window AUMID
                    if (value.Type != 31)
                        throw new InvalidOperationException("The window exposes an unsupported application identity.");
                    return Marshal.PtrToStringUni(value.Pointer);
                }
                finally { PropVariantClear(ref value); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey { public Guid Format; public uint Id; }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public IntPtr Pointer;
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant value);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetApplicationUserModelId(SafeProcessHandle process, ref uint length, StringBuilder? value);
    }
}
