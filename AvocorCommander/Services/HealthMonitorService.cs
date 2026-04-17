using AvocorCommander.Models;

namespace AvocorCommander.Services;

/// <summary>
/// Periodically checks connected devices for liveness and auto-reconnects
/// devices that have AutoConnect=true but are currently disconnected.
/// </summary>
public sealed class HealthMonitorService : IDisposable
{
    private Timer? _timer;
    private readonly DatabaseService   _db;
    private readonly ConnectionManager _connMgr;
    private volatile bool _running;

    /// <summary>
    /// Raised when a device's connection status changes due to health monitoring.
    /// Payload: (deviceId, status) where status is "reconnected" or "disconnected".
    /// </summary>
    public event EventHandler<(int deviceId, string status)>? DeviceStatusChanged;

    public HealthMonitorService(DatabaseService db, ConnectionManager connMgr)
    {
        _db      = db;
        _connMgr = connMgr;
    }

    /// <summary>Start the health monitor timer.</summary>
    public void Start(int intervalSeconds = 60)
    {
        if (_timer != null) return;
        _timer = new Timer(OnTick, null, TimeSpan.FromSeconds(intervalSeconds), TimeSpan.FromSeconds(intervalSeconds));
    }

    /// <summary>Stop the health monitor timer.</summary>
    public void Stop()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private async void OnTick(object? state)
    {
        if (_running) return;      // skip if previous tick is still running
        _running = true;

        try
        {
            var allDevices = _db.GetAllDevices();

            // 1. Auto-reconnect devices with AutoConnect=true that are disconnected
            foreach (var device in allDevices.Where(d => d.AutoConnect && !_connMgr.IsConnected(d.Id)))
            {
                try
                {
                    bool ok = await _connMgr.ConnectAsync(device);
                    if (ok)
                    {
                        _db.LogCommand(new AuditLogEntry
                        {
                            Timestamp     = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                            DeviceName    = device.DeviceName,
                            DeviceAddress = device.IPAddress,
                            CommandName   = "[Health] Auto-Reconnect",
                            CommandCode   = "",
                            Response      = "Success",
                            Success       = true,
                        });
                        DeviceStatusChanged?.Invoke(this, (device.Id, "reconnected"));
                    }
                }
                catch
                {
                    // Connection attempt failed — will retry next tick
                }
            }

            // 2. Discover apps on B-Series devices (once after connect)
            foreach (var device in allDevices.Where(d => _connMgr.IsConnected(d.Id)))
            {
                try
                {
                    var series = _db.GetSeriesForModel(device.ModelNumber);
                    if (series != "B-Series") continue;

                    var existing = _db.GetDeviceCommands(device.Id);
                    if (existing.Count > 0) continue;  // Already discovered

                    var discovered = await AppDiscoveryService.DiscoverAppsAsync(_connMgr, _db, device.Id);
                    if (discovered.Count > 0)
                    {
                        _db.SetDeviceCommands(device.Id, discovered);
                        DeviceStatusChanged?.Invoke(this, (device.Id, "apps_discovered"));
                    }
                }
                catch
                {
                    // App discovery failed — will retry next tick since commands are still empty
                }
            }

            // 3. Verify connected devices are still alive via a lightweight query
            var connectedIds = _connMgr.ConnectedDeviceIds;
            foreach (int deviceId in connectedIds)
            {
                var device = allDevices.FirstOrDefault(d => d.Id == deviceId);
                if (device == null) continue;

                string? series = _db.GetSeriesForModel(device.ModelNumber);
                if (string.IsNullOrEmpty(series)) continue;

                var commands = _db.GetCommandsBySeries(series);
                var healthCmd = commands.FirstOrDefault(c =>
                    string.Equals(c.CommandName, "Get Power State", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.CommandName, "Power Query", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.CommandName, "Get Power", StringComparison.OrdinalIgnoreCase));

                if (healthCmd == null) continue;

                byte[] bytes = healthCmd.GetBytes();
                if (bytes.Length == 0) continue;

                try
                {
                    var response = await _connMgr.SendAsync(deviceId, bytes);
                    if (response == null && !_connMgr.IsConnected(deviceId))
                    {
                        // Device appears dead — ConnectionManager already evicted it.
                        // Try one reconnect if AutoConnect is on.
                        DeviceStatusChanged?.Invoke(this, (deviceId, "disconnected"));

                        if (device.AutoConnect)
                        {
                            bool ok = await _connMgr.ConnectAsync(device);
                            if (ok)
                                DeviceStatusChanged?.Invoke(this, (deviceId, "reconnected"));
                        }
                    }
                }
                catch
                {
                    // Send failed — device likely disconnected; ConnectionManager handles eviction
                }
            }
        }
        catch
        {
            // Guard against unexpected errors so the timer keeps ticking
        }
        finally
        {
            _running = false;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
