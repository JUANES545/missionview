using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowScatter;
using Xunit;

public class OverviewChromeHitTestTests
{
    [Fact]
    public void LayeredChromeHasNonzeroRenderedAlphaBehindTransparentThumbnailHitTargets()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            Window? host = null;
            OverviewChromeWindow? chrome = null;
            try
            {
                var canvas = new Canvas { Width = 256, Height = 128, Background = Brushes.Transparent };
                var target = new Border
                {
                    Width = 64,
                    Height = 64,
                    Background = Brushes.Transparent
                };
                Canvas.SetLeft(target, 32);
                Canvas.SetTop(target, 32);
                canvas.Children.Add(target);
                host = new Window();
                new WindowInteropHelper(host).EnsureHandle();
                chrome = new OverviewChromeWindow(host, canvas) { Width = 256, Height = 128 };
                new WindowInteropHelper(chrome).EnsureHandle();
                chrome.ApplyTemplate();
                chrome.Measure(new Size(256, 128));
                chrome.Arrange(new Rect(0, 0, 256, 128));
                chrome.UpdateLayout();

                var visual = (FrameworkElement)VisualTreeHelper.GetChild(chrome, 0);
                visual.Measure(new Size(256, 128));
                visual.Arrange(new Rect(0, 0, 256, 128));
                visual.UpdateLayout();
                var bitmap = new RenderTargetBitmap(256, 128, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect(48, 48, 1, 1), pixel, 4, 0);
                Assert.InRange(pixel[3], 1, 2);
                bitmap.CopyPixels(new Int32Rect(240, 112, 1, 1), pixel, 4, 0);
                Assert.InRange(pixel[3], 1, 2);
                Assert.False(host.IsVisible);
                Assert.False(chrome.IsVisible);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                chrome?.Close();
                host?.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
