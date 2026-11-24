using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WindowScatter;
using Xunit;
using static WindowScatter.Win32Interop;

public class AppExposeNativeTests
{
    [Theory]
    [InlineData("desktop")]
    [InlineData("missing")]
    [InlineData("tool")]
    [InlineData("untitled")]
    public void PointerNoTargetSuppressesShiftShortcutWithoutDispatchOrFocusChanges(string kind)
        => RunSta(() =>
        {
            using var tool = new HwndSource(new HwndSourceParameters("Pointer no-target tool")
            {
                WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080,
                PositionX = -30000, PositionY = -28000, Width = 200, Height = 100
            });
            using var untitled = new HwndSource(new HwndSourceParameters("")
            {
                WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000000,
                PositionX = -30000, PositionY = -28000, Width = 200, Height = 100
            });
            IntPtr hit = kind switch
            {
                "desktop" => GetShellWindow(),
                "tool" => tool.Handle,
                "untitled" => untitled.Handle,
                _ => IntPtr.Zero
            };
            if (kind == "desktop") Assert.NotEqual(IntPtr.Zero, hit);
            // Exercise the same native eligibility check that produced the user's warning,
            // without ever showing a dialog or opening an overview.
            if (kind == "tool")
                Assert.False(ApplicationIdentityReader.IsEligiblePointedWindow(Target(hit)));
            var activation = new WindowActivation();
            var interaction = new ScatterInteraction();
            var hotkeys = new OverviewHotkeys((_, _) => true, _ => true);
            Assert.True(hotkeys.TryInitialize("Ctrl+Alt+K", out var error), error);
            IntPtr foreground = GetForegroundWindow();
            var input = new OverviewShortcutInput(interaction, () => activation.Capture(foreground),
                () => activation.CapturePointerWindow(hit));
            input.Process(0xA2, true, false, hotkeys);
            input.Process(0xA4, true, false, hotkeys);
            input.Process(0xA0, true, false, hotkeys);
            for (int press = 0; press < 2; press++)
            {
                var queued = input.Process('K', true, false, hotkeys);
                Assert.True(queued.Suppress);
                Assert.False(queued.Activate); // The real hook cannot schedule UI or activation.
                Assert.Null(queued.CaptureError);
                Assert.Equal(default, queued.ApplicationWindow);
                Assert.False(interaction.IsOpen);
                Assert.Equal(foreground, GetForegroundWindow());
                input.Process('K', false, false, hotkeys);
            }
            input.Process(0xA0, false, false, hotkeys);
            var normal = input.Process('K', true, false, hotkeys);
            Assert.True(normal.Activate);
            Assert.False(normal.AppExpose);
            Assert.Null(normal.CaptureError);
        });

    [Fact]
    public void PointerValidShiftScopeAndRealCaptureFailureKeepTheirDispatchPaths()
        => RunSta(() =>
        {
            using var app = new HwndSource(new HwndSourceParameters("Pointer eligible app")
            {
                WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000000,
                PositionX = -30000, PositionY = -28000, Width = 200, Height = 100
            });
            SetAppId(app.Handle, "MissionView.Tests!EligiblePointer");
            IntPtr hit = app.Handle;
            IntPtr foreground = GetForegroundWindow();
            var activation = new WindowActivation();
            var hotkeys = new OverviewHotkeys((_, _) => true, _ => true);
            Assert.True(hotkeys.TryInitialize("Ctrl+Alt+K", out var error), error);
            var input = new OverviewShortcutInput(new(), () => activation.Capture(foreground),
                () => activation.CapturePointerWindow(hit));
            input.Process(0xA2, true, false, hotkeys);
            input.Process(0xA4, true, false, hotkeys);
            input.Process(0xA0, true, false, hotkeys);
            var queued = input.Process('K', true, false, hotkeys);
            Assert.True(queued.Activate);
            Assert.True(queued.AppExpose);
            Assert.Null(queued.CaptureError);
            var request = AppExposeRequest.Capture(() => queued.ApplicationWindow, _ => false,
                ApplicationIdentityReader.Read, queued.Foreground);
            Assert.Equal("MissionView.Tests!EligiblePointer", request.Application.AppUserModelId);
            Assert.Equal(activation.Capture(foreground), request.Foreground);
            Assert.True(request.Includes(Target(app.Handle)));
            Assert.False(request.Includes(Target(app.Handle) with { ProcessId = uint.MaxValue }));
            Assert.Equal(foreground, GetForegroundWindow());
            app.Dispose();
            input.Process('K', false, false, hotkeys);
            var failed = input.Process('K', true, false, hotkeys);
            Assert.True(failed.Suppress);
            Assert.True(failed.Activate); // Dispatch retains the existing ReportShortcutError path.
            Assert.True(failed.AppExpose);
            Assert.NotNull(failed.CaptureError);
            Assert.Equal(default, failed.ApplicationWindow);
            Assert.Equal(foreground, GetForegroundWindow());
        });

    [Fact]
    public void PointedChildResolvesItsOwnedDialogWithoutForegroundingAndKeepsDialogIdentity()
        => RunSta(() =>
        {
            using var owner = new HwndSource(new HwndSourceParameters("Pointer owner fixture")
            {
                WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000000,
                PositionX = -30000, PositionY = -28000, Width = 800, Height = 600
            });
            using var dialog = new HwndSource(new HwndSourceParameters("Pointer dialog fixture")
            {
                ParentWindow = owner.Handle, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000000,
                PositionX = -30000, PositionY = -28000, Width = 400, Height = 300
            });
            using var child = new HwndSource(new HwndSourceParameters("Pointer child fixture")
            {
                ParentWindow = dialog.Handle, WindowStyle = 0x50000000, Width = 200, Height = 100
            });
            SetAppId(owner.Handle, "MissionView.Tests!PointerOwner");
            var activation = new WindowActivation();
            // Other parallel fixtures can overlap these off-screen coordinates.
            var captured = activation.CapturePointerWindow(child.Handle);
            Assert.Equal(dialog.Handle, captured.Handle);
            Assert.Equal(Target(dialog.Handle), captured);
            Assert.NotEqual(owner.Handle, captured.Handle);
            Assert.Throws<InvalidOperationException>(() => ApplicationIdentityReader.IsEligiblePointedWindow(Target(child.Handle)));
            Assert.True(ApplicationIdentityReader.IsEligiblePointedWindow(captured));
            Assert.Equal("MissionView.Tests!PointerOwner", ApplicationIdentityReader.Read(captured).AppUserModelId);
            SetAppId(dialog.Handle, "MissionView.Tests!PointerDialog");
            Assert.Equal("MissionView.Tests!PointerDialog", ApplicationIdentityReader.Read(captured).AppUserModelId);
            Assert.NotEqual(owner.Handle, GetForegroundWindow());
            Assert.NotEqual(dialog.Handle, GetForegroundWindow());
            Assert.NotEqual(child.Handle, GetForegroundWindow());
        });

    [Fact]
    public void PointedWindowValidationRejectsMissingReusedAndToolWindowIdentities()
        => RunSta(() =>
        {
            var activation = new WindowActivation();
            Assert.Equal(default, activation.CapturePointerWindow(IntPtr.Zero));
            using var tool = new HwndSource(new HwndSourceParameters("Pointer tooltip fixture")
            {
                WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080,
                PositionX = -32000, PositionY = -32000, Width = 200, Height = 100
            });
            var captured = activation.CapturePointerWindow(tool.Handle);
            Assert.Equal(default, captured);
            Assert.False(ApplicationIdentityReader.IsEligiblePointedWindow(Target(tool.Handle)));
            var reused = Target(tool.Handle) with { ProcessId = uint.MaxValue };
            Assert.Throws<InvalidOperationException>(() => ApplicationIdentityReader.Read(reused));
            using var hidden = new HwndSource(new HwndSourceParameters("Hidden pointer fixture")
                { WindowStyle = unchecked((int)0x80000000) });
            Assert.Throws<InvalidOperationException>(() => ApplicationIdentityReader.IsEligiblePointedWindow(Target(hidden.Handle)));
            IntPtr destroyed = tool.Handle;
            tool.Dispose();
            Assert.Throws<InvalidOperationException>(() => activation.CapturePointerWindow(destroyed));
        });

    [Theory]
    [InlineData(320, 480, 1)]
    [InlineData(1920, 1080, 1)]
    [InlineData(1200, 1600, 2)]
    public void MinimizedStripScrollsOverflowAndRetainsSelectableRealTitlesWithoutFakeThumbnails(double width, double height, double dpi)
        => RunSta(() =>
        {
            var windows = Enumerable.Range(1, 20).Select(n => Info(n, $"Real window {n}")).ToArray();
            var clicks = new List<IntPtr>();
            var strip = new MinimizedWindowStrip(windows, width, height, dpi, clicks.Add, _ => { }, _ => null);
            var canvas = new Canvas { Width = width, Height = height };
            canvas.Children.Add(strip);
            canvas.Measure(new Size(width, height));
            canvas.Arrange(new Rect(0, 0, width, height));
            canvas.UpdateLayout();
            Assert.Equal(20, strip.Thumbs.Count);
            Assert.InRange(Canvas.GetTop(strip), height * .6, height);
            foreach (var thumb in strip.Thumbs)
            {
                Assert.True(thumb.WasMinimized);
                Assert.Equal(windows.Single(w => w.Handle == thumb.WindowHandle).WasMaximized, thumb.WasMaximized);
                Assert.Equal(IntPtr.Zero, thumb.ThumbnailHandle);
                Assert.Null(UIElementAutomationPeer.CreatePeerForElement(thumb.ClickBorder));
                Assert.Null(thumb.ClickBorder.ContextMenu);
                Assert.False(thumb.ClickBorder.Focusable);
                var button = Assert.IsAssignableFrom<Button>(thumb.ClickBorder.Child);
                Assert.Contains("moving", Assert.IsType<ToolTip>(button.ToolTip).Content.ToString());
                var peer = UIElementAutomationPeer.CreatePeerForElement(button);
                Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
                Assert.Equal($"{thumb.TitleLabel.Text}, minimized", peer.GetName());
            }
            var first = strip.Thumbs[0];
            var firstButton = (Button)first.ClickBorder.Child;
            var invoke = Assert.IsAssignableFrom<IInvokeProvider>(UIElementAutomationPeer.CreatePeerForElement(firstButton).GetPattern(PatternInterface.Invoke));
            invoke.Invoke();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(first.WindowHandle, Assert.Single(clicks));
            var last = strip.Thumbs[^1];
            strip.SetSelected(last.WindowHandle, reveal: true);
            canvas.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            canvas.UpdateLayout();
            Assert.True(Assert.IsType<MinimizedWindowButton>(last.ClickBorder.Child).IsSelected);
            var scroll = Assert.IsType<ScrollViewer>(((DockPanel)strip.Child).Children[1]);
            if (width <= 400) Assert.True(scroll.VerticalOffset > 0);
            Assert.InRange(last.TargetY, Canvas.GetTop(strip) * dpi, height * dpi);
        });

    [Fact]
    public void MinimizedBandLeavesNormalLayoutsUnchangedWhenEmptyAndSeparateWhenPresent()
        => RunSta(() =>
        {
            foreach (var (width, height, dpi) in new[] { (1920d, 1080d, 1d), (1200d, 1600d, 2d) })
            {
                var normal = Enumerable.Range(1, 10).Select(n =>
                {
                    var item = Info(n, "Normal");
                    item.WasMinimized = false;
                    item.OriginalRect = new PhysicalWindowRect { Left = n * 10, Top = n * 10, Right = n * 10 + 800, Bottom = n * 10 + 600 };
                    return item;
                }).ToList();
                var calculator = new WindowLayoutCalculator();
                var original = calculator.CalculateLayouts(normal, width, height, dpi);
                var unchanged = calculator.CalculateLayouts(normal, width,
                    height - MinimizedShelfGeometry.Calculate(width / dpi, height / dpi, 0, SystemParameters.VerticalScrollBarWidth).ReservedHeight * dpi, dpi);
                Assert.Equal(original.Select(l => (l.X, l.Y, l.Width, l.Height)), unchanged.Select(l => (l.X, l.Y, l.Width, l.Height)));
                double usable = height - MinimizedShelfGeometry.Calculate(width / dpi, height / dpi, 20, SystemParameters.VerticalScrollBarWidth).ReservedHeight * dpi;
                var app = calculator.CalculateLayouts(normal, width, usable, dpi);
                Assert.All(app, l => Assert.True(l.Y + l.Height + 36 * dpi < usable));
            }
        });

    [Fact]
    public void NativeRegistrationConflictRollsBackPairOnDedicatedMessageOnlyHandles()
        => RunSta(() =>
        {
            using var source = new HwndSource(new HwndSourceParameters("AppExpose native hotkey fixture")
                { ParentWindow = new IntPtr(-3) });
            using var blocker = new HwndSource(new HwndSourceParameters("AppExpose native conflict fixture")
                { ParentWindow = new IntPtr(-3) });
            // Fixtures reserve F23/F24 briefly, never send keys or use the user's shortcuts.
            const int blockerId = 0x4D20;
            bool blocked = false;
            try
            {
                var old = new OverviewHotkey(3, 0x86); // F23
                var next = new OverviewHotkey(3, 0x87); // F24
                var registered = new Dictionary<int, OverviewHotkey>();
                var fixture = new OverviewHotkeys((id, key) =>
                {
                    var actual = key.Key == 0x7B ? old with { Modifiers = key.Modifiers } : next with { Modifiers = key.Modifiers };
                    if (!RegisterHotKey(source.Handle, id, actual.NativeModifiers | 0x4000, (uint)actual.Key)) return false;
                    registered[id] = actual;
                    return true;
                }, id =>
                {
                    bool result = UnregisterHotKey(source.Handle, id);
                    if (result) registered.Remove(id);
                    return result;
                });
                try
                {
                    Assert.True(fixture.TryInitialize("Ctrl+Alt+F12", out var error), error);
                    Assert.Empty(error);
                    Assert.True(fixture.TryApply("Ctrl+Alt+Shift+F12", true, out _));
                    Assert.True(fixture.TryApply("Ctrl+Alt+F12", false, out _));
                    blocked = RegisterHotKey(blocker.Handle, blockerId, next.WithShift.NativeModifiers, (uint)next.Key);
                    Assert.True(blocked);
                    Assert.False(fixture.TryApply("Ctrl+Alt+F1", false, out var failure));
                    Assert.Contains("previous shortcuts are unchanged", failure);
                    Assert.True(fixture.MatchesNormal(0x7B, 3));
                    Assert.Equal(old, registered[OverviewHotkeys.NormalId]);
                    Assert.Equal(old.WithShift, registered[OverviewHotkeys.AppExposeId]);
                }
                finally { Assert.True(fixture.Release()); }
            }
            finally
            {
                if (blocked) Assert.True(UnregisterHotKey(blocker.Handle, blockerId));
            }
        });

    [Fact]
    public void NativePerWindowAumidSeparatesWindowsInOneProcessWithoutReadingCommandsOrActivatingThem()
        => RunSta(() =>
        {
            using var first = new HwndSource(new HwndSourceParameters("AppExpose first identity fixture")
                { ParentWindow = new IntPtr(-3) });
            using var second = new HwndSource(new HwndSourceParameters("AppExpose second identity fixture")
                { ParentWindow = new IntPtr(-3) });
            SetAppId(first.Handle, "MissionView.Tests!First");
            SetAppId(second.Handle, "MissionView.Tests!Second");
            var one = ApplicationIdentityReader.Read(Target(first.Handle));
            var two = ApplicationIdentityReader.Read(Target(second.Handle));
            Assert.Equal("MissionView.Tests!First", one.AppUserModelId);
            Assert.Equal("MissionView.Tests!Second", two.AppUserModelId);
            Assert.False(one.Matches(two));
            Assert.Equal(one.ExecutablePath, two.ExecutablePath);
            SetAppId(second.Handle, "MissionView.Tests!First");
            Assert.True(one.Matches(ApplicationIdentityReader.Read(Target(second.Handle))));
        });

    [Fact]
    public void OwnedDialogInheritsSameProcessOwnerIdentityButExplicitDialogIdentityWins()
        => RunSta(() =>
        {
            using var owner = new HwndSource(new HwndSourceParameters("AppExpose owned identity fixture")
                { WindowStyle = unchecked((int)0x80000000) });
            using var dialog = new HwndSource(new HwndSourceParameters("AppExpose dialog identity fixture")
                { ParentWindow = owner.Handle, WindowStyle = unchecked((int)0x80000000) });
            Assert.Equal(owner.Handle, GetAncestor(dialog.Handle, GA_ROOTOWNER));
            SetAppId(owner.Handle, "MissionView.Tests!Owner");
            Assert.Equal("MissionView.Tests!Owner", ApplicationIdentityReader.Read(Target(dialog.Handle)).AppUserModelId);
            SetAppId(dialog.Handle, "MissionView.Tests!Dialog");
            Assert.Equal("MissionView.Tests!Dialog", ApplicationIdentityReader.Read(Target(dialog.Handle)).AppUserModelId);
        });

    [Fact]
    public void HiddenNativeMinimizedFixtureKeepsPlacementMinimizationAndForegroundThroughSelectionAndCancel()
        => RunSta(() =>
        {
            using var source = new HwndSource(new HwndSourceParameters("AppExpose minimized state fixture")
            {
                WindowStyle = unchecked((int)0xA0000000), ExtendedWindowStyle = 0x08000080,
                PositionX = -32000, PositionY = -32000, Width = 800, Height = 600
            });
            Assert.True(IsIconic(source.Handle));
            var before = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            Assert.True(GetWindowPlacement(source.Handle, ref before));
            byte[] beforeBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref before, 1)).ToArray();
            IntPtr foreground = GetForegroundWindow();
            var state = new ScatterInteraction();
            state.BeginSession(Target(source.Handle));
            var info = Info(1, "Real minimized fixture");
            info.Handle = source.Handle;
            info.Identity = Target(source.Handle);
            MinimizedWindowStrip? strip = null;
            int confirmations = 0;
            var windows = new[] { info }.Concat(Enumerable.Range(2, 19).Select(i => Info(i, $"Fixture {i}"))).ToArray();
            strip = new MinimizedWindowStrip(windows, 800, 600, 1, _ => confirmations++,
                handle => strip!.SetSelected(handle), _ => null);
            strip.Measure(new Size(strip.Width, strip.Height));
            strip.Arrange(new Rect(0, 0, strip.Width, strip.Height));
            strip.SetSelected(source.Handle);
            var button = Assert.IsType<MinimizedWindowButton>(strip.Thumbs[0].ClickBorder.Child);
            var peer = UIElementAutomationPeer.CreatePeerForElement(button);
            ((System.Windows.Automation.Provider.ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)).Select();
            strip.Thumbs[0].ClickBorder.RaiseEvent(new System.Windows.Input.MouseEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
            var scroll = (ScrollViewer)((DockPanel)strip.Child).Children[1];
            scroll.ScrollToEnd();
            scroll.UpdateLayout();
            Assert.True(scroll.VerticalOffset > 0);
            foreach (var routed in new[] { System.Windows.Input.Keyboard.KeyDownEvent, System.Windows.Input.Keyboard.KeyUpEvent })
                button.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                    source, 0, System.Windows.Input.Key.Space) { RoutedEvent = routed });
            Assert.Equal(0, confirmations);
            state.RequestDismiss();
            Assert.True(state.TryBeginClose(null, out _));
            var after = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            Assert.True(GetWindowPlacement(source.Handle, ref after));
            Assert.Equal(beforeBytes, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref after, 1)).ToArray());
            Assert.True(IsIconic(source.Handle));
            Assert.Equal(foreground, GetForegroundWindow());
        });

    private static WindowInfo Info(int id, string title) =>
        new() { Handle = new(id), Identity = new(new(id), 1, 2), WasMinimized = true, WasMaximized = id % 2 == 0, Title = title };

    private static WindowActivationTarget Target(IntPtr window)
    {
        uint thread = GetWindowThreadProcessId(window, out uint process);
        return new(window, thread, process);
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void SetAppId(IntPtr window, string appId)
    {
        Guid iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, ref iid, out var store));
        var value = new PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(appId) };
        try
        {
            var key = new PropertyKey { Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
            Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value));
            Marshal.ThrowExceptionForHR(store.Commit());
        }
        finally { Marshal.FreeCoTaskMem(value.Pointer); Marshal.ReleaseComObject(store); }
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
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();
}
