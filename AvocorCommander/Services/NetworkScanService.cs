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

                // MAC discovery priority:
                //  1. ARP cache (works when PC shares the broadcast domain with the display)
                //  2. Series-specific "Get MAC Address" control command (works across subnets,
                //     but only for series that expose it: E-50, S-Series, and H-Series which
                //     uses the same envelope as S-Series)
                arpTable.TryGetValue(ip, out var macFromArp);
                string mac = macFromArp ?? string.Empty;
                if (string.IsNullOrEmpty(mac))
                {
                    var queried = await QueryMacAsync(ip, port, hint, ct);
                    if (!string.IsNullOrEmpty(queried)) mac = queried;
                }

                onResult(new ScanResult
                {
                    IpAddress     = ip,
                    MacAddress    = mac,
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

    /// <summary>
    /// Queries a display over TCP for its MAC address using the series-specific
    /// "Get MAC Address" control command. Returns a normalised xx:xx:xx:xx:xx:xx
    /// string on success, or null on any failure (unknown series, timeout, no
    /// parseable MAC in response).
    /// </summary>
    private static async Task<string?> QueryMacAsync(string ip, int port, string series, CancellationToken ct)
    {
        // Only these series documented as supporting a Get MAC query.
        // H-Series shares the S-Series frame envelope (`3A 30 31 G/S/r …`), so
        // the same command usually works on it.
        string? hexCode = series switch
        {
            "S-Series" => "3A 30 31 47 53 30 30 33 0D",
            "H-Series" => "3A 30 31 47 53 30 30 33 0D",
            "E-50"     => "6B 30 31 67 73 30 30 30 0D",
            _          => null,
        };
        if (hexCode == null) return null;

        try
        {
            var query = hexCode.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                               .Select(t => Convert.ToByte(t, 16))
                               .ToArray();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(2000);
            using var client = new TcpClient { SendTimeout = 1500, ReceiveTimeout = 1500 };
            await client.ConnectAsync(ip, port, cts.Token);
            using var stream = client.GetStream();

            await stream.WriteAsync(query, cts.Token);

            // Read up to 256 bytes, stopping early on a CR terminator.
            var buf   = new byte[256];
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
                    if (total > 0 && buf[total - 1] == 0x0D) break;
                }
            }
            catch (OperationCanceledException) { /* read timed out; parse what we got */ }

            if (total == 0) return null;

            // Avocor displays respond with the MAC as ASCII text in the data field.
            // Pull any 12 hex chars (optionally with : or - separators) from the reply.
            var responseAscii = System.Text.Encoding.ASCII.GetString(buf, 0, total);

            var withSep = Regex.Match(responseAscii,
                @"([0-9A-Fa-f]{2}[:\-]){5}[0-9A-Fa-f]{2}");
            if (withSep.Success) return NormaliseMac(withSep.Value);

            var noSep = Regex.Match(responseAscii, @"[0-9A-Fa-f]{12}");
            if (noSep.Success) return NormaliseMac(noSep.Value);

            return null;
        }
        catch { return null; }
    }

    private static string NormaliseMac(string raw)
    {
        var clean = new string(raw.Where(c =>
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')).ToArray())
            .ToLowerInvariant();
        if (clean.Length != 12) return raw;
        return $"{clean[..2]}:{clean.Substring(2,2)}:{clean.Substring(4,2)}:{clean.Substring(6,2)}:{clean.Substring(8,2)}:{clean.Substring(10,2)}";
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
        var candidates = modelSeriesMap
            .Where(kv => string.Equals(kv.Value, series, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();

        if (candidates.Count == 0) return series;
        if (candidates.Count == 1) return candidates[0];

        // Multiple models share this SeriesPattern (e.g. H-Series covers AVE-9200,
        // AVH-6520/7520/8620, AVL-1050-X). Prefer the model whose 3rd character
        // matches the series letter — H-Series → AVH-*, A-Series → AVA-*, etc.
        // This surfaces the "native" model family for the series instead of the
        // alphabetically-first cross-listed model.
        if (series.Length > 0)
        {
            char wantChar = char.ToUpperInvariant(series[0]);
            var preferred = candidates
                .Where(m => m.Length > 2 && char.ToUpperInvariant(m[2]) == wantChar)
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (preferred != null) return preferred;
        }

        return candidates.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).First();
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
