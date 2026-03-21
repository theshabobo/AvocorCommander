using AvocorCommander.Models;
using AvocorCommander.Services;
using System.Windows;
using System.Windows.Controls;

namespace AvocorCommander.Dialogs;

public partial class AddDeviceDialog : Window
{
    private readonly DatabaseService _db;
    private readonly DeviceEntry?    _editing;

    public DeviceEntry? Result { get; private set; }

    public AddDeviceDialog(DatabaseService db, DeviceEntry? editing = null)
    {
        _db      = db;
        _editing = editing;
        InitializeComponent();
        PopulateModels();
        PopulateComPorts();
        if (editing != null) FillFromDevice(editing);
    }

    // ── Populate ──────────────────────────────────────────────────────────────

    private void PopulateModels()
    {
        var models = _db.GetDistinctModelNumbers();
        CmbModel.ItemsSource = models;
    }

    private void PopulateComPorts()
    {
        var ports = SerialConnectionService.GetAvailablePorts();
        CmbComPort.ItemsSource = ports;
        if (ports.Length > 0) CmbComPort.SelectedIndex = 0;
    }

    private void FillFromDevice(DeviceEntry d)
    {
        TxtName.Text          = d.DeviceName;
        CmbModel.Text         = d.ModelNumber;
        TxtIp.Text            = d.IPAddress;
        TxtPort.Text          = d.Port.ToString();
        TxtMac.Text           = d.MacAddress;
        TxtNotes.Text         = d.Notes;
        CmbComPort.Text       = d.ComPort;
        CmbBaud.Text          = d.BaudRate.ToString();

        if (d.ConnectionType == "Serial")
        {
            RdoSerial.IsChecked = true;
        }
        else
        {
            RdoTcp.IsChecked = true;
        }
        ChkAutoConnect.IsChecked = d.AutoConnect;
    }

    // ── Model selection → auto-fill port ─────────────────────────────────────

    private void CmbModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbModel.SelectedItem is not string model) return;

        // Auto-fill device name if user hasn't set one yet
        if (_editing == null && string.IsNullOrWhiteSpace(TxtName.Text))
            TxtName.Text = model;

        // Auto-fill port if not already set
        if (string.IsNullOrEmpty(TxtPort.Text))
        {
            var series = _db.GetSeriesForModel(model);
            if (series != null)
            {
                var cmds = _db.GetCommandsBySeries(series);
                var sample = cmds.FirstOrDefault();
                if (sample != null && sample.Port > 0)
                {
                    TxtPort.Text = sample.Port.ToString();
                    // Auto-select TCP when port is known
                    RdoTcp.IsChecked = true;
                }
                else if (sample != null && sample.Port == 0)
                {
                    // No TCP port — default to Serial
                    RdoSerial.IsChecked = true;
                }
            }
        }
    }

    // ── Connection type toggle ────────────────────────────────────────────────

    private void ConnType_Changed(object sender, RoutedEventArgs e)
    {
        if (TcpPanel == null || SerialPanel == null) return;
        TcpPanel.Visibility    = RdoTcp.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SerialPanel.Visibility = RdoSerial.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Buttons ───────────────────────────────────────────────────────────────

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtName.Text))
        {
            MessageBox.Show("Device name is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool isTcp = RdoTcp.IsChecked == true;

        Result = new DeviceEntry
        {
            Id             = _editing?.Id ?? 0,
            DeviceName     = TxtName.Text.Trim(),
            ModelNumber    = CmbModel.Text.Trim(),
            IPAddress      = TxtIp.Text.Trim(),
            Port           = int.TryParse(TxtPort.Text, out var p) ? p : 0,
            MacAddress     = TxtMac.Text.Trim(),
            Notes          = TxtNotes.Text.Trim(),
            ComPort        = CmbComPort.Text.Trim(),
            BaudRate       = int.TryParse(CmbBaud.Text, out var b) ? b : 9600,
            ConnectionType = isTcp ? "TCP" : "Serial",
            AutoConnect    = ChkAutoConnect.IsChecked == true,
        };

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
