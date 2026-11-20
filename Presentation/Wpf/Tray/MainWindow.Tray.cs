namespace WindowScatter;

public partial class MainWindow
{
    internal static void BindTrayOverview(System.Windows.Forms.NotifyIcon icon, Func<Task> openOverview)
    {
        icon.MouseDoubleClick += async (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
                await openOverview();
        };
    }
}
