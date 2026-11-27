using WindowScatter;
using Xunit;

public class OverviewDesktopsTests
{
    [Fact]
    public async Task ProductionFallbackDoesNotSupplySyntheticDesktops()
    {
        Assert.False(new AppSettings().EnableVirtualDesktopStrip);
        var state = new OverviewDesktopState(new UnavailableOverviewDesktops());
        await state.OpenAsync();
        Assert.Null(state.Snapshot);
        Assert.Equal(UnavailableOverviewDesktops.Reason, state.Unavailable);
        Assert.Null(await state.ChangeAsync(Guid.NewGuid(), null));
    }

    [Fact]
    public async Task MoveLeavesActiveDesktopUnchangedAndUncertainSwitchInvalidatesSnapshot()
    {
        var provider = new TestProvider();
        var state = new OverviewDesktopState(provider);
        await state.OpenAsync();
        var window = new WindowActivationTarget(new IntPtr(7), 8, 9);
        var move = state.ChangeAsync(provider.Target, window);
        Assert.Equal(window, provider.Window);
        Assert.Null(await state.ChangeAsync(provider.Target, null));
        provider.Completion.SetResult(DesktopChange.Confirmed);
        Assert.Equal(DesktopChange.Confirmed, await move);
        Assert.Equal(provider.Source, state.Snapshot!.Active);

        provider.Completion = new();
        var change = state.ChangeAsync(provider.Target, null);
        provider.Completion.SetResult(DesktopChange.Unconfirmed);
        Assert.Equal(DesktopChange.Unconfirmed, await change);
        Assert.Null(state.Snapshot);
        Assert.Null(await state.ChangeAsync(provider.Target, null));
    }

    [Fact]
    public async Task ClosedOverviewDiscardsLateConfirmationAndPreventsDuplicateDispatch()
    {
        var provider = new TestProvider();
        var state = new OverviewDesktopState(provider);
        await state.OpenAsync();
        var change = state.ChangeAsync(provider.Target, null);
        state.Close();
        await state.OpenAsync();
        Assert.Null(await state.ChangeAsync(provider.Target, null));
        provider.Completion.SetResult(DesktopChange.Confirmed);
        Assert.Null(await change);
        Assert.Equal(provider.Source, state.Snapshot!.Active);
        Assert.False(state.Pending);
    }

    private sealed class TestProvider : IOverviewDesktops
    {
        internal readonly Guid Source = Guid.NewGuid();
        internal readonly Guid Target = Guid.NewGuid();
        internal TaskCompletionSource<DesktopChange> Completion = new();
        internal WindowActivationTarget? Window;

        public Task<DesktopRead> ReadAsync() =>
            Task.FromResult(new DesktopRead(new DesktopSnapshot([Source, Target], Source, "test-epoch"), null));
        public Task<DesktopChange> SwitchAsync(DesktopSnapshot snapshot, Guid target) => Completion.Task;
        public Task<DesktopChange> MoveAsync(DesktopSnapshot snapshot, WindowActivationTarget window, Guid target)
        {
            Window = window;
            return Completion.Task;
        }
    }
}
