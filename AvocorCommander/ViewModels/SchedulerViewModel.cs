using AvocorCommander.Core;
using AvocorCommander.Models;
using AvocorCommander.Services;
using System.Collections.ObjectModel;
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

    public ICommand AddRuleCommand            { get; }
    public ICommand EditRuleCommand           { get; }
    public ICommand DeleteRuleCommand         { get; }
    public ICommand ToggleRuleCommand         { get; }
    public ICommand RunNowCommand             { get; }
    public ICommand ToggleSchedulerCommand    { get; }
    public ICommand RefreshCommand            { get; }

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

    private void App_OnSchedulerFired(string ruleName)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(
            () => StatusMessage = $"Rule fired: {ruleName}  at {DateTime.Now:HH:mm:ss}");
    }
}
