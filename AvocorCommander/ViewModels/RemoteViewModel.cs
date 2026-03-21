using AvocorCommander.Core;
using AvocorCommander.Models;
using AvocorCommander.Services;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;

namespace AvocorCommander.ViewModels;

public sealed class RemoteViewModel : BaseViewModel
{
    private readonly DatabaseService   _db;
    private readonly ConnectionManager _connMgr;

    // ── Connected devices ─────────────────────────────────────────────────────

    public ObservableCollection<DeviceEntry> ConnectedDevices { get; } = [];

    private DeviceEntry? _selectedDevice;
    public DeviceEntry? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            Set(ref _selectedDevice, value);
            ResolveCommands();
            if (value != null) _ = QueryPowerStateAsync(value);
        }
    }

    public bool HasConnectedDevices => ConnectedDevices.Count > 0;

    // ── Power state tracking ──────────────────────────────────────────────────

    // Separate power commands since toggle logic differs by series
    private CommandEntry? _powerToggleCmd;
    private CommandEntry? _powerOnCmd;
    private CommandEntry? _powerOffCmd;

    private CommandEntry? _powerQueryCmd;

    private bool _isPowerOn = true;   // Seeded by query on device select; optimistic fallback
    public bool IsPowerOn
    {
        get => _isPowerOn;
        private set => Set(ref _isPowerOn, value);
    }

    // ── Button states ─────────────────────────────────────────────────────────

    public RemoteButtonState Power   { get; } = new();
    public RemoteButtonState Home    { get; } = new();
    public RemoteButtonState Source  { get; } = new();
    public RemoteButtonState Up      { get; } = new();
    public RemoteButtonState Down    { get; } = new();
    public RemoteButtonState Left    { get; } = new();
    public RemoteButtonState Right   { get; } = new();
    public RemoteButtonState Ok      { get; } = new();
    public RemoteButtonState Back    { get; } = new();
    public RemoteButtonState VolUp   { get; } = new();
    public RemoteButtonState VolDown { get; } = new();
    public RemoteButtonState Menu    { get; } = new();

    // ── Status ────────────────────────────────────────────────────────────────

    private string _statusMessage = "Select a connected device.";
    public string StatusMessage
    {
        get => _statusMessage;
        set => Set(ref _statusMessage, value);
    }

    // ── Command ───────────────────────────────────────────────────────────────

    public ICommand SendButtonCommand { get; }

    // ── Flash timer ───────────────────────────────────────────────────────────

    private readonly DispatcherTimer _flashTimer;
    private RemoteButtonState? _lastFlashed;

    // ── Button → DB lookup priorities ────────────────────────────────────────
    // Each entry is tried in order; first match in the DB wins.
    // This is intentionally data-driven so future series auto-adapt
    // as long as they use matching CommandCategory / CommandName values.

    private static readonly Dictionary<string, (string Cat, string Name)[]> ButtonMap = new()
    {
        ["PowerToggle"] =
        [
            ("Power", "Power Toggle"),
        ],
        ["PowerOn"] =
        [
            ("Power", "Power On"),
            ("Power", "Wake"),
        ],
        ["PowerOff"] =
        [
            ("Power", "Power Off"),
            ("Power", "Standby"),
        ],
        ["PowerQuery"] =
        [
            ("Power", "Power ?"),
            ("Power", "Power Query"),
            ("Power", "Power Status"),
        ],
        ["Home"] =
        [
            ("Remote Control", "Home"),
            ("Video Source",   "Home"),
            ("Input",          "Home"),
            ("Source",         "Home"),
            ("Source",         "SET Home (Android)"),
        ],
        ["Source"] =
        [
            ("Remote Control", "Source"),
            ("Remote Control", "Input"),
        ],
        ["Up"] =
        [
            ("Remote Control", "Up"),
            ("Remote Control", "UP"),
            ("IR Emulation",   "Cursor Up"),
        ],
        ["Down"] =
        [
            ("Remote Control", "Down"),
            ("Remote Control", "DOWN"),
            ("IR Emulation",   "Cursor Down"),
        ],
        ["Left"] =
        [
            ("Remote Control", "Left"),
            ("Remote Control", "LEFT"),
            ("IR Emulation",   "Cursor Left"),
        ],
        ["Right"] =
        [
            ("Remote Control", "Right"),
            ("Remote Control", "RIGHT"),
            ("IR Emulation",   "Cursor Right"),
        ],
        ["Ok"] =
        [
            ("Remote Control", "OK"),
            ("Remote Control", "Confirm"),
            ("Remote Control", "Enter"),
            ("IR Emulation",   "OK"),
        ],
        ["Back"] =
        [
            ("Remote Control", "Back/Exit"),
            ("Remote Control", "Exit"),
            ("Remote Control", "Return/Back"),
            ("Remote Control", "Return"),
            ("IR Emulation",   "Back"),
        ],
        ["VolUp"] =
        [
            ("Remote Control", "Vol+"),
            ("Remote Control", "VOLUME+"),
            ("Sound",          "Volume UP"),
            ("Volume",         "Volume Up"),
            ("Volume",         "Volume Up by 1"),
            ("Audio",          "Volume+ by Increment 25%"),
        ],
        ["VolDown"] =
        [
            ("Remote Control", "Vol-"),
            ("Remote Control", "VOLUME-"),
            ("Sound",          "Volume DOWN"),
            ("Volume",         "Volume Down"),
            ("Volume",         "Volume Down by 1"),
            ("Audio",          "Volume- by Increment 25%"),
        ],
        ["Menu"] =
        [
            ("Remote Control", "Menu"),
            ("Remote Control", "MENU"),
        ],
    };

    // ── Constructor ───────────────────────────────────────────────────────────

    public RemoteViewModel(DatabaseService db, ConnectionManager connMgr)
    {
        _db      = db;
        _connMgr = connMgr;

        _connMgr.ConnectionChanged += OnConnectionChanged;

        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer.Stop();
            _lastFlashed?.ClearFlash();
            _lastFlashed = null;
        };

        SendButtonCommand = new AsyncRelayCommand<string>(SendButtonAsync);
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    public void LoadDevices()
    {
        var all = _db.GetAllDevices();
        ConnectedDevices.Clear();

        foreach (var d in all)
        {
            if (_connMgr.IsConnected(d.Id))
            {
                d.IsConnected = true;
                ConnectedDevices.Add(d);
            }
        }

        OnPropertyChanged(nameof(HasConnectedDevices));

        // Auto-select first connected device (or keep selection if still valid)
        if (SelectedDevice != null && ConnectedDevices.Any(d => d.Id == SelectedDevice.Id))
        {
            // Re-resolve in case commands changed
            ResolveCommands();
        }
        else
        {
            SelectedDevice = ConnectedDevices.FirstOrDefault();
        }

        StatusMessage = HasConnectedDevices
            ? $"Ready — {ConnectedDevices.Count} device(s) connected."
            : "No devices connected. Connect a device from the Devices section.";
    }

    // ── Connection change handler (live update) ────────────────────────────────

    private void OnConnectionChanged(object? sender, DeviceEntry device)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(LoadDevices);
    }

    // ── Command resolution ────────────────────────────────────────────────────

    private void ResolveCommands()
    {
        // Clear all button states
        Power.Command   = null;
        Home.Command    = null;
        Source.Command  = null;
        Up.Command      = null;
        Down.Command    = null;
        Left.Command    = null;
        Right.Command   = null;
        Ok.Command      = null;
        Back.Command    = null;
        VolUp.Command   = null;
        VolDown.Command = null;
        Menu.Command    = null;

        _powerToggleCmd = null;
        _powerOnCmd     = null;
        _powerOffCmd    = null;
        _powerQueryCmd  = null;

        if (SelectedDevice == null) return;

        var series = _db.GetSeriesForModel(SelectedDevice.ModelNumber);
        if (series == null) return;

        // Load all commands for this series once (avoids repeated DB hits)
        var allCmds = _db.GetCommandsBySeries(series);
        CommandEntry? Resolve(string key) => FindFirst(allCmds, ButtonMap[key]);

        // Power — resolve all variants including query
        _powerToggleCmd = Resolve("PowerToggle");
        _powerOnCmd     = Resolve("PowerOn");
        _powerOffCmd    = Resolve("PowerOff");
        _powerQueryCmd  = Resolve("PowerQuery");

        // Power button is available if any power command exists
        Power.Command = _powerToggleCmd ?? _powerOnCmd ?? _powerOffCmd;

        Home.Command    = Resolve("Home");
        Source.Command  = Resolve("Source");
        Up.Command      = Resolve("Up");
        Down.Command    = Resolve("Down");
        Left.Command    = Resolve("Left");
        Right.Command   = Resolve("Right");
        Ok.Command      = Resolve("Ok");
        Back.Command    = Resolve("Back");
        VolUp.Command   = Resolve("VolUp");
        VolDown.Command = Resolve("VolDown");
        Menu.Command    = Resolve("Menu");
    }

    private static CommandEntry? FindFirst(List<CommandEntry> cmds, (string Cat, string Name)[] priorities)
    {
        foreach (var (cat, name) in priorities)
        {
            var match = cmds.FirstOrDefault(c =>
                string.Equals(c.CommandCategory, cat,  StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.CommandName,     name, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return null;
    }

    // ── Send ──────────────────────────────────────────────────────────────────

    private async Task SendButtonAsync(string? buttonKey)
    {
        if (buttonKey == null || SelectedDevice == null) return;
        if (!_connMgr.IsConnected(SelectedDevice.Id))
        {
            StatusMessage = "Device is no longer connected.";
            return;
        }

        var (btnState, cmd) = buttonKey switch
        {
            "Power"   => (Power,   ResolvePowerCommand()),
            "Home"    => (Home,    Home.Command),
            "Source"  => (Source,  Source.Command),
            "Up"      => (Up,      Up.Command),
            "Down"    => (Down,    Down.Command),
            "Left"    => (Left,    Left.Command),
            "Right"   => (Right,   Right.Command),
            "Ok"      => (Ok,      Ok.Command),
            "Back"    => (Back,    Back.Command),
            "VolUp"   => (VolUp,   VolUp.Command),
            "VolDown" => (VolDown, VolDown.Command),
            "Menu"    => (Menu,    Menu.Command),
            _         => ((RemoteButtonState?)null, (CommandEntry?)null)
        };

        if (btnState == null || cmd == null) return;

        byte[] bytes = cmd.GetBytes();
        if (bytes.Length == 0)
        {
            Flash(btnState, success: false);
            StatusMessage = $"{buttonKey}: Cannot parse command bytes.";
            return;
        }

        string hexStr = string.Join(" ", bytes.Select(b => b.ToString("X2")));
        var response  = await _connMgr.SendAsync(SelectedDevice.Id, bytes);
        bool ok       = response != null;

        // For power toggle, flip the tracked state on success
        if (buttonKey == "Power" && ok) IsPowerOn = !IsPowerOn;

        Flash(btnState, ok);

        var series = _db.GetSeriesForModel(SelectedDevice.ModelNumber) ?? "";
        string parsed = response is { Length: > 0 }
            ? ResponseParser.Parse(response, bytes, cmd.CommandFormat, series)
            : ok ? "No response data." : "No response (timeout).";

        _db.LogCommand(new AuditLogEntry
        {
            Timestamp     = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            DeviceName    = SelectedDevice.DeviceName,
            DeviceAddress = SelectedDevice.IPAddress,
            CommandName   = $"[Remote] {buttonKey} — {cmd.CommandName}",
            CommandCode   = hexStr,
            Response      = parsed,
            Success       = ok,
        });

        StatusMessage = ok
            ? $"{buttonKey}: {cmd.CommandName} → {parsed}"
            : $"{buttonKey}: No response (timeout).";
    }

    /// <summary>
    /// Sends a power query command to seed IsPowerOn from the device's actual state.
    /// Fire-and-forget; abandoned if the device changes before the response arrives.
    /// </summary>
    private async Task QueryPowerStateAsync(DeviceEntry device)
    {
        if (_powerQueryCmd == null || !_connMgr.IsConnected(device.Id)) return;

        byte[] bytes = _powerQueryCmd.GetBytes();
        if (bytes.Length == 0) return;

        var response = await _connMgr.SendAsync(device.Id, bytes);

        // Guard: device may have changed while we were waiting
        if (response == null || device != SelectedDevice) return;

        // Parse response — look for "ON" or "OFF" in the ASCII reply
        string text = Encoding.ASCII.GetString(response).ToUpperInvariant();
        if      (text.Contains("ON"))  IsPowerOn = true;
        else if (text.Contains("OFF")) IsPowerOn = false;
        // If neither, leave IsPowerOn at its current value
    }

    private CommandEntry? ResolvePowerCommand()
    {
        // If a true toggle command exists, always use it
        if (_powerToggleCmd != null) return _powerToggleCmd;

        // Otherwise choose On or Off based on tracked state
        return IsPowerOn ? _powerOffCmd : _powerOnCmd;
    }

    private void Flash(RemoteButtonState btn, bool success)
    {
        // Clear any previous flash immediately
        _lastFlashed?.ClearFlash();
        _flashTimer.Stop();

        _lastFlashed = btn;
        if (success) btn.FlashSuccess = true;
        else         btn.FlashError   = true;

        _flashTimer.Start();
    }
}
