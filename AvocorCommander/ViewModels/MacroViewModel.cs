using AvocorCommander.Core;
using AvocorCommander.Models;
using AvocorCommander.Services;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

namespace AvocorCommander.ViewModels;

public sealed class MacroViewModel : BaseViewModel
{
    private readonly DatabaseService   _db;
    private readonly MacroRunnerService _runner;

    public ObservableCollection<MacroEntry>  Macros     { get; } = [];
    public ObservableCollection<DeviceEntry> AllDevices { get; } = [];
    public ObservableCollection<GroupEntry>  AllGroups  { get; } = [];

    private MacroEntry? _selectedMacro;
    public MacroEntry? SelectedMacro
    {
        get => _selectedMacro;
        set { Set(ref _selectedMacro, value); OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(Steps)); }
    }

    public bool HasSelection => SelectedMacro != null;

    public List<MacroStep> Steps => SelectedMacro?.Steps ?? [];

    private DeviceEntry? _runDevice;
    public DeviceEntry? RunDevice { get => _runDevice; set => Set(ref _runDevice, value); }

    private GroupEntry? _runGroup;
    public GroupEntry? RunGroup { get => _runGroup; set => Set(ref _runGroup, value); }

    private bool _runOnGroup;
    public bool RunOnGroup { get => _runOnGroup; set { Set(ref _runOnGroup, value); OnPropertyChanged(nameof(RunOnDevice)); } }
    public bool RunOnDevice { get => !_runOnGroup; set { RunOnGroup = !value; } }

    private string _statusMessage = "Select a macro to view its steps.";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; set { Set(ref _isRunning, value); CommandManager.InvalidateRequerySuggested(); } }

    // Events for dialog requests
    public event EventHandler?             AddMacroRequested;
    public event EventHandler<MacroEntry>? EditMacroRequested;

    public ICommand AddMacroCommand    { get; }
    public ICommand EditMacroCommand   { get; }
    public ICommand DeleteMacroCommand { get; }
    public ICommand RunMacroCommand    { get; }
    public ICommand RefreshCommand     { get; }

    public MacroViewModel(DatabaseService db, MacroRunnerService runner)
    {
        _db     = db;
        _runner = runner;

        _runner.StepCompleted += (_, msg) =>
            Application.Current?.Dispatcher.Invoke(() => StatusMessage = msg);
        _runner.RunFailed     += (_, msg) =>
            Application.Current?.Dispatcher.Invoke(() => { StatusMessage = $"Failed: {msg}"; IsRunning = false; });
        _runner.RunCompleted  += (_, _) =>
            Application.Current?.Dispatcher.Invoke(() => { StatusMessage = "Macro completed."; IsRunning = false; });

        AddMacroCommand    = new RelayCommand(() => AddMacroRequested?.Invoke(this, EventArgs.Empty));
        EditMacroCommand   = new RelayCommand<MacroEntry>(m => { if (m != null) EditMacroRequested?.Invoke(this, m); }, m => m != null);
        DeleteMacroCommand = new RelayCommand<MacroEntry>(DeleteMacro, m => m != null);
        RunMacroCommand    = new AsyncRelayCommand(RunMacroAsync, () => SelectedMacro != null && !IsRunning);
        RefreshCommand     = new RelayCommand(LoadData);
    }

    public void LoadData()
    {
        var macros  = _db.GetAllMacros();
        var devices = _db.GetAllDevices();
        var groups  = _db.GetAllGroups();

        Macros.Clear();
        foreach (var m in macros)  Macros.Add(m);

        AllDevices.Clear();
        foreach (var d in devices) AllDevices.Add(d);

        AllGroups.Clear();
        foreach (var g in groups)  AllGroups.Add(g);

        if (RunDevice == null && AllDevices.Count > 0) RunDevice = AllDevices[0];
        if (RunGroup  == null && AllGroups.Count  > 0) RunGroup  = AllGroups[0];

        StatusMessage = $"{Macros.Count} macro(s) loaded.";
    }

    public void AddMacro(MacroEntry macro)
    {
        int id = _db.InsertMacro(macro);
        macro.Id = id;
        _db.SetMacroSteps(id, macro.Steps);
        macro.Steps = _db.GetMacroSteps(id);
        Macros.Add(macro);
        SelectedMacro = macro;
        StatusMessage = $"Added macro: {macro.MacroName}";
    }

    public void UpdateMacro(MacroEntry macro)
    {
        _db.UpdateMacro(macro);
        _db.SetMacroSteps(macro.Id, macro.Steps);
        macro.Steps = _db.GetMacroSteps(macro.Id);

        var existing = Macros.FirstOrDefault(m => m.Id == macro.Id);
        if (existing != null)
        {
            existing.MacroName = macro.MacroName;
            existing.Notes     = macro.Notes;
            existing.Steps     = macro.Steps;
            OnPropertyChanged(nameof(Steps));
        }
        StatusMessage = $"Updated macro: {macro.MacroName}";
    }

    private void DeleteMacro(MacroEntry? macro)
    {
        if (macro == null) return;
        if (MessageBox.Show($"Delete macro '{macro.MacroName}'?", "Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _db.DeleteMacro(macro.Id);
        Macros.Remove(macro);
        if (SelectedMacro == macro) SelectedMacro = null;
        StatusMessage = $"Deleted: {macro.MacroName}";
    }

    private async Task RunMacroAsync()
    {
        if (SelectedMacro == null) return;
        if (SelectedMacro.Steps.Count == 0) { StatusMessage = "Macro has no steps."; return; }

        IsRunning = true;
        StatusMessage = $"Running macro: {SelectedMacro.MacroName}\u2026";

        if (RunOnGroup && RunGroup != null)
            await _runner.RunOnGroupAsync(SelectedMacro, RunGroup.Id);
        else if (RunDevice != null)
            await _runner.RunAsync(SelectedMacro, RunDevice.Id);
        else
        {
            StatusMessage = "No target selected.";
            IsRunning = false;
        }
    }
}
