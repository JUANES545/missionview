using System.Runtime.InteropServices;
using static WindowScatter.Win32Interop;

namespace WindowScatter;

internal interface IDwmDragApi
{
    void Validate(WindowActivationTarget source);
    IntPtr Register(IntPtr destination, IntPtr source);
    (int Width, int Height) SourceSize(IntPtr thumbnail);
    void Update(IntPtr thumbnail, DWM_THUMBNAIL_PROPERTIES properties);
    int Unregister(IntPtr thumbnail);
}

// Owns one registration on one existing overview surface; never owns or changes the source HWND.
internal sealed class DwmDragPreview : IDisposable
{
    private readonly IDwmDragApi api;
    private readonly WindowActivationTarget source;
    private IntPtr thumbnail;
    private (int Width, int Height) sourceSize;

    internal DwmDragPreview(IntPtr destination, WindowActivationTarget source, IDwmDragApi? api = null)
    {
        this.api = api ?? new DwmDragApi();
        this.source = source;
        try
        {
            this.api.Validate(source);
            thumbnail = this.api.Register(destination, source.Handle);
            if (thumbnail == IntPtr.Zero) throw new InvalidOperationException("DWM returned an empty drag thumbnail.");
            sourceSize = this.api.SourceSize(thumbnail);
            if (sourceSize.Width <= 0 || sourceSize.Height <= 0)
                throw new InvalidOperationException("DWM returned an empty drag source.");
            this.api.Validate(source);
            this.api.Update(thumbnail, new() { dwFlags = DWM_TNP_VISIBLE, fVisible = false });
        }
        catch { Dispose(); throw; }
    }

    internal void ValidateSource()
    {
        ObjectDisposedException.ThrowIf(thumbnail == IntPtr.Zero, this);
        api.Validate(source);
        if (api.SourceSize(thumbnail) != sourceSize)
            throw new InvalidOperationException("The source window changed size during the drag. Try again.");
    }

    internal void Update(PhysicalWindowRect image, PhysicalWindowRect monitor)
    {
        ValidateSource();
        var fragment = DragPreviewGeometry.Clip(image, monitor, sourceSize.Width, sourceSize.Height);
        var properties = new DWM_THUMBNAIL_PROPERTIES { dwFlags = DWM_TNP_VISIBLE, fVisible = fragment.HasValue };
        if (fragment is { } part)
        {
            properties.dwFlags |= DWM_TNP_RECTDESTINATION | DWM_TNP_RECTSOURCE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY;
            properties.rcDestination = Native(part.Destination);
            properties.rcSource = Native(part.Source);
            properties.opacity = 230;
            // rcSource uses the complete window size reported by DWM, not client or extended-frame offsets.
            properties.fSourceClientAreaOnly = false;
        }
        api.Update(thumbnail, properties);
    }

    private static RECT Native(PhysicalWindowRect rect) =>
        new() { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom };

    public void Dispose()
    {
        if (thumbnail == IntPtr.Zero) return;
        IntPtr owned = thumbnail;
        thumbnail = IntPtr.Zero;
        int result = api.Unregister(owned);
        if (result < 0) System.Diagnostics.Trace.TraceError($"DWM drag cleanup failed: 0x{result:X8}.");
    }
}

internal sealed class DwmDragApi : IDwmDragApi
{
    public void Validate(WindowActivationTarget source)
    {
        uint thread = GetWindowThreadProcessId(source.Handle, out uint process);
        if (source.Handle == IntPtr.Zero || source.ThreadId == 0 || source.ProcessId == 0 ||
            thread != source.ThreadId || process != source.ProcessId || !IsWindow(source.Handle) || IsIconic(source.Handle))
            throw new InvalidOperationException("The dragged window is no longer available.");
    }

    public IntPtr Register(IntPtr destination, IntPtr source)
    {
        Check(DwmRegisterThumbnail(destination, source, out var thumbnail), "create");
        return thumbnail;
    }

    public (int Width, int Height) SourceSize(IntPtr thumbnail)
    {
        Check(DwmQueryThumbnailSourceSize(thumbnail, out var size), "read the source size of");
        return (size.cx, size.cy);
    }

    public void Update(IntPtr thumbnail, DWM_THUMBNAIL_PROPERTIES properties) =>
        Check(DwmUpdateThumbnailProperties(thumbnail, ref properties), "update");

    public int Unregister(IntPtr thumbnail) => DwmUnregisterThumbnail(thumbnail);

    private static void Check(int result, string operation)
    {
        if (result < 0) throw new COMException($"DWM could not {operation} the drag preview.", result);
    }
}
