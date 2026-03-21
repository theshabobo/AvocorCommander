using AvocorCommander.Core;
using AvocorCommander.Models;
using AvocorCommander.Services;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace AvocorCommander.ViewModels;

public sealed class SchedulerViewModel : BaseViewModel
{
    private readonly DatabaseService  _db;
    private readonly SchedulerService _scheduler;

    public ObservableCollection<ScheduleRule> Rules { get; } = [];

    private ScheduleRule? _selectedRule;
    public ScheduleRule? SelectedRule
    {
        get => _selectedRule;
        set { Set(ref _selectedRule, value); OnPropertyChanged(nameof(HasSelection)); }
    }

    public bool HasSelection => SelectedRule != null;

    private string _statusMessage = "Ready.";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    public bool IsSchedulerRunning => _scheduler.IsRunning;
    public string SchedulerButtonLabel => _scheduler.IsRunning ? "Stop Scheduler" : "Start Scheduler";

    // ── Events ────────────────────────────────────────────────────────────────

    public event EventHandler?                  AddRuleRequested;
    public event EventHandler<ScheduleRule>?    EditRuleRequested;

    // ── Commands ─────────────────────────────────────────────────────────────

    public ICommand AddRuleCommand         { get; }
    public ICommand EditRuleCommand        { get; }
    public ICommand DeleteRuleCommand      { get; }
    public ICommand ToggleRuleCommand      { get; }
    public ICommand RunNowCommand          { get; }
    public ICommand ToggleSchedulerCommand { get; }
    public ICommand RefreshCommand         { get; }
    public ICommand ExportRulesCommand     { get; }
    public ICommand ImportRulesCommand     { get; }

    public SchedulerViewModel(DatabaseService db, SchedulerService scheduler)
    {
        _db        = db;
        _scheduler = scheduler;

        _scheduler.RuleFired  += (_, name) => App_OnSchedulerFired(name);
        _scheduler.RuleFailed += (_, msg)  => StatusMessage = $"Rule failed: {msg}";

        AddRuleCommand         = new RelayCommand(
            () => AddRuleRequested?.Invoke(this, EventArgs.Empty));
        EditRuleCommand        = new RelayCommand<ScheduleRule>(
            r => { if (r != null) EditRuleRequested?.Invoke(this, r); },
            r => r != null);
        DeleteRuleCommand      = new RelayCommand<ScheduleRule>(DeleteRule, r => r != null);
        ToggleRuleCommand      = new RelayCommand<ScheduleRule>(ToggleRule, r => r != null);
        RunNowCommand          = new AsyncRelayCommand<ScheduleRule>(RunNowAsync, r => r != null);
        ToggleSchedulerCommand = new RelayCommand(ToggleScheduler);
        RefreshCommand         = new RelayCommand(LoadRules);
        ExportRulesCommand     = new RelayCommand(ExportRules);
        ImportRulesCommand     = new RelayCommand(ImportRules);
    }

    public void LoadRules()
    {
        var rules = _db.GetAllScheduleRules();
        Rules.Clear();
        foreach (var r in rules) Rules.Add(r);
        StatusMessage = $"{Rules.Count} rule(s) loaded.  Scheduler: {(IsSchedulerRunning ? "Running" : "Stopped")}";
    }

    public void AddRule(ScheduleRule rule)
    {
        int newId = _db.InsertScheduleRule(rule);
        rule.Id = newId;
        Rules.Add(rule);
        StatusMessage = $"Added rule: {rule.RuleName}";
    }

    public void UpdateRule(ScheduleRule rule)
    {
        _db.UpdateScheduleRule(rule);
        // Refresh the matching item in the collection
        var existing = Rules.FirstOrDefault(r => r.Id == rule.Id);
        if (existing != null)
        {
            existing.RuleName     = rule.RuleName;
            existing.ScheduleTime = rule.ScheduleTime;
            existing.Recurrence   = rule.Recurrence;
            existing.IsEnabled    = rule.IsEnabled;
            existing.Notes        = rule.Notes;
        }
        StatusMessage = $"Updated rule: {rule.RuleName}";
    }

    private void DeleteRule(ScheduleRule? rule)
    {
        if (rule == null) return;
        var result = MessageBox.Show(
            $"Delete rule '{rule.RuleName}'?",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;
        _db.DeleteScheduleRule(rule.Id);
        Rules.Remove(rule);
        if (SelectedRule == rule) SelectedRule = null;
        StatusMessage = $"Deleted: {rule.RuleName}";
    }

    private void ToggleRule(ScheduleRule? rule)
    {
        if (rule == null) return;
        rule.IsEnabled = !rule.IsEnabled;
        _db.UpdateScheduleRule(rule);
        StatusMessage = $"{rule.RuleName}: {(rule.IsEnabled ? "Enabled" : "Disabled")}";
    }

    private void ToggleScheduler()
    {
        if (_scheduler.IsRunning) _scheduler.Stop();
        else                      _scheduler.Start();
        OnPropertyChanged(nameof(IsSchedulerRunning));
        OnPropertyChanged(nameof(SchedulerButtonLabel));
        StatusMessage = $"Scheduler {(IsSchedulerRunning ? "started" : "stopped")}.";
    }

    private async Task RunNowAsync(ScheduleRule? rule)
    {
        if (rule == null) return;
        StatusMessage = $"Running: {rule.RuleName}…";
        await _scheduler.RunRuleNowAsync(rule);
        StatusMessage = $"Completed: {rule.RuleName}  at {DateTime.Now:HH:mm:ss}";
    }

    private void ExportRules()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Export Schedule Rules",
            Filter     = "JSON (*.json)|*.json",
            DefaultExt = ".json",
            FileName   = $"ScheduleRules_{DateTime.Now:yyyyMMdd}",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var records = Rules.Select(r => new
            {
                r.RuleName, r.ScheduleTime, r.Recurrence, r.IsEnabled, r.Notes,
                r.CommandName, r.TargetName,
                TargetType = r.GroupId.HasValue ? "Group" : "Device",
            });
            var json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(dlg.FileName, json);
            StatusMessage = $"Exported {Rules.Count} rule(s) to {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex) { StatusMessage = $"Export failed: {ex.Message}"; }
    }

    private void ImportRules()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Import Schedule Rules",
            Filter = "JSON (*.json)|*.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var json    = System.IO.File.ReadAllText(dlg.FileName);
            var records = JsonSerializer.Deserialize<List<JsonElement>>(json);
            if (records == null) return;

            var allCommands = _db.GetAllCommands();
            var allDevices  = _db.GetAllDevices();
            var allGroups   = _db.GetAllGroups();
            int added = 0;

            foreach (var r in records)
            {
                var name = r.TryGetProperty("RuleName", out var p) ? p.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (Rules.Any(x => x.RuleName.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;

                var cmdName    = r.TryGetProperty("CommandName", out var cn) ? cn.GetString() ?? "" : "";
                var targetName = r.TryGetProperty("TargetName",  out var tn) ? tn.GetString() ?? "" : "";
                var targetType = r.TryGetProperty("TargetType",  out var tt) ? tt.GetString() ?? "Device" : "Device";

                var cmd = allCommands.FirstOrDefault(c =>
                    c.CommandName.Equals(cmdName, StringComparison.OrdinalIgnoreCase));
                if (cmd == null) continue;

                int? deviceId = null;
                int? groupId  = null;
                string resolvedTarget = targetName;

                if (targetType == "Group")
                {
                    var grp = allGroups.FirstOrDefault(g =>
                        g.GroupName.Equals(targetName, StringComparison.OrdinalIgnoreCase));
                    if (grp != null) { groupId = grp.Id; resolvedTarget = grp.GroupName; }
                }
                else
                {
                    var dev = allDevices.FirstOrDefault(d =>
                        d.DeviceName.Equals(targetName, StringComparison.OrdinalIgnoreCase));
                    if (dev != null) { deviceId = dev.Id; resolvedTarget = dev.DeviceName; }
                }

                if (deviceId == null && groupId == null) continue;

                AddRule(new ScheduleRule
                {
                    RuleName     = name,
                    ScheduleTime = r.TryGetProperty("ScheduleTime", out var st)  ? st.GetString()  ?? "08:00" : "08:00",
                    Recurrence   = r.TryGetProperty("Recurrence",   out var rec) ? rec.GetString() ?? "Daily"  : "Daily",
                    IsEnabled    = r.TryGetProperty("IsEnabled",    out var ie)  && ie.GetBoolean(),
                    Notes        = r.TryGetProperty("Notes",        out var nt)  ? nt.GetString()  ?? "" : "",
                    CommandId    = cmd.Id,
                    CommandName  = cmd.CommandName,
                    DeviceId     = deviceId,
                    GroupId      = groupId,
                    TargetName   = resolvedTarget,
                });
                added++;
            }
            StatusMessage = $"Imported {added} new rule(s) from {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex) { StatusMessage = $"Import failed: {ex.Message}"; }
    }

    private void App_OnSchedulerFired(string ruleName)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(
            () => StatusMessage = $"Rule fired: {ruleName}  at {DateTime.Now:HH:mm:ss}");
    }
}
