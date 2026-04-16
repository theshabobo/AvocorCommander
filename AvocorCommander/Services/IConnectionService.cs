namespace AvocorCommander.Services;

public interface IConnectionService : IDisposable
{
    bool IsConnected { get; }
    /// <summary>Message from the most recent failed ConnectAsync, or empty on success.</summary>
    string LastError { get; }
    event EventHandler<byte[]>? DataReceived;
    Task<bool>    ConnectAsync();
    Task          DisconnectAsync();
    Task<byte[]?> SendCommandAsync(byte[] data, int receiveTimeoutMs = 2000);
}
