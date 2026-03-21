using AvocorCommander.Core;
using AvocorCommander.Models;
using AvocorCommander.Services;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;

namespace AvocorCommander.ViewModels;

public sealed class AuditLogViewModel : BaseViewModel
{
    private readonly DatabaseService _db;

    private List<AuditLogEntry> _allEntries = [];

    public ObservableCollection<AuditLogEntry> FilteredEntries { get; } = [];
    public ObservableCollection<string>        DeviceOptions   { get; } = ["All"];

    public static IReadOnlyList<string> SuccessOptions { get; } = ["All", "Success", "Failures"];

    private string _filterText = string.Empty;
    public string FilterText
    {
        get => _filterText;
        set { Set(ref _filterText, value); ApplyFilter(); }
    }

    private string _filterDevice = "All";
    public string FilterDevice
    {
        get => _filterDevice;
        set { Set(ref _filterDevice, value); ApplyFilter(); }
    }

    private string _filterSuccess = "All";
    public string FilterSuccess
    {
        get => _filterSuccess;
        set { Set(ref _filterSuccess, value); ApplyFilter(); }
    }

    private DateTime? _filterDateFrom;
    public DateTime? FilterDateFrom
    {
        get => _filterDateFrom;
        set { Set(ref _filterDateFrom, value); ApplyFilter(); }
    }

    private DateTime? _filterDateTo;
    public DateTime? FilterDateTo
    {
        get => _filterDateTo;
        set { Set(ref _filterDateTo, value); ApplyFilter(); }
    }

    private string _statusMessage = "Ready.";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    // ── Commands ─────────────────────────────────────────────────────────────

    public ICommand RefreshCommand      { get; }
    public ICommand ClearCommand        { get; }
    public ICommand ExportCommand       { get; }
    public ICommand ClearFiltersCommand { get; }

    public AuditLogViewModel(DatabaseService db)
    {
        _db = db;

        RefreshCommand      = new RelayCommand(LoadLog);
        ClearCommand        = new RelayCommand(ClearLog);
        ExportCommand       = new RelayCommand(ExportLog);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
    }

    public void LoadLog()
    {
        _allEntries = _db.GetCommandLog(2000);
        RebuildDeviceOptions();
        ApplyFilter();
        StatusMessage = $"{_allEntries.Count} log entries.";
    }

    private void RebuildDeviceOptions()
    {
        var current = FilterDevice;
        DeviceOptions.Clear();
        DeviceOptions.Add("All");
        foreach (var name in _allEntries
            .Select(e => e.DeviceName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct()
            .OrderBy(n => n))
        {
            DeviceOptions.Add(name);
        }
        // Keep selection if it's still valid, else reset to All
        FilterDevice = DeviceOptions.Contains(current) ? current : "All";
    }

    /// <summary>Called by MainViewModel when the scheduler logs a new entry — appends without full reload.</summary>
    public void AppendFromDb()
    {
        var latest = _db.GetCommandLog(2000);
        var existing = _allEntries.Select(e => e.Id).ToHashSet();
        var newEntries = latest.Where(e => !existing.Contains(e.Id)).ToList();
        if (newEntries.Count == 0) return;

        _allEntries.AddRange(newEntries);
        // Add new device names to DeviceOptions if needed
        foreach (var name in newEntries.Select(e => e.DeviceName).Where(n => !string.IsNullOrEmpty(n) && !DeviceOptions.Contains(n)))
            DeviceOptions.Add(name);

        var text     = FilterText.Trim();
        var dateFrom = FilterDateFrom?.Date;
        var dateTo   = FilterDateTo?.Date;
        foreach (var e in newEntries)
        {
            if (!string.IsNullOrEmpty(text) &&
                !e.DeviceName.Contains(text,    StringComparison.OrdinalIgnoreCase) &&
                !e.CommandName.Contains(text,   StringComparison.OrdinalIgnoreCase) &&
                !e.CommandCode.Contains(text,   StringComparison.OrdinalIgnoreCase) &&
                !e.DeviceAddress.Contains(text, StringComparison.OrdinalIgnoreCase))
                continue;
            if (FilterDevice != "All" && !e.DeviceName.Equals(FilterDevice, StringComparison.OrdinalIgnoreCase)) continue;
            if (FilterSuccess == "Success"  && !e.Success) continue;
            if (FilterSuccess == "Failures" &&  e.Success) continue;
            if ((dateFrom.HasValue || dateTo.HasValue) && DateTime.TryParse(e.Timestamp, out var dt))
            {
                if (dateFrom.HasValue && dt.Date < dateFrom.Value) continue;
                if (dateTo.HasValue   && dt.Date > dateTo.Value)   continue;
            }
            FilteredEntries.Add(e);
        }
        StatusMessage = $"{_allEntries.Count} log entries.";
    }

    private void ApplyFilter()
    {
        FilteredEntries.Clear();
        var text     = FilterText.Trim();
        var dateFrom = FilterDateFrom?.Date;
        var dateTo   = FilterDateTo?.Date;

        foreach (var e in _allEntries)
        {
            // Text search
            if (!string.IsNullOrEmpty(text) &&
                !e.DeviceName.Contains(text,    StringComparison.OrdinalIgnoreCase) &&
                !e.CommandName.Contains(text,   StringComparison.OrdinalIgnoreCase) &&
                !e.CommandCode.Contains(text,   StringComparison.OrdinalIgnoreCase) &&
                !e.DeviceAddress.Contains(text, StringComparison.OrdinalIgnoreCase))
                continue;

            // Device filter
            if (FilterDevice != "All" &&
                !e.DeviceName.Equals(FilterDevice, StringComparison.OrdinalIgnoreCase))
                continue;

            // Success filter
            if (FilterSuccess == "Success"  && !e.Success) continue;
            if (FilterSuccess == "Failures" &&  e.Success) continue;

            // Date range
            if (dateFrom.HasValue || dateTo.HasValue)
            {
                if (!DateTime.TryParse(e.Timestamp, out var dt)) continue;
                if (dateFrom.HasValue && dt.Date < dateFrom.Value) continue;
                if (dateTo.HasValue   && dt.Date > dateTo.Value)   continue;
            }

            FilteredEntries.Add(e);
        }
    }

    private void ClearFilters()
    {
        _filterText    = string.Empty;
        _filterDevice  = "All";
        _filterSuccess = "All";
        _filterDateFrom = null;
        _filterDateTo   = null;
        OnPropertyChanged(nameof(FilterText));
        OnPropertyChanged(nameof(FilterDevice));
        OnPropertyChanged(nameof(FilterSuccess));
        OnPropertyChanged(nameof(FilterDateFrom));
        OnPropertyChanged(nameof(FilterDateTo));
        ApplyFilter();
    }

    private void ClearLog()
    {
        var result = System.Windows.MessageBox.Show(
            "Clear all audit log entries from the database?",
            "Clear Audit Log",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.Yes) return;

        _db.ClearCommandLog();
        _allEntries.Clear();
        FilteredEntries.Clear();
        StatusMessage = "Log cleared.";
    }

    private void ExportLog()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Export Audit Log",
            Filter     = "CSV (*.csv)|*.csv|Text (*.txt)|*.txt",
            DefaultExt = ".csv",
            FileName   = $"AuditLog_{DateTime.Now:yyyyMMdd_HHmmss}"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var sb  = new StringBuilder();
            var ext = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();

            if (ext == ".csv")
            {
                sb.AppendLine("Timestamp,Device,Address,Command,Code,Success,Notes");
                foreach (var e in FilteredEntries)
                    sb.AppendLine($"\"{e.Timestamp}\",\"{e.DeviceName}\",\"{e.DeviceAddress}\"," +
                                  $"\"{e.CommandName}\",\"{e.CommandCode}\",{e.Success},\"{e.Notes}\"");
            }
            else
            {
                foreach (var e in FilteredEntries)
                    sb.AppendLine($"{e.TimeString}  {(e.Success ? "OK" : "FAIL"),-6}  " +
                                  $"{e.DeviceName,-20}  {e.CommandName}  [{e.CommandCode}]");
            }

            System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            StatusMessage = $"Exported {FilteredEntries.Count} entries to {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }
}
