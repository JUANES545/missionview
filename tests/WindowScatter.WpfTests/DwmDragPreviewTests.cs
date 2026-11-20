using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Windows.Interop;
using System.Windows.Threading;
using WindowScatter;
using Xunit;
using static WindowScatter.Win32Interop;

public class DwmDragPreviewTests
{
    private static readonly WindowActivationTarget Identity = new(new(1), 2, 3);

    [Fact]
    public void FullFrameNativeCropAndConstructionFailureReleaseTheRealOwnedRegistration()
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var source = new HwndSource(new HwndSourceParameters("Drag source frame fixture")
                {
                    WindowStyle = 0x10CF0000, ExtendedWindowStyle = 0x08000000,
                    PositionX = -28000, PositionY = -26000, Width = 800, Height = 400
                });
                using var destination = new HwndSource(new HwndSourceParameters("Drag destination fixture")
                {
                    WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000000,
                    PositionX = -30000, PositionY = -28000, Width = 1000, Height = 800
                });
                var identity = new WindowActivation().Capture(source.Handle);
                var api = new NativeRecordingApi();
                Assert.True(GetWindowRect(source.Handle, out var before));
                IntPtr foreground = GetForegroundWindow();
                using (var preview = new DwmDragPreview(destination.Handle, identity, api))
                {
                    preview.Update(Rect(850, 350, 200, 100), Rect(0, 0, 1000, 800));
                    Assert.True(api.Last.fVisible);
                    Assert.False(api.Last.fSourceClientAreaOnly);
                    Assert.Equal(150, api.Last.rcDestination.Right - api.Last.rcDestination.Left);
                    Assert.Equal(0, DwmQueryThumbnailSourceSize(api.Handle, out var size));
                    Assert.Equal((size.cx * 3 + 2) / 4, api.Last.rcSource.Right);
                    Assert.Throws<InvalidOperationException>(() =>
                        api.Native.Validate(identity with { ProcessId = identity.ProcessId + 1 }));
                }
                Assert.Equal(1, api.Unregistered);
                Assert.Equal(0, api.CleanupResult);
                Assert.True(DwmQueryThumbnailSourceSize(api.Handle, out _) < 0);
                var failed = new NativeRecordingApi { FailQuery = true };
                Assert.Throws<COMException>(() => new DwmDragPreview(destination.Handle, identity, failed));
                Assert.Equal(1, failed.Unregistered);
                Assert.Equal(0, failed.CleanupResult);
                Assert.True(DwmQueryThumbnailSourceSize(failed.Handle, out _) < 0);
                Assert.Equal(foreground, GetForegroundWindow());
                Assert.False(IsIconic(source.Handle));
                Assert.True(GetWindowRect(source.Handle, out var after));
                Assert.Equal(before, after);
            }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
    }

    [Fact]
    public void ProductAdapterSendsComplementarySourceAndDestinationBeforePointerCrosses()
    {
        var a = new RecordingApi();
        var b = new RecordingApi();
        using var left = new DwmDragPreview(new(10), Identity, a);
        using var right = new DwmDragPreview(new(11), Identity, b);
        var geometry = new DragPreviewGeometry(200, 100, 1000, 800, .5, .5);
        var image = geometry.At(950, 400);
        left.Update(image, Rect(0, 0, 1000, 800));
        right.Update(image, Rect(1000, 0, 200, 800));
        Assert.True(a.Last.fVisible);
        Assert.True(b.Last.fVisible);
        Assert.Equal((850, 1000), (a.Last.rcDestination.Left, a.Last.rcDestination.Right));
        Assert.Equal((0, 50), (b.Last.rcDestination.Left, b.Last.rcDestination.Right));
        Assert.Equal((0, 600), (a.Last.rcSource.Left, a.Last.rcSource.Right));
        Assert.Equal((600, 800), (b.Last.rcSource.Left, b.Last.rcSource.Right));
        Assert.Equal((0, 400), (a.Last.rcSource.Top, a.Last.rcSource.Bottom));
        Assert.False(a.Last.fSourceClientAreaOnly);
        Assert.False(b.Last.fSourceClientAreaOnly);
        foreach (var properties in new[] { a.Last, b.Last })
        {
            Assert.NotEqual(0, properties.dwFlags & DWM_TNP_RECTSOURCE);
            Assert.NotEqual(0, properties.dwFlags & DWM_TNP_RECTDESTINATION);
        }
        left.Update(geometry.At(999, 400), Rect(0, 0, 1000, 800));
        right.Update(geometry.At(1000, 400), Rect(1000, 0, 200, 800));
        Assert.Equal((404, 800), (a.Last.rcSource.Right, b.Last.rcSource.Right));
        // The source seam moves four source pixels for one desktop pixel, not a rescaled full copy.
        Assert.Equal(400, b.Last.rcSource.Left);
        right.Update(geometry.At(500, 400), Rect(1000, 0, 200, 800));
        Assert.False(b.Last.fVisible);
    }

    [Theory]
    [InlineData("size")]
    [InlineData("initialize")]
    public void FailedConstructionReleasesRegistrationOnce(string failure)
    {
        var api = new RecordingApi { Failure = failure };
        Assert.Throws<COMException>(() => new DwmDragPreview(new(10), Identity, api));
        Assert.Equal(1, api.Unregistered);
        Assert.Equal(0, api.Active);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("resize")]
    [InlineData("update")]
    public void FailureMidGestureIsNotSuccessAndDisposeIsIdempotent(string failure)
    {
        var api = new RecordingApi();
        var preview = new DwmDragPreview(new(10), Identity, api);
        api.Failure = failure;
        Assert.ThrowsAny<Exception>(() => preview.Update(Rect(0, 0, 200, 100), Rect(0, 0, 1000, 800)));
        preview.Dispose();
        preview.Dispose();
        Assert.Equal(1, api.Unregistered);
        Assert.Equal(0, api.Active);
        Assert.Throws<ObjectDisposedException>(() => preview.ValidateSource());
    }

    private static PhysicalWindowRect Rect(int x, int y, int width, int height) =>
        new() { Left = x, Top = y, Right = x + width, Bottom = y + height };

    private sealed class NativeRecordingApi : IDwmDragApi
    {
        internal readonly DwmDragApi Native = new();
        internal IntPtr Handle;
        internal int Unregistered, CleanupResult;
        internal bool FailQuery;
        internal DWM_THUMBNAIL_PROPERTIES Last;
        public void Validate(WindowActivationTarget source) => Native.Validate(source);
        public IntPtr Register(IntPtr destination, IntPtr source) => Handle = Native.Register(destination, source);
        public (int Width, int Height) SourceSize(IntPtr thumbnail) =>
            FailQuery ? throw new COMException("Injected failure after real registration.") : Native.SourceSize(thumbnail);
        public void Update(IntPtr thumbnail, DWM_THUMBNAIL_PROPERTIES properties)
        {
            Native.Update(thumbnail, properties);
            Last = properties;
        }
        public int Unregister(IntPtr thumbnail)
        {
            Unregistered++;
            return CleanupResult = Native.Unregister(thumbnail);
        }
    }

    private sealed class RecordingApi : IDwmDragApi
    {
        internal string? Failure;
        internal int Active, Unregistered;
        internal DWM_THUMBNAIL_PROPERTIES Last;
        public void Validate(WindowActivationTarget source)
        {
            Assert.Equal(Identity, source);
            if (Failure == "identity") throw new InvalidOperationException("Reused identity.");
        }
        public IntPtr Register(IntPtr destination, IntPtr source) { Active++; return new(101); }
        public (int Width, int Height) SourceSize(IntPtr thumbnail)
        {
            if (Failure == "size") throw new COMException("Query failed.");
            return Failure == "resize" ? (801, 400) : (800, 400);
        }
        public void Update(IntPtr thumbnail, DWM_THUMBNAIL_PROPERTIES properties)
        {
            if (Failure is "initialize" or "update") throw new COMException("Update failed.");
            Last = properties;
        }
        public int Unregister(IntPtr thumbnail) { Unregistered++; Active--; return 0; }
    }
}
