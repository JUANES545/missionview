using System.Text.Json;
using WindowScatter;
using Xunit;

public class WindowEnumerationDiagnosticTests
{
    [Fact]
    public void MetadataKeepsIndependentSameProcessOwnedAppAndAuxiliarySurfacesWithoutClassifyingThem()
    {
        var diagnostic = new WindowEnumerationDiagnostics();
        diagnostic.Record(new() { Hwnd = 1, ProcessId = 50, RootOwner = 1, ClassName = "NativeMain" });
        diagnostic.Record(new() { Hwnd = 2, ProcessId = 50, RootOwner = 2, ClassName = "NativeMain" });
        diagnostic.Record(new() { Hwnd = 3, ProcessId = 50, Owner = 1, RootOwner = 1, ExtendedStyle = 0x40000 });
        diagnostic.Record(new() { Hwnd = 4, ProcessId = 50, Owner = 1, RootOwner = 1, ExtendedStyle = 0x80 });
        diagnostic.Record(new() { Hwnd = 5, ProcessId = 50, RootOwner = 5, ClassName = "CallSurface" });
        var included = new List<IntPtr> { new(1), new(2), new(3), new(5) };
        WithFile(path =>
        {
            Assert.True(diagnostic.Write(path, included, true));
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var rows = json.RootElement.GetProperty("Windows").EnumerateArray().ToArray();
            Assert.Equal(5, rows.Length);
            Assert.Equal([1L, 2, 3, 4, 5], rows.Select(r => r.GetProperty("Metadata").GetProperty("Hwnd").GetInt64()));
            Assert.Equal([true, true, true, false, true], rows.Select(r => r.GetProperty("IncludedInOverview").GetBoolean()));
            Assert.Equal([new IntPtr(1), new(2), new(3), new(5)], included);
            Assert.DoesNotContain("Title", File.ReadAllText(path));
            Assert.DoesNotContain(AppContext.BaseDirectory, File.ReadAllText(path));
        });
    }

    [Fact]
    public void CaptureIsBoundedAndNextSnapshotReplacesPreviousFile()
    {
        var diagnostic = new WindowEnumerationDiagnostics();
        for (int i = 0; i < WindowEnumerationDiagnostics.MaxWindows + 10; i++)
            diagnostic.Record(new() { Hwnd = i + 1 });
        WithFile(path =>
        {
            Assert.True(diagnostic.Write(path, [], true));
            using (var json = JsonDocument.Parse(File.ReadAllText(path)))
            {
                Assert.True(json.RootElement.GetProperty("Truncated").GetBoolean());
                Assert.Equal(WindowEnumerationDiagnostics.MaxWindows + 10,
                    json.RootElement.GetProperty("EnumeratedWindows").GetInt32());
                Assert.Equal(WindowEnumerationDiagnostics.MaxWindows, json.RootElement.GetProperty("Windows").GetArrayLength());
            }
            var next = new WindowEnumerationDiagnostics();
            next.Record(new() { Hwnd = 9999 });
            Assert.True(next.Write(path, [], false));
            using var replacement = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Single(replacement.RootElement.GetProperty("Windows").EnumerateArray());
            Assert.False(replacement.RootElement.GetProperty("Truncated").GetBoolean());
            Assert.False(replacement.RootElement.GetProperty("EnumerationSucceeded").GetBoolean());
        });
    }

    [Fact]
    public void WriteFailureIsExplicitAndDoesNotReplaceExistingSnapshot()
    {
        WithFile(path =>
        {
            File.WriteAllText(path, "previous snapshot");
            Assert.False(new WindowEnumerationDiagnostics().Write(Path.Combine(path, "child.json"), [], true));
            Assert.Equal("previous snapshot", File.ReadAllText(path));
        });
    }

    private static void WithFile(Action<string> test)
    {
        string path = Path.Combine(AppContext.BaseDirectory, $"enum-diagnostic-test-{Guid.NewGuid():N}.json");
        try { test(path); }
        finally { File.Delete(path); }
    }
}
