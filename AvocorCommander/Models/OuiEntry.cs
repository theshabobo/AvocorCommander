namespace AvocorCommander.Models;

/// <summary>Mirrors one OUITable row.</summary>
public sealed class OuiEntry
{
    public int    Id            { get; set; }
    public string OUIPrefix     { get; set; } = string.Empty;
    public string SeriesLabel   { get; set; } = string.Empty;
    public string SeriesPattern { get; set; } = string.Empty;
    public string Notes         { get; set; } = string.Empty;
}
