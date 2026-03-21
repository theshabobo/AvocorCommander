using AvocorCommander.Models;

namespace AvocorCommander.Services;

/// <summary>
/// Maintains active TCP/Serial connections keyed by DeviceEntry.Id.
/// Shared singleton — injected into ViewModels that need connection state.
/// </summary>
public sealed class ConnectionManager : IDisposable
{
    private readonly Dictionary<int, IConnectionService> _active  = [];
    private readonly Dictionary<int, DeviceEntry>        _devices = [];  // for silent reconnect
    private readonly object _lock = new();

    public event EventHandler<DeviceEntry>? ConnectionChanged;

    // ── Public API ────────────────────────────────────────────────────────────

    public bool IsConnected(int deviceId)
    {
        lock (_lock)
            return _active.TryGetValue(deviceId, out var svc) && svc.IsConnected;
    }

    public IConnectionService? GetService(int deviceId)
    {
        lock (_lock)
            return _active.TryGetValue(deviceId, out var svc) ? svc : null;
    }

    public IReadOnlyCollection<int> ConnectedDeviceIds
    {
        get { lock (_lock) return _active.Keys.ToList().AsReadOnly(); }
    }

    public async Task<bool> ConnectAsync(DeviceEntry device)
    {
        // Disconnect any existing connection for this device first
        await DisconnectAsync(device.Id);

        IConnectionService svc = device.ConnectionType == "Serial"
            ? new SerialConnectionService(device.ComPort, device.BaudRate)
            : new TcpConnectionService(device.IPAddress, device.Port);

        device.IsConnecting = true;
        bool ok = await svc.ConnectAsync();
        device.IsConnecting = false;

        if (ok)
        {
            lock (_lock) { _active[device.Id] = svc; _devices[device.Id] = device; }
            device.IsConnected = true;
        }
        else
        {
            svc.Dispose();
        }

        ConnectionChanged?.Invoke(this, device);
        return ok;
    }

    public async Task DisconnectAsync(int deviceId)
    {
        IConnectionService? svc;
        lock (_lock)
        {
            if (!_active.TryGetValue(deviceId, out svc)) return;
            _active.Remove(deviceId);
            _devices.Remove(deviceId);
        }
        await svc.DisconnectAsync();
        svc.Dispose();
    }

    public async Task DisconnectAsync(DeviceEntry device)
    {
        await DisconnectAsync(device.Id);
        device.IsConnected  = false;
        device.IsConnecting = false;
        ConnectionChanged?.Invoke(this, device);
    }

    /// <summary>
    /// Send bytes; returns response or null. If the connection was silently dropped
    /// (IsConnected false after the send fails) one reconnect attempt is made automatically.
    /// </summary>
    public async Task<byte[]?> SendAsync(int deviceId, byte[] data)
    {
        IConnectionService? svc;
        lock (_lock) _active.TryGetValue(deviceId, out svc);
        if (svc == null) return null;

        var response = await svc.SendCommandAsync(data);

        // If null AND the socket reports disconnected → silent reconnect + retry (TCP only;
        // SerialConnectionService.IsConnected checks _port.IsOpen which is always correct).
        if (response == null && !svc.IsConnected)
        {
            DeviceEntry? device;
            lock (_lock) _devices.TryGetValue(deviceId, out device);

            if (device != null)
            {
                bool reconnected = await svc.ConnectAsync();
                if (reconnected)
                {
                    response = await svc.SendCommandAsync(data);
                }
                else
                {
                    // Give up — evict and notify UI so the device shows as disconnected
                    lock (_lock) { _active.Remove(deviceId); _devices.Remove(deviceId); }
                    svc.Dispose();
                    device.IsConnected = false;
                    ConnectionChanged?.Invoke(this, device);
                }
            }
        }

        return response;
    }

    public void Dispose()
    {
        List<IConnectionService> svcs;
        lock (_lock)
        {
            svcs = [.._active.Values];
            _active.Clear();
            _devices.Clear();
        }
        foreach (var s in svcs)
        {
            try { s.DisconnectAsync().GetAwaiter().GetResult(); } catch { }
            s.Dispose();
        }
    }
}
