using System.ComponentModel;
using System.Reflection;
using System.Runtime.ExceptionServices;
using WindowScatter;
using Xunit;
using Forms = System.Windows.Forms;

public class TrayOverviewTests
{
    [Theory]
    [InlineData(Forms.MouseButtons.Left, 1)]
    [InlineData(Forms.MouseButtons.Right, 0)]
    [InlineData(Forms.MouseButtons.Middle, 0)]
    public void ActualNotifyIconEventsOpenOverviewOnlyForLeftDoubleClick(Forms.MouseButtons button, int expected)
        => Sta(() =>
        {
            using var icon = new Forms.NotifyIcon();
            using var menu = new Forms.ContextMenuStrip();
            int calls = 0, menuClicks = 0, opening = 0;
            using var action = new Forms.ToolStripMenuItem("Existing action", null, (_, _) => menuClicks++);
            menu.Items.Add(action);
            CancelEventHandler cancelOpening = (_, e) => { opening++; e.Cancel = true; };
            menu.Opening += cancelOpening;
            icon.ContextMenuStrip = menu;
            MainWindow.BindTrayOverview(icon, () => { calls++; return Task.CompletedTask; });

            Assert.False(icon.Visible);
            // NotifyIcon raises the button-agnostic event and then the mouse event for a double click.
            Raise(icon, "OnDoubleClick", EventArgs.Empty);
            Raise(icon, "OnMouseDoubleClick", new Forms.MouseEventArgs(button, 2, 0, 0, 0));
            Assert.Equal(expected, calls);
            Assert.Same(menu, icon.ContextMenuStrip);
            Assert.Same(action, Assert.Single(menu.Items.Cast<Forms.ToolStripItem>()));
            menu.Show(new System.Drawing.Point(-30000, -30000));
            Assert.Equal(1, opening);
            Assert.False(menu.Visible);
            action.PerformClick();
            Assert.Equal(1, menuClicks);
            Assert.False(icon.Visible);
        });

    private static void Raise(Forms.NotifyIcon icon, string method, EventArgs args)
        => typeof(Forms.NotifyIcon).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(icon, [args]);

    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
