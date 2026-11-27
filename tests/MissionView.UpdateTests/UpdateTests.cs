using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowScatter;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MissionView.UpdateTests;

public sealed class UpdateTests
{
    [Theory]
    [InlineData("0.1.0", "0.0.99", 1)]
    [InlineData("0.10.0", "0.9.9", 1)]
    [InlineData("2.0.0", "10.0.0", -1)]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("65534.65534.65534", "65534.65534.65533", 1)]
    public void ComparesPublishedThreePartVersionsNumerically(string a, string b, int expected)
    {
        Assert.True(UpdateVersion.TryParse(a, out var first));
        Assert.True(UpdateVersion.TryParse(b, out var second));
        Assert.Equal(expected, Math.Sign(first.CompareTo(second)));
        Assert.Equal(a, first.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v0.1.0")]
    [InlineData("01.1.0")]
    [InlineData("0.1.0-beta")]
    [InlineData("0.1.0+build")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.3\n")]
    [InlineData("-1.2.3")]
    [InlineData("65535.0.0")]
    [InlineData("999999999999999999999.0.0")]
    public void RejectsUnsupportedVersions(string? input) => Assert.False(UpdateVersion.TryParse(input, out _));

    [Fact]
    public async Task FindsLatestOwnedStableReleaseNotInheritedOrDraftOrPrerelease()
    {
        using var fixture = new Fixture("0.10.0");
        var inherited = fixture.AddRelease("99.0.0", owned: false);
        var draft = fixture.AddRelease("100.0.0");
        draft["draft"] = true;
        var prerelease = fixture.AddRelease("101.0.0");
        prerelease["prerelease"] = true;
        fixture.SetListing(inherited, draft, prerelease, fixture.Release);
        var release = await fixture.Service.CheckAsync(new(0, 9, 0), default);
        Assert.Equal(new UpdateVersion(0, 10, 0), release!.Version);
        Assert.Equal(Fixture.Commit, release.SourceSha);
        Assert.DoesNotContain(fixture.Requests, u => u.Contains("/tags/v100") || u.Contains("/tags/v101"));
        Assert.DoesNotContain(fixture.Requests, u => u.EndsWith(".exe") || u.EndsWith(".zip"));
    }

    [Fact]
    public async Task ResolvesActualAnnotatedCommitInsteadOfMutableTargetCommitish()
    {
        using var fixture = new Fixture();
        fixture.Release["target_commitish"] = "unrelated-branch";
        fixture.SetListing(fixture.Release);
        var release = await fixture.Service.CheckAsync(new(0, 0, 1), default);
        Assert.Equal(Fixture.Commit, release!.SourceSha);
        Assert.Contains(fixture.Requests, u => u.EndsWith("/git/commits/" + Fixture.Commit));
    }

    [Fact]
    public async Task UpToDateRequiresSuccessfulValidatedMetadataAndNeverDowngrades()
    {
        using var fixture = new Fixture();
        Assert.Null(await fixture.Service.CheckAsync(new(0, 1, 0), default));
        Assert.Null(await fixture.Service.CheckAsync(new(1, 0, 0), default));
        fixture.Manifest["sourceSha"] = new string('b', 40);
        fixture.RebuildMetadata();
        await Assert.ThrowsAsync<UpdateException>(() => fixture.Service.CheckAsync(new(1, 0, 0), default));
    }

    [Fact]
    public async Task NoOwnedStableReleaseIsAnErrorNotUpToDate()
    {
        using var fixture = new Fixture();
        fixture.SetListing(fixture.AddRelease("9.0.0", owned: false));
        await Assert.ThrowsAsync<UpdateException>(() => fixture.Service.CheckAsync(new(0, 1, 0), default));
    }

    [Theory]
    [InlineData("lightweight")]
    [InlineData("wrongTagSha")]
    [InlineData("wrongTagName")]
    [InlineData("wrongCommit")]
    [InlineData("nestedTag")]
    public async Task RejectsInvalidAnnotatedTagProvenance(string failure)
    {
        using var fixture = new Fixture();
        switch (failure)
        {
            case "lightweight": fixture.Reference["object"]!["type"] = "commit"; break;
            case "wrongTagSha": fixture.Annotation["sha"] = new string('b', 40); break;
            case "wrongTagName": fixture.Annotation["tag"] = "v9.9.9"; break;
            case "wrongCommit": fixture.CommitObject["sha"] = new string('b', 40); break;
            case "nestedTag": fixture.Annotation["object"]!["type"] = "tag"; break;
        }
        await Assert.ThrowsAsync<UpdateException>(() => fixture.Service.CheckAsync(new(0, 0, 1), default));
    }

    [Theory]
    [InlineData("sourceSha", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("sourceSha", "main")]
    [InlineData("version", "0.2.0")]
    [InlineData("runtime", "win-arm64")]
    [InlineData("targetFramework", "net11.0-windows")]
    public async Task RejectsManifestInconsistentWithRelease(string key, string value)
    {
        using var fixture = new Fixture();
        fixture.Manifest[key] = value;
        fixture.RebuildMetadata();
        await Assert.ThrowsAsync<UpdateException>(() => fixture.Service.CheckAsync(new(0, 0, 1), default));
    }

    [Fact]
    public async Task RejectsMissingAndNonSelfContainedManifest()
    {
        using var fixture = new Fixture();
        fixture.Manifest.Remove("runtime");
        fixture.Manifest["selfContained"] = false;
        fixture.RebuildMetadata();
        await Assert.ThrowsAsync<UpdateException>(() => fixture.Service.CheckAsync(new(0, 0, 1), default));
    }

    [Theory]
    [InlineData("foreignRepo")]
    [InlineData("missingAsset")]
    [InlineData("duplicateAsset")]
    [InlineData("foreignAsset")]
    [InlineData("insecureAsset")]
    [InlineData("userInfo")]
    [InlineData("nonDefaultPort")]
    [InlineData("missingDigest")]
    [InlineData("badDigest")]
    [InlineData("zeroSize")]
    [InlineData("negativeSize")]
    [InlineData("hugeSize")]
    public async Task RejectsUnsafeReleaseMetadata(string failure)
    {
        using var fixture = new Fixture();
        var assets = fixture.Release["assets"]!.AsArray();
        switch (failure)
        {
            case "foreignRepo": fixture.Release["html_url"] = "https://github.com/upstream/missionview/releases/tag/v0.1.0"; break;
            case "missingAsset": assets.RemoveAt(0); break;
            case "duplicateAsset": assets[1] = assets[0]!.DeepClone(); break;
            case "foreignAsset": assets[0]!["browser_download_url"] = "https://evil.example/installer.exe"; break;
            case "insecureAsset": assets[0]!["browser_download_url"] = fixture.SetupUrl.Replace("https:", "http:"); break;
            case "userInfo": assets[0]!["browser_download_url"] = fixture.SetupUrl.Replace("github.com", "user@github.com"); break;
            case "nonDefaultPort": assets[0]!["browser_download_url"] = fixture.SetupUrl.Replace("github.com", "github.com:444"); break;
            case "missingDigest": assets[0]!.AsObject().Remove("digest"); break;
            case "badDigest": assets[0]!["digest"] = "sha256:123"; break;
            case "zeroSize": assets[0]!["size"] = 0; break;
            case "negativeSize": assets[0]!["size"] = -1; break;
            case "hugeSize": assets[0]!["size"] = 512L * 1024 * 1024 + 1; break;
        }
        fixture.SetListing(fixture.Release);
        await Assert.ThrowsAsync<UpdateException>(() => fixture.Service.CheckAsync(new(0, 0, 1), default));
        Assert.DoesNotContain(fixture.Requests, u => u.Contains("evil") || u.EndsWith(".exe"));
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("hash")]
    [InlineData("duplicateJson")]
    [InlineData("invalidJson")]
    [InlineData("oversizedListing")]
    [InlineData("checksumMismatch")]
    [InlineData("checksumDuplicate")]
    public async Task RejectsCorruptOrUnboundedMetadata(string failure)
    {
        using var fixture = new Fixture();
        switch (failure)
        {
            case "truncated": fixture.SetBytes(fixture.ManifestUrl, fixture.ManifestBytes[..^1]); break;
            case "hash": fixture.SetBytes(fixture.ManifestUrl, Enumerable.Repeat((byte)'x', fixture.ManifestBytes.Length).ToArray()); break;
            case "duplicateJson": fixture.SetRawManifest("{\"version\":\"0.1.0\",\"version\":\"0.1.0\"}"); break;
            case "invalidJson": fixture.SetRawManifest("{"); break;
            case "oversizedListing": fixture.SetBytes(Fixture.ListUrl, new byte[2 * 1024 * 1024 + 1]); break;
            case "checksumMismatch": fixture.SetChecksums(new string('f', 64) + "  " + fixture.SetupName + "\n" +
                fixture.ChecksumText.Split('\n')[1] + "\n" + fixture.ChecksumText.Split('\n')[2] + "\n"); break;
            case "checksumDuplicate": fixture.SetChecksums(string.Concat(Enumerable.Repeat(fixture.ChecksumText.Split('\n')[0] + "\n", 3))); break;
        }
        await Assert.ThrowsAsync<UpdateException>(() => fixture.Service.CheckAsync(new(0, 0, 1), default));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(204)]
    [InlineData(206)]
    public async Task HttpFailuresAreExplicitAndNeverUpToDate(int code)
    {
        using var fixture = new Fixture();
        fixture.Routes[Fixture.ListUrl] = () => new((HttpStatusCode)code);
        using var controller = fixture.Controller();
        await controller.CheckAsync();
        Assert.Equal(UpdateStatus.Error, controller.State.Status);
        Assert.DoesNotContain("You're up to date", controller.State.Message);
        if (code is 403 or 429) Assert.Contains("rate limited", controller.State.Message);
    }

    [Fact]
    public async Task NetworkFailureIsExplicit()
    {
        using var fixture = new Fixture();
        fixture.BeforeRequest = (_, _) => throw new HttpRequestException("offline");
        using var controller = fixture.Controller();
        await controller.CheckAsync();
        Assert.Equal(UpdateStatus.Error, controller.State.Status);
        Assert.Contains("connection", controller.State.Message);
    }

    [Fact]
    public async Task TransportTimeoutIsNotMisreportedAsUserCancellation()
    {
        using var fixture = new Fixture();
        fixture.BeforeRequest = (_, _) => throw new TaskCanceledException("transport timeout");
        using var controller = fixture.Controller();
        await controller.CheckAsync();
        Assert.Equal(UpdateStatus.Error, controller.State.Status);
        Assert.Contains("timed out", controller.State.Message);
    }

    [Fact]
    public async Task MetadataWithoutContentLengthIsStillBounded()
    {
        using var fixture = new Fixture();
        fixture.Routes[Fixture.ListUrl] = () => Fixture.StreamResponse(new MemoryStream(new byte[2 * 1024 * 1024 + 1]), 8192);
        var exception = await Assert.ThrowsAsync<UpdateException>(() => fixture.CheckAsync());
        Assert.Contains("size limit", exception.Message);
    }

    [Fact]
    public async Task CompletePaginationSelectsByVersionNotListingOrder()
    {
        using var fixture = new Fixture();
        var ignored = fixture.AddRelease("99.0.0");
        ignored["draft"] = true;
        fixture.SetListing(Enumerable.Repeat(ignored, 20).ToArray());
        fixture.Routes[Fixture.Api + "/releases?per_page=20&page=2"] = () => new(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonArray(fixture.Release.DeepClone()).ToJsonString())
        };
        var release = await fixture.CheckAsync();
        Assert.Equal(new UpdateVersion(0, 1, 0), release.Version);
    }

    [Fact]
    public async Task TruncatedReleaseHistoryFailsInsteadOfClaimingUpToDate()
    {
        using var fixture = new Fixture();
        string page = new JsonArray(Enumerable.Range(0, 20).Select(_ => fixture.Release.DeepClone()).ToArray()).ToJsonString();
        for (int index = 1; index <= 10; index++)
            fixture.Routes[Fixture.Api + "/releases?per_page=20&page=" + index] =
                () => new(HttpStatusCode.OK) { Content = new StringContent(page) };
        var exception = await Assert.ThrowsAsync<UpdateException>(() => fixture.CheckAsync());
        Assert.Contains("lookup limit", exception.Message);
        Assert.Equal(10, fixture.Requests.Count);
    }

    [Fact]
    public async Task SharedControllerCoalescesBothEntryPointsAndAllOperations()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeRequest = async (_, token) => { entered.TrySetResult(); await unblock.Task.WaitAsync(token); };
        using var controller = fixture.Controller();
        var first = controller.CheckAsync();
        await entered.Task;
        Assert.Equal(UpdateStatus.Checking, controller.State.Status);
        Assert.True(controller.State.IsIndeterminate);
        Assert.Same(first, controller.CheckAsync());
        Assert.Same(first, controller.DownloadAsync());
        Assert.Same(first, controller.InstallAsync((_, _) => Task.FromResult(true)));
        Assert.Single(fixture.Requests);
        unblock.SetResult();
        await first;
        Assert.Equal(UpdateStatus.Available, controller.State.Status);
    }

    [Fact]
    public async Task BackgroundStagingSharesTheOperationAndOnlyRestartsAfterExplicitConsent()
    {
        using var fixture = new Fixture { Installed = true };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeRequest = async (request, token) =>
        {
            if (request.RequestUri!.AbsoluteUri != Fixture.ListUrl) return;
            entered.SetResult();
            await unblock.Task.WaitAsync(token);
        };
        string? installer = null;
        int shutdowns = 0;
        using var controller = fixture.Controller(shutdown: () => shutdowns++, restart: path =>
        {
            installer = path;
            return true;
        });
        try
        {
            Task staging = controller.StageInBackgroundAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(staging, controller.CheckAsync());
            unblock.SetResult();
            await staging;
            await controller.StageInBackgroundAsync();
            Assert.Equal(UpdateStatus.ReadyToInstall, controller.State.Status);
            Assert.Single(fixture.Requests, url => url == fixture.SetupUrl);
            Assert.Null(installer);
            Assert.Equal(0, shutdowns);

            await controller.InstallAsync((_, _) => Task.FromResult(false), restart: true);
            Assert.Equal(UpdateStatus.ReadyToInstall, controller.State.Status);
            Assert.Null(installer);
            await controller.InstallAsync((_, _) => Task.FromResult(true), restart: true);
            Assert.NotNull(installer);
            Assert.Equal(1, shutdowns);
        }
        finally
        {
            if (installer is not null)
            {
                File.Delete(installer);
                Directory.Delete(Path.GetDirectoryName(installer)!);
            }
        }
    }

    [Fact]
    public async Task PortableCanCheckManuallyButNeitherEntryPointCanStageInTheBackground()
    {
        using var fixture = new Fixture();
        using var controller = fixture.Controller();
        await controller.StageInBackgroundAsync();
        Assert.Empty(fixture.Requests);
        Assert.Equal(UpdateStatus.Idle, controller.State.Status);

        var release = await fixture.CheckAsync();
        await Assert.ThrowsAsync<UpdateException>(() =>
            fixture.Service.DownloadAsync(release, null, default, background: true));
        Assert.DoesNotContain(fixture.SetupUrl, fixture.Requests);
    }

    [Fact]
    public async Task TurningOffBackgroundUpdatesCancelsStagingWithoutCancellingManualChecks()
    {
        using var fixture = new Fixture { Installed = true };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeRequest = async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); };
        using var controller = fixture.Controller();
        Task staging = controller.StageInBackgroundAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        controller.CancelBackground();
        await staging;
        Assert.Equal(UpdateStatus.Cancelled, controller.State.Status);

        fixture.BeforeRequest = null;
        Task manual = controller.CheckAsync();
        controller.CancelBackground();
        await manual;
        Assert.Equal(UpdateStatus.Available, controller.State.Status);
        Assert.DoesNotContain(fixture.SetupUrl, fixture.Requests);
    }

    [Fact]
    public async Task CancellingTheSharedCheckReleasesSingleFlightForRetry()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeRequest = async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
        using var controller = fixture.Controller();
        var task = controller.CheckAsync();
        await entered.Task;
        controller.Cancel();
        await task;
        Assert.Equal(UpdateStatus.Cancelled, controller.State.Status);
        fixture.BeforeRequest = null;
        await controller.CheckAsync();
        Assert.Equal(UpdateStatus.Available, controller.State.Status);
    }

    [Fact]
    public async Task DownloadIsBoundedVerifiedAndReportsActualBytes()
    {
        using var fixture = new Fixture();
        var release = await fixture.CheckAsync();
        var updates = new List<UpdateProgress>();
        fixture.Routes[fixture.SetupUrl] = () => Fixture.StreamResponse(new MemoryStream(fixture.SetupBytes), 7777);
        using var download = await fixture.Service.DownloadAsync(release, new InlineProgress(updates.Add), default);
        Assert.Equal(fixture.SetupBytes, await File.ReadAllBytesAsync(download.InstallerPath));
        Assert.Equal(fixture.SetupBytes.Length, updates.Last().BytesReceived);
        Assert.Contains(updates, p => p.Status == UpdateStatus.Downloading && p.BytesReceived > 0 && p.BytesReceived < fixture.SetupBytes.Length);
        Assert.Equal(UpdateStatus.Verifying, updates.Last().Status);
        Assert.Equal(1d, updates.Last().Fraction);
        Assert.All(updates, p => Assert.Equal(fixture.SetupBytes.Length, p.TotalBytes));
        Assert.False(File.Exists(download.InstallerPath + ".partial"));
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("overflow")]
    [InlineData("hash")]
    [InlineData("length")]
    [InlineData("network")]
    public async Task DownloadFailureCleansOnlyOwnedTemporaryFiles(string failure)
    {
        using var fixture = new Fixture();
        var release = await fixture.CheckAsync();
        var before = Directory.GetDirectories(Path.GetTempPath(), "MissionView-update-*").ToHashSet();
        switch (failure)
        {
            case "truncated": fixture.Routes[fixture.SetupUrl] = () => Fixture.StreamResponse(new MemoryStream(fixture.SetupBytes[..^1]), 8192); break;
            case "overflow": fixture.Routes[fixture.SetupUrl] = () => Fixture.StreamResponse(new MemoryStream([.. fixture.SetupBytes, 0]), 8192); break;
            case "hash": fixture.SetBytes(fixture.SetupUrl, new byte[fixture.SetupBytes.Length]); break;
            case "length": fixture.SetBytes(fixture.SetupUrl, new byte[4]); break;
            case "network": fixture.Routes[fixture.SetupUrl] = () => throw new HttpRequestException("offline"); break;
        }
        if (failure == "network")
            await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Service.DownloadAsync(release, null, default));
        else
            await Assert.ThrowsAsync<UpdateException>(() => fixture.Service.DownloadAsync(release, null, default));
        Assert.Empty(Directory.GetDirectories(Path.GetTempPath(), "MissionView-update-*").Except(before));
    }

    [Fact]
    public async Task CancelDuringDownloadRemovesPartialAndDoesNotLaunchOrShutdown()
    {
        using var fixture = new Fixture();
        var blocked = new BlockingStream();
        fixture.Routes[fixture.SetupUrl] = () => Fixture.StreamResponse(blocked, 8192);
        var before = Directory.GetDirectories(Path.GetTempPath(), "MissionView-update-*").ToHashSet();
        int launches = 0, shutdowns = 0;
        using var controller = fixture.Controller(() => shutdowns++, _ => { launches++; return true; });
        await controller.CheckAsync();
        var task = controller.DownloadAsync();
        await blocked.Entered.Task;
        controller.Cancel();
        await task;
        Assert.Equal(UpdateStatus.Cancelled, controller.State.Status);
        Assert.Equal(0, launches);
        Assert.Equal(0, shutdowns);
        Assert.True(blocked.Disposed);
        Assert.Empty(Directory.GetDirectories(Path.GetTempPath(), "MissionView-update-*").Except(before));
    }

    [Theory]
    [InlineData("http://release-assets.githubusercontent.com/github-production-release-asset/a")]
    [InlineData("https://evil.example/github-production-release-asset/a")]
    [InlineData("https://release-assets.githubusercontent.com.evil.example/github-production-release-asset/a")]
    [InlineData("https://user@release-assets.githubusercontent.com/github-production-release-asset/a")]
    [InlineData("https://release-assets.githubusercontent.com:444/github-production-release-asset/a")]
    [InlineData("https://release-assets.githubusercontent.com/other/a")]
    [InlineData("https://github.com/other/repo/releases/download/v1/setup.exe")]
    public async Task RejectsMaliciousRedirectBeforeSendingIt(string destination)
    {
        using var fixture = new Fixture();
        var release = await fixture.CheckAsync();
        fixture.Redirect(fixture.SetupUrl, destination);
        await Assert.ThrowsAsync<UpdateException>(() => fixture.Service.DownloadAsync(release, null, default));
        Assert.DoesNotContain(destination, fixture.Requests);
    }

    [Fact]
    public async Task AllowsOnlyValidatedGithubCdnHops()
    {
        using var fixture = new Fixture();
        var release = await fixture.CheckAsync();
        const string cdn = "https://release-assets.githubusercontent.com/github-production-release-asset/1/2?signature=synthetic";
        fixture.Redirect(fixture.SetupUrl, cdn);
        fixture.SetBytes(cdn, fixture.SetupBytes);
        using var download = await fixture.Service.DownloadAsync(release, null, default);
        Assert.Contains(cdn, fixture.Requests);
    }

    [Fact]
    public async Task RejectsApiRedirectAndCdnRedirectLoop()
    {
        using var fixture = new Fixture();
        var release = await fixture.CheckAsync();
        const string cdn = "https://release-assets.githubusercontent.com/github-production-release-asset/loop";
        fixture.Redirect(fixture.SetupUrl, cdn);
        fixture.Redirect(cdn, cdn);
        await Assert.ThrowsAsync<UpdateException>(() => fixture.Service.DownloadAsync(release, null, default));
        fixture.Redirect(Fixture.ListUrl, "https://evil.example/");
        await Assert.ThrowsAsync<UpdateException>(() => fixture.CheckAsync());
        Assert.DoesNotContain("https://evil.example/", fixture.Requests);
    }

    [Fact]
    public async Task InstallerRequiresConsentAndSuccessfulLaunchBeforeShutdown()
    {
        using var fixture = new Fixture();
        var calls = new List<string>();
        string? installer = null;
        using var controller = fixture.Controller(() => calls.Add("shutdown"), path =>
        {
            installer = path;
            Assert.True(File.Exists(path));
            calls.Add("launch");
            return true;
        });
        Assert.False(controller.HasInstallerStarted);
        await controller.CheckAsync();
        await controller.DownloadAsync();
        await controller.InstallAsync((_, _) => { calls.Add("decline"); return Task.FromResult(false); });
        Assert.Equal(UpdateStatus.ReadyToInstall, controller.State.Status);
        Assert.Equal(["decline"], calls);
        await controller.InstallAsync((_, _) => { calls.Add("approve"); return Task.FromResult(true); });
        Assert.Equal(["decline", "approve", "launch", "shutdown"], calls);
        Assert.Equal(UpdateStatus.InstallerStarted, controller.State.Status);
        Assert.True(controller.HasInstallerStarted);
        Assert.Contains("not yet confirmed", controller.State.Message);
        var stateAfterLaunch = controller.State;
        int requestsAfterLaunch = fixture.Requests.Count;
        try
        {
            var repeatedCheck = await Assert.ThrowsAsync<UpdateException>(() => controller.CheckAsync());
            var repeatedDownload = await Assert.ThrowsAsync<UpdateException>(() => controller.DownloadAsync());
            var repeatedInstall = await Assert.ThrowsAsync<UpdateException>(() =>
                controller.InstallAsync((_, _) => throw new Exception("must not prompt or launch twice")));
            Assert.All(new[] { repeatedCheck, repeatedDownload, repeatedInstall },
                error => Assert.Contains("already started", error.Message));
            Assert.Equal(4, calls.Count);
            Assert.Equal(requestsAfterLaunch, fixture.Requests.Count);
            Assert.Same(stateAfterLaunch, controller.State);
        }
        finally
        {
            controller.Dispose();
            Assert.True(File.Exists(installer));
            File.Delete(installer!);
            Directory.Delete(Path.GetDirectoryName(installer!)!);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaunchFailureOrUacCancellationNeverShutsDown(bool throws)
    {
        using var fixture = new Fixture();
        int shutdowns = 0;
        using var controller = fixture.Controller(() => shutdowns++, _ =>
            throws ? throw new System.ComponentModel.Win32Exception(1223) : false);
        await controller.CheckAsync();
        await controller.DownloadAsync();
        await controller.InstallAsync((_, _) => Task.FromResult(true));
        Assert.Equal(UpdateStatus.Error, controller.State.Status);
        Assert.False(controller.HasInstallerStarted);
        Assert.Equal(0, shutdowns);
        Assert.Contains("unchanged", controller.State.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RechecksDownloadedInstallerAfterConsentAndRejectsTampering(bool background)
    {
        using var fixture = new Fixture { Installed = background };
        int launches = 0;
        bool Launch(string _) { launches++; return true; }
        using var controller = fixture.Controller(start: Launch, restart: Launch);
        var before = Directory.GetDirectories(Path.GetTempPath(), "MissionView-update-*").ToHashSet();
        if (background) await controller.StageInBackgroundAsync();
        else
        {
            await controller.CheckAsync();
            await controller.DownloadAsync();
        }
        string directory = Assert.Single(Directory.GetDirectories(Path.GetTempPath(), "MissionView-update-*").Except(before));
        await controller.InstallAsync(async (_, _) =>
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, fixture.SetupName), new byte[fixture.SetupBytes.Length]);
            return true;
        }, restart: background);
        Assert.Equal(UpdateStatus.Error, controller.State.Status);
        Assert.Contains("checksum changed", controller.State.Message);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task CancellationWhileConfirmingNeverLaunchesTheInstaller()
    {
        using var fixture = new Fixture();
        int launches = 0, shutdowns = 0;
        using var controller = fixture.Controller(() => shutdowns++, _ => { launches++; return true; });
        await controller.CheckAsync();
        await controller.DownloadAsync();
        await controller.InstallAsync((_, _) =>
        {
            controller.Cancel();
            return Task.FromResult(true);
        });
        Assert.Equal(UpdateStatus.Cancelled, controller.State.Status);
        Assert.Equal(0, launches);
        Assert.Equal(0, shutdowns);
    }

    [Fact]
    public async Task InvalidCommandOrderIsAnExplicitErrorAndCanRecover()
    {
        using var fixture = new Fixture();
        using var controller = fixture.Controller();
        await controller.DownloadAsync();
        Assert.Equal(UpdateStatus.Error, controller.State.Status);
        await controller.InstallAsync((_, _) => Task.FromResult(true));
        Assert.Equal(UpdateStatus.Error, controller.State.Status);
        Assert.Empty(fixture.Requests);
        await controller.CheckAsync();
        Assert.Equal(UpdateStatus.Available, controller.State.Status);
    }

    [Fact]
    public async Task DispatcherFailureCannotCompleteTheOperationAsSuccess()
    {
        using var fixture = new Fixture();
        using var controller = new UpdateController(fixture.Service, new(0, 0, 1), () => { },
            startInstaller: _ => throw new InvalidOperationException("Real installer forbidden in tests."),
            supportedPlatform: () => true,
            uiContext: new RejectedContext());
        await Assert.ThrowsAsync<AggregateException>(() => controller.CheckAsync());
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task UnsupportedPlatformDoesNotDownloadOrInstall()
    {
        using var fixture = new Fixture();
        using var controller = fixture.Controller(platform: () => false);
        await controller.CheckAsync();
        await controller.DownloadAsync();
        Assert.Equal(UpdateStatus.Error, controller.State.Status);
        Assert.DoesNotContain(fixture.SetupUrl, fixture.Requests);
    }

    [Fact]
    public async Task NotificationsConfirmationAndShutdownRunOnUiWhileLaunchDoesNotBlockDispatcher()
    {
        using var ui = new UiContext();
        using var fixture = new Fixture();
        var threads = new ConcurrentBag<int>();
        int progressEvents = 0;
        int startedNotifications = 0;
        string? installer = null;
        int launchThread = -1;
        UpdateController? controller = null;
        await ui.InvokeAsync(() =>
        {
            controller = fixture.Controller(() => threads.Add(Environment.CurrentManagedThreadId), path =>
            {
                installer = path;
                launchThread = Environment.CurrentManagedThreadId;
                return true;
            });
            controller.PropertyChanged += (_, e) =>
            {
                Assert.Contains(e.PropertyName, new[] { "State", "HasInstallerStarted" });
                if (e.PropertyName == "HasInstallerStarted")
                {
                    Assert.True(controller.HasInstallerStarted);
                    startedNotifications++;
                }
                threads.Add(Environment.CurrentManagedThreadId);
            };
            controller.ProgressChanged += (_, _) => { progressEvents++; threads.Add(Environment.CurrentManagedThreadId); };
        });
        var updates = Assert.IsType<UpdateController>(controller);
        using (updates)
        {
            await updates.CheckAsync();
            await updates.DownloadAsync();
            await updates.InstallAsync((release, _) =>
            {
                threads.Add(Environment.CurrentManagedThreadId);
                return Task.FromResult(true);
            });
            Assert.NotEmpty(threads);
            Assert.True(progressEvents > 0);
            Assert.Equal(1, startedNotifications);
            Assert.All(threads, id => Assert.Equal(ui.ThreadId, id));
            Assert.NotEqual(ui.ThreadId, launchThread);
            Assert.Equal(UpdateStatus.InstallerStarted, updates.State.Status);
        }
        File.Delete(installer!);
        Directory.Delete(Path.GetDirectoryName(installer!)!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownFailureAfterLaunchIsNotReportedAsAnUnchangedInstallationOrRelaunched(bool applicationException)
    {
        using var fixture = new Fixture();
        int launches = 0;
        string? installer = null;
        using var controller = fixture.Controller(() => throw (applicationException
            ? new ApplicationException("shutdown failed") : new InvalidOperationException("dispatcher unavailable")), path =>
        {
            installer = path;
            launches++;
            return true;
        });
        await controller.CheckAsync();
        await controller.DownloadAsync();
        await controller.InstallAsync((_, _) => Task.FromResult(true));
        Assert.Equal(UpdateStatus.Error, controller.State.Status);
        Assert.True(controller.HasInstallerStarted);
        Assert.Contains("installer started", controller.State.Message);
        Assert.DoesNotContain("unchanged", controller.State.Message);
        var shutdownError = controller.State;
        int requestsAfterLaunch = fixture.Requests.Count;
        try
        {
            await Assert.ThrowsAsync<UpdateException>(() => controller.CheckAsync());
            await Assert.ThrowsAsync<UpdateException>(() => controller.DownloadAsync());
            await Assert.ThrowsAsync<UpdateException>(() =>
                controller.InstallAsync((_, _) => throw new Exception("must not prompt twice")));
            Assert.Equal(1, launches);
            Assert.Equal(requestsAfterLaunch, fixture.Requests.Count);
            Assert.Same(shutdownError, controller.State);
        }
        finally
        {
            controller.Dispose();
            File.Delete(installer!);
            Directory.Delete(Path.GetDirectoryName(installer!)!);
        }
    }

    [Fact]
    public async Task ShutdownCanDisposeControllerWithoutDeletingLaunchedInstallerOrAllowingReentrantUpdates()
    {
        using var fixture = new Fixture();
        using var ui = new UiContext();
        int launches = 0, shutdowns = 0;
        string? installer = null;
        Task? reentrantCheck = null;
        UpdateController? controller = null;
        await ui.InvokeAsync(() => controller = fixture.Controller(() =>
        {
            shutdowns++;
            Assert.True(controller!.HasInstallerStarted);
            reentrantCheck = controller.CheckAsync();
            controller.Dispose();
        }, path =>
        {
            installer = path;
            launches++;
            return true;
        }));
        var updates = Assert.IsType<UpdateController>(controller);
        try
        {
            await updates.CheckAsync();
            await updates.DownloadAsync();
            await updates.InstallAsync((_, _) => Task.FromResult(true));
            Assert.Equal(UpdateStatus.InstallerStarted, updates.State.Status);
            Assert.True(updates.HasInstallerStarted);
            Assert.Equal(1, launches);
            Assert.Equal(1, shutdowns);
            Assert.NotNull(reentrantCheck);
            await Assert.ThrowsAsync<UpdateException>(() => reentrantCheck!);
            await Assert.ThrowsAsync<UpdateException>(() => updates.CheckAsync());
            await Assert.ThrowsAsync<UpdateException>(() =>
                updates.InstallAsync((_, _) => throw new Exception("must not prompt twice")));
            Assert.True(File.Exists(installer));
            Assert.Equal(1, launches);
        }
        finally
        {
            updates.Dispose();
            if (installer is not null)
            {
                File.Delete(installer);
                Directory.Delete(Path.GetDirectoryName(installer)!);
            }
        }
    }

    [Fact]
    public async Task DisposalRacingOperationCompletionReleasesTheServiceExactlyOnce()
    {
        int leaks = 0, duplicates = 0;
        for (int iteration = 0; iteration < 20000; iteration++)
        {
            using var completedState = new ManualResetEventSlim();
            var service = new DisposalProbe();
            using var controller = service.Controller();
            controller.PropertyChanged += (_, _) =>
            {
                if (controller.State.Status == UpdateStatus.UpToDate) completedState.Set();
            };
            Task operation = controller.CheckAsync();
            Assert.True(completedState.Wait(TimeSpan.FromSeconds(10)));
            Thread.SpinWait(iteration % 30);
            controller.Dispose();
            await operation;
            if (service.Disposals == 0) leaks++;
            if (service.Disposals > 1) duplicates++;
        }
        Assert.True(leaks == 0 && duplicates == 0,
            $"Completion/disposal races leaked {leaks} services and disposed {duplicates} services twice.");
    }

    [Fact]
    public async Task FailedTemporaryCleanupStillDisposesTheServiceAndReportsTheFailure()
    {
        var service = new DisposalProbe { HasRelease = true, FailCleanup = true };
        using var controller = service.Controller();
        await controller.CheckAsync();
        await controller.DownloadAsync();
        Assert.Equal(UpdateStatus.ReadyToInstall, controller.State.Status);

        var error = Assert.Throws<IOException>(() => controller.Dispose());
        Assert.Contains("Synthetic locked installer", error.Message);
        Assert.Equal(1, service.Cleanups);
        Assert.Equal(1, service.Disposals);
        controller.Dispose();
        Assert.Equal(1, service.Disposals);
    }

    private sealed class DisposalProbe : IUpdateService
    {
        internal int Disposals, Cleanups;
        internal bool HasRelease { get; init; }
        internal bool FailCleanup { get; init; }
        public bool IsInstalled => false;
        private readonly UpdateRelease release = new(new(0, 1, 0), new string('a', 40),
            new("synthetic-not-an-executable", new Uri("https://example.invalid/synthetic"), 1, new string('a', 64)));

        internal UpdateController Controller() => new(this, default,
            () => throw new Exception("Shutdown is forbidden in disposal tests."),
            _ => throw new Exception("Process launch is forbidden in disposal tests."),
            new InlineContext(), () => true);

        public Task<UpdateRelease?> CheckAsync(UpdateVersion current, CancellationToken token) =>
            Task.FromResult<UpdateRelease?>(HasRelease ? release : null);

        public Task<VerifiedUpdate> DownloadAsync(UpdateRelease value, IProgress<UpdateProgress>? progress,
            CancellationToken token, bool background) =>
            Task.FromResult(new VerifiedUpdate("synthetic-no-directory", "synthetic-no-file", value, () =>
            {
                Interlocked.Increment(ref Cleanups);
                if (FailCleanup) throw new IOException("Synthetic locked installer cleanup failure.");
            }));

        public Task<IAsyncDisposable> LockVerifiedInstallerAsync(VerifiedUpdate update, CancellationToken token) =>
            throw new Exception("File effects are forbidden in disposal tests.");

        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    private sealed class InlineContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    private sealed class InlineProgress(Action<UpdateProgress> action) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => action(value);
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Api = "https://api.github.com/repos/JUANES545/missionview";
        internal const string ListUrl = Api + "/releases?per_page=20&page=1";
        internal const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string TagSha = "cccccccccccccccccccccccccccccccccccccccc";
        internal Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new();
        internal ConcurrentBag<string> Requests { get; } = new();
        internal Func<HttpRequestMessage, CancellationToken, Task>? BeforeRequest { get; set; }
        internal UpdateService Service { get; }
        internal bool Installed { get; init; }
        internal string Version { get; }
        internal JsonObject Release { get; }
        internal JsonObject Reference { get; }
        internal JsonObject Annotation { get; }
        internal JsonObject CommitObject { get; } = new() { ["sha"] = Commit };
        internal JsonObject Manifest { get; }
        internal byte[] ManifestBytes { get; private set; } = [];
        internal byte[] SetupBytes { get; } = Enumerable.Range(0, 200000).Select(i => (byte)(i % 251)).ToArray();
        internal string SetupName => $"MissionView-{Version}-Setup.exe";
        internal string SetupUrl => AssetUrl(SetupName);
        internal string ManifestUrl => AssetUrl("release-manifest.json");
        internal string ChecksumText { get; private set; } = "";

        internal Fixture(string version = "0.1.0")
        {
            Version = version;
            Service = new UpdateService(new Handler(this), () => Installed);
            Reference = new() { ["object"] = new JsonObject { ["type"] = "tag", ["sha"] = TagSha } };
            Annotation = new()
            {
                ["sha"] = TagSha, ["tag"] = "v" + version, ["message"] = $"MissionView release v{version}\n\nSource: {Commit}",
                ["object"] = new JsonObject { ["type"] = "commit", ["sha"] = Commit }
            };
            Release = AddRelease(version);
            SetJson(Api + "/git/ref/tags/v" + version, () => Reference);
            SetJson(Api + "/git/tags/" + TagSha, () => Annotation);
            SetJson(Api + "/git/commits/" + Commit, () => CommitObject);
            Manifest = new()
            {
                ["targetFramework"] = "net10.0-windows10.0.26100.0", ["version"] = version,
                ["selfContained"] = true, ["runtime"] = "win-x64", ["sourceSha"] = Commit
            };
            RebuildMetadata();
        }

        internal JsonObject AddRelease(string version, bool owned = true)
        {
            if (version != Version)
            {
                string sha = new string(owned ? 'd' : 'e', 40);
                SetJson(Api + "/git/ref/tags/v" + version, () => new JsonObject
                {
                    ["object"] = new JsonObject { ["type"] = "tag", ["sha"] = sha }
                });
                SetJson(Api + "/git/tags/" + sha, () => new JsonObject
                {
                    ["sha"] = sha, ["tag"] = "v" + version, ["message"] = owned ? $"MissionView release v{version}" : $"Upstream v{version}",
                    ["object"] = new JsonObject { ["type"] = "commit", ["sha"] = Commit }
                });
            }
            return new JsonObject
            {
                ["tag_name"] = "v" + version, ["draft"] = false, ["prerelease"] = false,
                ["html_url"] = $"https://github.com/JUANES545/missionview/releases/tag/v{version}",
                ["target_commitish"] = "main", ["assets"] = new JsonArray()
            };
        }

        internal void RebuildMetadata()
        {
            ManifestBytes = Encoding.UTF8.GetBytes(Manifest.ToJsonString());
            RebuildAssets();
        }

        internal void SetRawManifest(string text)
        {
            ManifestBytes = Encoding.UTF8.GetBytes(text);
            RebuildAssets();
        }

        private void RebuildAssets()
        {
            var payloads = new Dictionary<string, byte[]>
            {
                [SetupName] = SetupBytes,
                [$"MissionView-{Version}-win-x64.zip"] = [1, 2, 3],
                ["release-manifest.json"] = ManifestBytes
            };
            ChecksumText = string.Concat(payloads.Select(pair => Hash(pair.Value) + "  " + pair.Key + "\n"));
            payloads.Add("SHA256SUMS.txt", Encoding.UTF8.GetBytes(ChecksumText));
            var assets = new JsonArray();
            foreach (var (name, bytes) in payloads)
            {
                assets.Add(new JsonObject
                {
                    ["name"] = name, ["size"] = bytes.Length, ["digest"] = "sha256:" + Hash(bytes),
                    ["browser_download_url"] = AssetUrl(name)
                });
                SetBytes(AssetUrl(name), bytes);
            }
            Release["assets"] = assets;
            SetListing(Release);
        }

        internal void SetChecksums(string content)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            var asset = Release["assets"]!.AsArray().Single(a => a!["name"]!.GetValue<string>() == "SHA256SUMS.txt")!;
            asset["size"] = bytes.Length;
            asset["digest"] = "sha256:" + Hash(bytes);
            SetBytes(AssetUrl("SHA256SUMS.txt"), bytes);
            SetListing(Release);
        }

        internal void SetListing(params JsonObject[] releases) =>
            SetJson(ListUrl, () => new JsonArray(releases.Select(r => r.DeepClone()).ToArray()));

        internal void SetBytes(string url, byte[] bytes) => Routes[url] = () => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        private void SetJson(string url, Func<JsonNode> node) =>
            Routes[url] = () => new(HttpStatusCode.OK) { Content = new StringContent(node().ToJsonString()) };
        internal void Redirect(string url, string target) => Routes[url] = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(target);
            return response;
        };
        private string AssetUrl(string name) => $"https://github.com/JUANES545/missionview/releases/download/v{Version}/{name}";
        private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        internal async Task<UpdateRelease> CheckAsync() => (await Service.CheckAsync(new(0, 0, 1), default))!;
        internal UpdateController Controller(Action? shutdown = null, Func<string, bool>? start = null,
            Func<bool>? platform = null, Func<string, bool>? restart = null) =>
            new(Service, new(0, 0, 1), shutdown ?? (() => { }), start ?? (_ => throw new InvalidOperationException("Real installer forbidden in tests.")),
                uiContext: null,
                supportedPlatform: platform ?? (() => true), restartInstaller: restart);
        public void Dispose() => Service.Dispose();

        internal static HttpResponseMessage StreamResponse(Stream stream, int chunk) =>
            new(HttpStatusCode.OK) { Content = new UnknownLengthContent(new ChunkStream(stream, chunk)) };

        private sealed class Handler(Fixture fixture) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string url = request.RequestUri!.AbsoluteUri;
                fixture.Requests.Add(url);
                Assert.Null(request.Headers.Authorization);
                if (fixture.BeforeRequest is not null) await fixture.BeforeRequest(request, cancellationToken);
                Assert.True(fixture.Routes.TryGetValue(url, out var route), $"Unexpected HTTP request: {url}");
                var response = route!();
                response.RequestMessage = request;
                return response;
            }
        }
    }

    private sealed class UnknownLengthContent(Stream stream) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => stream.CopyToAsync(target);
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult(stream);
        protected override void Dispose(bool disposing) { if (disposing) stream.Dispose(); base.Dispose(disposing); }
    }

    private sealed class ChunkStream(Stream inner, int chunk) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, chunk));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer[..Math.Min(chunk, buffer.Length)], cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class BlockingStream : Stream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class UiContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();
        private readonly Thread thread;
        internal int ThreadId => thread.ManagedThreadId;
        internal UiContext()
        {
            thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var item in queue.GetConsumingEnumerable()) item.Callback(item.State);
            }) { IsBackground = true };
            thread.Start();
        }
        public override void Post(SendOrPostCallback d, object? state) => queue.Add((d, state));
        internal Task InvokeAsync(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(_ =>
            {
                try { action(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
            }, null);
            return completion.Task;
        }
        public void Dispose() { queue.CompleteAdding(); thread.Join(); queue.Dispose(); }
    }

    private sealed class RejectedContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) =>
            throw new InvalidOperationException("dispatcher unavailable");
    }
}
