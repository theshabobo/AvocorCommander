using AvocorCommander.Models;
using System.Net.Sockets;

namespace AvocorCommander.Services;

/// <summary>
/// Wakes an Avocor display from standby using whichever path the device
/// actually honours.
///
/// Avocor displays keep their NIC alive in soft-off. They do NOT respond to
/// the normal Wake-on-LAN magic packet — instead the "Wake From Sleep"
/// setting just permits the Power On command (sent over the normal control
/// port) while the display is in standby. So to wake reliably we:
///
///   1. Still broadcast the WOL magic packet, in case the display is
///      genuinely asleep at the NIC level (belt-and-suspenders, cheap).
///   2. Open a short-lived TCP connection to the device's control port and
///      send the per-series "Power On" command from the DeviceList.
///
/// Returns a WakeResult describing which paths were attempted and whether
/// the TCP wake saw an ACK from the display.
/// </summary>
public static class DeviceWakeService
{
    public sealed record WakeResult(
        bool   MagicPacketSent,
        bool   PowerOnSent,
        bool   PowerOnAckd,
        string Detail);

    public static async Task<WakeResult> WakeAsync(
        DeviceEntry     device,
        DatabaseService db,
        CancellationToken ct = default)
    {
        // 1. Magic packet (cheap, non-fatal on failure)
        bool wolSent = false;
        if (!string.IsNullOrWhiteSpace(device.MacAddress))
        {
            try
            {
                await WakeOnLanService.SendAsync(device.MacAddress, device.IPAddress, ct);
                wolSent = true;
            }
            catch { /* ignore */ }
        }

        // 2. Native Power-On command over TCP. This is what actually wakes
        //    Avocor displays when they're in soft-off.
        if (device.ConnectionType != "TCP" ||
            string.IsNullOrWhiteSpace(device.IPAddress) ||
            device.Port <= 0)
        {
            return new WakeResult(wolSent, false, false,
                wolSent ? "WOL magic packet sent (no TCP address to try Power On)"
                        : "No MAC for WOL and no TCP address to try Power On");
        }

        var series = db.GetSeriesForModel(device.ModelNumber);
        if (string.IsNullOrEmpty(series))
            return new WakeResult(wolSent, false, false, $"Unknown series for '{device.ModelNumber}' — can't build Power On");

        var powerOn = db.GetCommandsBySeries(series)
            .FirstOrDefault(c =>
                string.Equals(c.CommandName, "Power On", StringComparison.OrdinalIgnoreCase));
        if (powerOn == null)
            return new WakeResult(wolSent, false, false, $"No 'Power On' command defined for {series}");

        byte[] bytes = powerOn.GetBytes();
        if (bytes.Length == 0)
            return new WakeResult(wolSent, false, false, $"Could not encode Power On bytes for {series}");

        // Short-lived TCP connection — can't reuse ConnectionManager because
        // the user may not have "Connected" the device yet, and we don't
        // want to leave a persistent connection behind.
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(5000);

            using var client = new TcpClient { SendTimeout = 3000, ReceiveTimeout = 3000 };
            await client.ConnectAsync(device.IPAddress, device.Port, cts.Token);
            using var stream = client.GetStream();

            await stream.WriteAsync(bytes, cts.Token);

            // Give the display a moment to reply. Avocor displays usually
            // ACK within a few hundred milliseconds.
            var buf   = new byte[64];
            int total = 0;
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            readCts.CancelAfter(1500);
            try
            {
                while (total < buf.Length)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), readCts.Token);
                    if (n <= 0) break;
                    total += n;
                    if (buf[total - 1] == 0x0D) break;  // response terminator for most series
                }
            }
            catch (OperationCanceledException) { /* fine, just no ACK */ }

            bool ackd = total > 0;
            string detail = ackd
                ? $"Power On sent over TCP; display replied ({total} byte(s))."
                : "Power On sent over TCP; no reply within 1.5s.";

            return new WakeResult(wolSent, true, ackd, detail);
        }
        catch (Exception ex)
        {
            return new WakeResult(wolSent, false, false,
                $"TCP Power On to {device.IPAddress}:{device.Port} failed — {ex.Message}");
        }
    }
}
