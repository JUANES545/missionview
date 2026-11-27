using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindowScatter;

internal sealed class UpdateService : IUpdateService
{
    private const string Repository = "JUANES545/missionview";
    private const string ApiRoot = "https://api.github.com/repos/" + Repository;
    private const long MaximumInstallerSize = 512L * 1024 * 1024;
    private const int MaximumMetadataSize = 2 * 1024 * 1024;
    private readonly HttpClient client;
    private readonly Func<bool>? isInstalled;
    public bool IsInstalled => isInstalled?.Invoke() == true;

    // Injected handlers must also leave redirects to this service, so every hop is checked before sending.
    internal UpdateService(HttpMessageHandler? handler = null, Func<bool>? isInstalled = null)
    {
        if (handler is HttpClientHandler { AllowAutoRedirect: true } ||
            handler is SocketsHttpHandler { AllowAutoRedirect: true })
            throw new ArgumentException("Update HTTP handlers must disable automatic redirects.", nameof(handler));
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MissionView-ManualUpdater/1.0");
        this.isInstalled = isInstalled;
    }

    internal async Task<UpdateRelease?> CheckAsync(UpdateVersion currentVersion, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var candidates = new List<(UpdateVersion Version, JsonElement Release)>();
            bool complete = false;
            for (int page = 1; page <= 10; page++)
            {
                using var document = await GetJsonAsync($"{ApiRoot}/releases?per_page=20&page={page}", timeout.Token).ConfigureAwait(false);
                var releases = document.RootElement;
                Require(releases.ValueKind == JsonValueKind.Array, "Invalid release listing.");
                Require(releases.GetArrayLength() <= 20, "Release listing exceeds its page limit.");
                foreach (var release in releases.EnumerateArray())
                {
                    if (Boolean(release, "draft") || Boolean(release, "prerelease")) continue;
                    string tag = Text(release, "tag_name");
                    if (tag.StartsWith('v') && UpdateVersion.TryParse(tag[1..], out var version))
                        candidates.Add((version, release.Clone()));
                }
                if (releases.GetArrayLength() < 20) { complete = true; break; }
            }
            Require(complete, "Release history exceeds the supported lookup limit; check releases manually.");
            foreach (var candidate in candidates.OrderByDescending(c => c.Version))
            {
                string tag = "v" + candidate.Version;
                string? sourceSha = await OwnedTagCommitAsync(tag, timeout.Token).ConfigureAwait(false);
                if (sourceSha is null) continue;
                var verified = await VerifyReleaseAsync(candidate.Release, candidate.Version, sourceSha, timeout.Token).ConfigureAwait(false);
                return verified.Version.CompareTo(currentVersion) > 0 ? verified : null;
            }
            throw new UpdateException("No stable MissionView release with an owned annotated tag was found.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("The update check timed out. Try again.");
        }
    }

    private async Task<string?> OwnedTagCommitAsync(string tag, CancellationToken token)
    {
        using var reference = await GetJsonAsync($"{ApiRoot}/git/ref/tags/{tag}", token).ConfigureAwait(false);
        var target = Property(reference.RootElement, "object");
        if (Text(target, "type") != "tag") return null;
        string tagSha = Sha(target, "sha");
        using var annotation = await GetJsonAsync($"{ApiRoot}/git/tags/{tagSha}", token).ConfigureAwait(false);
        var root = annotation.RootElement;
        Require(Sha(root, "sha") == tagSha, "Annotated tag identity does not match its reference.");
        if (Text(root, "message").Split('\n')[0].TrimEnd('\r') != $"MissionView release {tag}")
            return null;
        Require(Text(root, "tag") == tag, "Annotated tag name does not match the release.");
        var commit = Property(root, "object");
        Require(Text(commit, "type") == "commit", "The release tag must point directly to a commit.");
        string source = Sha(commit, "sha");
        using var actual = await GetJsonAsync($"{ApiRoot}/git/commits/{source}", token).ConfigureAwait(false);
        Require(Sha(actual.RootElement, "sha") == source, "The release commit could not be verified.");
        return source;
    }

    private async Task<UpdateRelease> VerifyReleaseAsync(JsonElement release, UpdateVersion version, string sourceSha, CancellationToken token)
    {
        string tag = "v" + version;
        Require(Text(release, "html_url") == $"https://github.com/{Repository}/releases/tag/{tag}",
            "Release repository identity does not match MissionView.");
        var assets = Property(release, "assets");
        Require(assets.ValueKind == JsonValueKind.Array && assets.GetArrayLength() == 4,
            "The release must contain exactly the four published assets.");
        string setup = $"MissionView-{version}-Setup.exe";
        string zip = $"MissionView-{version}-win-x64.zip";
        string[] names = [setup, zip, "release-manifest.json", "SHA256SUMS.txt"];
        var byName = new Dictionary<string, UpdateAsset>(StringComparer.Ordinal);
        foreach (var asset in assets.EnumerateArray())
        {
            string name = Text(asset, "name");
            Require(names.Contains(name, StringComparer.Ordinal) && !byName.ContainsKey(name), "Unexpected or duplicate release asset.");
            long size = Integer(asset, "size");
            long limit = name == setup || name == zip ? MaximumInstallerSize : 64 * 1024;
            Require(size > 0 && size <= limit, "Release asset size is missing or exceeds the supported limit.");
            string digest = Text(asset, "digest");
            Require(Regex.IsMatch(digest, @"\Asha256:[a-f0-9]{64}\z"), "The release asset SHA-256 digest is missing or invalid.");
            string url = Text(asset, "browser_download_url");
            Require(url == $"https://github.com/{Repository}/releases/download/{tag}/{name}",
                "Release asset URL is not the expected MissionView download.");
            byName.Add(name, new UpdateAsset(name, new Uri(url), size, digest[7..]));
        }
        byte[] checksumBytes = await GetAssetBytesAsync(byName["SHA256SUMS.txt"], token).ConfigureAwait(false);
        var sums = ParseChecksums(checksumBytes, [setup, zip, "release-manifest.json"]);
        foreach (string name in sums.Keys)
            Require(sums[name] == byName[name].Sha256, "The checksum list disagrees with the GitHub asset digest.");
        byte[] manifestBytes = await GetAssetBytesAsync(byName["release-manifest.json"], token).ConfigureAwait(false);
        using var manifest = ParseJson(manifestBytes);
        var metadata = manifest.RootElement;
        Require(Text(metadata, "version") == version.ToString() && Sha(metadata, "sourceSha") == sourceSha,
            "Release manifest version or source SHA does not match the annotated release tag.");
        Require(Text(metadata, "runtime") == "win-x64" && Boolean(metadata, "selfContained") &&
            Text(metadata, "targetFramework") == "net10.0-windows10.0.26100.0",
            "The release runtime or target framework is unsupported; review this release manually.");
        return new UpdateRelease(version, sourceSha, byName[setup]);
    }

    internal async Task<VerifiedUpdate> DownloadAsync(UpdateRelease release, IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken, bool background = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (background && !IsInstalled)
            throw new UpdateException("Background downloads require a registered MissionView installation.");
        var asset = release.Installer;
        Require(asset.Name == $"MissionView-{release.Version}-Setup.exe" &&
            asset.Url.AbsoluteUri == $"https://github.com/{Repository}/releases/download/v{release.Version}/{asset.Name}" &&
            asset.Size > 0 && asset.Size <= MaximumInstallerSize && Regex.IsMatch(asset.Sha256, @"\A[a-f0-9]{64}\z"),
            "The installer download descriptor is invalid.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        string directory = Directory.CreateTempSubdirectory("MissionView-update-").FullName;
        string finalPath = Path.Combine(directory, asset.Name);
        var result = new VerifiedUpdate(directory, finalPath, release, () =>
        {
            // Only the two owned files are removable; never sweep the user's temporary directory.
            File.Delete(finalPath);
            File.Delete(finalPath + ".partial");
            Directory.Delete(directory);
        });
        try
        {
            progress?.Report(new(UpdateStatus.Downloading, 0, asset.Size));
            using (var response = await SendAsync(asset.Url, true, timeout.Token).ConfigureAwait(false))
            {
                CheckLength(response, asset.Size);
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                await using var output = new FileStream(finalPath + ".partial", FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long received = 0;
                byte[] buffer = new byte[81920];
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    received += count;
                    Require(received <= asset.Size, "The installer is larger than its declared release size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, count);
                    progress?.Report(new(UpdateStatus.Downloading, received, asset.Size));
                }
                progress?.Report(new(UpdateStatus.Verifying, received, asset.Size));
                Require(received == asset.Size, "The installer download was truncated.");
                Require(Convert.ToHexStringLower(hash.GetHashAndReset()) == asset.Sha256, "The installer SHA-256 checksum does not match.");
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            timeout.Token.ThrowIfCancellationRequested();
            File.Move(finalPath + ".partial", finalPath);
            return result;
        }
        catch (Exception failure)
        {
            try { result.Dispose(); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                throw new UpdateException($"Download failed ({failure.Message}). Temporary installer cleanup also failed: {cleanup.Message}");
            }
            if (failure is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                throw new UpdateException("The installer download timed out. Try again.");
            throw;
        }
    }

    Task<UpdateRelease?> IUpdateService.CheckAsync(UpdateVersion current, CancellationToken token) => CheckAsync(current, token);
    Task<VerifiedUpdate> IUpdateService.DownloadAsync(UpdateRelease release, IProgress<UpdateProgress>? progress,
        CancellationToken token, bool background) => DownloadAsync(release, progress, token, background);
    async Task<IAsyncDisposable> IUpdateService.LockVerifiedInstallerAsync(VerifiedUpdate update, CancellationToken token) =>
        await OpenVerifiedInstallerAsync(update, token).ConfigureAwait(false);

    internal static async Task<FileStream> OpenVerifiedInstallerAsync(VerifiedUpdate update, CancellationToken token)
    {
        var stream = new FileStream(update.InstallerPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            Require(stream.Length == update.Release.Installer.Size, "The downloaded installer size changed.");
            byte[] hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
            Require(Convert.ToHexStringLower(hash) == update.Release.Installer.Sha256, "The downloaded installer checksum changed.");
            token.ThrowIfCancellationRequested();
            return stream;
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task<byte[]> GetAssetBytesAsync(UpdateAsset asset, CancellationToken token)
    {
        using var response = await SendAsync(asset.Url, true, token).ConfigureAwait(false);
        CheckLength(response, asset.Size);
        byte[] bytes = await ReadBoundedAsync(response, (int)asset.Size, token).ConfigureAwait(false);
        Require(bytes.LongLength == asset.Size, "Release metadata download was truncated.");
        Require(Convert.ToHexStringLower(SHA256.HashData(bytes)) == asset.Sha256, "Release metadata checksum does not match.");
        return bytes;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken token)
    {
        using var response = await SendAsync(new Uri(url), false, token).ConfigureAwait(false);
        return ParseJson(await ReadBoundedAsync(response, MaximumMetadataSize, token).ConfigureAwait(false));
    }

    private async Task<HttpResponseMessage> SendAsync(Uri initial, bool asset, CancellationToken token)
    {
        Uri current = initial;
        for (int hop = 0; hop <= 5; hop++)
        {
            ValidateUri(current, initial, asset);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.ParseAdd(asset ? "application/octet-stream" : "application/vnd.github+json");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri != current)
            {
                response.Dispose();
                throw new UpdateException("The HTTP handler followed an unvalidated redirect.");
            }
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                Require(asset && location is not null && hop < 5, "Unexpected update metadata redirect.");
                current = new Uri(current, location!);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                response.Dispose();
                throw new UpdateException(status is 403 or 429
                    ? $"GitHub refused the update request (HTTP {status}, possibly rate limited). Try again later."
                    : $"The update request failed (HTTP {status}).");
            }
            if (response.StatusCode != HttpStatusCode.OK)
            {
                response.Dispose();
                throw new UpdateException("The update server returned an unexpected partial or empty response.");
            }
            return response;
        }
        throw new UpdateException("Too many download redirects.");
    }

    private static void ValidateUri(Uri uri, Uri initial, bool asset)
    {
        Require(uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0,
            "An update URL has an unsafe scheme, port or authority.");
        if (!asset)
        {
            Require(uri.Host == "api.github.com" && uri.AbsolutePath.StartsWith($"/repos/{Repository}/", StringComparison.Ordinal),
                "An update API URL is outside the MissionView repository.");
            return;
        }
        Require(uri == initial ||
            ((uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com") &&
             uri.AbsolutePath.StartsWith("/github-production-release-asset/", StringComparison.Ordinal)),
            "The installer redirected to an untrusted download host or path.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        Require(response.Content.Headers.ContentLength is not long length || length <= maximum, "Update metadata exceeds its size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            Require(output.Length + count <= maximum, "Update metadata exceeds its size limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static void CheckLength(HttpResponseMessage response, long expected) =>
        Require(response.Content.Headers.ContentLength is not long length || length == expected, "The download Content-Length does not match the release asset.");

    private static Dictionary<string, string> ParseChecksums(byte[] bytes, string[] expected)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new UpdateException("The release checksum list is not UTF-8."); }
        var lines = text.TrimEnd('\r', '\n').Split('\n');
        Require(lines.Length == expected.Length, "The release checksum list is incomplete.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd('\r');
            var match = Regex.Match(line, @"\A([a-f0-9]{64})  ([a-zA-Z0-9.-]+)\z");
            Require(match.Success && expected.Contains(match.Groups[2].Value, StringComparer.Ordinal) &&
                result.TryAdd(match.Groups[2].Value, match.Groups[1].Value), "The release checksum list contains an invalid or duplicate entry.");
        }
        return result;
    }

    private static JsonDocument ParseJson(byte[] bytes)
    {
        try
        {
            var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            try { RejectDuplicates(document.RootElement); return document; }
            catch { document.Dispose(); throw; }
        }
        catch (JsonException) { throw new UpdateException("Update metadata contains invalid JSON."); }
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                Require(names.Add(property.Name), "Update metadata contains duplicate fields.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }

    private static JsonElement Property(JsonElement value, string name)
    {
        Require(value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out _), $"Update metadata is missing '{name}'.");
        return value.GetProperty(name);
    }

    private static string Text(JsonElement value, string name)
    {
        var property = Property(value, name);
        Require(property.ValueKind == JsonValueKind.String, $"Update metadata field '{name}' is not text.");
        return property.GetString()!;
    }

    private static bool Boolean(JsonElement value, string name)
    {
        var property = Property(value, name);
        Require(property.ValueKind is JsonValueKind.True or JsonValueKind.False, $"Update metadata field '{name}' is not a boolean.");
        return property.GetBoolean();
    }

    private static long Integer(JsonElement value, string name)
    {
        var property = Property(value, name);
        Require(property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out _), $"Update metadata field '{name}' is not an integer.");
        return property.GetInt64();
    }

    private static string Sha(JsonElement value, string name)
    {
        string sha = Text(value, name);
        Require(Regex.IsMatch(sha, @"\A[a-f0-9]{40}\z"), "Update metadata contains an invalid source SHA.");
        return sha;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new UpdateException(message);
    }

    public void Dispose() => client.Dispose();
}
