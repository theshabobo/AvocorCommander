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

    private string _filterText = string.Empty;
    public string FilterText
    {
        get => _filterText;
        set { Set(ref _filterText, value); ApplyFilter(); }
    }

    private string _statusMessage = "Ready.";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    // ── Commands ─────────────────────────────────────────────────────────────

    public ICommand RefreshCommand { get; }
    public ICommand ClearCommand   { get; }
    public ICommand ExportCommand  { get; }

    public AuditLogViewModel(DatabaseService db)
    {
        _db = db;

        RefreshCommand = new RelayCommand(LoadLog);
        ClearCommand   = new RelayCommand(ClearLog);
        ExportCommand  = new RelayCommand(ExportLog);
    }

    public void LoadLog()
    {
        _allEntries = _db.GetCommandLog(2000);
        ApplyFilter();
        StatusMessage = $"{_allEntries.Count} log entries.";
    }

    /// <summary>Called by MainViewModel when the scheduler logs a new entry — appends without full reload.</summary>
    public void AppendFromDb()
    {
        var latest = _db.GetCommandLog(2000);
        var existing = _allEntries.Select(e => e.Id).ToHashSet();
        var newEntries = latest.Where(e => !existing.Contains(e.Id)).ToList();
        if (newEntries.Count == 0) return;

        _allEntries.AddRange(newEntries);
        var filter = FilterText.Trim();
        foreach (var e in newEntries)
        {
            if (string.IsNullOrEmpty(filter) ||
                e.DeviceName.Contains(filter,    StringComparison.OrdinalIgnoreCase) ||
                e.CommandName.Contains(filter,   StringComparison.OrdinalIgnoreCase) ||
                e.CommandCode.Contains(filter,   StringComparison.OrdinalIgnoreCase) ||
                e.DeviceAddress.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                FilteredEntries.Add(e);
            }
        }
        StatusMessage = $"{_allEntries.Count} log entries.";
    }

    private void ApplyFilter()
    {
        FilteredEntries.Clear();
        var filter = FilterText.Trim();
        var entries = string.IsNullOrEmpty(filter)
            ? _allEntries
            : _allEntries.Where(e =>
                e.DeviceName.Contains(filter,    StringComparison.OrdinalIgnoreCase) ||
                e.CommandName.Contains(filter,   StringComparison.OrdinalIgnoreCase) ||
                e.CommandCode.Contains(filter,   StringComparison.OrdinalIgnoreCase) ||
                e.DeviceAddress.Contains(filter, StringComparison.OrdinalIgnoreCase));

        foreach (var e in entries) FilteredEntries.Add(e);
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
