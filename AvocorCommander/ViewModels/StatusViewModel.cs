using AvocorCommander.Core;
using AvocorCommander.Models;
using AvocorCommander.Services;
using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using System.Windows.Input;

namespace AvocorCommander.ViewModels;

public sealed class DeviceStatusInfo : BaseViewModel
{
    private bool   _isOnline;
    private string _lastSeen    = "—";
    private string _latency     = "—";
    private int    _pingMs;

    public required DeviceEntry Device { get; init; }

    public bool   IsOnline  { get => _isOnline;  set { Set(ref _isOnline, value);  OnPropertyChanged(nameof(StatusColor)); OnPropertyChanged(nameof(StatusLabel)); } }
    public string LastSeen  { get => _lastSeen;  set => Set(ref _lastSeen, value); }
    public string Latency   { get => _latency;   set => Set(ref _latency, value); }
    public int    PingMs    { get => _pingMs;     set => Set(ref _pingMs, value); }

    public string StatusLabel => IsOnline ? "Online" : "Offline";
    public string StatusColor => IsOnline ? "#16C080" : "#E74C3C";
}

public sealed class StatusViewModel : BaseViewModel
{
    private readonly DatabaseService   _db;
    private readonly ConnectionManager _connMgr;

    private Timer? _timer;
    private bool   _polling;

    public ObservableCollection<DeviceStatusInfo> DeviceStatuses { get; } = [];

    // ── Polling interval ─────────────────────────────────────────────────────

    private int _pollIntervalSeconds = 30;
    public int PollIntervalSeconds
    {
        get => _pollIntervalSeconds;
        set { Set(ref _pollIntervalSeconds, value); RestartTimerIfRunning(); }
    }

    public bool IsPolling
    {
        get => _polling;
        set { Set(ref _polling, value); OnPropertyChanged(nameof(PollButtonLabel)); }
    }

    public string PollButtonLabel => IsPolling ? "Stop Polling" : "Start Polling";

    // ── Status bar ────────────────────────────────────────────────────────────

    private string _statusMessage = "Ready.";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    private int _onlineCount;
    public int OnlineCount { get => _onlineCount; set => Set(ref _onlineCount, value); }

    // ── Commands ─────────────────────────────────────────────────────────────

    public ICommand TogglePollingCommand { get; }
    public ICommand RefreshAllCommand    { get; }
    public ICommand LoadCommand          { get; }

    public StatusViewModel(DatabaseService db, ConnectionManager connMgr)
    {
        _db      = db;
        _connMgr = connMgr;

        TogglePollingCommand = new RelayCommand(TogglePolling);
        RefreshAllCommand    = new AsyncRelayCommand(PollAllAsync);
        LoadCommand          = new RelayCommand(LoadDevices);
    }

    public void LoadDevices()
    {
        var devices = _db.GetAllDevices();
        DeviceStatuses.Clear();
        foreach (var d in devices)
            DeviceStatuses.Add(new DeviceStatusInfo { Device = d, IsOnline = _connMgr.IsConnected(d.Id) });
        UpdateCounts();
        StatusMessage = $"{devices.Count} device(s) loaded.";
    }

    // ── Polling ───────────────────────────────────────────────────────────────

    private void TogglePolling()
    {
        if (IsPolling) StopPolling();
        else           StartPolling();
    }

    public void StartPolling()
    {
        IsPolling = true;
        _timer    = new Timer(async _ => await PollAllAsync(),
            null, TimeSpan.Zero, TimeSpan.FromSeconds(PollIntervalSeconds));
        StatusMessage = $"Polling every {PollIntervalSeconds}s…";
    }

    public void StopPolling()
    {
        IsPolling = false;
        _timer?.Dispose();
        _timer    = null;
        StatusMessage = "Polling stopped.";
    }

    private void RestartTimerIfRunning()
    {
        if (!IsPolling) return;
        StopPolling();
        StartPolling();
    }

    private async Task PollAllAsync()
    {
        var tasks = DeviceStatuses.Select(s => PingDeviceAsync(s)).ToList();
        await Task.WhenAll(tasks);
        UpdateCounts();
        System.Windows.Application.Current?.Dispatcher.Invoke(
            () => StatusMessage = $"Last poll: {DateTime.Now:HH:mm:ss}  ·  {OnlineCount}/{DeviceStatuses.Count} online");
    }

    private static async Task PingDeviceAsync(DeviceStatusInfo status)
    {
        if (string.IsNullOrWhiteSpace(status.Device.IPAddress)) return;
        try
        {
            using var ping  = new Ping();
            var       reply = await ping.SendPingAsync(status.Device.IPAddress, 1500);
            bool      ok    = reply.Status == System.Net.NetworkInformation.IPStatus.Success;

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                status.IsOnline = ok;
                status.LastSeen = ok ? DateTime.Now.ToString("HH:mm:ss") : status.LastSeen;
                status.PingMs   = ok ? (int)reply.RoundtripTime : -1;
                status.Latency  = ok ? $"{reply.RoundtripTime} ms" : "—";
            });
        }
        catch
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(
                () => { status.IsOnline = false; status.Latency = "—"; });
        }
    }

    private void UpdateCounts()
    {
        OnlineCount = DeviceStatuses.Count(s => s.IsOnline);
    }

    public void Dispose()
    {
        StopPolling();
    }
}
