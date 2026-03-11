using AvocorCommander.Core;

namespace AvocorCommander.Models;

public sealed class ScheduleRule : BaseViewModel
{
    private int    _id;
    private string _ruleName    = string.Empty;
    private int?   _deviceId;
    private int?   _groupId;
    private int    _commandId;
    private string _commandName = string.Empty;
    private string _targetName  = string.Empty;
    private string _scheduleTime = "08:00";
    private string _recurrence  = "Daily";
    private bool   _isEnabled   = true;
    private string _notes       = string.Empty;
    private string _lastFiredAt = string.Empty;
    private string _lastResult  = string.Empty;

    public int    Id           { get => _id;           set => Set(ref _id, value); }
    public string RuleName     { get => _ruleName;     set => Set(ref _ruleName, value); }
    public int?   DeviceId     { get => _deviceId;     set => Set(ref _deviceId, value); }
    public int?   GroupId      { get => _groupId;      set => Set(ref _groupId, value); }
    public int    CommandId    { get => _commandId;    set => Set(ref _commandId, value); }
    public string CommandName  { get => _commandName;  set => Set(ref _commandName, value); }
    public string TargetName   { get => _targetName;   set => Set(ref _targetName, value); }
    public string ScheduleTime { get => _scheduleTime; set => Set(ref _scheduleTime, value); }
    public string Recurrence   { get => _recurrence;   set => Set(ref _recurrence, value); }
    public bool   IsEnabled    { get => _isEnabled;    set => Set(ref _isEnabled, value); }
    public string Notes        { get => _notes;        set => Set(ref _notes, value); }
    public string LastFiredAt  { get => _lastFiredAt;  set { Set(ref _lastFiredAt, value);  OnPropertyChanged(nameof(LastRunDisplay)); } }
    public string LastResult   { get => _lastResult;   set { Set(ref _lastResult,  value);  OnPropertyChanged(nameof(LastRunDisplay)); } }

    public string Summary      => $"{ScheduleTime}  \u00b7  {Recurrence}  \u2192  {CommandName}  \u2192  {TargetName}";
    public string LastRunDisplay => string.IsNullOrEmpty(LastFiredAt)
        ? "Never run"
        : $"Last: {LastFiredAt}  \u00b7  {LastResult}";
}
