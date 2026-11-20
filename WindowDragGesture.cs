namespace WindowScatter
{
    internal sealed class WindowDragGesture
    {
        public WindowActivationTarget Target { get; private set; }
        public bool IsPressed => Target.Handle != IntPtr.Zero;
        public bool IsDragging { get; private set; }
        private int x, y, thresholdX, thresholdY;

        public void Press(WindowActivationTarget target, int x, int y, int thresholdX, int thresholdY)
        {
            Cancel();
            if (target.Handle == IntPtr.Zero || thresholdX <= 0 || thresholdY <= 0)
                throw new ArgumentException("A drag needs a window identity and native drag thresholds.");
            Target = target;
            this.x = x; this.y = y;
            this.thresholdX = thresholdX; this.thresholdY = thresholdY;
        }

        public bool Move(int x, int y)
        {
            if (IsPressed && (Math.Abs((long)x - this.x) > thresholdX || Math.Abs((long)y - this.y) > thresholdY))
                IsDragging = true;
            return IsDragging;
        }

        public (WindowActivationTarget Target, bool Dragged) Release()
        {
            var result = (Target, IsDragging);
            Cancel();
            return result;
        }

        public void Cancel() { Target = default; IsDragging = false; }
    }
}
