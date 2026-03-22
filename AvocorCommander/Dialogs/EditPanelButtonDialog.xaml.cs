using AvocorCommander.Models;
using AvocorCommander.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using WpfControls = System.Windows.Controls;

namespace AvocorCommander.Dialogs;

// ── Action row ────────────────────────────────────────────────────────────────

public sealed class PanelActionRow : INotifyPropertyChanged
{
    private readonly DatabaseService   _db;

    private DeviceEntry?  _selectedDevice;
    private string        _selectedSeries  = string.Empty;
    private CommandEntry? _selectedCommand;

    public List<DeviceEntry>                   Devices { get; }
    public List<string>                        AllSeries { get; }
    public ObservableCollection<CommandEntry>  Commands { get; } = new();

    public DeviceEntry? SelectedDevice
    {
        get => _selectedDevice;
        set { _selectedDevice = value; PC(); }
    }

    public string SelectedSeries
    {
        get => _selectedSeries;
        set
        {
            _selectedSeries = value; PC();
            Commands.Clear();
            SelectedCommand = null;
            if (string.IsNullOrEmpty(value)) return;
            foreach (var c in _db.GetCommandsBySeries(value))
                Commands.Add(c);
        }
    }

    public CommandEntry? SelectedCommand
    {
        get => _selectedCommand;
        set { _selectedCommand = value; PC(); }
    }

    public PanelActionRow(DatabaseService db, List<DeviceEntry> allDevices, List<string> allSeries)
    {
        _db       = db;
        Devices   = allDevices;
        AllSeries = allSeries;
    }

    public void LoadFrom(PanelButtonAction action, List<DeviceEntry> allDevices)
    {
        SelectedDevice = allDevices.FirstOrDefault(d => d.Id == action.DeviceId);
        // Populate Commands list by setting series from any matching entry in DeviceList
        var entry = _db.GetAllCommands().FirstOrDefault(c => c.CommandCode == action.CommandCode);
        if (entry != null)
        {
            SelectedSeries  = entry.SeriesPattern;
            SelectedCommand = Commands.FirstOrDefault(c => c.CommandCode == action.CommandCode);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void PC([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

// ── Dialog ────────────────────────────────────────────────────────────────────

public partial class EditPanelButtonDialog : Window
{
    private readonly DatabaseService   _db;
    private readonly List<DeviceEntry> _allDevices;
    private readonly List<string>      _allSeries;

    private readonly ObservableCollection<PanelActionRow> _actionsA = new();
    private readonly ObservableCollection<PanelActionRow> _actionsB = new();

    private string _selectedIcon  = PanelIcons.All[1].Symbol;
    private string _selectedColor = PanelColors.All[0];

    public PanelButton?            ResultButton   { get; private set; }
    public List<PanelButtonAction>? ResultActionsA { get; private set; }
    public List<PanelButtonAction>? ResultActionsB { get; private set; }

    public EditPanelButtonDialog(
        DatabaseService db,
        PanelButton? existing      = null,
        List<PanelButtonAction>? existingActionsA = null,
        List<PanelButtonAction>? existingActionsB = null)
    {
        _db         = db;
        _allDevices = db.GetAllDevices();
        _allSeries  = db.GetDistinctSeries();

        InitializeComponent();

        ActionsAList.ItemsSource = _actionsA;
        ActionsBList.ItemsSource = _actionsB;

        BuildIconPicker();
        BuildColorPicker();

        if (existing != null)
        {
            TxtLabel.Text       = existing.Label;
            ChkToggle.IsChecked = existing.IsToggle;
            _selectedIcon       = existing.Icon;
            _selectedColor      = existing.Color;
            SelectIcon(_selectedIcon);
            SelectColor(_selectedColor);

            if (existingActionsA != null)
                foreach (var a in existingActionsA)
                {
                    var row = new PanelActionRow(_db, _allDevices, _allSeries);
                    row.LoadFrom(a, _allDevices);
                    _actionsA.Add(row);
                }
            if (existingActionsB != null)
                foreach (var a in existingActionsB)
                {
                    var row = new PanelActionRow(_db, _allDevices, _allSeries);
                    row.LoadFrom(a, _allDevices);
                    _actionsB.Add(row);
                }
        }
    }

    // ── Icon / colour pickers ─────────────────────────────────────────────────

    private void BuildIconPicker()
    {
        IconPanel.Children.Clear();
        foreach (var (_, symbol) in PanelIcons.All)
        {
            var captured = symbol;
            var rb = new WpfControls.RadioButton
            {
                Content   = captured,
                Style     = (Style)Resources["IconChoice"],
                GroupName = "Icons",
                IsChecked = captured == _selectedIcon,
            };
            rb.Checked += (_, _) => _selectedIcon = captured;
            IconPanel.Children.Add(rb);
        }
    }

    private void SelectIcon(string symbol)
    {
        foreach (WpfControls.RadioButton rb in IconPanel.Children.OfType<WpfControls.RadioButton>())
            if ((string?)rb.Content == symbol) { rb.IsChecked = true; break; }
    }

    private void BuildColorPicker()
    {
        ColorPanel.Children.Clear();
        foreach (var hex in PanelColors.All)
        {
            var captured = hex;
            var brush    = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
            var rb = new WpfControls.RadioButton
            {
                Tag       = brush,
                Style     = (Style)Resources["ColorSwatch"],
                GroupName = "Colors",
                IsChecked = captured == _selectedColor,
            };
            rb.Checked += (_, _) => _selectedColor = captured;
            ColorPanel.Children.Add(rb);
        }
    }

    private void SelectColor(string hex)
    {
        int idx = PanelColors.All.IndexOf(hex);
        if (idx >= 0 && idx < ColorPanel.Children.Count)
            ((WpfControls.RadioButton)ColorPanel.Children[idx]).IsChecked = true;
    }

    // ── Action row helpers ────────────────────────────────────────────────────

    private PanelActionRow MakeRow() => new(_db, _allDevices, _allSeries);

    private void AddActionA_Click(object sender, RoutedEventArgs e) => _actionsA.Add(MakeRow());
    private void AddActionB_Click(object sender, RoutedEventArgs e) => _actionsB.Add(MakeRow());

    private void RemoveActionA_Click(object sender, RoutedEventArgs e)
    {
        if (((WpfControls.Button)sender).Tag is PanelActionRow row) _actionsA.Remove(row);
    }

    private void RemoveActionB_Click(object sender, RoutedEventArgs e)
    {
        if (((WpfControls.Button)sender).Tag is PanelActionRow row) _actionsB.Remove(row);
    }

    // ── Save / Cancel ─────────────────────────────────────────────────────────

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtLabel.Text))
        {
            MessageBox.Show("Please enter a label for the button.", "Validation",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ResultButton = new PanelButton
        {
            Label    = TxtLabel.Text.Trim(),
            Icon     = _selectedIcon,
            Color    = _selectedColor,
            IsToggle = ChkToggle.IsChecked == true,
        };

        ResultActionsA = BuildList(_actionsA, 0);
        ResultActionsB = BuildList(_actionsB, 1);
        DialogResult   = true;
    }

    private static List<PanelButtonAction> BuildList(IEnumerable<PanelActionRow> rows, int phase)
    {
        var list  = new List<PanelButtonAction>();
        int order = 0;
        foreach (var row in rows)
        {
            if (row.SelectedDevice == null || row.SelectedCommand == null) continue;
            list.Add(new PanelButtonAction
            {
                Phase         = phase,
                DeviceId      = row.SelectedDevice.Id,
                DeviceName    = row.SelectedDevice.DeviceName,
                CommandCode   = row.SelectedCommand.CommandCode,
                CommandName   = row.SelectedCommand.CommandName,
                CommandFormat = row.SelectedCommand.CommandFormat,
                SortOrder     = order++,
            });
        }
        return list;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
