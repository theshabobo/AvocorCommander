using AvocorCommander.Models;
using AvocorCommander.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace AvocorCommander.Dialogs;

// Editable row bound to the DataGrid
public sealed class MacroStepEdit : INotifyPropertyChanged
{
    private readonly DatabaseService _db;

    private int    _stepOrder;
    private string _series          = string.Empty;
    private CommandEntry? _selectedCommand;
    private int    _delayMs;

    public int    StepOrder { get => _stepOrder; set { _stepOrder = value; PC(); } }
    public string Series
    {
        get => _series;
        set
        {
            _series = value; PC();
            Commands.Clear();
            if (!string.IsNullOrEmpty(value))
                foreach (var c in _db.GetCommandsBySeries(value)) Commands.Add(c);
            SelectedCommand = null;
        }
    }
    public CommandEntry? SelectedCommand { get => _selectedCommand; set { _selectedCommand = value; PC(); } }
    public int    DelayMs    { get => _delayMs;    set { _delayMs = value; PC(); } }

    public ObservableCollection<CommandEntry> Commands { get; } = [];

    public MacroStepEdit(DatabaseService db) { _db = db; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void PC([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public partial class AddEditMacroDialog : Window
{
    private readonly DatabaseService _db;
    private readonly MacroEntry?     _editing;

    public List<string> AllSeries { get; }

    public ObservableCollection<MacroStepEdit> Steps { get; } = [];

    public MacroEntry? Result { get; private set; }

    public AddEditMacroDialog(DatabaseService db, MacroEntry? editing = null)
    {
        _db      = db;
        _editing = editing;
        AllSeries = db.GetDistinctSeries();
        InitializeComponent();
        DataContext  = this;
        StepGrid.ItemsSource = Steps;

        if (editing != null)
        {
            TxtName.Text  = editing.MacroName;
            TxtNotes.Text = editing.Notes;
            foreach (var s in editing.Steps)
            {
                var row = new MacroStepEdit(_db) { StepOrder = s.StepOrder, DelayMs = s.DelayAfterMs };
                row.Series = s.SeriesPattern;
                row.SelectedCommand = row.Commands.FirstOrDefault(c => c.Id == s.CommandId);
                Steps.Add(row);
            }
        }
    }

    private void AddStep_Click(object sender, RoutedEventArgs e)
    {
        Steps.Add(new MacroStepEdit(_db) { StepOrder = Steps.Count + 1 });
    }

    private void RemoveStep_Click(object sender, RoutedEventArgs e)
    {
        if (StepGrid.SelectedItem is MacroStepEdit row)
        {
            Steps.Remove(row);
            RenumberSteps();
        }
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        int idx = StepGrid.SelectedIndex;
        if (idx <= 0) return;
        Steps.Move(idx, idx - 1);
        RenumberSteps();
        StepGrid.SelectedIndex = idx - 1;
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        int idx = StepGrid.SelectedIndex;
        if (idx < 0 || idx >= Steps.Count - 1) return;
        Steps.Move(idx, idx + 1);
        RenumberSteps();
        StepGrid.SelectedIndex = idx + 1;
    }

    private void RenumberSteps()
    {
        for (int i = 0; i < Steps.Count; i++)
            Steps[i].StepOrder = i + 1;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtName.Text))
        {
            MessageBox.Show("Macro name is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = new MacroEntry
        {
            Id        = _editing?.Id ?? 0,
            MacroName = TxtName.Text.Trim(),
            Notes     = TxtNotes.Text.Trim(),
            Steps     = Steps.Select((s, i) => new MacroStep
            {
                StepOrder    = i + 1,
                CommandId    = s.SelectedCommand?.Id ?? 0,
                CommandName  = s.SelectedCommand?.CommandName ?? string.Empty,
                SeriesPattern = s.Series,
                DelayAfterMs = s.DelayMs,
            }).Where(s => s.CommandId != 0).ToList(),
        };

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
