namespace WindowScatter
{
    internal readonly record struct OverviewShortcutEvent(bool Suppress, bool Activate, bool AppExpose,
        int Modifiers, WindowActivationTarget Foreground, WindowActivationTarget ApplicationWindow = default,
        string? CaptureError = null);

    internal sealed class OverviewShortcutInput
    {
        private readonly KeyboardModifierState modifiers = new(), recordingModifiers = new();
        private readonly ScatterInteraction interaction;
        private readonly Func<WindowActivationTarget> captureForeground;
        private readonly Func<WindowActivationTarget> capturePointerWindow;
        private bool wasRecording;
        private readonly HashSet<int> pressedKeys = new(), suppressedKeys = new();

        public OverviewShortcutInput(ScatterInteraction interaction, Func<WindowActivationTarget> captureForeground,
            Func<WindowActivationTarget> capturePointerWindow)
        {
            this.interaction = interaction;
            this.captureForeground = captureForeground;
            this.capturePointerWindow = capturePointerWindow;
        }

        public void Reset()
        {
            modifiers.Clear();
            interaction.ActivationKeyUp();
        }

        public OverviewShortcutEvent Process(int key, bool down, bool recording, OverviewHotkeys? bindings)
        {
            if (recording)
            {
                if (!wasRecording) recordingModifiers.Clear();
                wasRecording = true;
                recordingModifiers.Update(key, down);
                if (down) pressedKeys.Add(key);
                else { pressedKeys.Remove(key); suppressedKeys.Remove(key); }
                Reset();
                return new(false, false, false, recordingModifiers.Modifiers, default);
            }
            if (wasRecording)
            {
                wasRecording = false;
                recordingModifiers.Clear();
                Reset();
            }
            modifiers.Update(key, down);
            if (!down)
            {
                pressedKeys.Remove(key);
                return new(suppressedKeys.Remove(key), false, false, modifiers.Modifiers, default);
            }
            bool firstDown = pressedKeys.Add(key);
            bool normal = bindings?.MatchesNormal(key, modifiers.Modifiers) == true;
            bool app = bindings?.MatchesAppExpose(key, modifiers.Modifiers) == true;
            if (firstDown && (normal || app)) suppressedKeys.Add(key);
            bool activate = firstDown && (normal || app);
            // Capture in the hook, before its caller can schedule or activate an overlay.
            bool opening = activate && !interaction.IsOpen;
            var foreground = opening ? captureForeground() : default;
            WindowActivationTarget applicationWindow = default;
            string? captureError = null;
            if (opening && app)
            {
                try
                {
                    applicationWindow = capturePointerWindow();
                    if (applicationWindow == default) activate = false;
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException ||
                    ex is InvalidOperationException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                    captureError = ex.Message;
                }
            }
            return new(suppressedKeys.Contains(key), activate, app, modifiers.Modifiers, foreground, applicationWindow, captureError);
        }
    }
}
