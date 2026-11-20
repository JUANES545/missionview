using System.Text.Json;
using WindowScatter;
using Xunit;

public sealed class AppSettingsTests : IDisposable
{
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
