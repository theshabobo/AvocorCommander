using AvocorCommander.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace AvocorCommander.Services;

/// <summary>
/// Scans an IP range for Avocor displays via ICMP ping + ARP OUI filtering
/// with TCP port-probe fallback for routed subnets.
/// </summary>
public static class NetworkScanService
{
    private const int MaxConcurrent    = 50;
    private const int PingTimeoutMs    = 1000;
    private const int TcpTimeoutMs     = 800;

    private static readonly (int Port, string SeriesHint)[] AvocorPorts =
    [
        (59595, "A-Series"),
        (4664,  "E-Group1"),
        (59596, "K-Series"),
        (4660,  "E-50"),
        (10180, "B-Series"),
    ];

    public static async Task ScanAsync(
        string                           startIp,
        string                           endIp,
        Dictionary<string, string>       ouiSeriesMap,
        Dictionary<string, string>       modelSeriesMap,   // modelNumber → seriesPattern
        Action<ScanResult>               onResult,
        IProgress<(int done, int total)> progress,
        CancellationToken                ct)
    {
        var ips   = EnumerateIPs(startIp, endIp);
        int total = ips.Count;
        int done  = 0;

        // Pre-read ARP cache
        var preArp = await ReadArpAsync();

        // Ping all IPs
        var sem         = new SemaphoreSlim(MaxConcurrent);
        var pingResults = new ConcurrentDictionary<string, bool>();

        var pingTasks = ips.Select(async ip =>
        {
            await sem.WaitAsync(ct);
            try
            {
                try
                {
                    using var ping  = new Ping();
                    var reply = await ping.SendPingAsync(ip, PingTimeoutMs);
                    pingResults[ip] = reply.Status == IPStatus.Success;
                }
                catch { pingResults[ip] = false; }
            }
            finally
            {
                Interlocked.Increment(ref done);
                progress.Report((done, total));
                sem.Release();
            }
        }).ToList();

        await Task.WhenAll(pingTasks);
        ct.ThrowIfCancellationRequested();

        // Post-ping ARP
        var postArp = await ReadArpAsync();
        var arpTable = new Dictionary<string, string>(preArp, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in postArp) arpTable[kvp.Key] = kvp.Value;

        var ipsSet      = new HashSet<string>(ips, StringComparer.OrdinalIgnoreCase);
        var foundViaArp = new ConcurrentBag<string>();

        var processTasks = arpTable.Keys
            .Where(ip => ipsSet.Contains(ip))
            .Select(async ip =>
            {
                ct.ThrowIfCancellationRequested();
                if (!arpTable.TryGetValue(ip, out var mac) || string.IsNullOrEmpty(mac)) return;
                if (mac is "ff:ff:ff:ff:ff:ff" or "00:00:00:00:00:00") return;

                string prefix = mac.Length >= 8 ? mac[..8].ToUpperInvariant() : mac;
                if (!ouiSeriesMap.TryGetValue(prefix, out var series)) return;

                foundViaArp.Add(ip);

                bool   online   = pingResults.TryGetValue(ip, out var o) && o;
                string hostname = online ? await ResolveHostAsync(ip) : string.Empty;
                string model    = MatchModel(series, modelSeriesMap);

                onResult(new ScanResult
                {
                    IpAddress     = ip,
                    MacAddress    = mac,
                    Vendor        = series,
                    ModelNumber   = model,
                    SeriesPattern = series,
                    Hostname      = hostname,
                    IsOnline      = online,
                });
            }).ToList();

        await Task.WhenAll(processTasks);
        ct.ThrowIfCancellationRequested();

        // TCP fallback for routed subnets
        var foundSet = new HashSet<string>(foundViaArp, StringComparer.OrdinalIgnoreCase);
        var tcpCandidates = pingResults
            .Where(k => k.Value && !foundSet.Contains(k.Key))
            .Select(k => k.Key).ToList();

        var tcpSem   = new SemaphoreSlim(MaxConcurrent);
        var tcpTasks = tcpCandidates.Select(async ip =>
        {
            ct.ThrowIfCancellationRequested();
            await tcpSem.WaitAsync(ct);
            try
            {
                var (port, hint) = await FindAvocorPortAsync(ip, ct);
                if (hint == null) return;

                string hostname = await ResolveHostAsync(ip);
                string model    = MatchModel(hint, modelSeriesMap);

                onResult(new ScanResult
                {
                    IpAddress     = ip,
                    MacAddress    = string.Empty,
                    Vendor        = hint,
                    ModelNumber   = model,
                    SeriesPattern = hint,
                    Hostname      = hostname,
                    IsOnline      = true,
                });
            }
            finally { tcpSem.Release(); }
        }).ToList();

        await Task.WhenAll(tcpTasks);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<(int port, string? hint)> FindAvocorPortAsync(string ip, CancellationToken ct)
    {
        var tasks = AvocorPorts
            .Select(p => TryTcpAsync(ip, p.Port, ct)
                .ContinueWith(t => (p.Port, t.IsCompletedSuccessfully && t.Result ? p.SeriesHint : null),
                    TaskContinuationOptions.ExecuteSynchronously))
            .ToList();

        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks);
            tasks.Remove(done);
            var (port, hint) = await done;
            if (hint != null) return (port, hint);
        }
        return (0, null);
    }

    private static async Task<bool> TryTcpAsync(string ip, int port, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TcpTimeoutMs);
            using var client = new TcpClient();
            await client.ConnectAsync(ip, port, cts.Token);
            return true;
        }
        catch { return false; }
    }

    private static async Task<Dictionary<string, string>> ReadArpAsync()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var psi = new ProcessStartInfo("arp", "-a")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                CreateNoWindow         = true
            };
            using var proc = Process.Start(psi)!;
            string output  = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            foreach (var line in output.Split('\n'))
            {
                var m = Regex.Match(line.Trim(),
                    @"^(\d+\.\d+\.\d+\.\d+)\s+([0-9a-f]{2}[-:][0-9a-f]{2}[-:][0-9a-f]{2}[-:][0-9a-f]{2}[-:][0-9a-f]{2}[-:][0-9a-f]{2})\s+",
                    RegexOptions.IgnoreCase);
                if (m.Success)
                    result[m.Groups[1].Value] = m.Groups[2].Value.Replace('-', ':').ToLowerInvariant();
            }
        }
        catch { }
        return result;
    }

    private static async Task<string> ResolveHostAsync(string ip)
    {
        try
        {
            using var cts   = new CancellationTokenSource(1000);
            var       entry = await Dns.GetHostEntryAsync(ip).WaitAsync(cts.Token);
            var       host  = entry.HostName;
            int       dot   = host.IndexOf('.');
            return dot > 0 ? host[..dot] : host;
        }
        catch { return string.Empty; }
    }

    private static string MatchModel(string series, Dictionary<string, string> modelSeriesMap)
    {
        // Exact lookup: find a model whose series pattern matches the detected series
        var match = modelSeriesMap.FirstOrDefault(kv =>
            string.Equals(kv.Value, series, StringComparison.OrdinalIgnoreCase));
        return match.Key ?? series;
    }

    private static List<string> EnumerateIPs(string start, string end)
    {
        if (!IPAddress.TryParse(start, out var s) || !IPAddress.TryParse(end, out var e)) return [];
        long sl = ToLong(s), el = ToLong(e);
        if (sl > el) (sl, el) = (el, sl);
        var list = new List<string>();
        for (long i = sl; i <= el && list.Count <= 65535; i++)
            list.Add(FromLong(i));
        return list;
    }

    private static long ToLong(IPAddress ip) { var b = ip.GetAddressBytes(); return ((long)b[0] << 24) | ((long)b[1] << 16) | ((long)b[2] << 8) | b[3]; }
    private static string FromLong(long ip) => $"{(ip >> 24) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}";
}
