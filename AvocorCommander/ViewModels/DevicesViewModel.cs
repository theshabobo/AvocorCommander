using AvocorCommander.Core;
using AvocorCommander.Models;
using AvocorCommander.Services;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace AvocorCommander.ViewModels;

public sealed class DevicesViewModel : BaseViewModel
{
    private readonly DatabaseService   _db;
    private readonly ConnectionManager _connMgr;

    public ObservableCollection<DeviceEntry> Devices { get; } = [];

    private DeviceEntry? _selectedDevice;
    public DeviceEntry? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            Set(ref _selectedDevice, value);
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => SelectedDevice != null;

    // ── Status bar ───────────────────────────────────────────────────────────

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set => Set(ref _statusMessage, value);
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    public ICommand AddDeviceCommand    { get; }
    public ICommand EditDeviceCommand   { get; }
    public ICommand DeleteDeviceCommand { get; }
    public ICommand ConnectCommand      { get; }
    public ICommand DisconnectCommand   { get; }
    public ICommand ScanNetworkCommand  { get; }
    public ICommand RefreshCommand      { get; }
    public ICommand ExportDevicesCommand { get; }
    public ICommand ImportDevicesCommand { get; }
    public ICommand WakeOnLanCommand    { get; }
    public ICommand ViewHistoryCommand  { get; }

    // ── Events raised for View ────────────────────────────────────────────────

    public event EventHandler?                     AddDeviceRequested;
    public event EventHandler<DeviceEntry>?        EditDeviceRequested;
    public event EventHandler?                     ScanNetworkRequested;
    public event EventHandler<string>?             ViewHistoryRequested;

    public DevicesViewModel(DatabaseService db, ConnectionManager connMgr)
    {
        _db      = db;
        _connMgr = connMgr;

        _connMgr.ConnectionChanged += OnConnectionChanged;

        AddDeviceCommand    = new RelayCommand(
            () => AddDeviceRequested?.Invoke(this, EventArgs.Empty));
        EditDeviceCommand   = new RelayCommand<DeviceEntry>(
            d => { if (d != null) EditDeviceRequested?.Invoke(this, d); },
            d => d != null);
        DeleteDeviceCommand = new AsyncRelayCommand<DeviceEntry>(DeleteDeviceAsync,
            d => d != null);
        ConnectCommand      = new AsyncRelayCommand<DeviceEntry>(ConnectAsync,
            d => d != null && !d.IsConnected && !d.IsConnecting);
        DisconnectCommand   = new AsyncRelayCommand<DeviceEntry>(DisconnectAsync,
            d => d != null && d.IsConnected);
        ScanNetworkCommand   = new RelayCommand(
            () => ScanNetworkRequested?.Invoke(this, EventArgs.Empty));
        RefreshCommand       = new RelayCommand(LoadDevices);
        ExportDevicesCommand = new RelayCommand(ExportDevices);
        ImportDevicesCommand = new RelayCommand(ImportDevices);
        WakeOnLanCommand     = new AsyncRelayCommand<DeviceEntry>(WakeOnLanAsync, d => d != null && !string.IsNullOrWhiteSpace(d.MacAddress));
        ViewHistoryCommand   = new RelayCommand<DeviceEntry>(d => { if (d != null) ViewHistoryRequested?.Invoke(this, d.DeviceName); }, d => d != null);
    }

    // ── Initialization ────────────────────────────────────────────────────────

    public void LoadDevices()
    {
        var saved = _db.GetAllDevices();
        Devices.Clear();
        foreach (var d in saved)
        {
            d.IsConnected = _connMgr.IsConnected(d.Id);
            Devices.Add(d);
        }
        StatusMessage = $"{Devices.Count} device{(Devices.Count == 1 ? "" : "s")} loaded.";
    }

    // ── CRUD operations ───────────────────────────────────────────────────────

    public void AddDevice(DeviceEntry device)
    {
        // Auto-fill port from DB if model is set and port is 0
        if (device.Port == 0 && !string.IsNullOrEmpty(device.ModelNumber))
        {
            var series   = _db.GetSeriesForModel(device.ModelNumber);
            var commands = series != null ? _db.GetCommandsBySeries(series) : [];
            var sample   = commands.FirstOrDefault();
            if (sample != null) device.Port = sample.Port;
        }

        int newId = _db.InsertDevice(device);
        device.Id = newId;
        Devices.Add(device);
        SelectedDevice  = device;
        StatusMessage   = $"Added: {device.DeviceName}";
    }

    public void UpdateDevice(DeviceEntry device)
    {
        _db.UpdateDevice(device);
        StatusMessage = $"Updated: {device.DeviceName}";
    }

    private async Task DeleteDeviceAsync(DeviceEntry? device)
    {
        if (device == null) return;

        var result = MessageBox.Show(
            $"Remove '{device.DeviceName}' from the device list?",
            "Confirm Remove",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        if (device.IsConnected)
            await _connMgr.DisconnectAsync(device);

        _db.DeleteDevice(device.Id);
        Devices.Remove(device);
        if (SelectedDevice == device)
            SelectedDevice = Devices.FirstOrDefault();

        StatusMessage = $"Removed: {device.DeviceName}";
    }

    // ── Connection ────────────────────────────────────────────────────────────

    private async Task ConnectAsync(DeviceEntry? device)
    {
        if (device == null) return;
        StatusMessage = $"Connecting to {device.DeviceName}…";
        bool ok = await _connMgr.ConnectAsync(device);
        StatusMessage = ok
            ? $"Connected: {device.DeviceName}"
            : $"Connection failed: {device.DeviceName}";
    }

    private async Task DisconnectAsync(DeviceEntry? device)
    {
        if (device == null) return;
        await _connMgr.DisconnectAsync(device);
        StatusMessage = $"Disconnected: {device.DeviceName}";
    }

    private void OnConnectionChanged(object? sender, DeviceEntry device)
    {
        var existing = Devices.FirstOrDefault(d => d.Id == device.Id);
        if (existing != null)
        {
            existing.IsConnected  = device.IsConnected;
            existing.IsConnecting = device.IsConnecting;

            // Record connection history when device comes online
            if (device.IsConnected)
            {
                var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                _db.UpdateDeviceLastSeen(device.Id, ts);
                existing.LastSeenAt = ts;
            }
        }
    }

    private void ExportDevices()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Export Device List",
            Filter     = "JSON (*.json)|*.json",
            DefaultExt = ".json",
            FileName   = $"Devices_{DateTime.Now:yyyyMMdd}"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var records = Devices.Select(d => new
            {
                d.DeviceName, d.ModelNumber, d.IPAddress, d.Port,
                d.BaudRate, d.ComPort, d.MacAddress, d.ConnectionType, d.Notes
            });
            var json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(dlg.FileName, json);
            StatusMessage = $"Exported {Devices.Count} device(s) to {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex) { StatusMessage = $"Export failed: {ex.Message}"; }
    }

    private void ImportDevices()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Import Device List",
            Filter = "JSON (*.json)|*.json"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = System.IO.File.ReadAllText(dlg.FileName);
            var records = JsonSerializer.Deserialize<List<JsonElement>>(json);
            if (records == null) return;

            int added = 0;
            foreach (var r in records)
            {
                var name = r.TryGetProperty("DeviceName", out var p) ? p.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (Devices.Any(d => d.DeviceName.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;

                var device = new DeviceEntry
                {
                    DeviceName     = name,
                    ModelNumber    = r.TryGetProperty("ModelNumber",    out var p2) ? p2.GetString() ?? "" : "",
                    IPAddress      = r.TryGetProperty("IPAddress",      out var p3) ? p3.GetString() ?? "" : "",
                    Port           = r.TryGetProperty("Port",           out var p4) ? p4.GetInt32()       : 0,
                    BaudRate       = r.TryGetProperty("BaudRate",       out var p5) ? p5.GetInt32()       : 9600,
                    ComPort        = r.TryGetProperty("ComPort",        out var p6) ? p6.GetString() ?? "" : "",
                    MacAddress     = r.TryGetProperty("MacAddress",     out var p7) ? p7.GetString() ?? "" : "",
                    ConnectionType = r.TryGetProperty("ConnectionType", out var p8) ? p8.GetString() ?? "TCP" : "TCP",
                    Notes          = r.TryGetProperty("Notes",          out var p9) ? p9.GetString() ?? "" : "",
                };
                AddDevice(device);
                added++;
            }
            StatusMessage = $"Imported {added} new device(s) from {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex) { StatusMessage = $"Import failed: {ex.Message}"; }
    }

    // ── Called from scan dialog to add discovered devices ─────────────────────

    public void ImportFromScan(ScanResult scan)
    {
        var device = new DeviceEntry
        {
            DeviceName     = !string.IsNullOrEmpty(scan.ModelNumber) ? scan.ModelNumber
                           : !string.IsNullOrEmpty(scan.Hostname)    ? scan.Hostname
                           : scan.IpAddress,
            IPAddress      = scan.IpAddress,
            ModelNumber    = scan.ModelNumber,
            MacAddress     = scan.MacAddress,
            ConnectionType = "TCP",
        };

        // Look up default port for the series
        if (!string.IsNullOrEmpty(scan.SeriesPattern))
        {
            var cmds = _db.GetCommandsBySeries(scan.SeriesPattern);
            device.Port = cmds.FirstOrDefault()?.Port ?? 0;
        }

        AddDevice(device);
    }

    // ── Wake on LAN ───────────────────────────────────────────────────────────

    private async Task WakeOnLanAsync(DeviceEntry? device)
    {
        if (device == null) return;
        var mac = device.MacAddress.Trim();
        if (string.IsNullOrEmpty(mac)) return;
        try
        {
            var macBytes = mac.Split(':', '-', '.').Select(s => Convert.ToByte(s, 16)).ToArray();
            if (macBytes.Length != 6) throw new FormatException("MAC must be 6 bytes.");
            var packet = new byte[6 + 16 * 6];
            for (int i = 0; i < 6; i++) packet[i] = 0xFF;
            for (int i = 0; i < 16; i++) Array.Copy(macBytes, 0, packet, 6 + i * 6, 6);
            using var udp = new UdpClient();
            udp.EnableBroadcast = true;
            await udp.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
            _db.LogCommand(new AuditLogEntry
            {
                Timestamp     = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                DeviceName    = device.DeviceName, DeviceAddress = device.IPAddress,
                CommandName   = "Wake on LAN", CommandCode = mac, Success = true,
            });
            StatusMessage = $"WoL packet sent to {device.DeviceName} ({mac})";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Wake on LAN failed: {ex.Message}";
        }
    }
}
