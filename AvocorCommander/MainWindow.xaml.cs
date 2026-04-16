using AvocorCommander.Dialogs;
using AvocorCommander.Models;
using AvocorCommander.Services;
using AvocorCommander.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace AvocorCommander;

public partial class MainWindow : Window
{
    private MainViewModel VM => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;

        // Wire dialog events
        vm.DevicesVM.AddDeviceRequested   += OnAddDevice;
        vm.DevicesVM.EditDeviceRequested  += OnEditDevice;
        vm.DevicesVM.ScanNetworkRequested += OnScanNetwork;
        vm.SchedulerVM.AddRuleRequested   += OnAddRule;
        vm.SchedulerVM.EditRuleRequested  += OnEditRule;
        vm.MacroVM.AddMacroRequested      += OnAddMacro;
        vm.MacroVM.EditMacroRequested     += OnEditMacro;

        // Macro "pause for user" step — show modal prompt
        vm.MacroRunner.PromptRequested    += OnMacroPromptRequested;
    }

    private void OnMacroPromptRequested(object? sender, MacroRunnerService.PromptRequest req)
    {
        Dispatcher.Invoke(() =>
        {
            var text = $"{req.Message}\n\nMacro: {req.MacroName}\nDevice: {req.DeviceName}\n\nContinue?";
            var result = MessageBox.Show(
                this, text,
                "Macro paused",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.OK);
            req.Response.TrySetResult(result == MessageBoxResult.OK);
        });
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        VM.Initialize();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            Hide();
    }

    // ── Dialog handlers ───────────────────────────────────────────────────────

    private void OnAddDevice(object? sender, EventArgs e)
    {
        var dlg = new AddDeviceDialog(VM.Database) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
            VM.DevicesVM.AddDevice(dlg.Result);
    }

    private void OnEditDevice(object? sender, DeviceEntry device)
    {
        var dlg = new AddDeviceDialog(VM.Database, device) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            // Copy edited values back to the existing instance
            device.DeviceName     = dlg.Result.DeviceName;
            device.ModelNumber    = dlg.Result.ModelNumber;
            device.IPAddress      = dlg.Result.IPAddress;
            device.Port           = dlg.Result.Port;
            device.MacAddress     = dlg.Result.MacAddress;
            device.Notes          = dlg.Result.Notes;
            device.ComPort        = dlg.Result.ComPort;
            device.BaudRate       = dlg.Result.BaudRate;
            device.ConnectionType = dlg.Result.ConnectionType;
            device.AutoConnect    = dlg.Result.AutoConnect;
            VM.DevicesVM.UpdateDevice(device);
        }
    }

    private void OnScanNetwork(object? sender, EventArgs e)
    {
        var dlg = new NetworkScanDialog(VM.Database) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            foreach (var result in dlg.SelectedResults)
                VM.DevicesVM.ImportFromScan(result);
        }
    }

    private void OnAddRule(object? sender, EventArgs e)
    {
        var dlg = new AddEditRuleDialog(VM.Database) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
            VM.SchedulerVM.AddRule(dlg.Result);
    }

    private void OnEditRule(object? sender, ScheduleRule rule)
    {
        var dlg = new AddEditRuleDialog(VM.Database, rule) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
            VM.SchedulerVM.UpdateRule(dlg.Result);
    }

    private void OnAddMacro(object? sender, EventArgs e)
    {
        var dlg = new AddEditMacroDialog(VM.Database) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
            VM.MacroVM.AddMacro(dlg.Result);
    }

    private void OnEditMacro(object? sender, MacroEntry macro)
    {
        var dlg = new AddEditMacroDialog(VM.Database, macro) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
            VM.MacroVM.UpdateMacro(dlg.Result);
    }
}
