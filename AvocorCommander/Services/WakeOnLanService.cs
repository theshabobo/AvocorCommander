using System.Net;
using System.Net.Sockets;

namespace AvocorCommander.Services;

/// <summary>
/// Builds a standard Wake-on-LAN magic packet and sends it on every
/// destination that could plausibly deliver it to the target NIC:
///
///   1. 255.255.255.255 (limited broadcast) — for same-subnet targets
///   2. The target's /24 directed broadcast (e.g. 192.168.13.255) — for
///      cross-subnet targets, when the router is configured to forward
///      directed broadcasts (most enterprise gear allows this; many
///      consumer routers don't by default, so this may still fail)
///   3. Unicast to the target IP — some NICs listen on their own IP even
///      in sleep states
///
/// Each destination is hit on both UDP port 9 (discard — classic WOL) and
/// UDP port 7 (echo), since some Avocor firmware families listen on only
/// one of the two.
/// </summary>
public static class WakeOnLanService
{
    /// <summary>
    /// Sends a WOL magic packet for the given MAC.
    /// Pass <paramref name="targetIp"/> so directed-broadcast and unicast
    /// destinations can be derived — without it we can only broadcast to
    /// the sender's own subnet, which doesn't cross routers.
    /// Throws FormatException for a malformed MAC.
    /// </summary>
    public static async Task SendAsync(string macAddress, string? targetIp = null, CancellationToken ct = default)
    {
        var macBytes = ParseMac(macAddress);

        // Magic packet: 6×0xFF header, then MAC repeated 16 times.
        var packet = new byte[6 + 16 * 6];
        for (int i = 0; i < 6;  i++) packet[i] = 0xFF;
        for (int i = 0; i < 16; i++) Array.Copy(macBytes, 0, packet, 6 + i * 6, 6);

        using var udp = new UdpClient();
        udp.EnableBroadcast = true;

        var destinations = new List<IPAddress> { IPAddress.Broadcast };

        if (!string.IsNullOrWhiteSpace(targetIp) &&
            IPAddress.TryParse(targetIp, out var ip) &&
            ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            // /24 directed broadcast (e.g. 192.168.13.5 → 192.168.13.255).
            // We don't know the real mask, but /24 is the overwhelming majority.
            destinations.Add(new IPAddress(new[] { b[0], b[1], b[2], (byte)255 }));
            // Unicast to the target IP — last-resort.
            destinations.Add(ip);
        }

        foreach (var dest in destinations)
        foreach (var port in new[] { 9, 7 })
        {
            try
            {
                await udp.SendAsync(packet, packet.Length, new IPEndPoint(dest, port))
                    .ConfigureAwait(false);
            }
            catch
            {
                // Some destinations may be unroutable from the current
                // interface — keep trying the others.
            }
        }
    }

    /// <summary>Parses a MAC string with any of `:`, `-`, or `.` separators into 6 bytes.</summary>
    public static byte[] ParseMac(string macAddress)
    {
        var mac = (macAddress ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(mac))
            throw new FormatException("MAC address is empty.");

        var parts = mac.Split(':', '-', '.');
        var bytes = parts.Select(s => Convert.ToByte(s, 16)).ToArray();
        if (bytes.Length != 6)
            throw new FormatException("MAC address must be 6 bytes (e.g. AA:BB:CC:DD:EE:FF).");
        return bytes;
    }
}
