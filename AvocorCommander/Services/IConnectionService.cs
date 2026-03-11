namespace AvocorCommander.Services;

public interface IConnectionService : IDisposable
{
    bool IsConnected { get; }
    event EventHandler<byte[]>? DataReceived;
    Task<bool>    ConnectAsync();
    Task          DisconnectAsync();
    Task<byte[]?> SendCommandAsync(byte[] data, int receiveTimeoutMs = 2000);
}
