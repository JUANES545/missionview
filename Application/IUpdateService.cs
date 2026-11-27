namespace WindowScatter;

internal interface IUpdateService : IDisposable
{
    bool IsInstalled { get; }
    Task<UpdateRelease?> CheckAsync(UpdateVersion current, CancellationToken token);
    Task<VerifiedUpdate> DownloadAsync(UpdateRelease release, IProgress<UpdateProgress>? progress, CancellationToken token, bool background = false);
    Task<IAsyncDisposable> LockVerifiedInstallerAsync(VerifiedUpdate update, CancellationToken token);
}

// The service owns these exact temporary files. Application coordinates retention/release, not filesystem effects.
internal sealed class VerifiedUpdate(string directory, string installerPath, UpdateRelease release, Action cleanup) : IDisposable
{
    private bool retained, disposed;
    internal string DirectoryPath { get; } = directory;
    internal string InstallerPath { get; } = installerPath;
    internal UpdateRelease Release { get; } = release;
    internal void RetainForInstaller() => retained = true;
    public void Dispose()
    {
        if (disposed || retained) return;
        cleanup();
        disposed = true;
    }
}
