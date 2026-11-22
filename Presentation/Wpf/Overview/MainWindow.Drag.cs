using System.Windows.Interop;
using static WindowScatter.Win32Interop;

namespace WindowScatter;

public partial class MainWindow
{
    private PhysicalWindowRect DragMonitorBounds => new()
    {
        Left = monitor.Left, Top = monitor.Top, Right = monitor.Right, Bottom = monitor.Bottom
    };

    private MainWindow? DragDestination(POINT cursor) =>
        views.FirstOrDefault(v => DragPreviewGeometry.Contains(v.DragMonitorBounds, cursor.X, cursor.Y));

    private (WindowActivationTarget Target, bool Dragged, MainWindow Source, MainWindow? Destination) FinishDrag(POINT cursor)
    {
        try
        {
            var source = dragSource ?? throw new InvalidOperationException("The drag source is no longer available.");
            var destination = DragDestination(cursor);
            if (drag.IsDragging)
            {
                (source.dragPreview ?? throw new InvalidOperationException("The drag preview is no longer available.")).ValidateSource();
                if (destination == null)
                    throw new InvalidOperationException("Release over an available display to move the window.");
            }
            var result = drag.Release();
            return (result.Target, result.Dragged, source, destination);
        }
        finally { CancelDrag(); }
    }

    private void StartDragPreview()
    {
        previewKey.Release();
        dragGeometry = new(dragThumb!.TargetWidth, dragThumb.TargetHeight,
            dragSource!.monitor.Width, dragSource.monitor.Height, dragGrabX, dragGrabY);
        foreach (var view in views)
        {
            view.ResetPreview();
            view.animationManager.StopAllAnimations();
            view.thumbnailManager.BeginTransition(() => { });
            view.dragPreview = new(new WindowInteropHelper(view).Handle, drag.Target);
        }
        if (!dragSource.animationManager.UpdateThumbnailPosition(dragThumb, 72))
            throw new InvalidOperationException("DWM could not mark the dragged thumbnail.");
    }

    private void UpdateDragPreview(POINT cursor)
    {
        var image = dragGeometry!.At(cursor.X, cursor.Y);
        var destination = DragDestination(cursor);
        foreach (var view in views)
        {
            bool inside = ReferenceEquals(view, destination);
            bool valid = inside && !ReferenceEquals(view, dragSource) && view.enumeratedWindowCount < 20;
            view.MoveStatus.Text = inside ? ReferenceEquals(view, dragSource)
                ? views.Count > 1 ? "Move to another display; Esc cancels" : "Only this display is shown; Esc cancels" :
                valid ? "Release to move here" : "Display already shows 20 windows" : view.IdleMoveStatus;
            view.dragPreview!.Update(image, view.DragMonitorBounds);
        }
    }

    private void CancelDrag()
    {
        drag.Cancel();
        dragGeometry = null;
        var capture = dragCapture;
        dragCapture = null;
        if (capture?.IsMouseCaptured == true) capture.ReleaseMouseCapture();
        foreach (var view in views.ToArray())
        {
            bool hadGhost = view.dragPreview != null;
            view.dragPreview?.Dispose();
            view.dragPreview = null;
            if (view.monitor != null) view.MoveStatus.Text = view.IdleMoveStatus;
            if (hadGhost && scatterInteraction.IsOpen && !isTransitioning && !isClosing && !movingWindow)
            {
                if (dragThumb != null && ReferenceEquals(view, dragSource) && !view.animationManager.UpdateThumbnailPosition(dragThumb))
                    System.Diagnostics.Trace.TraceError("DWM could not restore the source thumbnail after dragging.");
                view.thumbnailManager.SetSelectedWindow(selectedWindowHandle);
                view.animationManager.StartKeepAlive();
            }
        }
        dragSource = null;
        dragThumb = null;
    }
}
