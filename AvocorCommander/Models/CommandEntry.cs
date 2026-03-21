namespace AvocorCommander.Models;

/// <summary>Mirrors one DeviceList row.</summary>
public sealed class CommandEntry
{
    public int    Id              { get; set; }
    public string SeriesPattern   { get; set; } = string.Empty;
    public string CommandCategory { get; set; } = string.Empty;
    public string CommandName     { get; set; } = string.Empty;
    public string CommandCode     { get; set; } = string.Empty;
    public string Notes           { get; set; } = string.Empty;
    public int    Port            { get; set; }
    public string CommandFormat   { get; set; } = "HEX";

    /// <summary>True when CommandCode still contains placeholder bytes (XX / YY).</summary>
    public bool HasVariableBytes =>
        CommandCode.Contains("XX", StringComparison.OrdinalIgnoreCase) ||
        CommandCode.Contains("YY", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parse CommandCode hex string → byte array for transmission.</summary>
    public byte[] GetBytes(string? overrideCode = null)
    {
        var code = overrideCode ?? CommandCode;
        if (string.IsNullOrWhiteSpace(code)) return [];

        if (CommandFormat == "ASCII")
        {
            return System.Text.Encoding.ASCII.GetBytes(code + "\r");
        }

        // HEX format: "AA BB CC …"
        try
        {
            return code
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => System.Convert.ToByte(t, 16))
                .ToArray();
        }
        catch { return []; }
    }
}
