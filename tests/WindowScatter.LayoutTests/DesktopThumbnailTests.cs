using WindowScatter;
using Xunit;
using static WindowScatter.Win32Interop;

public class DesktopThumbnailTests
{
    [Theory]
    [InlineData(-3840, -1080, -1920, -1080)]
    [InlineData(1000, 500, 2920, 1580)]
    [InlineData(0, 0, 0, 0)]
    public void SourceCropUsesWindowLocalCoordinates(int sourceX, int sourceY, int monitorX, int monitorY)
    {
        var source = new RECT { Left = sourceX, Top = sourceY, Right = sourceX + 6000, Bottom = sourceY + 3000 };
        var monitor = new RECT { Left = monitorX, Top = monitorY, Right = monitorX + 1920, Bottom = monitorY + 1080 };
        DWM_THUMBNAIL_PROPERTIES actual = default;
        bool result = DesktopThumbnail.TryUpdate(new IntPtr(1), monitor, source, 127,
            (IntPtr handle, ref DWM_THUMBNAIL_PROPERTIES properties) => { actual = properties; return 0; });

        Assert.True(result);
        Assert.Equal((monitorX - sourceX, monitorY - sourceY, monitorX - sourceX + 1920, monitorY - sourceY + 1080),
            (actual.rcSource.Left, actual.rcSource.Top, actual.rcSource.Right, actual.rcSource.Bottom));
        Assert.Equal((0, 0, 1920, 1080),
            (actual.rcDestination.Left, actual.rcDestination.Top, actual.rcDestination.Right, actual.rcDestination.Bottom));
        Assert.Equal(127, actual.opacity);
        Assert.True(actual.fVisible);
        Assert.False(actual.fSourceClientAreaOnly);
    }

    [Fact]
    public void UpdateFailureIsReportedForFallback()
    {
        var bounds = new RECT { Right = 1920, Bottom = 1080 };
        bool result = DesktopThumbnail.TryUpdate(new IntPtr(1), bounds, bounds, 255,
            (IntPtr handle, ref DWM_THUMBNAIL_PROPERTIES properties) => unchecked((int)0x80070057));
        Assert.False(result);
    }

    [Theory]
    [InlineData(-100, 0, 1920, 1080)]
    [InlineData(0, 0, 3840, 1080)]
    [InlineData(0, 0, 0, 1080)]
    public void InvalidOrUncoveredMonitorFallsBackWithoutUpdating(int left, int top, int right, int bottom)
    {
        var source = new RECT { Right = 1920, Bottom = 1080 };
        var monitor = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        bool updated = false;
        bool result = DesktopThumbnail.TryUpdate(new IntPtr(1), monitor, source, 255,
            (IntPtr handle, ref DWM_THUMBNAIL_PROPERTIES properties) => { updated = true; return 0; });
        Assert.False(result);
        Assert.False(updated);
    }
}
