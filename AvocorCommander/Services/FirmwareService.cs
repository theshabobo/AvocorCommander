using AvocorCommander.Core;
using AvocorCommander.Models;

namespace AvocorCommander.Services;

/// <summary>
/// Queries a device for its firmware/model version via the appropriate
/// series-specific "Get Version" or "Get Model Version" command.
/// </summary>
public static class FirmwareService
{
    /// <summary>
    /// Sends a version query to the specified device and parses the response.
    /// Returns null if the device is not connected, the series is unknown,
    /// or no version command exists for that series.
    /// </summary>
    public static async Task<string?> QueryFirmwareVersionAsync(
        ConnectionManager connMgr,
        DatabaseService   db,
        int               deviceId)
    {
        if (!connMgr.IsConnected(deviceId))
            return null;

        var device = db.GetAllDevices().FirstOrDefault(d => d.Id == deviceId);
        if (device == null) return null;

        string? series = db.GetSeriesForModel(device.ModelNumber);
        if (string.IsNullOrEmpty(series)) return null;

        var commands = db.GetCommandsBySeries(series);

        // Look for a version-query command by common naming conventions
        var versionCmd = commands.FirstOrDefault(c =>
            string.Equals(c.CommandName, "Get Version", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.CommandName, "Get Model Version", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.CommandName, "Get Firmware Version", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.CommandName, "Version Query", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.CommandName, "Get FW Version", StringComparison.OrdinalIgnoreCase));

        if (versionCmd == null) return null;

        byte[] bytes = versionCmd.GetBytes();
        if (bytes.Length == 0) return null;

        var response = await connMgr.SendAsync(deviceId, bytes);
        if (response == null) return null;

        string parsed = ResponseParser.Parse(response, bytes, versionCmd.CommandFormat, series);
        return string.IsNullOrWhiteSpace(parsed) ? null : parsed.Trim();
    }
}
