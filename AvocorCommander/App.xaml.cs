using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;

namespace AvocorCommander;

public partial class App : System.Windows.Application
{
    private NotifyIcon? _trayIcon;
    private Mutex? _singleInstanceMutex;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
    private const int SW_RESTORE = 9;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single-instance enforcement: if another instance is already running,
        // bring its window to the front and exit this one.
        _singleInstanceMutex = new Mutex(true, "AvocorCommander_SingleInstance", out bool isNew);
        if (!isNew)
        {
            BringExistingInstanceToFront();
            Shutdown();
            return;
        }

        // Extract the icon from the exe itself (set via ApplicationIcon in csproj)
        Icon trayIcon;
        try
        {
            trayIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        }
        catch
        {
            trayIcon = SystemIcons.Application;
        }

        _trayIcon = new NotifyIcon
        {
            Icon    = trayIcon,
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

    private static void BringExistingInstanceToFront()
    {
        var current = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName(current.ProcessName);
        foreach (var proc in processes)
        {
            if (proc.Id == current.Id) continue;
            if (proc.MainWindowHandle != IntPtr.Zero)
            {
                if (IsIconic(proc.MainWindowHandle))
                    ShowWindow(proc.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(proc.MainWindowHandle);
                return;
            }
        }
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
        (MainWindow?.DataContext as IDisposable)?.Dispose();
        _trayIcon?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
