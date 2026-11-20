using System.Runtime.ExceptionServices;
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
                manager.SetSelectedWindow(selected);
                manager.SetPreviewAnimating(true);
                Assert.All(thumbs, t =>
                {
                    Assert.False(t.ClickBorder.IsHitTestVisible);
                    Assert.Equal(0, t.TitleLabel.Opacity);
                });
                Assert.Equal(1, selectedOutline.Opacity);
                bool animationStarted = false;
                manager.BeginTransition(() =>
                {
                    Assert.All(thumbs, thumb => Assert.Equal(0, thumb.HighlightBorder.Opacity));
                    Assert.Equal(selected, thumbs[0].WindowHandle);
                    Assert.Equal(other, thumbs[1].WindowHandle);
                    animationStarted = true;
                });
                Assert.True(animationStarted);
                Assert.All(thumbs, thumb => Assert.Equal(0, thumb.HighlightBorder.Opacity));
                manager.SetSelectedWindow(selected);
                manager.SetPreviewAnimating(false);
                Assert.All(thumbs, t =>
                {
                    Assert.True(t.ClickBorder.IsHitTestVisible);
                    Assert.Equal(1, t.TitleLabel.Opacity);
                });
                Assert.Equal(1, selectedOutline.Opacity);
                Assert.Equal(0, otherOutline.Opacity);
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
