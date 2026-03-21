using AvocorCommander.Core;
using AvocorCommander.Models;
using AvocorCommander.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Text.Json;
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

    public ICommand AddMacroCommand       { get; }
    public ICommand EditMacroCommand      { get; }
    public ICommand DeleteMacroCommand    { get; }
    public ICommand DuplicateMacroCommand { get; }
    public ICommand ExportMacrosCommand   { get; }
    public ICommand ImportMacrosCommand   { get; }
    public ICommand RunMacroCommand       { get; }
    public ICommand RefreshCommand        { get; }

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

        AddMacroCommand       = new RelayCommand(() => AddMacroRequested?.Invoke(this, EventArgs.Empty));
        EditMacroCommand      = new RelayCommand<MacroEntry>(m => { if (m != null) EditMacroRequested?.Invoke(this, m); }, m => m != null);
        DeleteMacroCommand    = new RelayCommand<MacroEntry>(DeleteMacro, m => m != null);
        DuplicateMacroCommand = new RelayCommand<MacroEntry>(DuplicateMacro, m => m != null);
        ExportMacrosCommand   = new RelayCommand(ExportMacros);
        ImportMacrosCommand   = new RelayCommand(ImportMacros);
        RunMacroCommand       = new AsyncRelayCommand(RunMacroAsync, () => SelectedMacro != null && !IsRunning);
        RefreshCommand        = new RelayCommand(LoadData);
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

    private void DuplicateMacro(MacroEntry? macro)
    {
        if (macro == null) return;
        var copy = new MacroEntry
        {
            MacroName = $"{macro.MacroName} (Copy)",
            Notes     = macro.Notes,
            Steps     = macro.Steps.Select(s => new MacroStep
            {
                StepOrder     = s.StepOrder,
                CommandId     = s.CommandId,
                CommandName   = s.CommandName,
                SeriesPattern = s.SeriesPattern,
                DelayAfterMs  = s.DelayAfterMs,
            }).ToList(),
        };
        AddMacro(copy);
        StatusMessage = $"Duplicated: {macro.MacroName}";
    }

    private void ExportMacros()
    {
        var dlg = new SaveFileDialog
        {
            Title      = "Export Macros",
            Filter     = "JSON (*.json)|*.json",
            DefaultExt = ".json",
            FileName   = $"Macros_{DateTime.Now:yyyyMMdd}",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var records = Macros.Select(m => new
            {
                m.MacroName,
                m.Notes,
                Steps = m.Steps.Select(s => new
                {
                    s.StepOrder, s.CommandName, s.SeriesPattern, s.DelayAfterMs
                }).ToList(),
            });
            var json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(dlg.FileName, json);
            StatusMessage = $"Exported {Macros.Count} macro(s) to {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex) { StatusMessage = $"Export failed: {ex.Message}"; }
    }

    private void ImportMacros()
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Import Macros",
            Filter = "JSON (*.json)|*.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var json    = System.IO.File.ReadAllText(dlg.FileName);
            var records = JsonSerializer.Deserialize<List<JsonElement>>(json);
            if (records == null) return;

            var allCommands = _db.GetAllCommands();
            int added = 0;

            foreach (var r in records)
            {
                var name = r.TryGetProperty("MacroName", out var p) ? p.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (Macros.Any(m => m.MacroName.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;

                var steps = new List<MacroStep>();
                if (r.TryGetProperty("Steps", out var stepsEl))
                {
                    int order = 1;
                    foreach (var se in stepsEl.EnumerateArray())
                    {
                        var cmdName = se.TryGetProperty("CommandName", out var cn) ? cn.GetString() ?? "" : "";
                        var cmd     = allCommands.FirstOrDefault(c =>
                            c.CommandName.Equals(cmdName, StringComparison.OrdinalIgnoreCase));
                        if (cmd == null) continue;
                        steps.Add(new MacroStep
                        {
                            StepOrder     = se.TryGetProperty("StepOrder",    out var so) ? so.GetInt32() : order,
                            CommandId     = cmd.Id,
                            CommandName   = cmd.CommandName,
                            SeriesPattern = cmd.SeriesPattern,
                            DelayAfterMs  = se.TryGetProperty("DelayAfterMs", out var d)  ? d.GetInt32()  : 0,
                        });
                        order++;
                    }
                }

                AddMacro(new MacroEntry
                {
                    MacroName = name,
                    Notes     = r.TryGetProperty("Notes", out var notes) ? notes.GetString() ?? "" : "",
                    Steps     = steps,
                });
                added++;
            }
            StatusMessage = $"Imported {added} new macro(s) from {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex) { StatusMessage = $"Import failed: {ex.Message}"; }
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
