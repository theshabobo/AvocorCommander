using AvocorCommander.Models;
using AvocorCommander.Services;
using System.Windows;
using System.Windows.Controls;

namespace AvocorCommander.Dialogs;

public partial class AddEditRuleDialog : Window
{
    private readonly DatabaseService _db;
    private readonly ScheduleRule?   _editing;

    public ScheduleRule? Result { get; private set; }

    public AddEditRuleDialog(DatabaseService db, ScheduleRule? editing = null)
    {
        _db      = db;
        _editing = editing;
        InitializeComponent();
        PopulateCombos();
        if (editing != null) FillFromRule(editing);
    }

    private void PopulateCombos()
    {
        var devices = _db.GetAllDevices();
        CmbDevice.ItemsSource = devices;
        if (devices.Count > 0) CmbDevice.SelectedIndex = 0;

        var groups = _db.GetAllGroups();
        CmbGroup.ItemsSource = groups;
        if (groups.Count > 0) CmbGroup.SelectedIndex = 0;

        var series = _db.GetDistinctSeries();
        CmbSeries.ItemsSource = series;
        if (series.Count > 0) CmbSeries.SelectedIndex = 0;

        // Auto-select series for the initially selected device
        AutoSelectSeriesForDevice(CmbDevice.SelectedItem as DeviceEntry);
    }

    private void FillFromRule(ScheduleRule r)
    {
        TxtRuleName.Text      = r.RuleName;
        TxtTime.Text          = r.ScheduleTime;
        TxtNotes.Text         = r.Notes;
        ChkEnabled.IsChecked  = r.IsEnabled;

        // Recurrence
        var items = CmbRecurrence.Items.Cast<ComboBoxItem>().ToList();
        var match = items.FirstOrDefault(i => i.Content?.ToString() == r.Recurrence);
        if (match != null) CmbRecurrence.SelectedItem = match;

        // Target
        if (r.GroupId.HasValue)
        {
            RdoGroup.IsChecked = true;
            var groups = CmbGroup.ItemsSource as List<GroupEntry>;
            CmbGroup.SelectedItem = groups?.FirstOrDefault(g => g.Id == r.GroupId.Value);
        }
        else if (r.DeviceId.HasValue)
        {
            RdoDevice.IsChecked = true;
            var devices = CmbDevice.ItemsSource as List<DeviceEntry>;
            CmbDevice.SelectedItem = devices?.FirstOrDefault(d => d.Id == r.DeviceId.Value);
            AutoSelectSeriesForDevice(CmbDevice.SelectedItem as DeviceEntry);
        }

        // Select the saved command after series is populated
        if (CmbCommand.ItemsSource is List<CommandEntry> cmds)
        {
            var savedCmd = cmds.FirstOrDefault(c => c.Id == r.CommandId);
            if (savedCmd != null) CmbCommand.SelectedItem = savedCmd;
        }
    }

    private void TargetType_Changed(object sender, RoutedEventArgs e)
    {
        if (CmbDevice == null || CmbGroup == null) return;
        CmbDevice.Visibility = RdoDevice.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CmbGroup.Visibility  = RdoGroup.IsChecked  == true ? Visibility.Visible : Visibility.Collapsed;

        if (RdoDevice.IsChecked == true)
            AutoSelectSeriesForDevice(CmbDevice.SelectedItem as DeviceEntry);
    }

    private void CmbDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RdoDevice.IsChecked == true)
            AutoSelectSeriesForDevice(CmbDevice.SelectedItem as DeviceEntry);
    }

    private void AutoSelectSeriesForDevice(DeviceEntry? device)
    {
        if (device == null || CmbSeries == null) return;
        var series = _db.GetSeriesForModel(device.ModelNumber);
        if (series != null && CmbSeries.Items.Contains(series))
            CmbSeries.SelectedItem = series;
    }

    private void CmbSeries_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbSeries.SelectedItem is string series)
        {
            var cmds = _db.GetCommandsBySeries(series);
            CmbCommand.ItemsSource = cmds;
            if (cmds.Count > 0) CmbCommand.SelectedIndex = 0;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtRuleName.Text))
        {
            MessageBox.Show("Rule name is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (CmbCommand.SelectedItem is not CommandEntry cmd)
        {
            MessageBox.Show("Please select a command.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var recurrence = (CmbRecurrence.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Daily";

        Result = new ScheduleRule
        {
            Id           = _editing?.Id ?? 0,
            RuleName     = TxtRuleName.Text.Trim(),
            CommandId    = cmd.Id,
            CommandName  = cmd.CommandName,
            ScheduleTime = TxtTime.Text.Trim(),
            Recurrence   = recurrence,
            IsEnabled    = ChkEnabled.IsChecked == true,
            Notes        = TxtNotes.Text.Trim(),
        };

        if (RdoDevice.IsChecked == true && CmbDevice.SelectedItem is DeviceEntry dev)
        {
            Result.DeviceId    = dev.Id;
            Result.TargetName  = dev.DeviceName;
        }
        else if (RdoGroup.IsChecked == true && CmbGroup.SelectedItem is GroupEntry grp)
        {
            Result.GroupId    = grp.Id;
            Result.TargetName = grp.GroupName;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
