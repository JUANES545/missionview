using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using WindowScatter;
using Xunit;
using static WindowScatter.Win32Interop;

public class ThumbnailStackingTests
{
    [Fact]
    public void RegistrationFollowsNativeBackToFrontNotLayoutSizeOrNavigationOrder()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var registered = new List<IntPtr>();
                var thumbs = new List<WindowThumb>();
                var manager = new ThumbnailManager(new Canvas(), thumbs, IntPtr.Zero, _ => { }, _ => { },
                    handle => { registered.Add(handle); return (-1, IntPtr.Zero); });
                manager.SourceOrder = [new(2), new(3), new(1)];
                var layouts = new List<WindowLayout>
                {
                    new() { Window = new() { Handle = new(1), OriginalRect = new PhysicalWindowRect { Right = 900, Bottom = 600 } } },
                    new() { Window = new() { Handle = new(2), OriginalRect = new PhysicalWindowRect { Right = 800, Bottom = 600 } } },
                    new() { Window = new() { Handle = new(3), OriginalRect = new PhysicalWindowRect { Right = 700, Bottom = 600 } } }
                };
                manager.RegisterThumbnails(layouts, new WindowAnimationManager(thumbs));
                Assert.Equal(new IntPtr[] { new(1), new(3), new(2) }, registered);
                Assert.Equal(new IntPtr[] { new(1), new(2), new(3) }, layouts.Select(l => l.Window.Handle));
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
