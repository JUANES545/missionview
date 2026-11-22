using System.Text.Json;
using WindowScatter;
using Xunit;

public sealed class IndependentShortcutPreferencesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MissionView-independent-shortcut", Guid.NewGuid().ToString("N"));
    private string Current => Path.Combine(root, "current.json");
    private string Legacy => Path.Combine(root, "legacy.json");
    private JsonSettingsStore Store => new(Current, Legacy);

    [Theory]
    [InlineData("Win+W", 0x57, 12)]
    [InlineData("Win+F12", 0x7B, 12)]
    [InlineData("Win+Shift+F12", 0, 0)]
    public void MissingIndependentFieldMigratesWithoutChangingLegacyBindingsOrOriginalBytes(string normal, int key, int modifiers)
    {
        Directory.CreateDirectory(root);
        string json = JsonSerializer.Serialize(new { Hotkey = normal, AnimationSpeed = 0.63 });
        File.WriteAllText(Legacy, json);
        var loaded = Store.Load();
        Assert.Null(loaded.AppExposeHotkey);
        Assert.Equal(normal, loaded.Hotkey);
        Assert.Equal(0.63, loaded.AnimationSpeed);
        var pair = OverviewShortcutPair.Parse(loaded.Hotkey, loaded.AppExposeHotkey);
        Assert.Equal(key == 0 ? null : new OverviewHotkey(modifiers, key), pair.AppExpose);
        Assert.Equal(json, File.ReadAllText(Legacy));
        Store.Save(loaded);
        Assert.Null(Store.Load().AppExposeHotkey);
        Assert.Equal(pair, OverviewShortcutPair.Parse(Store.Load().Hotkey, Store.Load().AppExposeHotkey));
    }

    [Fact]
    public void DefaultAndExplicitNullPreserveFactoryDerivationWithoutReadingARealProfile()
    {
        var factory = new AppSettings();
        Assert.Equal("Win+W", factory.Hotkey);
        Assert.Null(factory.AppExposeHotkey);
        Directory.CreateDirectory(root);
        const string json = """{"Hotkey":"Win+F12","AppExposeHotkey":null}""";
        File.WriteAllText(Current, json);
        var loaded = Store.Load();
        Assert.Null(loaded.AppExposeHotkey);
        Assert.Equal(new OverviewHotkey(12, 0x7B), OverviewShortcutPair.Parse(loaded.Hotkey, loaded.AppExposeHotkey).AppExpose);
        Assert.Equal(json, File.ReadAllText(Current));
    }

    [Fact]
    public void IndependentBindingPersistsCopiesAndChangesOverviewWithoutReDeriving()
    {
        var settings = new AppSettings { Hotkey = "Win+F12", AppExposeHotkey = "Ctrl+Alt+E", AnimationSpeed = 0.63 };
        Store.Save(settings);
        var loaded = Store.Load();
        Assert.Equal("Ctrl+Alt+E", loaded.AppExposeHotkey);
        var candidate = loaded.Copy();
        candidate.Hotkey = "Alt+F1";
        loaded.CopyFrom(candidate);
        Store.Save(loaded);
        var after = Store.Load();
        Assert.Equal("Alt+F1", after.Hotkey);
        Assert.Equal("Ctrl+Alt+E", after.AppExposeHotkey);
        Assert.Equal(new OverviewHotkey(3, 'E'), OverviewShortcutPair.Parse(after.Hotkey, after.AppExposeHotkey).AppExpose);
        Assert.Equal(0.63, after.AnimationSpeed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Shift+E")]
    [InlineData("Win+F12")]
    [InlineData("Alt+E+F")]
    public void InvalidIndependentFieldNeverFallsBackToLegacyOrRewritesSavedBytes(string independent)
    {
        Directory.CreateDirectory(root);
        string invalid = JsonSerializer.Serialize(new { Hotkey = "Win+F12", AppExposeHotkey = independent });
        File.WriteAllText(Current, invalid);
        File.WriteAllText(Legacy, """{"Hotkey":"Win+W"}""");
        Assert.Throws<InvalidDataException>(() => Store.Load());
        Assert.Equal(invalid, File.ReadAllText(Current));
        Assert.Equal("""{"Hotkey":"Win+W"}""", File.ReadAllText(Legacy));
    }

    [Fact]
    public void FailedPersistenceRestoresOccupiedHookPairAndReopenUsesTheSameSavedRoutes()
    {
        var settings = new AppSettings { Hotkey = "Win+W" };
        Store.Save(settings);
        var originalBytes = File.ReadAllBytes(Current);
        var active = new Dictionary<int, OverviewHotkey>();
        OverviewHotkeys Create() => new((id, key) =>
        {
            if (key.Key == 'W' || active.ContainsKey(id) || active.Values.Contains(key)) return false;
            active.Add(id, key);
            return true;
        }, active.Remove, () => true);
        var bindings = Create();
        Assert.True(bindings.TryInitialize(settings.Hotkey, settings.AppExposeHotkey, out _));
        Assert.Empty(active);
        var candidate = settings.Copy();
        candidate.Hotkey = "Ctrl+F1";
        candidate.AppExposeHotkey = "Alt+E";
        File.Move(Current, Current + ".preserved");
        Directory.CreateDirectory(Current);
        var errors = new List<Exception>();
        Assert.False(new PreferencesService(settings, Store.Save).TryApply(candidate,
            () => throw new Exception("Persistence must fail before external settings change."),
            () =>
            {
                if (!bindings.TryApply(settings.Hotkey, settings.AppExposeHotkey, false, out var error))
                    throw new InvalidOperationException(error);
            }, errors.Add, () =>
            {
                if (!bindings.TryApply(candidate.Hotkey, candidate.AppExposeHotkey, false, out var error))
                    throw new InvalidOperationException(error);
            }));
        Assert.Single(errors);
        Assert.Empty(active);
        Assert.True(bindings.MatchesNormal('W', 8));
        Assert.True(bindings.MatchesAppExpose('W', 12));
        Assert.Equal("Win+W", settings.Hotkey);
        Assert.Null(settings.AppExposeHotkey);
        Assert.Equal(originalBytes, File.ReadAllBytes(Current + ".preserved"));
        Assert.True(bindings.Release());
        Directory.Delete(Current);
        File.Move(Current + ".preserved", Current);
        var loaded = Store.Load();
        var reopened = Create();
        Assert.True(reopened.TryInitialize(loaded.Hotkey, loaded.AppExposeHotkey, out _));
        Assert.Empty(active);
        Assert.True(reopened.MatchesNormal('W', 8));
        Assert.True(reopened.MatchesAppExpose('W', 12));
        Assert.True(reopened.Release());
    }

    [Fact]
    public void FailedSaveAfterPairReservationRestoresBothSettingsAndActualReservations()
    {
        var settings = new AppSettings { Hotkey = "Win+F12", AppExposeHotkey = "Alt+E" };
        Store.Save(settings);
        var originalBytes = File.ReadAllBytes(Current);
        var active = new Dictionary<int, OverviewHotkey>();
        var hotkeys = new OverviewHotkeys((id, key) =>
        {
            if (active.ContainsKey(id) || active.Values.Contains(key)) return false;
            active.Add(id, key);
            return true;
        }, active.Remove);
        Assert.True(hotkeys.TryInitialize(settings.Hotkey, settings.AppExposeHotkey, out _));
        var before = active.OrderBy(p => p.Key).ToArray();
        var candidate = settings.Copy();
        candidate.Hotkey = "Ctrl+F1";
        candidate.AppExposeHotkey = "Win+X";
        File.Move(Current, Current + ".preserved");
        Directory.CreateDirectory(Current);
        int externalApplied = 0;
        var errors = new List<Exception>();
        Assert.False(new PreferencesService(settings, Store.Save).TryApply(candidate,
            () => externalApplied++,
            () =>
            {
                if (!hotkeys.TryApply(settings.Hotkey, settings.AppExposeHotkey, false, out var error))
                    throw new InvalidOperationException(error);
            }, errors.Add, () =>
            {
                if (!hotkeys.TryApply(candidate.Hotkey, candidate.AppExposeHotkey, false, out var error))
                    throw new InvalidOperationException(error);
            }));
        Assert.Equal("Win+F12", settings.Hotkey);
        Assert.Equal("Alt+E", settings.AppExposeHotkey);
        Assert.Equal(before, active.OrderBy(p => p.Key).ToArray());
        Assert.Equal(originalBytes, File.ReadAllBytes(Current + ".preserved"));
        Assert.Equal(0, externalApplied);
        Assert.Single(errors);
    }

    [Fact]
    public void FailureAfterPersistenceRestoresIndependentFieldInMemoryAndOnDisk()
    {
        var settings = new AppSettings { Hotkey = "Win+F12", AppExposeHotkey = "Alt+E" };
        Store.Save(settings);
        var candidate = settings.Copy();
        candidate.AppExposeHotkey = "Ctrl+K";
        string? active = settings.AppExposeHotkey;
        var errors = new List<Exception>();
        Assert.False(new PreferencesService(settings, Store.Save).TryApply(candidate,
            () => throw new UnauthorizedAccessException("An external preference was refused."),
            () => active = settings.AppExposeHotkey, errors.Add,
            () => active = candidate.AppExposeHotkey));
        Assert.Equal("Alt+E", active);
        Assert.Equal("Alt+E", settings.AppExposeHotkey);
        Assert.Equal("Alt+E", Store.Load().AppExposeHotkey);
        Assert.IsType<UnauthorizedAccessException>(Assert.Single(errors));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
