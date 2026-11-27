using System.Text.Json;
using WindowScatter;
using Xunit;

public sealed class AppSettingsTests : IDisposable
{
    [Fact]
    public void BackgroundUpdatesDefaultOffAndUseTransactionalPersistence()
    {
        Assert.False(new AppSettings().InstallUpdatesAutomatically);
        Put(Legacy, Profile);
        var settings = Store.Load();
        Assert.False(settings.InstallUpdatesAutomatically);

        var candidate = settings.Copy();
        candidate.InstallUpdatesAutomatically = true;
        var preferences = new PreferencesService(settings, Store.Save);
        Assert.False(preferences.TryApply(candidate, () => throw new IOException("Synthetic apply failure"),
            () => { }, error => Assert.IsType<IOException>(error)));
        Assert.False(settings.InstallUpdatesAutomatically);
        Assert.False(Store.Load().InstallUpdatesAutomatically);
        Assert.True(preferences.TryApply(candidate, () => { }, () => { }, error => throw error));
        Assert.True(Store.Load().InstallUpdatesAutomatically);
    }

    [Fact]
    public void GeneralOverviewMinimizedDefaultsOnAndAnExplicitFalsePersistsIndependently()
    {
        Assert.True(new AppSettings().ShowMinimizedWindowsInOverview);
        Put(Current, Profile);
        var settings = Store.Load();
        Assert.True(settings.ShowMinimizedWindowsInOverview);
        Assert.True(settings.ShowMinimizedWindows);
        var candidate = settings.Copy();
        candidate.ShowMinimizedWindowsInOverview = false;
        Assert.True(new PreferencesService(settings, Store.Save).TryApply(candidate, () => { }, () => { },
            error => throw error));
        Assert.False(settings.ShowMinimizedWindowsInOverview);
        var reopened = Store.Load();
        Assert.False(reopened.ShowMinimizedWindowsInOverview);
        Assert.True(reopened.ShowMinimizedWindows);
    }

    private readonly string root = Path.Combine(Path.GetTempPath(), "MissionView-settings-tests", Guid.NewGuid().ToString("N"));
    private string Current => Path.Combine(root, "user", "settings.json");
    private string Legacy => Path.Combine(root, "exe", "settings.json");
    private JsonSettingsStore Store => new(Current, Legacy);
    private const string Profile = """
        {"Hotkey":"Win+F12","EnableHotCorners":false,"HotCornerPosition":"BottomRight",
         "HotCornerDelay":937,"AnimationSpeed":0.63,"RunOnStartup":true,"Renderer":"legacy"}
        """;

    private static void Put(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    [Fact]
    public void LegacyProfileMigratesAllKnownPreferencesAndSurvivesRepeatedLoadAndSave()
    {
        Put(Legacy, Profile);
        var settings = Store.Load();
        AssertProfile(settings);
        Assert.Equal(Profile, File.ReadAllText(Legacy));
        Store.Save(settings);
        Put(Legacy, """{"Hotkey":"Alt+F1"}""");
        AssertProfile(Store.Load());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Current)!, "*.tmp"));
    }

    private static void AssertProfile(AppSettings settings)
    {
        Assert.Equal("Win+F12", settings.Hotkey);
        Assert.False(settings.EnableHotCorners);
        Assert.Equal("BottomRight", settings.HotCornerPosition);
        Assert.Equal(937, settings.HotCornerDelay);
        Assert.Equal(0.63, settings.AnimationSpeed);
        Assert.True(settings.RunOnStartup);
        Assert.Equal("legacy", settings.Renderer);
        Assert.Equal("White", settings.SelectionBorderColor);
        Assert.Equal("Standard", settings.ThumbnailSize);
        Assert.Equal(160d / 255 * 100, settings.BackgroundDimming);
        Assert.False(settings.ReduceAnimations);
    }

    [Fact]
    public void ExistingNewSettingsAreAuthoritativeEvenWhenLegacyCannotBeRead()
    {
        Put(Current, Profile);
        Directory.CreateDirectory(Legacy);
        AssertProfile(Store.Load());
        Assert.Equal(Profile, File.ReadAllText(Current));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("""{"Hotkey":null}""")]
    [InlineData("""{"HotCornerPosition":null}""")]
    [InlineData("""{"HotCornerDelay":-1}""")]
    [InlineData("""{"AnimationSpeed":0}""")]
    [InlineData("""{"RunOnStartup":"yes"}""")]
    [InlineData("""{"SelectionBorderColor":"Red"}""")]
    [InlineData("""{"SelectionBorderColor":null}""")]
    [InlineData("""{"ThumbnailSize":"Huge"}""")]
    [InlineData("""{"ThumbnailSize":null}""")]
    [InlineData("""{"BackgroundDimming":-1}""")]
    [InlineData("""{"BackgroundDimming":101}""")]
    [InlineData("""{"ReduceAnimations":"yes"}""")]
    public void CorruptNewSettingsNeverFallBackToLegacyOrOverwriteEitherFile(string invalid)
    {
        Put(Current, invalid);
        Put(Legacy, Profile);
        var error = Record.Exception(() => Store.Load());
        Assert.NotNull(error);
        Assert.True(JsonSettingsStore.IsPersistenceError(error));
        Assert.Equal(invalid, File.ReadAllText(Current));
        Assert.Equal(Profile, File.ReadAllText(Legacy));
    }

    [Fact]
    public void CorruptLegacyIsNotMigratedOrReplacedWithDefaults()
    {
        Put(Legacy, "{");
        Assert.Throws<JsonException>(() => Store.Load());
        Assert.False(File.Exists(Current));
        Assert.Equal("{", File.ReadAllText(Legacy));
    }

    [Fact]
    public void ReadFailureInNewLocationDoesNotUseLegacyOrCreateDefaults()
    {
        Directory.CreateDirectory(Current);
        Put(Legacy, Profile);
        var error = Record.Exception(() => Store.Load());
        Assert.NotNull(error);
        Assert.True(JsonSettingsStore.IsPersistenceError(error));
        Assert.True(Directory.Exists(Current));
        Assert.Equal(Profile, File.ReadAllText(Legacy));
    }

    [Fact]
    public void InvalidEditCannotReplaceValidSavedPreferences()
    {
        Put(Current, Profile);
        var settings = Store.Load();
        settings.AnimationSpeed = double.NaN;
        Assert.Throws<InvalidDataException>(() => Store.Save(settings));
        Assert.Equal(Profile, File.ReadAllText(Current));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Current)!, "*.tmp"));
    }

    [Fact]
    public void MissingBothCreatesOriginalFactoryDefaultsInNewLocationOnly()
    {
        var settings = Store.Load();
        Assert.Equal(JsonSerializer.Serialize(new AppSettings()), JsonSerializer.Serialize(settings));
        Assert.True(File.Exists(Current));
        Assert.False(File.Exists(Legacy));
    }

    [Fact]
    public void OlderProfilesKeepAllDisplaysAndMinimizedWindowsEnabled()
    {
        Put(Current, Profile);
        var settings = Store.Load();
        Assert.Equal("All", settings.OverviewMonitorScope);
        Assert.True(settings.ShowMinimizedWindows);
        Store.Save(settings);
        Assert.Equal("All", Store.Load().OverviewMonitorScope);
        Assert.True(Store.Load().ShowMinimizedWindows);
    }

    [Fact]
    public void DisplayOptionsPersistCopyAndRollbackWithTheOtherPreferences()
    {
        Put(Current, Profile);
        var settings = Store.Load();
        var candidate = settings.Copy();
        candidate.OverviewMonitorScope = "Current";
        candidate.ShowMinimizedWindows = false;
        Assert.Equal("All", settings.OverviewMonitorScope);
        Assert.True(settings.ShowMinimizedWindows);
        Assert.True(new PreferencesService(settings, Store.Save).TryApply(candidate, () => { }, () => { }, error => throw error));
        Assert.Equal("Current", settings.OverviewMonitorScope);
        Assert.False(settings.ShowMinimizedWindows);
        Assert.Equal("Current", Store.Load().OverviewMonitorScope);
        Assert.False(Store.Load().ShowMinimizedWindows);

        candidate = settings.Copy();
        candidate.OverviewMonitorScope = "All";
        candidate.ShowMinimizedWindows = true;
        var errors = new List<Exception>();
        Assert.False(new PreferencesService(settings, Store.Save).TryApply(candidate,
            () => throw new IOException("apply failed"), () => { }, errors.Add));
        Assert.Single(errors);
        Assert.Equal("Current", settings.OverviewMonitorScope);
        Assert.False(settings.ShowMinimizedWindows);
        Assert.Equal("Current", Store.Load().OverviewMonitorScope);
        Assert.False(Store.Load().ShowMinimizedWindows);
    }

    [Theory]
    [InlineData("""{"OverviewMonitorScope":null}""")]
    [InlineData("""{"OverviewMonitorScope":"current"}""")]
    [InlineData("""{"OverviewMonitorScope":"Primary"}""")]
    [InlineData("""{"OverviewMonitorScope":1}""")]
    [InlineData("""{"ShowMinimizedWindows":null}""")]
    [InlineData("""{"ShowMinimizedWindows":"false"}""")]
    public void InvalidDisplayOptionsNeverOverwriteTheSavedProfile(string invalid)
    {
        Put(Current, invalid);
        var error = Record.Exception(() => Store.Load());
        Assert.NotNull(error);
        Assert.True(JsonSettingsStore.IsPersistenceError(error));
        Assert.Equal(invalid, File.ReadAllText(Current));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Primary")]
    public void UnsupportedScopeEditIsReportedWithoutApplyingOrReplacingPreferences(string? scope)
    {
        Put(Current, Profile);
        var settings = Store.Load();
        var candidate = settings.Copy();
        candidate.OverviewMonitorScope = scope!;
        int applied = 0;
        var errors = new List<Exception>();
        Assert.False(new PreferencesService(settings, Store.Save).TryApply(candidate,
            () => applied++, () => { }, errors.Add));
        Assert.IsType<InvalidDataException>(Assert.Single(errors));
        Assert.Equal(0, applied);
        Assert.Equal("All", settings.OverviewMonitorScope);
        Assert.Equal(Profile, File.ReadAllText(Current));
    }

    [Fact]
    public void FailedMigrationPreservesLegacyAndReportsFailureInsteadOfReturningDefaults()
    {
        Put(Legacy, Profile);
        File.WriteAllText(Path.GetDirectoryName(Current)!, "blocks directory creation");
        Assert.Throws<IOException>(() => Store.Load());
        Assert.Equal(Profile, File.ReadAllText(Legacy));
        Assert.False(File.Exists(Current));
    }

    [Fact]
    public void FailedAtomicSavePreservesFileAndPreferenceAndDoesNotApplyRegistryChange()
    {
        Put(Current, Profile);
        var settings = Store.Load();
        int applied = 0;
        var notifications = new List<Exception>();
        string preserved = Current + ".original";
        File.Move(Current, preserved);
        Directory.CreateDirectory(Current);
        Assert.False(new PreferencesService(settings, Store.Save).TrySetRunOnStartup(false, () => applied++, notifications.Add));
        AssertProfile(settings);
        Assert.Equal(Profile, File.ReadAllText(preserved));
        Assert.Equal(0, applied);
        Assert.Single(notifications);
        Assert.True(JsonSettingsStore.IsPersistenceError(notifications[0]));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Current)!, "*.tmp"));
        Directory.Delete(Current);
        File.Move(preserved, Current);
        Assert.True(new PreferencesService(settings, Store.Save).TrySetRunOnStartup(false, () => applied++, notifications.Add));
        Assert.Equal(1, applied);
        Assert.Single(notifications);
        Assert.False(Store.Load().RunOnStartup);
        Assert.Equal("Win+F12", Store.Load().Hotkey);
    }

    [Fact]
    public void FailedExternalApplyRestoresAllPreferencesFileAndExternalState()
    {
        Put(Current, Profile);
        var settings = Store.Load();
        var candidate = settings.Copy();
        candidate.Hotkey = "Ctrl+F10";
        candidate.EnableHotCorners = true;
        candidate.HotCornerPosition = "TopRight";
        candidate.HotCornerDelay = 100;
        candidate.AnimationSpeed = 0.1;
        candidate.RunOnStartup = false;
        string activeHotkey = settings.Hotkey;
        bool registry = settings.RunOnStartup;
        var errors = new List<Exception>();
        Assert.False(new PreferencesService(settings, Store.Save).TryApply(candidate, () =>
        {
            registry = false;
            throw new UnauthorizedAccessException("registry refused");
        }, () =>
        {
            activeHotkey = settings.Hotkey;
            registry = settings.RunOnStartup;
        }, errors.Add, () => activeHotkey = candidate.Hotkey));
        AssertProfile(settings);
        AssertProfile(Store.Load());
        Assert.Equal("Win+F12", activeHotkey);
        Assert.True(registry);
        Assert.IsType<UnauthorizedAccessException>(Assert.Single(errors));
    }

    [Theory]
    [InlineData("White", "Compact", 0)]
    [InlineData("Blue", "Standard", 100)]
    [InlineData("Mint", "Large", 78.5)]
    public void PresentationPreferencesRoundTripAndRollBackAsOneTransaction(string border, string size, double dimming)
    {
        Put(Current, Profile);
        var settings = Store.Load();
        var candidate = settings.Copy();
        candidate.SelectionBorderColor = border;
        candidate.ThumbnailSize = size;
        candidate.BackgroundDimming = dimming;
        candidate.ReduceAnimations = true;
        AssertProfile(settings);
        Assert.True(new PreferencesService(settings, Store.Save).TryApply(candidate, () => { }, () => { }, e => throw e));
        var saved = Store.Load();
        Assert.Equal(border, saved.SelectionBorderColor);
        Assert.Equal(size, saved.ThumbnailSize);
        Assert.Equal(dimming, saved.BackgroundDimming);
        Assert.True(saved.ReduceAnimations);
        string before = File.ReadAllText(Current);
        var reset = new AppSettings();
        Assert.False(new PreferencesService(settings, Store.Save).TryApply(reset,
            () => throw new IOException("refused"), () => { }, e => Assert.IsType<IOException>(e)));
        Assert.Equal(before, File.ReadAllText(Current));
        Assert.Equal(border, settings.SelectionBorderColor);
        Assert.Equal(size, settings.ThumbnailSize);
        Assert.Equal(dimming, settings.BackgroundDimming);
        Assert.True(settings.ReduceAnimations);
        candidate = settings.Copy();
        candidate.BackgroundDimming = double.NaN;
        Assert.Throws<InvalidDataException>(() => Store.Save(candidate));
        Assert.Equal(before, File.ReadAllText(Current));
    }

    [Fact]
    public void SaveFailureAfterHotkeyReservationRestoresReservationWithoutTouchingExternalPreferences()
    {
        Put(Current, Profile);
        var settings = Store.Load();
        var candidate = settings.Copy();
        candidate.Hotkey = "Alt+F9";
        File.Move(Current, Current + ".preserved");
        Directory.CreateDirectory(Current);
        string active = settings.Hotkey;
        int applied = 0, rolledBack = 0;
        var errors = new List<Exception>();
        Assert.False(new PreferencesService(settings, Store.Save).TryApply(candidate, () => applied++, () =>
        {
            active = settings.Hotkey;
            rolledBack++;
        }, errors.Add, () => active = candidate.Hotkey));
        AssertProfile(settings);
        Assert.Equal("Win+F12", active);
        Assert.Equal(0, applied);
        Assert.Equal(1, rolledBack);
        Assert.Single(errors);
        Assert.Equal(Profile, File.ReadAllText(Current + ".preserved"));
    }

    [Fact]
    public void HotkeyConflictLeavesSavedBytesAndSettingsIntactAndReportsError()
    {
        Put(Current, Profile);
        var settings = Store.Load();
        var candidate = settings.Copy();
        candidate.Hotkey = "Ctrl+F9";
        int applied = 0;
        var errors = new List<Exception>();
        Assert.False(new PreferencesService(settings, Store.Save).TryApply(candidate, () => applied++, () => { }, errors.Add,
            () => throw new InvalidOperationException("Shortcut is already in use.")));
        AssertProfile(settings);
        Assert.Equal(Profile, File.ReadAllText(Current));
        Assert.Equal(0, applied);
        Assert.Equal("Shortcut is already in use.", Assert.Single(errors).Message);
    }

    [Fact]
    public void RollbackFailureIsExplicitAndStillRestoresSavedPreferences()
    {
        Put(Current, Profile);
        var settings = Store.Load();
        var candidate = settings.Copy();
        candidate.RunOnStartup = false;
        var errors = new List<Exception>();
        Assert.False(new PreferencesService(settings, Store.Save).TryApply(candidate, () => throw new IOException("apply"),
            () => throw new IOException("rollback"), errors.Add));
        AssertProfile(settings);
        AssertProfile(Store.Load());
        var error = Assert.IsType<AggregateException>(Assert.Single(errors));
        Assert.Equal(new[] { "apply", "rollback" }, error.InnerExceptions.Select(e => e.Message));
    }

    [Fact]
    public void SuccessfulEditPreservesRendererAndUsesExistingSettingsPath()
    {
        Put(Current, Profile);
        var settings = Store.Load();
        var candidate = settings.Copy();
        candidate.Hotkey = "Alt+F9";
        candidate.EnableHotCorners = true;
        candidate.AnimationSpeed = 0.125;
        Assert.True(new PreferencesService(settings, Store.Save).TryApply(candidate, () => { }, () => throw new Exception("unexpected rollback"),
            error => throw error));
        var loaded = Store.Load();
        Assert.Equal("Alt+F9", loaded.Hotkey);
        Assert.True(loaded.EnableHotCorners);
        Assert.Equal(0.125, loaded.AnimationSpeed);
        Assert.Equal("legacy", loaded.Renderer);
        Assert.Equal("BottomRight", loaded.HotCornerPosition);
        Assert.Equal(937, loaded.HotCornerDelay);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
