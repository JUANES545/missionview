using System.Windows;

namespace WindowScatter
{
    public partial class App : Application
    {
        internal AppSettings Settings { get; private set; } = null!;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            try { Settings = AppSettings.Load(); }
            catch (Exception ex) when (AppSettings.IsPersistenceError(ex))
            {
                System.Diagnostics.Trace.TraceError($"Could not load or migrate settings: {ex}");
                MessageBox.Show($"MissionView could not load or migrate your settings. No existing settings were replaced.\n\n{ex.Message}",
                    "MissionView", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
    }
}
