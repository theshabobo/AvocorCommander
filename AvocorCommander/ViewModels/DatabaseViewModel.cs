using AvocorCommander.Core;
using AvocorCommander.Models;
using AvocorCommander.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace AvocorCommander.ViewModels;

/// <summary>
/// Database management section — CRUD for DeviceList, Models, OUITable,
/// Groups, StoredDevices column view, and MACFilter.
/// </summary>
public sealed class DatabaseViewModel : BaseViewModel
{
    private readonly DatabaseService _db;

    // ── Tabs ─────────────────────────────────────────────────────────────────

    private string _selectedTab = "Commands";
    public string SelectedTab
    {
        get => _selectedTab;
        set
        {
            Set(ref _selectedTab, value);
            OnPropertyChanged(nameof(IsCommandsTab));
            OnPropertyChanged(nameof(IsModelsTab));
            OnPropertyChanged(nameof(IsOuiTab));
            OnPropertyChanged(nameof(IsGroupsTab));
            LoadCurrentTab();
        }
    }

    // ── Commands (DeviceList) tab ─────────────────────────────────────────────

    public ObservableCollection<CommandEntry> Commands { get; } = [];

    private CommandEntry? _selectedCommand;
    public CommandEntry? SelectedCommand
    {
        get => _selectedCommand;
        set { Set(ref _selectedCommand, value); OnPropertyChanged(nameof(HasCommandSelection)); PopulateCommandEdit(); }
    }

    public bool HasCommandSelection => SelectedCommand != null;

    // Edit form fields
    private string _cmdSeries   = string.Empty;
    private string _cmdCategory = string.Empty;
    private string _cmdName     = string.Empty;
    private string _cmdCode     = string.Empty;
    private string _cmdNotes    = string.Empty;
    private int    _cmdPort;
    private string _cmdFormat   = "HEX";

    public string CmdSeries   { get => _cmdSeries;   set => Set(ref _cmdSeries, value); }
    public string CmdCategory { get => _cmdCategory; set => Set(ref _cmdCategory, value); }
    public string CmdName     { get => _cmdName;     set => Set(ref _cmdName, value); }
    public string CmdCode     { get => _cmdCode;     set => Set(ref _cmdCode, value); }
    public string CmdNotes    { get => _cmdNotes;    set => Set(ref _cmdNotes, value); }
    public int    CmdPort     { get => _cmdPort;     set => Set(ref _cmdPort, value); }
    public string CmdFormat   { get => _cmdFormat;   set => Set(ref _cmdFormat, value); }

    public ObservableCollection<string> AllSeries { get; } = [];

    // ── Models tab ───────────────────────────────────────────────────────────

    public ObservableCollection<ModelEntry> ModelEntries { get; } = [];
    private ModelEntry? _selectedModel;
    public ModelEntry? SelectedModel
    {
        get => _selectedModel;
        set { Set(ref _selectedModel, value); OnPropertyChanged(nameof(HasModelSelection)); PopulateModelEdit(); }
    }
    public bool HasModelSelection => SelectedModel != null;

    private string _modelNumber = string.Empty;
    private string _modelSeries = string.Empty;
    public string ModelNumber  { get => _modelNumber; set => Set(ref _modelNumber, value); }
    public string ModelSeries  { get => _modelSeries; set => Set(ref _modelSeries, value); }

    // ── OUI tab ───────────────────────────────────────────────────────────────

    public ObservableCollection<OuiEntry> OuiEntries { get; } = [];
    private OuiEntry? _selectedOui;
    public OuiEntry? SelectedOui
    {
        get => _selectedOui;
        set { Set(ref _selectedOui, value); OnPropertyChanged(nameof(HasOuiSelection)); PopulateOuiEdit(); }
    }
    public bool HasOuiSelection => SelectedOui != null;

    private string _ouiPrefix  = string.Empty;
    private string _ouiLabel   = string.Empty;
    private string _ouiSeries  = string.Empty;
    private string _ouiNotes   = string.Empty;
    public string OuiPrefix  { get => _ouiPrefix;  set => Set(ref _ouiPrefix, value); }
    public string OuiLabel   { get => _ouiLabel;   set => Set(ref _ouiLabel, value); }
    public string OuiSeries  { get => _ouiSeries;  set => Set(ref _ouiSeries, value); }
    public string OuiNotes   { get => _ouiNotes;   set => Set(ref _ouiNotes, value); }

    // ── Groups tab ───────────────────────────────────────────────────────────

    public ObservableCollection<GroupEntry>  GroupEntries   { get; } = [];
    public ObservableCollection<DeviceEntry> AllDevices     { get; } = [];
    private GroupEntry? _selectedGroup;
    public GroupEntry? SelectedGroup
    {
        get => _selectedGroup;
        set { Set(ref _selectedGroup, value); OnPropertyChanged(nameof(HasGroupSelection)); PopulateGroupEdit(); }
    }
    public bool HasGroupSelection => SelectedGroup != null;

    private string _groupName  = string.Empty;
    private string _groupNotes = string.Empty;
    public string GroupName  { get => _groupName;  set => Set(ref _groupName, value); }
    public string GroupNotes { get => _groupNotes; set => Set(ref _groupNotes, value); }

    // ── Status ────────────────────────────────────────────────────────────────

    private string _statusMessage = "Select a tab to manage database records.";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    // ── Commands (ICommand) ───────────────────────────────────────────────────

    public ICommand SaveCommandCmd           { get; }
    public ICommand DeleteCommandCmd         { get; }
    public ICommand NewCommandCmd            { get; }
    public ICommand ExportCommandsCsvCommand { get; }
    public ICommand ImportCommandsCsvCommand { get; }

    public ICommand SaveModelCmd         { get; }
    public ICommand DeleteModelCmd       { get; }
    public ICommand NewModelCmd          { get; }

    public ICommand SaveOuiCmd           { get; }
    public ICommand DeleteOuiCmd         { get; }
    public ICommand NewOuiCmd            { get; }

    public ICommand SaveGroupCmd         { get; }
    public ICommand DeleteGroupCmd       { get; }
    public ICommand NewGroupCmd          { get; }
    public ICommand ToggleGroupMemberCmd { get; }

    public ICommand RefreshCmd           { get; }
    public ICommand SwitchTabCommand     { get; }

    public bool IsCommandsTab => SelectedTab == "Commands";
    public bool IsModelsTab   => SelectedTab == "Models";
    public bool IsOuiTab      => SelectedTab == "OUI";
    public bool IsGroupsTab   => SelectedTab == "Groups";

    public DatabaseViewModel(DatabaseService db)
    {
        _db = db;

        SaveCommandCmd           = new RelayCommand(SaveCommand,   () => !string.IsNullOrWhiteSpace(CmdName));
        DeleteCommandCmd         = new RelayCommand(DeleteCommand, () => HasCommandSelection);
        NewCommandCmd            = new RelayCommand(NewCommand);
        ExportCommandsCsvCommand = new RelayCommand(ExportCommandsCsv);
        ImportCommandsCsvCommand = new RelayCommand(ImportCommandsCsv);

        SaveModelCmd         = new RelayCommand(SaveModel,   () => !string.IsNullOrWhiteSpace(ModelNumber));
        DeleteModelCmd       = new RelayCommand(DeleteModel, () => HasModelSelection);
        NewModelCmd          = new RelayCommand(NewModel);

        SaveOuiCmd           = new RelayCommand(SaveOui,   () => !string.IsNullOrWhiteSpace(OuiPrefix));
        DeleteOuiCmd         = new RelayCommand(DeleteOui, () => HasOuiSelection);
        NewOuiCmd            = new RelayCommand(NewOui);

        SaveGroupCmd         = new RelayCommand(SaveGroup,   () => !string.IsNullOrWhiteSpace(GroupName));
        DeleteGroupCmd       = new RelayCommand(DeleteGroup, () => HasGroupSelection);
        NewGroupCmd          = new RelayCommand(NewGroup);
        ToggleGroupMemberCmd = new RelayCommand<DeviceEntry>(ToggleGroupMember);

        RefreshCmd           = new RelayCommand(LoadCurrentTab);
        SwitchTabCommand     = new RelayCommand<string>(tab => { if (!string.IsNullOrEmpty(tab)) SelectedTab = tab; });
    }

    public void Initialize()
    {
        var series = _db.GetDistinctSeries();
        AllSeries.Clear();
        foreach (var s in series) AllSeries.Add(s);
        LoadCurrentTab();
    }

    private void LoadCurrentTab()
    {
        switch (SelectedTab)
        {
            case "Commands": LoadCommands(); break;
            case "Models":   LoadModels();   break;
            case "OUI":      LoadOui();      break;
            case "Groups":   LoadGroups();   break;
        }
    }

    // ── Commands CRUD ─────────────────────────────────────────────────────────

    private void LoadCommands()
    {
        var cmds = _db.GetAllCommands();
        Commands.Clear();
        foreach (var c in cmds) Commands.Add(c);
        StatusMessage = $"{cmds.Count} command(s) in DeviceList.";
    }

    private void PopulateCommandEdit()
    {
        if (SelectedCommand == null) return;
        CmdSeries   = SelectedCommand.SeriesPattern;
        CmdCategory = SelectedCommand.CommandCategory;
        CmdName     = SelectedCommand.CommandName;
        CmdCode     = SelectedCommand.CommandCode;
        CmdNotes    = SelectedCommand.Notes;
        CmdPort     = SelectedCommand.Port;
        CmdFormat   = SelectedCommand.CommandFormat;
    }

    private void NewCommand()
    {
        SelectedCommand = null;
        CmdSeries = AllSeries.FirstOrDefault() ?? string.Empty;
        CmdCategory = CmdName = CmdCode = CmdNotes = string.Empty;
        CmdPort = 0; CmdFormat = "HEX";
    }

    private void SaveCommand()
    {
        var c = new CommandEntry
        {
            Id              = SelectedCommand?.Id ?? 0,
            SeriesPattern   = CmdSeries,
            CommandCategory = CmdCategory,
            CommandName     = CmdName,
            CommandCode     = CmdCode,
            Notes           = CmdNotes,
            Port            = CmdPort,
            CommandFormat   = CmdFormat,
        };

        if (c.Id == 0) { _db.InsertCommand(c); StatusMessage = $"Inserted: {c.CommandName}"; }
        else           { _db.UpdateCommand(c); StatusMessage = $"Updated: {c.CommandName}"; }

        LoadCommands();
    }

    private void DeleteCommand()
    {
        if (SelectedCommand == null) return;
        if (Confirm($"Delete command '{SelectedCommand.CommandName}'?"))
        {
            _db.DeleteCommand(SelectedCommand.Id);
            StatusMessage = $"Deleted: {SelectedCommand.CommandName}";
            LoadCommands();
        }
    }

    // ── Models CRUD ───────────────────────────────────────────────────────────

    private void LoadModels()
    {
        var models = _db.GetAllModels();
        ModelEntries.Clear();
        foreach (var m in models) ModelEntries.Add(m);
        StatusMessage = $"{models.Count} model(s).";
    }

    private void PopulateModelEdit()
    {
        if (SelectedModel == null) return;
        ModelNumber = SelectedModel.ModelNumber;
        ModelSeries = SelectedModel.SeriesPattern;
    }

    private void NewModel() { SelectedModel = null; ModelNumber = ModelSeries = string.Empty; }

    private void SaveModel()
    {
        var m = new ModelEntry { Id = SelectedModel?.Id ?? 0, ModelNumber = ModelNumber, SeriesPattern = ModelSeries };
        if (m.Id == 0) { _db.InsertModel(m);  StatusMessage = $"Inserted: {m.ModelNumber}"; }
        else           { _db.UpdateModel(m);  StatusMessage = $"Updated: {m.ModelNumber}"; }
        LoadModels();
    }

    private void DeleteModel()
    {
        if (SelectedModel == null) return;
        if (Confirm($"Delete model '{SelectedModel.ModelNumber}'?"))
        {
            _db.DeleteModel(SelectedModel.Id);
            StatusMessage = $"Deleted: {SelectedModel.ModelNumber}";
            LoadModels();
        }
    }

    // ── OUI CRUD ──────────────────────────────────────────────────────────────

    private void LoadOui()
    {
        var entries = _db.GetAllOuiEntries();
        OuiEntries.Clear();
        foreach (var e in entries) OuiEntries.Add(e);
        StatusMessage = $"{entries.Count} OUI entries.";
    }

    private void PopulateOuiEdit()
    {
        if (SelectedOui == null) return;
        OuiPrefix = SelectedOui.OUIPrefix;
        OuiLabel  = SelectedOui.SeriesLabel;
        OuiSeries = SelectedOui.SeriesPattern;
        OuiNotes  = SelectedOui.Notes;
    }

    private void NewOui() { SelectedOui = null; OuiPrefix = OuiLabel = OuiSeries = OuiNotes = string.Empty; }

    private void SaveOui()
    {
        var e = new OuiEntry { Id = SelectedOui?.Id ?? 0, OUIPrefix = OuiPrefix, SeriesLabel = OuiLabel, SeriesPattern = OuiSeries, Notes = OuiNotes };
        if (e.Id == 0) { _db.InsertOuiEntry(e);  StatusMessage = $"Inserted: {e.OUIPrefix}"; }
        else           { _db.UpdateOuiEntry(e);  StatusMessage = $"Updated: {e.OUIPrefix}"; }
        LoadOui();
    }

    private void DeleteOui()
    {
        if (SelectedOui == null) return;
        if (Confirm($"Delete OUI '{SelectedOui.OUIPrefix}'?"))
        {
            _db.DeleteOuiEntry(SelectedOui.Id);
            StatusMessage = $"Deleted: {SelectedOui.OUIPrefix}";
            LoadOui();
        }
    }

    // ── Groups CRUD ───────────────────────────────────────────────────────────

    private void LoadGroups()
    {
        var groups  = _db.GetAllGroups();
        var devices = _db.GetAllDevices();

        GroupEntries.Clear();
        foreach (var g in groups) GroupEntries.Add(g);

        AllDevices.Clear();
        foreach (var d in devices) AllDevices.Add(d);

        StatusMessage = $"{groups.Count} group(s), {devices.Count} devices.";
    }

    private void PopulateGroupEdit()
    {
        if (SelectedGroup == null) return;
        GroupName  = SelectedGroup.GroupName;
        GroupNotes = SelectedGroup.Notes;
    }

    private void NewGroup() { SelectedGroup = null; GroupName = GroupNotes = string.Empty; }

    private void SaveGroup()
    {
        var g = new GroupEntry { Id = SelectedGroup?.Id ?? 0, GroupName = GroupName, Notes = GroupNotes };
        if (g.Id == 0) { int newId = _db.InsertGroup(g); g.Id = newId; StatusMessage = $"Inserted: {g.GroupName}"; }
        else           { _db.UpdateGroup(g); StatusMessage = $"Updated: {g.GroupName}"; }
        LoadGroups();
    }

    private void DeleteGroup()
    {
        if (SelectedGroup == null) return;
        if (Confirm($"Delete group '{SelectedGroup.GroupName}'?"))
        {
            _db.DeleteGroup(SelectedGroup.Id);
            StatusMessage = $"Deleted: {SelectedGroup.GroupName}";
            LoadGroups();
        }
    }

    private void ToggleGroupMember(DeviceEntry? device)
    {
        if (device == null || SelectedGroup == null) return;
        if (SelectedGroup.MemberDeviceIds.Contains(device.Id))
            SelectedGroup.MemberDeviceIds.Remove(device.Id);
        else
            SelectedGroup.MemberDeviceIds.Add(device.Id);

        _db.SetGroupMembers(SelectedGroup.Id, SelectedGroup.MemberDeviceIds);
        OnPropertyChanged(nameof(SelectedGroup));
    }

    // ── CSV Import / Export ─────────────────────────────────────────────────

    private static string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        int i = 0;
        while (i <= line.Length)
        {
            if (i == line.Length) { fields.Add(string.Empty); break; }

            if (line[i] == '"')
            {
                var sb = new StringBuilder();
                i++; // skip opening quote
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i += 2; }
                        else { i++; break; }
                    }
                    else { sb.Append(line[i]); i++; }
                }
                fields.Add(sb.ToString());
                if (i < line.Length && line[i] == ',') i++; // skip comma
            }
            else
            {
                int next = line.IndexOf(',', i);
                if (next < 0) { fields.Add(line[i..]); break; }
                fields.Add(line[i..next]);
                i = next + 1;
            }
        }
        return fields;
    }

    private void ExportCommandsCsv()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "CSV Files (*.csv)|*.csv",
            FileName = "commands_export.csv",
            Title = "Export Commands as CSV"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            var cmds = _db.GetAllCommands();
            var sb = new StringBuilder();
            sb.AppendLine("SeriesPattern,CommandCategory,CommandName,CommandCode,Notes,Port,CommandFormat");

            foreach (var c in cmds)
            {
                sb.AppendLine(string.Join(",",
                    CsvEscape(c.SeriesPattern),
                    CsvEscape(c.CommandCategory),
                    CsvEscape(c.CommandName),
                    CsvEscape(c.CommandCode),
                    CsvEscape(c.Notes),
                    c.Port.ToString(),
                    CsvEscape(c.CommandFormat)));
            }

            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            StatusMessage = $"Exported {cmds.Count} command(s) to CSV.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportCommandsCsv()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "CSV Files (*.csv)|*.csv",
            Title = "Import Commands from CSV"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            var lines = File.ReadAllLines(dlg.FileName, Encoding.UTF8);
            if (lines.Length < 2) { MessageBox.Show("CSV file is empty or has no data rows.", "Import", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            // Skip header
            var existing = _db.GetAllCommands();
            var existingKeys = new HashSet<string>(
                existing.Select(c => $"{c.SeriesPattern}|{c.CommandCategory}|{c.CommandName}"),
                StringComparer.OrdinalIgnoreCase);

            int imported = 0, skipped = 0, errors = 0;

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                try
                {
                    var fields = ParseCsvLine(lines[i]);
                    if (fields.Count < 7) { errors++; continue; }

                    var series   = fields[0].Trim();
                    var category = fields[1].Trim();
                    var name     = fields[2].Trim();
                    var code     = fields[3].Trim();
                    var notes    = fields[4].Trim();
                    var port     = int.TryParse(fields[5].Trim(), out var p) ? p : 0;
                    var format   = fields[6].Trim();

                    var key = $"{series}|{category}|{name}";
                    if (existingKeys.Contains(key)) { skipped++; continue; }

                    _db.InsertCommand(new CommandEntry
                    {
                        SeriesPattern   = series,
                        CommandCategory = category,
                        CommandName     = name,
                        CommandCode     = code,
                        Notes           = notes,
                        Port            = port,
                        CommandFormat   = string.IsNullOrEmpty(format) ? "HEX" : format,
                    });
                    existingKeys.Add(key);
                    imported++;
                }
                catch { errors++; }
            }

            MessageBox.Show($"Imported {imported}, Skipped {skipped}, Errors {errors}", "CSV Import Results", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusMessage = $"CSV import: {imported} imported, {skipped} skipped, {errors} errors.";
            LoadCommands();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Import failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static bool Confirm(string message)
        => MessageBox.Show(message, "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question)
           == MessageBoxResult.Yes;
}
