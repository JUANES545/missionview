using WindowScatter;
using Xunit;

public class WindowMoveOperationTests
{
    [Fact]
    public async Task HungNativeRequestTimesOutWithoutClaimingCompletionOrStartingASecondMove()
    {
        using var release = new ManualResetEventSlim();
        var operation = new WindowMoveOperation(TimeSpan.FromMilliseconds(100));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int writes = 0;
        try
        {
            var pending = operation.RunAsync(_ =>
            {
                Interlocked.Increment(ref writes);
                entered.SetResult();
                release.Wait(TimeSpan.FromSeconds(5));
            }, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAsync<TimeoutException>(() => pending);
            Assert.True(operation.IsPending);
            await Assert.ThrowsAsync<InvalidOperationException>(() => operation.RunAsync(_ => writes++, CancellationToken.None));
            Assert.Equal(1, writes);
        }
        finally
        {
            release.Set();
            Assert.True(SpinWait.SpinUntil(() => !operation.IsPending, TimeSpan.FromSeconds(5)));
        }
        await operation.RunAsync(_ => writes++, CancellationToken.None);
        Assert.Equal(2, writes);
    }

    [Fact]
    public async Task SessionCancellationReleasesTheWaitButDoesNotPretendToRevokeAnIssuedNativeCall()
    {
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new WindowMoveOperation();
        var pending = operation.RunAsync(token =>
        {
            entered.SetResult();
            release.Wait(TimeSpan.FromSeconds(5));
            token.ThrowIfCancellationRequested();
        }, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.True(operation.IsPending);
        }
        finally
        {
            release.Set();
            Assert.True(SpinWait.SpinUntil(() => !operation.IsPending, TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task CancellationBeforeCommitAndNativeFailureNeverBecomeSuccessfulMoves()
    {
        var operation = new WindowMoveOperation();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            operation.RunAsync(_ => throw new Exception("Must not execute."), cancellation.Token));
        Assert.False(operation.IsPending);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            operation.RunAsync(_ => throw new InvalidOperationException("Native rejection"), CancellationToken.None));
        Assert.False(operation.IsPending);
    }
}
