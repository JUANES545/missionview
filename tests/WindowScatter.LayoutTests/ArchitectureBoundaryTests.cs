using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

public class ArchitectureBoundaryTests
{
    [Fact]
    public void InnerLayersContainNoPlatformOrStorageEffects()
    {
        var assembly = typeof(ArchitectureBoundaryTests).Assembly;
        var sources = assembly.GetManifestResourceNames().Where(n => n.StartsWith("Architecture.", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(sources);
        foreach (var name in sources)
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            string source = reader.ReadToEnd();
            Assert.False(Regex.IsMatch(source,
                @"\b(?:DllImport|LibraryImport)\s*\(|\bSystem\.Windows\b|\bMicrosoft\.Win32\b|" +
                @"\b(?:File|Directory)\s*\.\s*(?:Read|Write|Delete|Move|Replace|Open|Create)|\bProcess\s*\.\s*Start\b"),
                $"{name} leaks a presentation, native, filesystem or process effect into an inner layer.");
        }
    }
}
