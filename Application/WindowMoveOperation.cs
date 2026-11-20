namespace WindowScatter
{
    internal sealed class WindowMoveOperation
    {
        private readonly TimeSpan timeout;
        private Task? pending;
        public bool IsPending => pending is { IsCompleted: false };

        public WindowMoveOperation(TimeSpan? timeout = null) => this.timeout = timeout ?? TimeSpan.FromSeconds(2);

        public async Task RunAsync(Action<CancellationToken> move, CancellationToken cancellation)
        {
            if (IsPending) throw new InvalidOperationException("A previous native move is still pending. Wait for Windows to reply.");
            cancellation.ThrowIfCancellationRequested();
            var operation = Task.Run(() => { cancellation.ThrowIfCancellationRequested(); move(cancellation); });
            pending = operation;
            try { await operation.WaitAsync(timeout, cancellation); }
            catch
            {
                // A USER32 call already in progress cannot be revoked on timeout.
                _ = operation.ContinueWith(t => System.Diagnostics.Trace.TraceError($"Late window move failure: {t.Exception}"),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw;
            }
        }
    }
}
