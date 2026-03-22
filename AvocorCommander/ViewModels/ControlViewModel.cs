using AvocorCommander.Core;
using AvocorCommander.Models;
using AvocorCommander.Services;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Input;

namespace AvocorCommander.ViewModels;

public enum SendTarget { SelectedDevice, SelectedGroup, AllConnected }

public sealed class ControlViewModel : BaseViewModel
{
    private readonly DatabaseService   _db;
    private readonly ConnectionManager _connMgr;

    // ── Target selection ─────────────────────────────────────────────────────

    public ObservableCollection<DeviceEntry>  AvailableDevices { get; } = [];
    public ObservableCollection<GroupEntry>   AvailableGroups  { get; } = [];

    private DeviceEntry? _selectedDevice;
    public DeviceEntry? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            Set(ref _selectedDevice, value);
            RefreshCategories();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private GroupEntry? _selectedGroup;
    public GroupEntry? SelectedGroup { get => _selectedGroup; set => Set(ref _selectedGroup, value); }

    private SendTarget _sendTarget = SendTarget.SelectedDevice;
    public SendTarget SendTarget
    {
        get => _sendTarget;
        set
        {
            Set(ref _sendTarget, value);
            OnPropertyChanged(nameof(TargetDevice));
            OnPropertyChanged(nameof(TargetGroup));
            OnPropertyChanged(nameof(TargetAll));
        }
    }

    public bool TargetDevice { get => SendTarget == SendTarget.SelectedDevice; set { if (value) SendTarget = SendTarget.SelectedDevice; } }
    public bool TargetGroup  { get => SendTarget == SendTarget.SelectedGroup;  set { if (value) SendTarget = SendTarget.SelectedGroup; } }
    public bool TargetAll    { get => SendTarget == SendTarget.AllConnected;   set { if (value) SendTarget = SendTarget.AllConnected; } }

    // ── Command selection ─────────────────────────────────────────────────────

    public ObservableCollection<string>       AvailableCategories { get; } = [];
    public ObservableCollection<CommandEntry> AvailableCommands   { get; } = [];

    private string? _selectedCategory;
    public string? SelectedCategory
    {
        get => _selectedCategory;
        set { Set(ref _selectedCategory, value); RefreshCommands(); }
    }

    private CommandEntry? _selectedCommand;
    public CommandEntry? SelectedCommand
    {
        get => _selectedCommand;
        set
        {
            Set(ref _selectedCommand, value);
            RawCode = value?.CommandCode ?? string.Empty;
            OnPropertyChanged(nameof(HasVariableBytes));
            OnPropertyChanged(nameof(HasCommand));
        }
    }

    public bool HasVariableBytes => _selectedCommand?.HasVariableBytes ?? false;
    public bool HasCommand       => _selectedCommand != null;

    // ── Raw code ──────────────────────────────────────────────────────────────

    private string _rawCode = string.Empty;
    public string RawCode { get => _rawCode; set => Set(ref _rawCode, value); }

    // ── Log (in-memory for this session) ─────────────────────────────────────

    public ObservableCollection<AuditLogEntry> SessionLog { get; } = [];

    // ── Status bar ───────────────────────────────────────────────────────────

    private string _statusMessage = "Ready.";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ICommand SendCommand        { get; }
    public ICommand WakeOnLanCommand   { get; }
    public ICommand ClearLogCommand    { get; }
    public ICommand ExportLogCommand   { get; }
    public ICommand RefreshCommand     { get; }

    public ControlViewModel(DatabaseService db, ConnectionManager connMgr)
    {
        _db      = db;
        _connMgr = connMgr;

        SendCommand      = new AsyncRelayCommand(SendAsync,      () => SelectedCommand != null);
        WakeOnLanCommand = new AsyncRelayCommand(WakeOnLanAsync, CanWakeOnLan);
        ClearLogCommand  = new RelayCommand(() => SessionLog.Clear());
        ExportLogCommand = new RelayCommand(ExportLog);
        RefreshCommand   = new RelayCommand(LoadData);
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    public void LoadData()
    {
        var devices = _db.GetAllDevices();
        AvailableDevices.Clear();
        foreach (var d in devices)
        {
            d.IsConnected = _connMgr.IsConnected(d.Id);
            AvailableDevices.Add(d);
        }

        var groups = _db.GetAllGroups();
        AvailableGroups.Clear();
        foreach (var g in groups) AvailableGroups.Add(g);

        SelectedDevice = AvailableDevices.FirstOrDefault(d => d.IsConnected)
                      ?? AvailableDevices.FirstOrDefault();
    }

    /// <summary>Pre-select a device by ID after LoadData() has been called.</summary>
    public void SelectDevice(int deviceId)
    {
        var device = AvailableDevices.FirstOrDefault(d => d.Id == deviceId);
        if (device != null) SelectedDevice = device;
    }

    // ── Category / command refresh ────────────────────────────────────────────

    private void RefreshCategories()
    {
        AvailableCategories.Clear();
        SelectedCategory = null;
        if (SelectedDevice == null) return;

        var series = _db.GetSeriesForModel(SelectedDevice.ModelNumber);
        if (series == null) return;

        var cats = _db.GetCommandsBySeries(series)
            .Select(c => c.CommandCategory).Distinct().Order().ToList();

        foreach (var c in cats) AvailableCategories.Add(c);
        SelectedCategory = cats.FirstOrDefault();
    }

    private void RefreshCommands()
    {
        AvailableCommands.Clear();
        SelectedCommand = null;
        if (SelectedDevice == null || SelectedCategory == null) return;

        var series = _db.GetSeriesForModel(SelectedDevice.ModelNumber);
        if (series == null) return;

        var cmds = _db.GetCommandsBySeries(series)
            .Where(c => c.CommandCategory == SelectedCategory)
            .OrderBy(c => c.CommandName).ToList();

        foreach (var c in cmds) AvailableCommands.Add(c);
        SelectedCommand = cmds.FirstOrDefault();
    }

    // ── Send ──────────────────────────────────────────────────────────────────

    private async Task SendAsync()
    {
        if (SelectedCommand == null) return;

        byte[] bytes = SelectedCommand.GetBytes(RawCode);
        if (bytes.Length == 0)
        {
            Log("—", "Cannot parse command bytes — check the Raw Command field.", false);
            return;
        }

        var targets = GetTargetDevices();
        if (targets.Count == 0)
        {
            StatusMessage = "No connected devices to send to.";
            return;
        }

        if (targets.Count > 1)
            StatusMessage = $"Sending to {targets.Count} devices…";

        string hexStr = string.Join(" ", bytes.Select(b => b.ToString("X2")));

        foreach (var device in targets)
        {
            if (!_connMgr.IsConnected(device.Id))
            {
                Log(device.DeviceName, "Not connected — skipped.", false);
                continue;
            }

            Log(device.DeviceName, $"▶ {SelectedCommand.CommandName}  [{hexStr}]", true);

            var response = await _connMgr.SendAsync(device.Id, bytes);
            bool ok = response != null;

            var series = _db.GetSeriesForModel(device.ModelNumber) ?? "";
            string parsedResponse = response is { Length: > 0 }
                ? ResponseParser.Parse(response, bytes, SelectedCommand.CommandFormat, series)
                : ok ? "No response data." : "No response (timeout).";

            // Audit log to DB
            _db.LogCommand(new AuditLogEntry
            {
                Timestamp     = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                DeviceName    = device.DeviceName,
                DeviceAddress = device.IPAddress,
                CommandName   = SelectedCommand.CommandName,
                CommandCode   = hexStr,
                Response      = parsedResponse,
                Success       = ok,
            });

            Log(device.DeviceName, $"◀ {parsedResponse}", ok);
        }

        StatusMessage = $"Sent to {targets.Count} device(s) at {DateTime.Now:HH:mm:ss}";
    }

    private List<DeviceEntry> GetTargetDevices()
    {
        return SendTarget switch
        {
            SendTarget.SelectedDevice => SelectedDevice != null ? [SelectedDevice] : [],
            SendTarget.SelectedGroup  => SelectedGroup == null ? [] :
                AvailableDevices
                    .Where(d => SelectedGroup.MemberDeviceIds.Contains(d.Id) && _connMgr.IsConnected(d.Id))
                    .ToList(),
            SendTarget.AllConnected => AvailableDevices
                    .Where(d => _connMgr.IsConnected(d.Id)).ToList(),
            _ => []
        };
    }

    private void Log(string deviceName, string message, bool success)
    {
        var entry = new AuditLogEntry
        {
            Timestamp   = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            DeviceName  = deviceName,
            CommandName = message,
            Success     = success,
        };
        SessionLog.Add(entry);
        while (SessionLog.Count > 500)
            SessionLog.RemoveAt(0);
        StatusMessage = message;
    }

    // ── Wake on LAN ───────────────────────────────────────────────────────────

    private bool CanWakeOnLan() =>
        SelectedDevice != null && !string.IsNullOrWhiteSpace(SelectedDevice.MacAddress);

    private async Task WakeOnLanAsync()
    {
        if (SelectedDevice == null) return;

        var mac = SelectedDevice.MacAddress.Trim();
        if (string.IsNullOrEmpty(mac))
        {
            Log(SelectedDevice.DeviceName, "Wake on LAN failed — no MAC address stored for this device.", false);
            return;
        }

        try
        {
            var macBytes = mac
                .Split(':', '-', '.')
                .Select(s => Convert.ToByte(s, 16))
                .ToArray();

            if (macBytes.Length != 6)
                throw new FormatException("MAC address must be 6 bytes.");

            // Magic packet: 6×0xFF then MAC repeated 16 times
            var packet = new byte[6 + 16 * 6];
            for (int i = 0; i < 6; i++)  packet[i] = 0xFF;
            for (int i = 0; i < 16; i++)
                Array.Copy(macBytes, 0, packet, 6 + i * 6, 6);

            using var udp = new UdpClient();
            udp.EnableBroadcast = true;
            await udp.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));

            Log(SelectedDevice.DeviceName, $"Wake on LAN packet sent to {mac}", true);

            _db.LogCommand(new AuditLogEntry
            {
                Timestamp     = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                DeviceName    = SelectedDevice.DeviceName,
                DeviceAddress = SelectedDevice.IPAddress,
                CommandName   = "Wake on LAN",
                CommandCode   = string.Join("-", macBytes.Select(b => b.ToString("X2"))),
                Success       = true,
            });
        }
        catch (Exception ex)
        {
            Log(SelectedDevice?.DeviceName ?? "—", $"Wake on LAN failed: {ex.Message}", false);
        }
    }

    private void ExportLog()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Export Session Log",
            Filter     = "Text File (*.txt)|*.txt|CSV (*.csv)|*.csv",
            DefaultExt = ".txt",
            FileName   = $"AvocorLog_{DateTime.Now:yyyyMMdd_HHmmss}"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var sb = new StringBuilder();
            string ext = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();
            if (ext == ".csv")
            {
                sb.AppendLine("Timestamp,Device,Message,Success");
                foreach (var e in SessionLog)
                    sb.AppendLine($"\"{e.Timestamp}\",\"{e.DeviceName}\",\"{e.CommandName}\",{e.Success}");
            }
            else
            {
                foreach (var e in SessionLog)
                    sb.AppendLine($"{e.TimeString}  {e.DeviceName,-20}  {e.CommandName}");
            }
            System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            StatusMessage = $"Log exported to {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }
}
