using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using WindowScatter;
using Xunit;
using static WindowScatter.Win32Interop;

public class OverviewChromeLifecycleTests
{
    [Fact]
    public void ChromeCanBeConstructedBeforeHostHandleAndOwnedBeforeEitherIsShown()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            Window? host = null;
            OverviewChromeWindow? chrome = null;
            try
            {
                host = new Window();
                Assert.Equal(IntPtr.Zero, new WindowInteropHelper(host).Handle);
                chrome = new OverviewChromeWindow(host, new Canvas());
                Assert.False(host.IsVisible);
                Assert.False(chrome.IsVisible);

                Assert.NotEqual(IntPtr.Zero, new WindowInteropHelper(host).EnsureHandle());
                Assert.NotEqual(IntPtr.Zero, new WindowInteropHelper(chrome).EnsureHandle());
                Assert.Equal("MissionView controls", new WindowAutomationPeer(chrome).GetName());
                Assert.Same(host, chrome.Owner);
                Assert.False(chrome.ShowInTaskbar);
                Assert.NotEqual(0, GetWindowLong(new WindowInteropHelper(chrome).Handle, GWL_EXSTYLE) & WS_EX_TOOLWINDOW);
                Assert.False(host.IsVisible);
                Assert.False(chrome.IsVisible);

                bool chromeClosed = false;
                chrome.Closed += (_, _) => chromeClosed = true;
                host.Close();
                Assert.True(chromeClosed);
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
