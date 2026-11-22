using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using WindowScatter;
using Xunit;
using static WindowScatter.Win32Interop;

public class MainWindowDragTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void CurrentScopeHasNoInvisibleDragDestinationAndNeverMovesTheNativeSource(int pointer) => RunSta(() =>
    {
        using var app = new MainWindow();
        app.OpenDisplays(
            [new() { Handle = new(1), Left = -1000, Top = -200, Width = 1000, Height = 800, DpiScale = 1.5 },
             new() { Handle = new(2), Width = 1000, Height = 800, DpiScale = 2 }],
            new AppSettings { OverviewMonitorScope = "Current" }, new(pointer));
        var thumb = app.AddNativeThumb(100, 300, 200, 100);
        app.ShowFixture();
        Assert.True(GetWindowRect(thumb.WindowHandle, out var before));
        app.BeginFixtureDrag(thumb, .5, .5);
        int invisibleX = pointer == 1 ? 500 : -500;
        app.DrawFixtureDrag(invisibleX, 400);
        Assert.Equal(1, app.DragSurfaceCount);
        Assert.Throws<InvalidOperationException>(() => app.FinishFixtureDrag(invisibleX, 400));
        Assert.False(app.DragPressed);
        Assert.Equal(0, app.DragSurfaceCount);
        Assert.True(GetWindowRect(thumb.WindowHandle, out var after));
        Assert.Equal(before, after);
        Assert.False(IsIconic(thumb.WindowHandle));
        Assert.Equal(0, app.ActivationRequests);
        Assert.Equal(new IntPtr(pointer), app.CurrentDisplay);
        Assert.Single(app.Snapshot());
    });

    [Theory]
    [InlineData(1, 1.5)]
    [InlineData(1.5, 2)]
    [InlineData(2, 1)]
    public void ActualDragPartialKeepsPhysicalSizeAcrossSimulatedDpiAndReleasesByPointerNotArea(double sourceDpi, double destinationDpi)
        => RunSta(() =>
        {
            using var app = new MainWindow(width: 1000, height: 800, dpi: sourceDpi);
            var other = app.AddView(1000, 0, 200, 800, destinationDpi);
            var thumb = app.AddNativeThumb(100, 300, 200, 100);
            app.ShowFixture();
            other.ShowFixture();
            Assert.True(GetWindowRect(thumb.WindowHandle, out var before));
            IntPtr foreground = GetForegroundWindow();
            app.BeginFixtureDrag(thumb, .8, .5);
            var a = app.DrawFixtureDrag(999, 400);
            var b = app.DrawFixtureDrag(1000, 400);
            Assert.Equal(200, a.Right - a.Left);
            Assert.Equal(200, b.Right - b.Left);
            Assert.Equal(100, b.Bottom - b.Top);
            Assert.Equal(a.Left + 1, b.Left);
            Assert.Equal(840, b.Left); // Most of the image remains on the origin.
            Assert.Equal(2, app.DragSurfaceCount);
            Assert.Same(other, app.FinishFixtureDrag(1000, 400));
            Assert.Equal(0, app.DragSurfaceCount);
            Assert.False(app.DragPressed);
            Assert.Equal(IntPtr.Zero, app.Clicked);
            Assert.True(GetWindowRect(thumb.WindowHandle, out var after));
            Assert.Equal(before, after);
            Assert.False(IsIconic(thumb.WindowHandle));
            Assert.Equal(foreground, GetForegroundWindow());
            Assert.Empty(app.Errors);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualPartialCancelsGapOrLostSourceWithoutConfirmingAndCleansEverySurface(bool destroySource)
        => RunSta(() =>
        {
            using var app = new MainWindow(width: 1000, height: 800);
            var other = app.AddView(1100, 0, 1000, 800, 1.5);
            var thumb = app.AddNativeThumb(100, 300, 200, 100);
            app.ShowFixture();
            other.ShowFixture();
            app.BeginFixtureDrag(thumb, .5, .5);
            app.DrawFixtureDrag(1050, 400);
            Assert.Equal(2, app.DragSurfaceCount);
            if (destroySource) app.DestroyFixtureDragSource();
            Assert.Throws<InvalidOperationException>(() => app.FinishFixtureDrag(destroySource ? 1200 : 1050, 400));
            Assert.Equal(0, app.DragSurfaceCount);
            Assert.False(app.DragPressed);
            Assert.Equal(IntPtr.Zero, app.Clicked);
            Assert.DoesNotContain(app.FixtureHandles, h => h == GetForegroundWindow());
        });

    [Fact]
    public void ActualPartialCancelsOnEscapeAndInvalidDestinationConstructionWithoutLeakingEarlierSurfaces()
        => RunSta(() =>
        {
            using var app = new MainWindow(width: 1000, height: 800);
            var other = app.AddView(-1000, -800, 1000, 800, 2);
            var thumb = app.AddNativeThumb(100, 300, 200, 100);
            app.ShowFixture();
            other.ShowFixture();
            app.BeginFixtureDrag(thumb, .5, .5);
            app.DrawFixtureDrag(-10, -10);
            Assert.Equal(2, app.DragSurfaceCount);
            Assert.True(app.Press(System.Windows.Input.Key.Escape));
            Assert.Equal(0, app.DragSurfaceCount);
            Assert.False(app.DragPressed);
            Assert.Equal(IntPtr.Zero, app.Clicked);
            // Closing only our destination HWND makes registration fail after origin registration.
            other.Close();
            Assert.ThrowsAny<Exception>(() => app.BeginFixtureDrag(thumb, .5, .5));
            Assert.Equal(0, app.DragSurfaceCount);
            Assert.False(app.DragPressed);
        });

    private static void RunSta(Action test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}

namespace WindowScatter
{
    public partial class MainWindow
    {
        internal int DragSurfaceCount => views.Count(v => v.dragPreview != null);
        internal bool DragPressed => drag.IsPressed;
        internal void BeginFixtureDrag(WindowThumb thumb, double grabX, double grabY)
        {
            drag.Press(thumb.Identity, 100, 300, 4, 4);
            dragSource = this;
            dragThumb = thumb;
            dragCapture = new Border();
            dragGrabX = grabX;
            dragGrabY = grabY;
            Assert.True(drag.Move(110, 300));
            try { StartDragPreview(); }
            catch { CancelDrag(); throw; }
        }
        internal PhysicalWindowRect DrawFixtureDrag(int x, int y)
        {
            try { UpdateDragPreview(new() { X = x, Y = y }); }
            catch { CancelDrag(); throw; }
            return dragGeometry!.At(x, y);
        }
        internal MainWindow? FinishFixtureDrag(int x, int y) => FinishDrag(new() { X = x, Y = y }).Destination;
        internal void DestroyFixtureDragSource() => sources.Single(w =>
            new WindowInteropHelper(w).Handle == drag.Target.Handle).Close();
    }
}
