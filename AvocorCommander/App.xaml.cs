using System.Drawing;
using System.Windows;
using System.Windows.Forms;

namespace AvocorCommander;

public partial class App : System.Windows.Application
{
    private NotifyIcon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _trayIcon = new NotifyIcon
        {
            Icon    = SystemIcons.Application,
            Text    = "Avocor Commander",
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show Window", null, (_, _) => ShowMainWindow());
        menu.Items.Add("-");
        menu.Items.Add("Exit", null, (_, _) => { _trayIcon.Visible = false; Shutdown(); });
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (MainWindow != null)
        {
            MainWindow.Show();
            MainWindow.WindowState = WindowState.Normal;
            MainWindow.Activate();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
