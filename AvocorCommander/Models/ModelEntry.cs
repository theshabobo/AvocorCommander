namespace AvocorCommander.Models;

/// <summary>Mirrors the Models table row.</summary>
public sealed class ModelEntry
{
    public int    Id             { get; set; }
    public string ModelNumber    { get; set; } = string.Empty;
    public string SeriesPattern  { get; set; } = string.Empty;
}
