using AvocorCommander.Core;

namespace AvocorCommander.Models;

public sealed class ScanResult : BaseViewModel
{
    private bool _isSelected;

    public string IpAddress    { get; set; } = string.Empty;
    public string MacAddress   { get; set; } = string.Empty;
    public string Vendor       { get; set; } = string.Empty;
    public string ModelNumber  { get; set; } = string.Empty;
    public string SeriesPattern { get; set; } = string.Empty;
    public string Hostname     { get; set; } = string.Empty;
    public bool   IsOnline     { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public string StatusLabel => IsOnline ? "Online" : "Offline";
    public string StatusColor => IsOnline ? "#16C080" : "#6A7A8A";
}
