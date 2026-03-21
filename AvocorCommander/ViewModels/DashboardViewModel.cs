using AvocorCommander.Core;
using AvocorCommander.Models;
using AvocorCommander.Services;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Input;

namespace AvocorCommander.ViewModels;

public sealed class DashboardViewModel : BaseViewModel, IDisposable
{
    private readonly DatabaseService   _db;
    private readonly ConnectionManager _connMgr;
    private Timer? _timer;

    public ObservableCollection<DeviceStatusInfo> Tiles { get; } = [];

    private string _summaryText = "Ready.";
    public string SummaryText { get => _summaryText; set => Set(ref _summaryText, value); }

    private bool _isRefreshing;
    public bool IsRefreshing { get => _isRefreshing; set => Set(ref _isRefreshing, value); }

    public ICommand RefreshCommand    { get; }
    public ICommand WakeOnLanCommand  { get; }

    public DashboardViewModel(DatabaseService db, ConnectionManager connMgr)
    {
        _db      = db;
        _connMgr = connMgr;

        _connMgr.ConnectionChanged += (_, _) =>
            System.Windows.Application.Current?.Dispatcher.Invoke(UpdateConnectionStates);

        RefreshCommand   = new AsyncRelayCommand(RefreshAllAsync);
        WakeOnLanCommand = new AsyncRelayCommand<DeviceStatusInfo>(WakeOnLanTileAsync);
    }

    public void LoadData()
    {
        var devices = _db.GetAllDevices();
        Tiles.Clear();
        foreach (var d in devices)
        {
            d.IsConnected = _connMgr.IsConnected(d.Id);
            Tiles.Add(new DeviceStatusInfo { Device = d });
        }

        UpdateSummary();

        // Restart 30-second auto-refresh timer
        _timer?.Dispose();
        _timer = new Timer(async _ => await RefreshAllAsync(),
            null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    private void UpdateConnectionStates()
    {
        foreach (var tile in Tiles)
            tile.Device.IsConnected = _connMgr.IsConnected(tile.Device.Id);
        UpdateSummary();
    }

    private async Task RefreshAllAsync()
    {
        if (Tiles.Count == 0) return;
        System.Windows.Application.Current?.Dispatcher.Invoke(() => IsRefreshing = true);

        await Task.WhenAll(Tiles.Select(PingTileAsync));

        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            UpdateSummary();
            IsRefreshing = false;
        });
    }

    private static async Task PingTileAsync(DeviceStatusInfo tile)
    {
        if (string.IsNullOrWhiteSpace(tile.Device.IPAddress)) return;
        try
        {
            using var ping  = new Ping();
            var       reply = await ping.SendPingAsync(tile.Device.IPAddress, 1500);
            bool      ok    = reply.Status == IPStatus.Success;
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                tile.IsOnline = ok;
                tile.LastSeen = ok ? DateTime.Now.ToString("HH:mm:ss") : tile.LastSeen;
                tile.PingMs   = ok ? (int)reply.RoundtripTime : -1;
                tile.Latency  = ok ? $"{reply.RoundtripTime} ms" : "—";
            });
        }
        catch
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(
                () => { tile.IsOnline = false; tile.Latency = "—"; });
        }
    }

    private void UpdateSummary()
    {
        int online    = Tiles.Count(t => t.IsOnline);
        int connected = Tiles.Count(t => t.Device.IsConnected);
        SummaryText   = $"{Tiles.Count} device(s)  ·  {online} online  ·  {connected} connected";
    }

    private async Task WakeOnLanTileAsync(DeviceStatusInfo? tile)
    {
        if (tile == null || string.IsNullOrWhiteSpace(tile.Device.MacAddress)) return;
        var mac = tile.Device.MacAddress.Trim();
        try
        {
            var macBytes = mac.Split(':', '-', '.').Select(s => Convert.ToByte(s, 16)).ToArray();
            if (macBytes.Length != 6) return;
            var packet = new byte[6 + 16 * 6];
            for (int i = 0; i < 6; i++) packet[i] = 0xFF;
            for (int i = 0; i < 16; i++) Array.Copy(macBytes, 0, packet, 6 + i * 6, 6);
            using var udp = new UdpClient();
            udp.EnableBroadcast = true;
            await udp.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                SummaryText = $"WoL sent to {tile.Device.DeviceName} ({mac})");
        }
        catch (Exception ex)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                SummaryText = $"WoL failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
