namespace WindowScatter
{
    internal sealed class PreferencesService(AppSettings settings, Action<AppSettings> save)
    {
        internal bool TryApply(AppSettings candidate, Action apply, Action rollback, Action<Exception> reportError,
            Action? prepare = null)
        {
            var previous = settings.Copy();
            bool persisted = false;
            bool rollbackRequired = false;
            try
            {
                candidate.Validate();
                rollbackRequired = prepare != null;
                prepare?.Invoke();
                settings.CopyFrom(candidate);
                save(settings);
                persisted = true;
                rollbackRequired = true;
                apply();
                return true;
            }
            catch (Exception error)
            {
                settings.CopyFrom(previous);
                var errors = new List<Exception> { error };
                try { if (rollbackRequired) rollback(); }
                catch (Exception rollbackError) { errors.Add(rollbackError); }
                try { if (persisted) save(settings); }
                catch (Exception saveError) { errors.Add(saveError); }
                reportError(errors.Count == 1 ? error : new AggregateException(
                    "Settings could not be applied, and restoring the previous state also failed.", errors));
                return false;
            }
        }

        internal bool TrySetRunOnStartup(bool enabled, Action apply, Action<Exception> reportError)
        {
            var candidate = settings.Copy();
            candidate.RunOnStartup = enabled;
            return TryApply(candidate, apply, apply, reportError);
        }

    }
}
