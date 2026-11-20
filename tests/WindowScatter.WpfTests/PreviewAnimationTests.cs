using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using WindowScatter;
using Xunit;

public class PreviewAnimationTests
{
    [Fact]
    public void ReleaseAnimationRestoresOriginalSlotWithoutMutatingLayoutAndReportsMissingDwmSource()
        => RunSta(() =>
        {
            var thumb = Thumb();
            var manager = new WindowAnimationManager([thumb]);
            var frame = new DispatcherFrame();
            bool? success = null;
            manager.StartPreviewAnimation(IntPtr.Zero, default, result =>
            {
                success = result;
                frame.Continue = false;
            });
            Pump(frame, manager);
            Assert.False(success);
            Assert.Equal((100d, 200d, 300d, 150d),
                (thumb.CurrentX, thumb.CurrentY, thumb.CurrentWidth, thumb.CurrentHeight));
            Assert.Equal((100d, 200d, 300d, 150d),
                (thumb.TargetX, thumb.TargetY, thumb.TargetWidth, thumb.TargetHeight));
            Assert.Equal((40d, 50d, 1200d, 600d),
                (thumb.StartX, thumb.StartY, thumb.StartWidth, thumb.StartHeight));
        });

    [Fact]
    public void ReleaseDuringExpansionReplacesTrajectoryAndDoesNotRunStaleCompletion()
        => RunSta(() =>
        {
            var thumb = Thumb();
            var manager = new WindowAnimationManager([thumb]);
            bool staleCompletion = false;
            manager.StartPreviewAnimation(thumb.WindowHandle, new(0, 0, 1600, 800), _ => staleCompletion = true);
            var frame = new DispatcherFrame();
            bool released = false;
            manager.StartPreviewAnimation(IntPtr.Zero, default, _ =>
            {
                released = true;
                frame.Continue = false;
            });
            Pump(frame, manager);
            Assert.True(released);
            Assert.False(staleCompletion);
            Assert.Equal(300, thumb.CurrentWidth);
            Assert.Equal(150, thumb.CurrentHeight);
        });

    [Fact]
    public void NavigationWhileHeldReturnsOldPreviewAndExpandsNewSelectionWithoutChangingSlots()
        => RunSta(() =>
        {
            var first = Thumb();
            var second = Thumb();
            second.WindowHandle = new IntPtr(20);
            second.TargetX = 600;
            var manager = new WindowAnimationManager([first, second]);
            bool staleCompletion = false;
            manager.StartPreviewAnimation(first.WindowHandle, new(0, 0, 1600, 800), _ => staleCompletion = true);
            Assert.True(manager.IsPreviewAnimating);
            Assert.False(manager.IsAnimating);
            var frame = new DispatcherFrame();
            manager.StartPreviewAnimation(second.WindowHandle, new(10, 30, 1400, 700), _ => frame.Continue = false);
            Pump(frame, manager);
            Assert.False(staleCompletion);
            Assert.Equal((100d, 200d, 300d, 150d),
                (first.CurrentX, first.CurrentY, first.CurrentWidth, first.CurrentHeight));
            Assert.Equal((10d, 30d, 1400d, 700d),
                (second.CurrentX, second.CurrentY, second.CurrentWidth, second.CurrentHeight));
            Assert.Equal((600d, 200d, 300d, 150d),
                (second.TargetX, second.TargetY, second.TargetWidth, second.TargetHeight));
        });

    [Fact]
    public void ClosingCancelsPreviewAndItsPendingCompletion()
        => RunSta(() =>
        {
            var manager = new WindowAnimationManager([Thumb()]);
            bool completed = false;
            manager.StartPreviewAnimation(new IntPtr(10), new(0, 0, 1600, 800), _ => completed = true);
            manager.StopAllAnimations();
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            Pump(frame, manager);
            Assert.False(completed);
            Assert.False(manager.IsPreviewAnimating);
            Assert.False(manager.IsAnimating);
        });

    [Fact]
    public void NativeDesktopHandoffCancelsPreviewHidesIndicatorsAndRunsBeforeAnyDispatcherFrame()
        => RunSta(() =>
        {
            var thumb = Thumb();
            thumb.HighlightBorder = new System.Windows.Controls.Border();
            thumb.TitleLabel = new System.Windows.Controls.TextBlock();
            thumb.ClickBorder = new System.Windows.Controls.Border();
            var controls = new System.Windows.Controls.Canvas();
            var thumbnails = new ThumbnailManager(controls, [thumb], IntPtr.Zero, _ => { }, _ => { });
            thumbnails.SetSelectedWindow(thumb.WindowHandle);
            var manager = new WindowAnimationManager([thumb]);
            bool staleCompletion = false;
            manager.StartPreviewAnimation(thumb.WindowHandle, new(0, 0, 1600, 800), _ => staleCompletion = true);
            var stopped = default(PreviewBounds);
            var desktop = new DesktopToggle();
            bool toggled = false, cleanupQueued = false;
            Assert.True(desktop.CompleteOverviewExit(() =>
            {
                manager.StopAllAnimations();
                stopped = new(thumb.CurrentX, thumb.CurrentY, thumb.CurrentWidth, thumb.CurrentHeight);
                thumbnails.BeginTransition(() => thumbnails.SetPreviewAnimating(true));
            }, () => controls.Visibility = System.Windows.Visibility.Hidden, () =>
            {
                Assert.False(manager.IsAnimating);
                Assert.False(manager.IsPreviewAnimating);
                Assert.Equal(stopped, new PreviewBounds(thumb.CurrentX, thumb.CurrentY, thumb.CurrentWidth, thumb.CurrentHeight));
                Assert.Equal(0, thumb.HighlightBorder.Opacity);
                Assert.Equal(0, thumb.TitleLabel.Opacity);
                Assert.False(thumb.ClickBorder.IsHitTestVisible);
                Assert.Equal(System.Windows.Visibility.Hidden, controls.Visibility);
                Assert.False(cleanupQueued);
                toggled = true;
            }, () => cleanupQueued = true));
            Assert.True(toggled);
            Assert.True(cleanupQueued);
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            Pump(frame, manager);
            Assert.False(staleCompletion);
            Assert.Equal(stopped, new PreviewBounds(thumb.CurrentX, thumb.CurrentY, thumb.CurrentWidth, thumb.CurrentHeight));
            Assert.Equal((100d, 200d, 300d, 150d), (thumb.TargetX, thumb.TargetY, thumb.TargetWidth, thumb.TargetHeight));
            Assert.Equal((40d, 50d, 1200d, 600d), (thumb.StartX, thumb.StartY, thumb.StartWidth, thumb.StartHeight));
        });

    [Fact]
    public void LiveReducedMotionStillCompletesPreviewAtRequestedBounds()
        => RunSta(() =>
        {
            var thumb = Thumb();
            var manager = new WindowAnimationManager([thumb]);
            manager.SetMotionEnabled(true);
            var frame = new DispatcherFrame();
            manager.StartPreviewAnimation(thumb.WindowHandle, new(10, 30, 1400, 700), _ => frame.Continue = false);
            manager.SetMotionEnabled(false);
            Pump(frame, manager);
            Assert.Equal(new PreviewBounds(10, 30, 1400, 700),
                new PreviewBounds(thumb.CurrentX, thumb.CurrentY, thumb.CurrentWidth, thumb.CurrentHeight));
        });

    [Fact]
    public void BlockedPresentationCannotTrapCleanupOrCompleteIntoTheNextGeneration()
        => RunSta(() =>
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var exited = new ManualResetEventSlim();
            int flushes = 0;
            var manager = new WindowAnimationManager([], () =>
            {
                if (Interlocked.Increment(ref flushes) == 1)
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(5));
                    exited.Set();
                }
                return 0;
            });
            manager.SetMotionEnabled(false);
            bool stale = false, current = false;
            try
            {
                manager.StartPreviewAnimation(IntPtr.Zero, default, _ => stale = true);
                Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
                manager.StopAllAnimations();
                Assert.False(release.IsSet);
                var frame = new DispatcherFrame();
                manager.StartPreviewAnimation(IntPtr.Zero, default, _ =>
                {
                    current = true;
                    frame.Continue = false;
                });
                release.Set();
                Pump(frame, manager);
                Assert.True(current);
                Assert.False(stale);
            }
            finally
            {
                release.Set();
                manager.StopAllAnimations();
                Assert.True(exited.Wait(TimeSpan.FromSeconds(2)));
            }
        });

    private static WindowThumb Thumb() => new()
    {
        WindowHandle = new IntPtr(10),
        StartX = 40, StartY = 50, StartWidth = 1200, StartHeight = 600,
        TargetX = 100, TargetY = 200, TargetWidth = 300, TargetHeight = 150,
        CurrentX = 0, CurrentY = 0, CurrentWidth = 1000, CurrentHeight = 500
    };

    private static void Pump(DispatcherFrame frame, WindowAnimationManager manager)
    {
        bool timedOut = false;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timeout.Tick += (_, _) => { timedOut = true; frame.Continue = false; };
        timeout.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timeout.Stop(); manager.StopAllAnimations(); }
        Assert.False(timedOut, "Preview completion did not reach the dispatcher.");
    }

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
