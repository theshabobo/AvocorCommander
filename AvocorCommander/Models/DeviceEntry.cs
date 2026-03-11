using AvocorCommander.Core;

namespace AvocorCommander.Models;

/// <summary>Mirrors the StoredDevices table row.</summary>
public sealed class DeviceEntry : BaseViewModel
{
    private int    _id;
    private string _deviceName  = string.Empty;
    private string _modelNumber = string.Empty;
    private string _ipAddress   = string.Empty;
    private int    _port;
    private int    _baudRate    = 9600;
    private string _comPort     = string.Empty;
    private string _macAddress  = string.Empty;
    private string _connectionType = "TCP";
    private string _notes       = string.Empty;
    private string _lastSeenAt  = string.Empty;

    public int    Id           { get => _id;             set => Set(ref _id, value); }
    public string DeviceName   { get => _deviceName;     set => Set(ref _deviceName, value); }
    public string ModelNumber  { get => _modelNumber;    set => Set(ref _modelNumber, value); }
    public string IPAddress    { get => _ipAddress;      set => Set(ref _ipAddress, value); }
    public int    Port         { get => _port;           set => Set(ref _port, value); }
    public int    BaudRate     { get => _baudRate;       set => Set(ref _baudRate, value); }
    public string ComPort      { get => _comPort;        set => Set(ref _comPort, value); }
    public string MacAddress   { get => _macAddress;     set => Set(ref _macAddress, value); }
    /// <summary>"TCP" or "Serial"</summary>
    public string ConnectionType { get => _connectionType; set => Set(ref _connectionType, value); }
    public string Notes        { get => _notes;          set => Set(ref _notes, value); }
    public string LastSeenAt   { get => _lastSeenAt;     set { Set(ref _lastSeenAt, value); OnPropertyChanged(nameof(LastSeenDisplay)); } }

    public string LastSeenDisplay => string.IsNullOrEmpty(LastSeenAt) ? "Never connected" : $"Last seen: {LastSeenAt}";

    public bool IsTcp    => ConnectionType == "TCP";
    public bool IsSerial => ConnectionType == "Serial";

    public string AddressSummary => IsTcp
        ? $"{IPAddress}:{Port}"
        : $"{ComPort} @ {BaudRate} baud";

    // Runtime state (not persisted)
    private bool   _isConnected;
    private bool   _isConnecting;
    private string _statusText = "Disconnected";

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            Set(ref _isConnected, value);
            StatusText = value ? "Connected" : "Disconnected";
            OnPropertyChanged(nameof(StatusColor));
        }
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        set
        {
            Set(ref _isConnecting, value);
            if (value) StatusText = "Connecting\u2026";
        }
    }

    public string StatusText  { get => _statusText; set => Set(ref _statusText, value); }
    public string StatusColor => IsConnected ? "#16C080" : "#E74C3C";
}
