using System.Text.Json;
using WindowScatter;
using Xunit;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MissionView-settings-tests", Guid.NewGuid().ToString("N"));
    private string Current => Path.Combine(root, "user", "settings.json");
    private string Legacy => Path.Combine(root, "exe", "settings.json");
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
        var settings = AppSettings.Load(Current, Legacy);
        AssertProfile(settings);
        Assert.Equal(Profile, File.ReadAllText(Legacy));
        settings.Save();
        Put(Legacy, """{"Hotkey":"Alt+F1"}""");
        AssertProfile(AppSettings.Load(Current, Legacy));
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
        AssertProfile(AppSettings.Load(Current, Legacy));
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
        var error = Record.Exception(() => AppSettings.Load(Current, Legacy));
        Assert.NotNull(error);
        Assert.True(AppSettings.IsPersistenceError(error));
        Assert.Equal(invalid, File.ReadAllText(Current));
        Assert.Equal(Profile, File.ReadAllText(Legacy));
    }

    [Fact]
    public void CorruptLegacyIsNotMigratedOrReplacedWithDefaults()
    {
        Put(Legacy, "{");
        Assert.Throws<JsonException>(() => AppSettings.Load(Current, Legacy));
        Assert.False(File.Exists(Current));
        Assert.Equal("{", File.ReadAllText(Legacy));
    }

    [Fact]
    public void ReadFailureInNewLocationDoesNotUseLegacyOrCreateDefaults()
    {
        Directory.CreateDirectory(Current);
        Put(Legacy, Profile);
        var error = Record.Exception(() => AppSettings.Load(Current, Legacy));
        Assert.NotNull(error);
        Assert.True(AppSettings.IsPersistenceError(error));
        Assert.True(Directory.Exists(Current));
        Assert.Equal(Profile, File.ReadAllText(Legacy));
    }

    [Fact]
    public void InvalidEditCannotReplaceValidSavedPreferences()
    {
        Put(Current, Profile);
        var settings = AppSettings.Load(Current, Legacy);
        settings.AnimationSpeed = double.NaN;
        Assert.Throws<InvalidDataException>(settings.Save);
        Assert.Equal(Profile, File.ReadAllText(Current));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Current)!, "*.tmp"));
    }

    [Fact]
    public void MissingBothCreatesOriginalFactoryDefaultsInNewLocationOnly()
    {
        var settings = AppSettings.Load(Current, Legacy);
        Assert.Equal(JsonSerializer.Serialize(new AppSettings()), JsonSerializer.Serialize(settings));
        Assert.True(File.Exists(Current));
        Assert.False(File.Exists(Legacy));
    }

    [Fact]
    public void FailedMigrationPreservesLegacyAndReportsFailureInsteadOfReturningDefaults()
    {
        Put(Legacy, Profile);
        File.WriteAllText(Path.GetDirectoryName(Current)!, "blocks directory creation");
        Assert.Throws<IOException>(() => AppSettings.Load(Current, Legacy));
        Assert.Equal(Profile, File.ReadAllText(Legacy));
        Assert.False(File.Exists(Current));
    }

    [Fact]
    public void FailedAtomicSavePreservesFileAndPreferenceAndDoesNotApplyRegistryChange()
    {
        Put(Current, Profile);
        var settings = AppSettings.Load(Current, Legacy);
        int applied = 0;
        var notifications = new List<Exception>();
        string preserved = Current + ".original";
        File.Move(Current, preserved);
        Directory.CreateDirectory(Current);
        Assert.False(settings.TrySetRunOnStartup(false, () => applied++, notifications.Add));
        AssertProfile(settings);
        Assert.Equal(Profile, File.ReadAllText(preserved));
        Assert.Equal(0, applied);
        Assert.Single(notifications);
        Assert.True(AppSettings.IsPersistenceError(notifications[0]));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Current)!, "*.tmp"));
        Directory.Delete(Current);
        File.Move(preserved, Current);
        Assert.True(settings.TrySetRunOnStartup(false, () => applied++, notifications.Add));
        Assert.Equal(1, applied);
        Assert.Single(notifications);
        Assert.False(AppSettings.Load(Current, Legacy).RunOnStartup);
        Assert.Equal("Win+F12", AppSettings.Load(Current, Legacy).Hotkey);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
