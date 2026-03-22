using AvocorCommander.Dialogs;
using AvocorCommander.Models;
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

        vm.PanelVM.AddSceneRequested        += OnAddScene;
        vm.PanelVM.RenameSceneRequested     += OnRenameScene;
        vm.PanelVM.AddPageRequested         += OnAddPage;
        vm.PanelVM.RenamePageRequested      += OnRenamePage;
        vm.PanelVM.EditGridSlotRequested    += OnEditGridSlot;
        vm.PanelVM.EditBottomButtonRequested += OnEditBottomButton;
        vm.PanelVM.KioskModeChanged         += OnKioskModeChanged;
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

    // ── Panel dialog handlers ─────────────────────────────────────────────────

    private void OnAddScene(object? sender, EventArgs e)
    {
        var dlg = new NameInputDialog("Scene name:", "Scene 1") { Owner = this };
        if (dlg.ShowDialog() == true)
            VM.PanelVM.AddScene(dlg.Result);
    }

    private void OnRenameScene(object? sender, PanelScene scene)
    {
        var dlg = new NameInputDialog("Scene name:", scene.Name) { Owner = this };
        if (dlg.ShowDialog() == true)
            VM.PanelVM.RenameScene(scene, dlg.Result);
    }

    private void OnAddPage(object? sender, EventArgs e)
    {
        var dlg = new NameInputDialog("Page name:", "Page 1") { Owner = this };
        if (dlg.ShowDialog() == true)
            VM.PanelVM.AddPage(dlg.Result);
    }

    private void OnRenamePage(object? sender, PanelPage page)
    {
        var dlg = new NameInputDialog("Page name:", page.Name) { Owner = this };
        if (dlg.ShowDialog() == true)
            VM.PanelVM.RenamePage(page, dlg.Result);
    }

    private void OnEditGridSlot(object? sender, PanelGridSlot slot)
    {
        var existing  = slot.Button?.Model;
        var actionsA  = slot.Button?.ActionsA;
        var actionsB  = slot.Button?.ActionsB;
        var dlg = new EditPanelButtonDialog(VM.Database, existing, actionsA, actionsB) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.ResultButton != null)
            VM.PanelVM.SaveGridSlot(slot, dlg.ResultButton, dlg.ResultActionsA ?? [], dlg.ResultActionsB ?? []);
    }

    private void OnEditBottomButton(object? sender, PanelButtonVM? bvm)
    {
        var existing = bvm?.Model;
        var actionsA = bvm?.ActionsA;
        var actionsB = bvm?.ActionsB;
        var dlg = new EditPanelButtonDialog(VM.Database, existing, actionsA, actionsB) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.ResultButton != null)
            VM.PanelVM.SaveBottomButton(bvm, dlg.ResultButton, dlg.ResultActionsA ?? [], dlg.ResultActionsB ?? []);
    }

    private void OnKioskModeChanged(object? sender, bool isKiosk)
    {
        SidebarBorder.Visibility = isKiosk ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.Width      = isKiosk ? new System.Windows.GridLength(0) : new System.Windows.GridLength(200);
    }
}
