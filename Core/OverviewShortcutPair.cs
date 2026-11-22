namespace WindowScatter
{
    internal readonly record struct OverviewHotkey(int Modifiers, int Key)
    {
        public const int Shift = 4;
        public uint NativeModifiers => (uint)((Modifiers & 12) | ((Modifiers & 1) << 1) | ((Modifiers & 2) >> 1));
        public OverviewHotkey WithShift => this with { Modifiers = Modifiers | Shift };

        public static OverviewHotkey Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Choose a supported shortcut.");
            var parts = text.Split('+');
            if (parts.Any(p => string.IsNullOrWhiteSpace(p)))
                throw new ArgumentException("Choose a supported shortcut.");
            int keys = 0;
            foreach (string part in parts)
            {
                string token = part.Trim().ToLowerInvariant();
                if (token is not ("ctrl" or "control" or "alt" or "shift" or "win" or "windows"))
                {
                    if (HotkeyParser.Parse(token).VirtualKeyCode == 0)
                        throw new ArgumentException("Choose a supported shortcut.");
                    keys++;
                }
            }
            var parsed = HotkeyParser.Parse(text);
            if (keys != 1) throw new ArgumentException("The shortcut must contain exactly one non-modifier key.");
            if (((parsed.ModifierKeys & 8) != 0 && parsed.VirtualKeyCode == 'L') ||
                ((parsed.ModifierKeys & 3) == 3 && parsed.VirtualKeyCode == 0x2E))
                throw new ArgumentException("Windows reserves this shortcut for system security. Choose another shortcut.");
            return new(parsed.ModifierKeys, parsed.VirtualKeyCode);
        }
    }

    internal readonly record struct OverviewShortcutPair(OverviewHotkey Normal, OverviewHotkey? AppExpose)
    {
        public static OverviewShortcutPair Parse(string normalText, string? appExposeText)
        {
            var normal = OverviewHotkey.Parse(normalText);
            if (appExposeText == null)
                return new(normal, (normal.Modifiers & OverviewHotkey.Shift) == 0 ? normal.WithShift : null);
            var app = OverviewHotkey.Parse(appExposeText);
            if ((app.Modifiers & 0x0B) == 0)
                throw new ArgumentException("Include Ctrl, Alt or Win in the App Expose shortcut.");
            if (app == normal)
                throw new ArgumentException("Overview and App Expose must use different shortcuts.");
            return new(normal, app);
        }

        public static bool TryParse(string normalText, string? appExposeText, out OverviewShortcutPair pair, out string error)
        {
            try
            {
                pair = Parse(normalText, appExposeText);
                error = "";
                return true;
            }
            catch (ArgumentException ex)
            {
                pair = default;
                error = ex.Message;
                return false;
            }
        }

        public static bool TryGetPreviewWarning(string normalText, string? appExposeText,
            out string warning, out string error)
        {
            warning = "";
            if (!TryParse(normalText, appExposeText, out var pair, out error)) return false;
            var widgets = new OverviewHotkey(8, 'W');
            warning = pair.Normal == widgets
                ? "Opens MissionView instead of Widgets while MissionView is running."
                : pair.AppExpose == widgets
                    ? "Opens App Expose instead of Widgets while MissionView is running."
                    : "Uses this shortcut for MissionView while it is running.";
            return true;
        }
    }
}
