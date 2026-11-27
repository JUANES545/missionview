using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WindowScatter;
using Xunit;

public class SelectionTransitionTests
{
    [Fact]
    public void SelectionOutlineIsHiddenBeforeTransitionWithoutChangingHandlesAndCanBeRestored()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var selected = new IntPtr(10);
                var other = new IntPtr(20);
                var selectedOutline = new Border { Child = new Border() };
                var otherOutline = new Border { Child = new Border() };
                var thumbs = new List<WindowThumb>
                {
                    new() { WindowHandle = selected, HighlightBorder = selectedOutline,
                        ClickBorder = new Border(), TitleLabel = new TextBlock() },
                    new() { WindowHandle = other, HighlightBorder = otherOutline,
                        ClickBorder = new Border(), TitleLabel = new TextBlock() }
                };
                var manager = new ThumbnailManager(new Canvas(), thumbs, IntPtr.Zero, _ => { }, _ => { });
                foreach (var thumb in thumbs)
                    OverviewCloseInput.Attach(thumb, "Document", () => true,
                        () => throw new InvalidOperationException("A transitioning close control must not act."));
                manager.SetSelectedWindow(selected);
                Assert.Equal(1, thumbs[0].CloseButton!.Opacity);
                Assert.Equal(Visibility.Visible, thumbs[0].CloseButton!.Visibility);
                manager.SetPreviewAnimating(true);
                Assert.All(thumbs, t =>
                {
                    Assert.False(t.ClickBorder.IsHitTestVisible);
                    Assert.Equal(0, t.TitleLabel.Opacity);
                });
                Assert.Equal(0, selectedOutline.Opacity);
                Assert.Equal(Visibility.Hidden, selectedOutline.Visibility);
                bool animationStarted = false;
                manager.BeginTransition(() =>
                {
                    Assert.All(thumbs, thumb => Assert.Equal(0, thumb.HighlightBorder.Opacity));
                    Assert.All(thumbs, thumb =>
                    {
                        Assert.Equal(Visibility.Hidden, thumb.CloseButton!.Visibility);
                        Assert.False(thumb.CloseButton.IsEnabled);
                    });
                    Assert.Equal(selected, thumbs[0].WindowHandle);
                    Assert.Equal(other, thumbs[1].WindowHandle);
                    animationStarted = true;
                });
                Assert.True(animationStarted);
                Assert.All(thumbs, thumb => Assert.Equal(0, thumb.HighlightBorder.Opacity));
                manager.SetSelectedWindow(selected);
                Assert.Equal(Visibility.Hidden, thumbs[0].CloseButton!.Visibility);
                manager.SetPreviewAnimating(false);
                Assert.All(thumbs, t =>
                {
                    Assert.True(t.ClickBorder.IsHitTestVisible);
                    Assert.Equal(1, t.TitleLabel.Opacity);
                    Assert.Equal(Visibility.Visible, t.CloseButton!.Visibility);
                    Assert.True(t.CloseButton.IsEnabled);
                });
                Assert.Equal(1, selectedOutline.Opacity);
                Assert.Equal(0, otherOutline.Opacity);
                var close = thumbs[0].CloseButton!;
                manager.CleanupAllThumbnails();
                Assert.Equal(Visibility.Hidden, close.Visibility);
                Assert.False(close.IsEnabled);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
