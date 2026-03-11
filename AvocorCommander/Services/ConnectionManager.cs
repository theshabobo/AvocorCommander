using AvocorCommander.Models;

namespace AvocorCommander.Services;

/// <summary>
/// Maintains active TCP/Serial connections keyed by DeviceEntry.Id.
/// Shared singleton — injected into ViewModels that need connection state.
/// </summary>
public sealed class ConnectionManager : IDisposable
{
    private readonly Dictionary<int, IConnectionService> _active = [];
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
            lock (_lock) _active[device.Id] = svc;
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

    /// <summary>Send bytes; returns response or null. Logs nothing — callers own logging.</summary>
    public async Task<byte[]?> SendAsync(int deviceId, byte[] data)
    {
        IConnectionService? svc;
        lock (_lock) _active.TryGetValue(deviceId, out svc);
        if (svc == null) return null;
        return await svc.SendCommandAsync(data);
    }

    public void Dispose()
    {
        List<IConnectionService> svcs;
        lock (_lock)
        {
            svcs = [.._active.Values];
            _active.Clear();
        }
        foreach (var s in svcs)
        {
            try { s.DisconnectAsync().GetAwaiter().GetResult(); } catch { }
            s.Dispose();
        }
    }
}
