using AvocorCommander.Core;
using AvocorCommander.Services;
using System.Windows.Input;
using System.Windows;

namespace AvocorCommander.ViewModels;

/// <summary>
/// Application shell.  Owns all section ViewModels and manages navigation.
/// </summary>
public sealed class MainViewModel : BaseViewModel, IDisposable
{
    // ── Services (shared singletons) ──────────────────────────────────────────

    public readonly DatabaseService    Database;
    public readonly ConnectionManager  Connections;
    public readonly SchedulerService   Scheduler;
    public readonly MacroRunnerService MacroRunner;

    // ── Section ViewModels ────────────────────────────────────────────────────

    public DevicesViewModel   DevicesVM   { get; }
    public ControlViewModel   ControlVM   { get; }
    public StatusViewModel    StatusVM    { get; }
    public SchedulerViewModel SchedulerVM { get; }
    public AuditLogViewModel  AuditLogVM  { get; }
    public DatabaseViewModel  DatabaseVM  { get; }
    public MacroViewModel     MacroVM     { get; }

    // ── Navigation ────────────────────────────────────────────────────────────

    private string _currentSection = "Devices";
    public string CurrentSection
    {
        get => _currentSection;
        set
        {
            Set(ref _currentSection, value);
            OnPropertyChanged(nameof(CurrentViewModel));
            OnSectionChanged(value);
        }
    }

    public ICommand NavDevicesCommand   { get; }
    public ICommand NavControlCommand   { get; }
    public ICommand NavStatusCommand    { get; }
    public ICommand NavSchedulerCommand { get; }
    public ICommand NavAuditLogCommand  { get; }
    public ICommand NavMacroCommand     { get; }
    public ICommand NavDatabaseCommand  { get; }
    public ICommand CheckForUpdatesCommand { get; }

    // ── Update ────────────────────────────────────────────────────────────────

    private readonly UpdateService _updateSvc = new();
    private UpdateInfo? _pendingUpdate;

    private string _updateStatus = "";
    public string UpdateStatus
    {
        get => _updateStatus;
        set => Set(ref _updateStatus, value);
    }

    private bool _isUpdating;
    public bool IsUpdating
    {
        get => _isUpdating;
        set { Set(ref _isUpdating, value); CommandManager.InvalidateRequerySuggested(); }
    }

    private int _downloadProgress;
    public int DownloadProgress
    {
        get => _downloadProgress;
        set => Set(ref _downloadProgress, value);
    }

    // ── Title / version ───────────────────────────────────────────────────────

    public string AppTitle   => "AVOCOR COMMANDER";
    public string AppVersion => "V3.0";

    public BaseViewModel CurrentViewModel => CurrentSection switch
    {
        "Devices"   => DevicesVM,
        "Control"   => ControlVM,
        "Status"    => StatusVM,
        "Scheduler" => SchedulerVM,
        "AuditLog"  => AuditLogVM,
        "Macros"    => MacroVM,
        "Database"  => DatabaseVM,
        _           => DevicesVM,
    };

    // ── Global status ─────────────────────────────────────────────────────────

    private string _globalStatus = "Ready.";
    public string GlobalStatus
    {
        get => _globalStatus;
        set => Set(ref _globalStatus, value);
    }

    public MainViewModel()
    {
        Database    = new DatabaseService();
        Connections = new ConnectionManager();
        Scheduler   = new SchedulerService(Database, Connections);
        MacroRunner = new MacroRunnerService(Database, Connections);

        DevicesVM   = new DevicesViewModel(Database, Connections);
        ControlVM   = new ControlViewModel(Database, Connections);
        StatusVM    = new StatusViewModel(Database, Connections);
        SchedulerVM = new SchedulerViewModel(Database, Scheduler);
        AuditLogVM  = new AuditLogViewModel(Database);
        DatabaseVM  = new DatabaseViewModel(Database);
        MacroVM     = new MacroViewModel(Database, MacroRunner);

        // Scheduler + MacroRunner → AuditLog live feed
        Scheduler.EntryLogged   += (_, _) =>
            System.Windows.Application.Current?.Dispatcher.Invoke(AuditLogVM.AppendFromDb);
        MacroRunner.EntryLogged += (_, _) =>
            System.Windows.Application.Current?.Dispatcher.Invoke(AuditLogVM.AppendFromDb);

        // Wire events from section VMs to dialogs (raised in MainWindow code-behind)
        NavDevicesCommand   = new RelayCommand(() => CurrentSection = "Devices");
        NavControlCommand   = new RelayCommand(() => CurrentSection = "Control");
        NavStatusCommand    = new RelayCommand(() => CurrentSection = "Status");
        NavSchedulerCommand = new RelayCommand(() => CurrentSection = "Scheduler");
        NavAuditLogCommand  = new RelayCommand(() => CurrentSection = "AuditLog");
        NavMacroCommand     = new RelayCommand(() => CurrentSection = "Macros");
        NavDatabaseCommand  = new RelayCommand(() => CurrentSection = "Database");

        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync,
            () => !IsUpdating);

        if (!_updateSvc.IsConfigured)
            UpdateStatus = "v" + UpdateService.CurrentVersion.ToString(3);
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_pendingUpdate != null)
        {
            // Second click = download
            var confirm = MessageBox.Show(
                $"Download and install v{_pendingUpdate.Version}?\n\n" +
                $"{_pendingUpdate.Notes}\n\nThe app will restart automatically.",
                "Install Update", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) return;

            IsUpdating     = true;
            UpdateStatus   = "Downloading…  0%";
            DownloadProgress = 0;
            var prog = new Progress<int>(p =>
            {
                DownloadProgress = p;
                UpdateStatus     = $"Downloading…  {p}%";
            });
            try
            {
                await _updateSvc.DownloadAndInstallAsync(_pendingUpdate, prog);
            }
            catch (Exception ex)
            {
                IsUpdating   = false;
                UpdateStatus = "Download failed.";
                MessageBox.Show(ex.Message, "Update Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }

        IsUpdating   = true;
        UpdateStatus = "Checking…";
        try
        {
            _pendingUpdate = await _updateSvc.CheckAsync();
            if (_pendingUpdate != null)
                UpdateStatus = $"v{_pendingUpdate.Version} available — click to install";
            else
                UpdateStatus = "Up to date  ✓";
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Check failed: {ex.Message}";
        }
        finally
        {
            IsUpdating = false;
        }
    }

    public void Initialize()
    {
        DevicesVM.LoadDevices();
        DatabaseVM.Initialize();
        // Load other sections lazily when first navigated to
    }

    private void OnSectionChanged(string section)
    {
        switch (section)
        {
            case "Control":
                ControlVM.LoadData();
                break;
            case "Status":
                StatusVM.LoadDevices();
                break;
            case "Scheduler":
                SchedulerVM.LoadRules();
                break;
            case "AuditLog":
                AuditLogVM.LoadLog();
                break;
            case "Macros":
                MacroVM.LoadData();
                break;
            case "Database":
                DatabaseVM.Initialize();
                break;
        }
        GlobalStatus = $"Section: {section}";
    }

    public void Dispose()
    {
        StatusVM.Dispose();
        Scheduler.Dispose();
        Connections.Dispose();
        Database.Dispose();
    }
}
