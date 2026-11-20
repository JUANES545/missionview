using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace WindowScatter
{
    internal sealed class JsonSettingsStore
    {
        private readonly string path, legacyPath;

        internal JsonSettingsStore(string path, string legacyPath)
        {
            this.path = path;
            this.legacyPath = legacyPath;
        }

        internal static JsonSettingsStore ForCurrentUser() => new(UserSettingsPath(),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json"));

        private static string UserSettingsPath()
        {
            string directory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(directory))
                throw new IOException("Windows did not provide a local application data directory.");
            return Path.Combine(directory, "MissionView", "settings.json");
        }

        internal AppSettings Load()
        {
            var settings = Read(path);
            if (settings != null) return settings;

            settings = Read(legacyPath) ?? new AppSettings();
            Write(settings, overwrite: false);
            return settings;
        }

        private static AppSettings? Read(string path)
        {
            string json;
            try { json = File.ReadAllText(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }

            var settings = JsonSerializer.Deserialize<AppSettings>(json)
                ?? throw new InvalidDataException($"Settings must be a JSON object: {path}");
            settings.Validate();
            return settings;
        }

        internal static bool IsPersistenceError(Exception error)
            => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException;

        internal void Save(AppSettings settings) => Write(settings, overwrite: true);

        private void Write(AppSettings settings, bool overwrite)
        {
            settings.Validate();
            string? temporaryPath = null;
            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };

                string json = JsonSerializer.Serialize(settings, options);
                string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
                Directory.CreateDirectory(directory);
                temporaryPath = Path.Combine(directory, $".settings-{Guid.NewGuid():N}.tmp");
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using var writer = new StreamWriter(stream, leaveOpen: true);
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
                if (overwrite && File.Exists(path))
                    File.Replace(temporaryPath, path, null);
                else
                    File.Move(temporaryPath, path);
                temporaryPath = null;
            }
            finally
            {
                if (temporaryPath != null)
                {
                    try { File.Delete(temporaryPath); }
                    catch (Exception ex) when (IsPersistenceError(ex))
                    {
                        System.Diagnostics.Trace.TraceError($"Could not remove temporary settings file: {ex.Message}");
                    }
                }
            }
        }

    }
}
