using System.Globalization;
using System.Text.RegularExpressions;

namespace WindowScatter;

internal readonly record struct UpdateVersion(int Major, int Minor, int Patch) : IComparable<UpdateVersion>
{
    public static bool TryParse(string? value, out UpdateVersion version)
    {
        version = default;
        if (value is null || !Regex.IsMatch(value, @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z"))
            return false;
        var parts = value.Split('.');
        if (!int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor) ||
            !int.TryParse(parts[2], out int patch) || major > 65534 || minor > 65534 || patch > 65534)
            return false;
        version = new(major, minor, patch);
        return true;
    }

    public int CompareTo(UpdateVersion other)
    {
        int result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        return result == 0 ? Patch.CompareTo(other.Patch) : result;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}

internal enum UpdateStatus
{
    Idle, Checking, UpToDate, Available, Downloading, Verifying, ReadyToInstall,
    AwaitingConfirmation, StartingInstaller, InstallerStarted, Cancelled, Error
}

internal sealed record UpdateState(UpdateStatus Status, string Message, bool IsBusy = false,
    bool IsIndeterminate = false, double? Progress = null, UpdateVersion? AvailableVersion = null);

internal sealed record UpdateProgress(UpdateStatus Status, long BytesReceived, long? TotalBytes)
{
    public double? Fraction => TotalBytes is > 0 ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0, 1) : null;
}

internal sealed record UpdateAsset(string Name, Uri Url, long Size, string Sha256);

internal sealed record UpdateRelease(UpdateVersion Version, string SourceSha, UpdateAsset Installer);

internal sealed class UpdateException(string message) : Exception(message);

