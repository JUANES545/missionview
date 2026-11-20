using System;
using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace WindowScatter
{
    public class AppSettings
    {
        public string Hotkey { get; set; } = "Win+W";
        public bool EnableHotCorners { get; set; } = true;
        public string HotCornerPosition { get; set; } = "TopLeft";
        public int HotCornerDelay { get; set; } = 500;
        public double AnimationSpeed { get; set; } = 0.25;

        /// <summary>Launch automatically at logon (HKCU Run key). Applied on startup.</summary>
        public bool RunOnStartup { get; set; } = false;

        /// <summary>"auto" = DirectComposition when supported, "dcomp" = force-on, "legacy" = force old DWM-thumbnail path.</summary>
        public string Renderer { get; set; } = "auto";

        private string? settingsPath;

        public static AppSettings Load() => Load(
            UserSettingsPath(),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json"));

        private static string UserSettingsPath()
        {
            string directory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(directory))
                throw new IOException("Windows did not provide a local application data directory.");
            return Path.Combine(directory, "MissionView", "settings.json");
        }

        internal static AppSettings Load(string path, string legacyPath)
        {
            var settings = Read(path);
            if (settings != null) return settings;

            settings = Read(legacyPath) ?? new AppSettings();
            settings.settingsPath = path;
            settings.Write(overwrite: false);
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
            settings.settingsPath = path;
            return settings;
        }

        private void Validate()
        {
            if (string.IsNullOrWhiteSpace(Hotkey) || HotkeyParser.Parse(Hotkey).VirtualKeyCode == 0)
                throw new InvalidDataException("Settings contain an unsupported hotkey.");
            if (HotCornerPosition == null || !new[] { "TopLeft", "TopRight", "BottomLeft", "BottomRight" }
                .Contains(HotCornerPosition, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Settings contain an unsupported hot corner.");
            if (HotCornerDelay < 0 || !double.IsFinite(AnimationSpeed) || AnimationSpeed <= 0)
                throw new InvalidDataException("Settings contain an invalid delay or animation speed.");
            if (Renderer == null || !new[] { "auto", "dcomp", "legacy" }.Contains(Renderer, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Settings contain an unsupported renderer.");
        }

        internal static bool IsPersistenceError(Exception error)
            => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException;

        public void Save() => Write(overwrite: true);

        private void Write(bool overwrite)
        {
            Validate();
            string path = settingsPath ??= UserSettingsPath();
            string? temporaryPath = null;
            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };

                string json = JsonSerializer.Serialize(this, options);
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

        internal bool TrySetRunOnStartup(bool enabled, Action apply, Action<Exception> reportError)
        {
            bool previous = RunOnStartup;
            RunOnStartup = enabled;
            try { Save(); }
            catch (Exception ex) when (IsPersistenceError(ex))
            {
                RunOnStartup = previous;
                reportError(ex);
                return false;
            }
            apply();
            return true;
        }

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "WindowScatter";

        /// <summary>
        /// Makes the registry match <see cref="RunOnStartup"/> so the app (including
        /// the plain .exe release) survives restarts (#15).
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public void ApplyRunOnStartup()
        {
            try
            {
                string? exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath))
                    return;

                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
                {
                    if (key == null)
                        return;

                    if (RunOnStartup)
                        key.SetValue(RunValueName, "\"" + exePath + "\"");
                    else if (key.GetValue(RunValueName) != null)
                        key.DeleteValue(RunValueName, throwOnMissingValue: false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to apply RunOnStartup: {ex.Message}");
            }
        }
    }
}
