using System.Net.Sockets;

namespace AvocorCommander.Services;

public sealed class TcpConnectionService : IConnectionService
{
    private readonly string _host;
    private readonly int    _port;

    private TcpClient?     _client;
    private NetworkStream? _stream;

    public bool IsConnected => _client?.Connected ?? false;

#pragma warning disable CS0067
    public event EventHandler<byte[]>? DataReceived;
#pragma warning restore CS0067

    public TcpConnectionService(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public async Task<bool> ConnectAsync()
    {
        await CleanupAsync();
        try
        {
            _client = new TcpClient { ReceiveTimeout = 4000, SendTimeout = 4000 };
            await _client.ConnectAsync(_host, _port);
            _stream = _client.GetStream();
            return true;
        }
        catch
        {
            await CleanupAsync();
            return false;
        }
    }

    public async Task DisconnectAsync() => await CleanupAsync();

    public async Task<byte[]?> SendCommandAsync(byte[] data, int receiveTimeoutMs = 2000)
    {
        if (_stream == null || !IsConnected) return null;
        try
        {
            await _stream.WriteAsync(data);

            using var cts    = new CancellationTokenSource(receiveTimeoutMs);
            var       buffer = new byte[4096];
            int       read   = await _stream.ReadAsync(buffer, cts.Token);

            return read > 0 ? buffer[..read] : null;
        }
        catch (OperationCanceledException) { return null; }
        catch                              { return null; }
    }

    private async Task CleanupAsync()
    {
        if (_stream != null) { await _stream.DisposeAsync(); _stream = null; }
        _client?.Close();
        _client?.Dispose();
        _client = null;
    }

    public void Dispose() => CleanupAsync().GetAwaiter().GetResult();
}
