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

    /// <summary>Populated with the last exception message from ConnectAsync, if any.</summary>
    public string LastError { get; private set; } = string.Empty;

    public async Task<bool> ConnectAsync()
    {
        await CleanupAsync();
        LastError = string.Empty;
        try
        {
            _client = new TcpClient { ReceiveTimeout = 4000, SendTimeout = 4000 };
            using var cts = new CancellationTokenSource(5000);
            await _client.ConnectAsync(_host, _port, cts.Token);
            _stream = _client.GetStream();
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
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

            using var cts   = new CancellationTokenSource(receiveTimeoutMs);
            var       buf   = new byte[4096];
            var       accum = new List<byte>(256);

            // ASCII commands end with CR (0x0D) — keep reading until the response CR arrives.
            // Binary commands (K-Series, E-Group1, etc.) do not end with CR — a single read
            // is sufficient since device frames arrive in one TCP segment.
            bool isAscii = data.Length > 0 && data[^1] == 0x0D;

            do
            {
                int read = await _stream.ReadAsync(buf, cts.Token);
                if (read == 0) break;   // remote closed connection
                accum.AddRange(buf.AsSpan(0, read));
            }
            while (isAscii && accum[^1] != 0x0D && !cts.IsCancellationRequested);

            return accum.Count > 0 ? [.. accum] : null;
        }
        catch (OperationCanceledException) { return null; }
        catch { await CleanupAsync(); return null; }  // socket error — mark disconnected so caller can reconnect
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
