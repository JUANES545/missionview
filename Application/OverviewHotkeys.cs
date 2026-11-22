namespace WindowScatter
{
    // Prepare both routes before publishing a new dispatch configuration.
    internal sealed class OverviewHotkeys
    {
        private readonly Func<int, OverviewHotkey, bool> register;
        private readonly Func<int, bool> unregister;
        private readonly Func<bool> canIntercept;
        private readonly Dictionary<int, OverviewHotkey> registered = new();
        private Dictionary<int, OverviewHotkey> intercepted = new();
        private OverviewHotkey? inheritedShiftShortcut;
        public OverviewHotkey? Normal { get; private set; }
        public OverviewHotkey? AppExpose { get; private set; }
        public bool IsFaulted { get; private set; }
        public int Revision { get; private set; }
        public const int NormalId = 0x4D10, AppExposeId = 0x4D11;

        public OverviewHotkeys(Func<int, OverviewHotkey, bool> register, Func<int, bool> unregister,
            Func<bool>? canIntercept = null)
        {
            this.register = register;
            this.unregister = unregister;
            this.canIntercept = canIntercept ?? (() => false);
        }

        public bool TryInitialize(string text, out string error) => TryInitialize(text, null, out error);

        public bool TryInitialize(string text, string? appExposeText, out string error)
            => TryApply(text, appExposeText, preserveExistingShiftShortcut: true, out error);

        public bool MatchesNormal(int key, int modifiers) => Normal is { } normal && normal.Key == key && normal.Modifiers == modifiers;
        public bool MatchesAppExpose(int key, int modifiers) => AppExpose is { } app && app.Key == key && app.Modifiers == modifiers;

        public bool TryApply(string text, bool preserveExistingShiftShortcut, out string error) =>
            TryApply(text, null, preserveExistingShiftShortcut, out error);

        public bool TryApply(string text, string? appExposeText, bool preserveExistingShiftShortcut, out string error)
        {
            if (!OverviewShortcutPair.TryParse(text, appExposeText, out var pair, out error)) return false;
            var normal = pair.Normal;
            bool shiftConflict = pair.AppExpose == null;
            if (shiftConflict && !preserveExistingShiftShortcut && inheritedShiftShortcut != normal)
            {
                error = "This shortcut already includes Shift. Choose one without Shift to enable both overview shortcuts. The previous shortcut is unchanged.";
                return false;
            }
            var desired = new Dictionary<int, OverviewHotkey>();
            desired.Add(NormalId, normal);
            if (pair.AppExpose is { } app) desired.Add(AppExposeId, app);
            var nextIntercepted = new Dictionary<int, OverviewHotkey>();
            if (intercepted.Count != 0 && !canIntercept())
            {
                error = "The overview keyboard handler is unavailable. The previous shortcuts are unchanged.";
                return false;
            }
            var previous = new Dictionary<int, OverviewHotkey>(registered);
            var removed = new List<KeyValuePair<int, OverviewHotkey>>();
            var added = new List<int>();
            foreach (var old in previous.Where(p => !desired.TryGetValue(p.Key, out var value) || value != p.Value))
            {
                if (!unregister(old.Key))
                    return Rollback(removed, added, "Windows could not release the previous shortcut.", out error);
                registered.Remove(old.Key);
                removed.Add(old);
            }
            foreach (var item in desired.Where(p => !registered.ContainsKey(p.Key)))
            {
                if (intercepted.TryGetValue(item.Key, out var current) && current == item.Value)
                {
                    nextIntercepted.Add(item.Key, item.Value);
                    continue;
                }
                if (!register(item.Key, item.Value))
                {
                    if (!canIntercept())
                        return Rollback(removed, added,
                            "Windows could not reserve a shortcut and the overview keyboard handler is unavailable.", out error);
                    nextIntercepted.Add(item.Key, item.Value);
                    continue;
                }
                registered.Add(item.Key, item.Value);
                added.Add(item.Key);
            }
            Normal = normal;
            AppExpose = pair.AppExpose;
            intercepted = nextIntercepted;
            Revision++;
            IsFaulted = false;
            if (shiftConflict)
            {
                inheritedShiftShortcut = normal;
                error = "Your existing shortcut includes Shift and still opens the general overview. App Expose is unavailable until you choose a shortcut without Shift.";
            }
            return true;
        }

        private bool Rollback(List<KeyValuePair<int, OverviewHotkey>> removed, List<int> added, string reason, out string error)
        {
            bool restored = true;
            foreach (int id in added)
            {
                if (unregister(id)) registered.Remove(id);
                else restored = false;
            }
            foreach (var old in removed)
            {
                if (registered.ContainsKey(old.Key)) { restored = false; continue; }
                if (register(old.Key, old.Value)) registered[old.Key] = old.Value;
                else restored = false;
            }
            if (!restored)
            {
                IsFaulted = true;
                Normal = AppExpose = null;
                intercepted.Clear();
                Revision++;
            }
            error = restored ? reason + " The previous shortcuts are unchanged." :
                reason + " Windows also rejected rollback. Shortcuts are disabled; use the tray and retry Settings.";
            return false;
        }

        public bool Release()
        {
            bool success = true;
            foreach (int id in registered.Keys.ToArray())
            {
                if (unregister(id)) registered.Remove(id);
                else success = false;
            }
            Normal = AppExpose = null;
            intercepted.Clear();
            Revision++;
            return success;
        }
    }
}
