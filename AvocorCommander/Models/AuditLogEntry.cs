namespace AvocorCommander.Models;

public sealed class AuditLogEntry
{
    public int    Id            { get; set; }
    public string Timestamp     { get; set; } = string.Empty;
    public string DeviceName    { get; set; } = string.Empty;
    public string DeviceAddress { get; set; } = string.Empty;
    public string CommandName   { get; set; } = string.Empty;
    public string CommandCode   { get; set; } = string.Empty;
    public string Response      { get; set; } = string.Empty;
    public bool   Success       { get; set; } = true;
    public string Notes         { get; set; } = string.Empty;

    // ── Display helpers ──────────────────────────────────────────────────────

    public string DirectionSymbol => Success ? "▶" : "✕";
    public string DirectionColor  => Success ? "#16C080" : "#E74C3C";

    public string TimeString
    {
        get
        {
            if (DateTime.TryParse(Timestamp, out var dt))
                return dt.ToString("HH:mm:ss");
            return Timestamp;
        }
    }
}
