namespace WindowScatter;

internal static class UpdateConsent
{
    internal static bool Confirm(string version, CancellationToken cancellation,
        Func<bool> resolveUnsavedChanges, Func<string, bool> askConfirmation)
    {
        if (cancellation.IsCancellationRequested || !resolveUnsavedChanges() || cancellation.IsCancellationRequested)
            return false;
        string message = $"Install MissionView {version}?\n\n" +
            "The download's integrity and release origin were verified. Its publisher signature (Authenticode) was not verified. " +
            "Review any Windows publisher, SmartScreen or UAC warnings before continuing.\n\n" +
            "The release manifest does not specify a minimum Windows version. This app's build target does not guarantee update compatibility.\n\n" +
            "MissionView will close after Setup starts. Setup will guide you through installation.";
        return askConfirmation(message) && !cancellation.IsCancellationRequested;
    }
}
