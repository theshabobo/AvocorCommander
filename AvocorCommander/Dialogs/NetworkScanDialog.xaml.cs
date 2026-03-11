using AvocorCommander.Models;
using AvocorCommander.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace AvocorCommander.Dialogs;

public partial class NetworkScanDialog : Window
{
    private readonly DatabaseService _db;

    public List<ScanResult> SelectedResults { get; private set; } = [];

    private CancellationTokenSource? _cts;
    private readonly ObservableCollection<ScanResult> _results = [];
    private bool _suppressSelectAll;

    public NetworkScanDialog(DatabaseService db)
    {
        _db = db;
        InitializeComponent();
        ResultsGrid.ItemsSource = _results;
        _results.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (ScanResult r in e.NewItems)
                    r.PropertyChanged += ScanResult_PropertyChanged;
            if (e.OldItems != null)
                foreach (ScanResult r in e.OldItems)
                    r.PropertyChanged -= ScanResult_PropertyChanged;
            UpdateButtonState();
        };
    }

    private void ScanResult_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ScanResult.IsSelected)) return;
        UpdateButtonState();

        // Keep Select All checkbox in sync without triggering SelectAll_Changed
        _suppressSelectAll = true;
        ChkSelectAll.IsChecked = _results.Count > 0 && _results.All(r => r.IsSelected)
            ? true
            : _results.Any(r => r.IsSelected) ? null : false;
        _suppressSelectAll = false;
    }

    private void UpdateButtonState()
    {
        BtnAddSelected.IsEnabled = _results.Any(r => r.IsSelected);
    }

    private async void StartScan_Click(object sender, RoutedEventArgs e)
    {
        _results.Clear();
        BtnScan.IsEnabled        = false;
        BtnAddSelected.IsEnabled = false;
        ChkSelectAll.IsChecked   = false;
        ScanProgress.Value       = 0;

        _cts = new CancellationTokenSource();

        var ouiMap  = _db.GetOuiSeriesMap();
        var models  = _db.GetDistinctModelNumbers();

        var progress = new Progress<(int done, int total)>(p =>
        {
            if (p.total > 0)
                ScanProgress.Value = p.done * 100.0 / p.total;
            TxtStatus.Text = $"Scanning… {p.done}/{p.total}";
        });

        try
        {
            await NetworkScanService.ScanAsync(
                TxtStartIp.Text.Trim(),
                TxtEndIp.Text.Trim(),
                ouiMap,
                models,
                result => Dispatcher.Invoke(() => _results.Add(result)),
                progress,
                _cts.Token);

            TxtStatus.Text = $"Scan complete. {_results.Count} Avocor device(s) found.";
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Scan error: {ex.Message}";
        }
        finally
        {
            BtnScan.IsEnabled  = true;
            ScanProgress.Value = 100;
        }
    }

    private void CancelScan_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Row highlight changes don't affect button state; checkbox PropertyChanged handles it
    }

    private void SelectAll_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSelectAll) return;
        bool check = ChkSelectAll.IsChecked == true;
        foreach (var r in _results)
            r.IsSelected = check;
    }

    private void AddSelected_Click(object sender, RoutedEventArgs e)
    {
        SelectedResults = _results.Where(r => r.IsSelected).ToList();
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        DialogResult = false;
    }
}
