using System.IO.Ports;

namespace AvocorCommander.Services;

public sealed class SerialConnectionService : IConnectionService
{
    private readonly string   _portName;
    private readonly int      _baudRate;
    private readonly int      _dataBits;
    private readonly Parity   _parity;
    private readonly StopBits _stopBits;

    private SerialPort? _port;

    public bool IsConnected => _port?.IsOpen ?? false;
    public string LastError { get; private set; } = string.Empty;
    public event EventHandler<byte[]>? DataReceived;

    public SerialConnectionService(
        string   portName,
        int      baudRate = 9600,
        int      dataBits = 8,
        Parity   parity   = Parity.None,
        StopBits stopBits = StopBits.One)
    {
        _portName = portName;
        _baudRate = baudRate;
        _dataBits = dataBits;
        _parity   = parity;
        _stopBits = stopBits;
    }

    public Task<bool> ConnectAsync()
    {
        LastError = string.Empty;
        try
        {
            _port = new SerialPort(_portName, _baudRate, _parity, _dataBits, _stopBits)
            {
                ReadTimeout  = 2000,
                WriteTimeout = 2000
            };
            _port.DataReceived += OnDataReceived;
            _port.Open();
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _port?.Dispose();
            _port = null;
            return Task.FromResult(false);
        }
    }

    public Task DisconnectAsync()
    {
        if (_port != null)
        {
            _port.DataReceived -= OnDataReceived;
            if (_port.IsOpen) _port.Close();
            _port.Dispose();
            _port = null;
        }
        return Task.CompletedTask;
    }

    public async Task<byte[]?> SendCommandAsync(byte[] data, int receiveTimeoutMs = 2000)
    {
        if (_port == null || !IsConnected) return null;
        try
        {
            _port.DiscardInBuffer();
            _port.Write(data, 0, data.Length);

            var deadline = DateTime.UtcNow.AddMilliseconds(receiveTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (_port.BytesToRead > 0)
                {
                    await Task.Delay(30);
                    var buffer = new byte[_port.BytesToRead];
                    _port.Read(buffer, 0, buffer.Length);
                    return buffer;
                }
                await Task.Delay(20);
            }
            return null;
        }
        catch { return null; }
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_port == null) return;
        try
        {
            var buffer = new byte[_port.BytesToRead];
            _port.Read(buffer, 0, buffer.Length);
            DataReceived?.Invoke(this, buffer);
        }
        catch { }
    }

    public static string[] GetAvailablePorts() => SerialPort.GetPortNames();
    public void Dispose() => DisconnectAsync().GetAwaiter().GetResult();
}
