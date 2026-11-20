using System.IO;

namespace WindowScatter
{
    public class AppSettings
    {
        public string Hotkey { get; set; } = "Win+W";
        public string? AppExposeHotkey { get; set; }
        public bool EnableHotCorners { get; set; } = true;
        public string HotCornerPosition { get; set; } = "TopLeft";
        public int HotCornerDelay { get; set; } = 500;
        public double AnimationSpeed { get; set; } = 0.25;

        /// <summary>Launch automatically at logon (HKCU Run key). Applied on startup.</summary>
        public bool RunOnStartup { get; set; } = false;

        /// <summary>Persisted renderer preference; the active overview uses DWM for every value.</summary>
        public string Renderer { get; set; } = "auto";

        internal void Validate()
        {
            if (!OverviewShortcutPair.TryParse(Hotkey, AppExposeHotkey, out _, out var shortcutError))
                throw new InvalidDataException(shortcutError);
            if (HotCornerPosition == null || !new[] { "TopLeft", "TopRight", "BottomLeft", "BottomRight" }
                .Contains(HotCornerPosition, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Settings contain an unsupported hot corner.");
            if (HotCornerDelay < 0 || !double.IsFinite(AnimationSpeed) || AnimationSpeed <= 0)
                throw new InvalidDataException("Settings contain an invalid delay or animation speed.");
            if (Renderer == null || !new[] { "auto", "dcomp", "legacy" }.Contains(Renderer, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Settings contain an unsupported renderer.");
        }

        internal AppSettings Copy() => (AppSettings)MemberwiseClone();

        internal void CopyFrom(AppSettings source)
        {
            Hotkey = source.Hotkey;
            AppExposeHotkey = source.AppExposeHotkey;
            EnableHotCorners = source.EnableHotCorners;
            HotCornerPosition = source.HotCornerPosition;
            HotCornerDelay = source.HotCornerDelay;
            AnimationSpeed = source.AnimationSpeed;
            RunOnStartup = source.RunOnStartup;
            Renderer = source.Renderer;
        }

    }
}
