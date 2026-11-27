using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowScatter;
using Xunit;

public sealed class UpdatePanelTests
{
    [Theory]
    [InlineData(@"D:\Custom\MissionView.exe", @"d:\custom\MissionView.exe", true)]
    [InlineData(@"D:\Portable\MissionView.exe", @"D:\Custom\MissionView.exe", false)]
    [InlineData(@"D:\Custom\MissionView.exe", @"D:\Custom\MissionView.exe,0", false)]
    public void SetupRegistrationMustMatchTheRunningExecutable(string executable, string registration, bool matches)
        => Assert.Equal(matches, UpdateInstaller.MatchesRegisteredExecutable(executable, registration));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AutomaticUpdatesCheckboxOnlyAppearsForRegisteredInstallations(bool installed)
        => RunSta(async () =>
        {
            using var fixture = new OfflineUpdates { Installed = installed };
            using var controller = fixture.Controller();
            var settings = new AppSettings();
            var panel = new UpdatePanel(controller, (_, _) => Task.FromResult(false), settings, candidate =>
            {
                settings.CopyFrom(candidate);
                return true;
            });
            panel.ActionRow.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            try
            {
                if (installed)
                {
                    var checkbox = Assert.Single(panel.AutomaticRow!.Children.OfType<CheckBox>());
                    Assert.Equal("Install updates automatically", AutomationProperties.GetAutomationId(checkbox));
                    Assert.Equal(AutomationProperties.GetAutomationId(checkbox), AutomationProperties.GetName(checkbox));
                    Assert.Equal(false, checkbox.IsChecked);
                    checkbox.IsChecked = true;
                    checkbox.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                    Assert.True(settings.InstallUpdatesAutomatically);
                }
                else Assert.Null(panel.AutomaticRow);

                await controller.CheckAsync();
                await controller.DownloadAsync();
                Assert.Equal(installed ? "Restart to update" : "Install update...", Action(panel).Content);
                Assert.Equal(0, fixture.Launches);
            }
            finally { Unload(panel); }
        });

    [Fact]
    public void ActionFailureBeforeTheControllerLeavesIdleIsVisibleAndAccessible()
        => RunSta(async () =>
        {
            using var fixture = new OfflineUpdates();
            using var controller = fixture.Controller();
            var panel = Panel(controller);
            var window = new SettingsWindow(new AppSettings(), _ => throw new Exception("Update error saved settings"), panel, () => true);
            var content = (UIElement)window.Content;
            window.Content = null;
            var root = new Border { Background = window.Background, Child = content };
            window.Content = root;
            window.ShowUpdates();
            try
            {
                var status = Status(panel);
                var peer = UIElementAutomationPeer.CreatePeerForElement(status)!;
                Assert.Equal(UpdateStatus.Idle, controller.State.Status);
                Assert.Equal(Visibility.Collapsed, status.Visibility);
                controller.Dispose();
                Click(Action(panel));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(UpdateStatus.Idle, controller.State.Status);
                Assert.Equal(Visibility.Visible, status.Visibility);
                Assert.StartsWith("The update action failed. ", status.Text);
                Assert.Same(window.Resources["FieldError"], status.Foreground);
                Assert.Equal(status.Text, peer.GetName());
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
                Assert.Equal(0, fixture.ListingRequests);
                Assert.Equal(0, fixture.Launches);
                Capture(root, "updates-action-failure-idle-offscreen.png");
            }
            finally { Unload(panel); window.Close(); }
        });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void UpdatePresentationDistinguishesCheckingAvailableUpToDateAndOfflineWithAccessibleActions(bool noRelease, bool offline)
        => RunSta(async () =>
        {
            using var fixture = new OfflineUpdates { HoldListing = true, FailListing = offline };
            using var controller = fixture.Controller(currentVersion: noRelease ? new(0, 2, 0) : new(0, 1, 0));
            var panel = Panel(controller);
            var window = new SettingsWindow(new AppSettings(), _ => throw new Exception("Update presentation saved settings"), panel, () => true);
            var content = (UIElement)window.Content;
            window.Content = null;
            var root = new Border { Background = window.Background, Child = content };
            window.Content = root;
            window.ShowUpdates();
            try
            {
                Capture(root, $"updates-idle-{noRelease}-{offline}-offscreen.png");
                Task check = controller.CheckAsync();
                await Until(controller, state => state.Status == UpdateStatus.Checking);
                Assert.Equal("Checking...", Status(panel).Text);
                Assert.False(Action(panel).IsEnabled);
                Assert.Equal(Visibility.Visible, Cancel(panel).Visibility);
                Capture(root, $"updates-checking-{noRelease}-{offline}-offscreen.png");
                fixture.ListingGate.SetResult();
                await check;
                Assert.Equal(offline ? UpdateStatus.Error : noRelease ? UpdateStatus.UpToDate : UpdateStatus.Available, controller.State.Status);
                Assert.True(Action(panel).IsEnabled);
                Assert.Equal(Visibility.Collapsed, Cancel(panel).Visibility);
                Assert.Equal(offline || noRelease ? "Check for updates" : "Download update", Action(panel).Content);
                Assert.Equal(offline ? controller.State.Message : noRelease ? "You're up to date." : "Version 0.2.0 available.", Status(panel).Text);
                Assert.Same(window.Resources[offline ? "FieldError" : noRelease ? "FieldFocus" : "FieldForeground"], Status(panel).Foreground);
                Assert.Same(window.Resources["FieldSelection"], Action(panel).Background);
                Assert.Same(window.Resources["FieldSelectionText"], Action(panel).Foreground);
                Capture(root, $"updates-result-{noRelease}-{offline}-offscreen.png");
                SettingsControlStyles.Apply(window.Resources, true);
                root.UpdateLayout();
                Assert.Equal(SystemColors.HighlightColor, ((SolidColorBrush)Action(panel).Background).Color);
                Assert.Equal(SystemColors.HighlightTextColor, ((SolidColorBrush)Action(panel).Foreground).Color);
                SettingsControlStyles.Apply(window.Resources, false);
                Assert.Equal(0, fixture.Launches);
            }
            finally { Unload(panel); window.Close(); }
        });

    [Fact]
    public void IdleShowsOneCheckActionWithoutRepeatingItAsStatus()
        => RunSta(() =>
        {
            using var fixture = new OfflineUpdates();
            using var controller = fixture.Controller();
            var panel = Panel(controller);
            try
            {
                Assert.Equal("Check for updates", Action(panel).Content);
                Assert.Equal("", Status(panel).Text);
                Assert.Equal(Visibility.Collapsed, Status(panel).Visibility);
                Assert.True(Action(panel).IsEnabled);
                Assert.Equal(0, fixture.ListingRequests);
            }
            finally { Unload(panel); }
            return Task.CompletedTask;
        });

    [Fact]
    public void SharedControllerCoalescesTwoConsumersAndPublishesStateOnDispatcher()
        => RunSta(async () =>
        {
            using var fixture = new OfflineUpdates { HoldListing = true };
            using var controller = fixture.Controller();
            var first = Panel(controller);
            var second = Panel(controller);
            int thread = Environment.CurrentManagedThreadId;
            var notifications = new List<int>();
            controller.PropertyChanged += (_, _) => notifications.Add(Environment.CurrentManagedThreadId);
            try
            {
                var checking = Until(controller, state => state.Status == UpdateStatus.Checking);
                Click(Action(first));
                Task shared = controller.CheckAsync();
                await checking;
                Assert.False(Action(first).IsEnabled);
                Assert.False(Action(second).IsEnabled);
                Assert.True(Progress(first).IsIndeterminate == SystemParameters.ClientAreaAnimation);
                Assert.Equal("Checking...", Status(second).Text);
                Click(Action(second));
                Assert.Same(shared, controller.CheckAsync());
                fixture.ListingGate.SetResult();
                await shared;
                Assert.Equal(1, fixture.ListingRequests);
                Assert.Equal(UpdateStatus.Available, controller.State.Status);
                Assert.Equal(Status(first).Text, Status(second).Text);
                Assert.True(Action(first).IsEnabled);
                Assert.True(Action(second).IsEnabled);
                Assert.NotEmpty(notifications);
                Assert.All(notifications, id => Assert.Equal(thread, id));
            }
            finally { Unload(first, second); }
        });

    [Fact]
    public void DownloadDisplaysMeasuredProgressAndSharesTheExistingOperation()
        => RunSta(async () =>
        {
            using var fixture = new OfflineUpdates { HoldDownload = true };
            using var controller = fixture.Controller();
            var panel = Panel(controller);
            try
            {
                await controller.CheckAsync();
                var half = Until(controller, state => state.Status == UpdateStatus.Downloading && state.Progress == 0.5);
                Click(Action(panel));
                Task download = controller.DownloadAsync();
                await half;
                Assert.Equal(0.5, Progress(panel).Value);
                Assert.False(Progress(panel).IsIndeterminate);
                Assert.Equal(Visibility.Visible, Progress(panel).Visibility);
                Assert.False(Action(panel).IsEnabled);
                Assert.Equal(Visibility.Visible, Cancel(panel).Visibility);
                Assert.Equal(controller.State.Message, Status(panel).Text);
                Click(Action(panel));
                Assert.Same(download, controller.DownloadAsync());
                fixture.DownloadGate.SetResult();
                await download;
                Assert.Equal(1, fixture.InstallerRequests);
                Assert.Equal(UpdateStatus.ReadyToInstall, controller.State.Status);
                Assert.True(Action(panel).IsEnabled);
                Assert.Equal(Visibility.Collapsed, Progress(panel).Visibility);
                Assert.Equal(Visibility.Collapsed, Cancel(panel).Visibility);
            }
            finally { Unload(panel); }
        });

    [Fact]
    public void InstallerStartedWithShutdownErrorKeepsErrorVisibleAndDisablesNewActions()
        => RunSta(async () =>
        {
            using var fixture = new OfflineUpdates();
            using var controller = fixture.Controller(() => throw new InvalidOperationException("Synthetic shutdown failure."));
            var panel = Panel(controller);
            try
            {
                await controller.CheckAsync();
                await controller.DownloadAsync();
                await controller.InstallAsync((_, _) => Task.FromResult(true));
                Assert.True(controller.HasInstallerStarted);
                Assert.Equal(UpdateStatus.Error, controller.State.Status);
                string error = controller.State.Message;
                Assert.Equal(error, Status(panel).Text);
                Assert.False(Action(panel).IsEnabled);
                Assert.Equal(Visibility.Collapsed, Cancel(panel).Visibility);
                Click(Action(panel));
                await Assert.ThrowsAsync<UpdateException>(() => controller.CheckAsync());
                Assert.Equal(error, Status(panel).Text);
                Assert.Equal(1, fixture.InstallerRequests);
                Assert.Equal(1, fixture.Launches);
            }
            finally { Unload(panel); }
        });

    private static UpdatePanel Panel(UpdateController controller)
    {
        var panel = new UpdatePanel(controller, (_, _) => Task.FromResult(false));
        panel.ActionRow.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        return panel;
    }

    private static Button Action(UpdatePanel panel) => panel.ActionRow.Children.OfType<Button>().First();
    private static Button Cancel(UpdatePanel panel) => panel.ActionRow.Children.OfType<Button>().Last();
    private static TextBlock Status(UpdatePanel panel) => panel.ActionRow.Children.OfType<TextBlock>().Single();
    private static ProgressBar Progress(UpdatePanel panel) => panel.ActionRow.Children.OfType<ProgressBar>().Single();
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    private static void Unload(params UpdatePanel[] panels)
    {
        foreach (var panel in panels) panel.ActionRow.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    }

    private static void Capture(FrameworkElement root, string name)
    {
        root.Measure(new Size(720, 480));
        root.Arrange(new Rect(0, 0, 720, 480));
        root.UpdateLayout();
        string? directory = Environment.GetEnvironmentVariable("MISSIONVIEW_SETTINGS_FIXTURES");
        if (directory == null) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(720, 480, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name));
        encoder.Save(output);
    }

    private static async Task Until(UpdateController controller, Func<UpdateState, bool> predicate)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        System.ComponentModel.PropertyChangedEventHandler handler = (_, _) =>
        {
            if (predicate(controller.State)) completion.TrySetResult();
        };
        controller.PropertyChanged += handler;
        try
        {
            if (predicate(controller.State)) return;
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { controller.PropertyChanged -= handler; }
    }

    private static void RunSta(Func<Task> test)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new System.Action(async () =>
            {
                try { await test(); }
                catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
                finally { dispatcher.InvokeShutdown(); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The isolated UI fixture did not finish.");
        error?.Throw();
    }

    private sealed class OfflineUpdates : IDisposable
    {
        private const string Api = "https://api.github.com/repos/JUANES545/missionview";
        private const string Downloads = "https://github.com/JUANES545/missionview/releases/download/v0.2.0/";
        private const string Setup = "MissionView-0.2.0-Setup.exe";
        private readonly Dictionary<string, byte[]> responses = new();
        private readonly UpdateService service;
        private string? launchedPath;
        internal bool HoldListing { get; init; }
        internal bool FailListing { get; init; }
        internal bool HoldDownload { get; init; }
        internal bool Installed { get; init; }
        internal TaskCompletionSource ListingGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DownloadGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ListingRequests, InstallerRequests, Launches;

        internal OfflineUpdates()
        {
            string tagSha = new('a', 40), commit = new('b', 40);
            byte[] installer = Encoding.UTF8.GetBytes(new string('x', 8192));
            var assets = new Dictionary<string, byte[]>
            {
                [Setup] = installer,
                ["MissionView-0.2.0-win-x64.zip"] = [1, 2, 3],
                ["release-manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    version = "0.2.0", sourceSha = commit, runtime = "win-x64",
                    selfContained = true, targetFramework = "net10.0-windows10.0.26100.0"
                })
            };
            assets["SHA256SUMS.txt"] = Encoding.UTF8.GetBytes(string.Join("", assets.Select(asset =>
                $"{Convert.ToHexStringLower(SHA256.HashData(asset.Value))}  {asset.Key}\n")));
            foreach (var asset in assets) responses.Add(Downloads + asset.Key, asset.Value);
            responses.Add(Api + "/releases?per_page=20&page=1", JsonSerializer.SerializeToUtf8Bytes(new[]
            {
                new
                {
                    draft = false, prerelease = false, tag_name = "v0.2.0",
                    html_url = "https://github.com/JUANES545/missionview/releases/tag/v0.2.0",
                    assets = assets.Select(asset => new
                    {
                        name = asset.Key, browser_download_url = Downloads + asset.Key, size = asset.Value.Length,
                        digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(asset.Value))
                    }).ToArray()
                }
            }));
            responses.Add(Api + "/git/ref/tags/v0.2.0", JsonSerializer.SerializeToUtf8Bytes(new { @object = new { type = "tag", sha = tagSha } }));
            responses.Add(Api + "/git/tags/" + tagSha, JsonSerializer.SerializeToUtf8Bytes(new
            {
                sha = tagSha, tag = "v0.2.0", message = "MissionView release v0.2.0",
                @object = new { type = "commit", sha = commit }
            }));
            responses.Add(Api + "/git/commits/" + commit, JsonSerializer.SerializeToUtf8Bytes(new { sha = commit }));
            service = new UpdateService(new Handler(this), () => Installed);
        }

        internal UpdateController Controller(System.Action? shutdown = null, UpdateVersion? currentVersion = null) => new(service, currentVersion ?? new(0, 1, 0), shutdown ?? (() => { }),
            path => { launchedPath = path; Launches++; return true; }, SynchronizationContext.Current, () => true);

        public void Dispose()
        {
            service.Dispose();
            if (launchedPath != null)
            {
                File.Delete(launchedPath);
                Directory.Delete(Path.GetDirectoryName(launchedPath)!);
            }
        }

        private sealed class Handler(OfflineUpdates fixture) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string url = request.RequestUri!.AbsoluteUri;
                if (!fixture.responses.TryGetValue(url, out byte[]? body))
                    throw new InvalidOperationException("Unexpected offline request: " + url);
                if (url.Contains("/releases?"))
                {
                    Interlocked.Increment(ref fixture.ListingRequests);
                    if (fixture.HoldListing) await fixture.ListingGate.Task.WaitAsync(cancellationToken);
                    if (fixture.FailListing) throw new HttpRequestException("Synthetic offline connection failure.");
                }
                bool installer = url == Downloads + Setup;
                if (installer) Interlocked.Increment(ref fixture.InstallerRequests);
                HttpContent content = installer && fixture.HoldDownload
                    ? new StreamContent(new PausedInstaller(body, fixture.DownloadGate.Task))
                    : new ByteArrayContent(body);
                content.Headers.ContentLength = body.Length;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request };
            }
        }

        private sealed class PausedInstaller(byte[] bytes, Task release) : MemoryStream(bytes, writable: false)
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (Position >= Length / 2) await release.WaitAsync(cancellationToken);
                return await base.ReadAsync(buffer[..Math.Min(buffer.Length, (int)(Length / 2))], cancellationToken);
            }
        }
    }
}
