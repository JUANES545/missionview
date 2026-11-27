using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security;

namespace WindowScatter;

internal sealed class UpdateController : INotifyPropertyChanged, IDisposable
{
    private readonly IUpdateService service;
    private readonly UpdateVersion currentVersion;
    private readonly Action requestShutdown;
    private readonly Func<string, bool> startInstaller;
    private readonly Func<string, bool>? restartInstaller;
    private readonly Func<bool> supportedPlatform;
    private readonly SynchronizationContext? context;
    private readonly object gate = new();
    private Task? operation;
    private CancellationTokenSource? cancellation;
    private bool backgroundOperation;
    private UpdateRelease? release;
    private VerifiedUpdate? download;
    private bool stagedInBackground;
    private volatile bool disposed;
    private volatile bool installerStarted;
    private int generation;
    private UpdateState state = new(UpdateStatus.Idle, "Check for updates...");

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<UpdateProgress>? ProgressChanged;
    public UpdateState State => state;
    public bool HasInstallerStarted => installerStarted;
    internal bool IsInstalled => service.IsInstalled;

    internal UpdateController(IUpdateService service, UpdateVersion currentVersion, Action requestShutdown,
        Func<string, bool> startInstaller, SynchronizationContext? uiContext,
        Func<bool> supportedPlatform, Func<string, bool>? restartInstaller = null)
    {
        this.service = service;
        this.currentVersion = currentVersion;
        this.requestShutdown = requestShutdown;
        this.startInstaller = startInstaller ?? throw new ArgumentNullException(nameof(startInstaller));
        this.supportedPlatform = supportedPlatform ?? throw new ArgumentNullException(nameof(supportedPlatform));
        this.restartInstaller = restartInstaller;
        context = uiContext ?? SynchronizationContext.Current;
    }

    public Task CheckAsync() => RunAsync(token => CheckForReleaseAsync(token, background: false));

    internal Task StageInBackgroundAsync() => RunAsync(async token =>
    {
        if (!IsInstalled || State.Status == UpdateStatus.ReadyToInstall) return;
        await CheckForReleaseAsync(token, background: true).ConfigureAwait(false);
    }, background: true);

    private async Task CheckForReleaseAsync(CancellationToken token, bool background)
    {
        token.ThrowIfCancellationRequested();
        ClearDownload();
        release = null;
        await SetStateAsync(new(UpdateStatus.Checking, "Checking for updates...", true, true)).ConfigureAwait(false);
        release = await service.CheckAsync(currentVersion, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (background && release is not null)
        {
            await DownloadReleaseAsync(token, background: true).ConfigureAwait(false);
            return;
        }
        await SetStateAsync(release is null
            ? new(UpdateStatus.UpToDate, "You're up to date.")
            : new(UpdateStatus.Available, $"MissionView {release.Version} is available.", AvailableVersion: release.Version)).ConfigureAwait(false);
    }

    public Task DownloadAsync() => RunAsync(token => DownloadReleaseAsync(token, background: false));

    private async Task DownloadReleaseAsync(CancellationToken token, bool background)
    {
        if (release is null) throw new UpdateException("Check for an available update before downloading.");
        EnsurePlatform();
        ClearDownload();
        int currentGeneration = generation;
        await SetStateAsync(new(UpdateStatus.Downloading, "Downloading update...", true, true, AvailableVersion: release.Version)).ConfigureAwait(false);
        download = await service.DownloadAsync(release,
            new DownloadProgress(progress => PublishProgress(progress, currentGeneration)), token, background).ConfigureAwait(false);
        if (token.IsCancellationRequested)
        {
            ClearDownload();
            token.ThrowIfCancellationRequested();
        }
        stagedInBackground = background;
        await SetStateAsync(new(UpdateStatus.ReadyToInstall,
            "Installer integrity verified. Publisher signature not verified by MissionView; review compatibility and Windows warnings.",
            Progress: 1, AvailableVersion: release.Version)).ConfigureAwait(false);
    }

    public Task InstallAsync(Func<UpdateRelease, CancellationToken, Task<bool>> confirm, bool restart = false) => RunAsync(async token =>
    {
        ArgumentNullException.ThrowIfNull(confirm);
        if (download is null || release is null) throw new UpdateException("Download and verify the installer before installing.");
        if ((stagedInBackground && !restart) || (restart && State.Status != UpdateStatus.ReadyToInstall))
            throw new UpdateException("Use Restart to update once the background download is ready.");
        EnsurePlatform();
        var launch = restart ? restartInstaller : startInstaller;
        if (launch is null || (restart && !IsInstalled))
            throw new UpdateException("Restarting to update requires a registered MissionView installation.");
        await SetStateAsync(new(UpdateStatus.AwaitingConfirmation, "Confirm installation. MissionView has not verified the publisher signature.", true,
            AvailableVersion: release.Version)).ConfigureAwait(false);
        bool approved = await OnContextAsync(() => confirm(release, token)).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!approved)
        {
            await SetStateAsync(new(UpdateStatus.ReadyToInstall, "Installation cancelled. Your current installation is unchanged.",
                AvailableVersion: release.Version)).ConfigureAwait(false);
            return;
        }
        await SetStateAsync(new(UpdateStatus.Verifying, "Verifying downloaded installer...", true, true,
            AvailableVersion: release.Version)).ConfigureAwait(false);
        await using var lockedInstaller = await service.LockVerifiedInstallerAsync(download, token).ConfigureAwait(false);
        await SetStateAsync(new(UpdateStatus.StartingInstaller, "Starting installer...", true, true,
            AvailableVersion: release.Version)).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!launch(download.InstallerPath))
            throw new UpdateException("The installer could not be started. Your current installation is unchanged.");
        installerStarted = true;
        download.RetainForInstaller();
        await OnContextAsync(() =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasInstallerStarted)));
            SetState(new(UpdateStatus.InstallerStarted,
                "Installer started. Installation is not yet confirmed; follow the Windows installer.",
                AvailableVersion: release.Version));
            try { requestShutdown(); }
            catch (Exception ex)
            {
                Trace.TraceError($"Installer started, but MissionView shutdown failed: {ex}");
                SetState(new(UpdateStatus.Error,
                    "The installer started, but MissionView could not finish its shutdown. Installation is not confirmed.",
                    AvailableVersion: release.Version));
            }
            return Task.FromResult(true);
        }).ConfigureAwait(false);
    });

    public void Cancel()
    {
        lock (gate) cancellation?.Cancel();
    }

    internal void CancelBackground()
    {
        lock (gate)
            if (backgroundOperation) cancellation?.Cancel();
    }

    private Task RunAsync(Func<CancellationToken, Task> action, bool background = false)
    {
        TaskCompletionSource completion;
        CancellationTokenSource source;
        lock (gate)
        {
            if (background && (disposed || installerStarted)) return Task.CompletedTask;
            if (installerStarted)
            {
                var error = new UpdateException("The installer has already started in this session. Follow the Windows installer and restart MissionView before checking again. No second installer will be started.");
                Trace.TraceWarning(error.Message);
                return Task.FromException(error);
            }
            ObjectDisposedException.ThrowIf(disposed, this);
            if (operation is { IsCompleted: false }) return operation;
            source = new CancellationTokenSource();
            cancellation = source;
            backgroundOperation = background;
            generation++;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            operation = completion.Task;
        }
        _ = Task.Run(() => ExecuteAsync(action, source, completion));
        return completion.Task;
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationTokenSource source, TaskCompletionSource completion)
    {
        Exception? unexpected = null;
        try
        {
            await action(source.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            bool cancelled = ex is OperationCanceledException && source.IsCancellationRequested;
            bool expected = cancelled || IsOperationError(ex);
            if (!cancelled) Trace.TraceError($"Update failed: {ex}");
            if (!expected) unexpected = ex;
            string message = installerStarted
                ? "The installer started, but MissionView could not finish its shutdown. Installation is not confirmed."
                : cancelled ? "Update cancelled. Your current installation is unchanged."
                : expected ? ErrorMessage(ex) : "An unexpected update error occurred. Your current installation is unchanged.";
            try
            {
                await SetStateAsync(new(cancelled && !installerStarted ? UpdateStatus.Cancelled : UpdateStatus.Error,
                    message, AvailableVersion: release?.Version)).ConfigureAwait(false);
            }
            catch (Exception reportingFailure)
            {
                Trace.TraceError($"Could not report the update failure to the UI: {reportingFailure}");
                unexpected = new AggregateException(ex, reportingFailure);
            }
        }
        finally
        {
            bool disposeResources;
            lock (gate)
            {
                if (ReferenceEquals(cancellation, source)) cancellation = null;
                source.Dispose();
                disposeResources = disposed;
            }
            if (disposeResources)
            {
                try { ClearDownload(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Trace.TraceError($"Could not remove the temporary update installer: {ex}");
                    unexpected ??= ex;
                }
                service.Dispose();
            }
            if (unexpected is not null) completion.TrySetException(unexpected);
            else completion.TrySetResult();
        }
    }

    private static bool IsOperationError(Exception ex) =>
        ex is UpdateException or HttpRequestException or IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or SecurityException or OperationCanceledException or InvalidOperationException;

    private static string ErrorMessage(Exception ex) => ex switch
    {
        UpdateException => ex.Message,
        HttpRequestException => "Could not reach GitHub. Check your connection and try again.",
        OperationCanceledException => "The update request timed out. Try again.",
        System.ComponentModel.Win32Exception => "Windows did not start the installer (or elevation was cancelled). Your current installation is unchanged.",
        UnauthorizedAccessException or SecurityException => "Windows denied access to the temporary installer. Your current installation is unchanged.",
        InvalidOperationException => "The update operation could not continue. Your current installation is unchanged.",
        _ => "The update could not read or write its temporary files. Your current installation is unchanged."
    };

    private void PublishProgress(UpdateProgress progress, int currentGeneration)
    {
        void Publish()
        {
            if (disposed || generation != currentGeneration || State.Status is not (UpdateStatus.Downloading or UpdateStatus.Verifying))
                return;
            SetState(new(progress.Status, progress.Status == UpdateStatus.Verifying ? "Verifying update..." : "Downloading update...",
                true, progress.Status == UpdateStatus.Verifying || progress.Fraction is null,
                progress.Fraction, release?.Version));
            ProgressChanged?.Invoke(this, progress);
        }
        if (context is null || SynchronizationContext.Current == context) Publish();
        else context.Post(_ => Publish(), null);
    }

    private Task SetStateAsync(UpdateState value) => disposed ? Task.CompletedTask : OnContextAsync(() =>
    {
        if (!disposed) SetState(value);
        return Task.FromResult(true);
    });

    private void SetState(UpdateState value)
    {
        state = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
    }

    private Task<T> OnContextAsync<T>(Func<Task<T>> action)
    {
        if (context is null || SynchronizationContext.Current == context) return action();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(async _ =>
        {
            try { completion.SetResult(await action()); }
            catch (Exception ex) { completion.SetException(ex); }
        }, null);
        return completion.Task;
    }

    private void EnsurePlatform()
    {
        if (!supportedPlatform())
            throw new UpdateException("This installer requires Windows x64, build 26100 or later. No installer was started.");
    }

    private void ClearDownload()
    {
        download?.Dispose();
        download = null;
        stagedInBackground = false;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            cancellation?.Cancel();
            if (cancellation is not null) return;
        }
        try { ClearDownload(); }
        finally { service.Dispose(); }
    }

    private sealed class DownloadProgress(Action<UpdateProgress> report) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => report(value);
    }
}
