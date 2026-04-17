namespace AvocorCommander.Services;

/// <summary>
/// Queries B-Series displays for installed apps and generates
/// Open/Close commands for each discovered application.
/// </summary>
public static class AppDiscoveryService
{
    /// <summary>
    /// Queries a B-Series display for installed apps and returns
    /// Open/Close command tuples for each discovered app.
    /// Returns empty if not B-Series, not connected, or query fails.
    /// </summary>
    public static async Task<List<(string category, string name, string code, string format)>>
        DiscoverAppsAsync(
            ConnectionManager connMgr,
            DatabaseService db,
            int deviceId)
    {
        var result = new List<(string, string, string, string)>();

        var device = db.GetAllDevices().FirstOrDefault(d => d.Id == deviceId);
        if (device == null) return result;

        var series = db.GetSeriesForModel(device.ModelNumber);
        if (series != "B-Series") return result;

        if (!connMgr.IsConnected(deviceId)) return result;

        // Send the !List ? command to enumerate installed apps
        var listCmd = System.Text.Encoding.ASCII.GetBytes("!List ?\r");
        var response = await connMgr.SendAsync(deviceId, listCmd);
        if (response == null || response.Length == 0) return result;

        // Parse response — expected format: "~RisePlayer|KorbytAnywhereClient"
        var text = System.Text.Encoding.ASCII.GetString(response)
            .Trim('\0', '\r', '\n', ' ', '\t');

        // Strip leading tilde (acknowledgement prefix)
        if (text.StartsWith('~')) text = text[1..].Trim();
        if (string.IsNullOrEmpty(text)) return result;

        // Handle "App List " prefix if present in some firmware versions
        if (text.StartsWith("App List ", StringComparison.OrdinalIgnoreCase))
            text = text[9..].Trim();

        var apps = text.Split('|', StringSplitOptions.RemoveEmptyEntries);

        foreach (var app in apps)
        {
            var appName = app.Trim();
            if (string.IsNullOrEmpty(appName)) continue;

            result.Add(("Application", $"Open {appName}", $"!Open {appName}", "ASCII"));
            result.Add(("Application", $"Close {appName}", $"!Close {appName}", "ASCII"));
        }

        return result;
    }
}
