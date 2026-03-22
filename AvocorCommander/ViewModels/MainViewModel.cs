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
    public RemoteViewModel    RemoteVM    { get; }
    public StatusViewModel    StatusVM    { get; }
    public SchedulerViewModel SchedulerVM { get; }
    public AuditLogViewModel  AuditLogVM  { get; }
    public DatabaseViewModel  DatabaseVM  { get; }
    public MacroViewModel     MacroVM     { get; }
    public AboutViewModel     AboutVM     { get; }
    public DashboardViewModel DashboardVM { get; }
    public GroupsViewModel    GroupsVM    { get; }
    public PanelViewModel     PanelVM     { get; }

    // ── Navigation ────────────────────────────────────────────────────────────

    private string _currentSection = "Dashboard";
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
    public ICommand NavRemoteCommand    { get; }
    public ICommand NavSchedulerCommand { get; }
    public ICommand NavAuditLogCommand  { get; }
    public ICommand NavMacroCommand     { get; }
    public ICommand NavDatabaseCommand  { get; }
    public ICommand NavAboutCommand     { get; }
    public ICommand NavDashboardCommand { get; }
    public ICommand NavGroupsCommand    { get; }
    public ICommand NavPanelCommand     { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand DismissBannerCommand   { get; }

    // ── Update ────────────────────────────────────────────────────────────────

    private readonly UpdateService _updateSvc = new();
    private UpdateInfo? _pendingUpdate;

    private static readonly string _dismissedVersionPath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "dismissed_update.txt");
    private string _dismissedVersion = LoadDismissedVersion();

    private static string LoadDismissedVersion()
    {
        try { return System.IO.File.Exists(_dismissedVersionPath)
                ? System.IO.File.ReadAllText(_dismissedVersionPath).Trim() : ""; }
        catch { return ""; }
    }

    private static void SaveDismissedVersion(string version)
    {
        try { System.IO.File.WriteAllText(_dismissedVersionPath, version); }
        catch { }
    }

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

    // Banner + sidebar button state derived from _pendingUpdate
    public bool   HasPendingUpdate   => _pendingUpdate != null;
    public string PendingUpdateText  => _pendingUpdate != null
        ? $"Version {_pendingUpdate.Version} is available — the app will restart automatically after install."
        : "";
    public string SidebarButtonLabel => _pendingUpdate != null
        ? $"↓  Install v{_pendingUpdate.Version}"
        : "↻  Check for Updates";

    private void SetPendingUpdate(UpdateInfo? info)
    {
        _pendingUpdate = info;
        OnPropertyChanged(nameof(HasPendingUpdate));
        OnPropertyChanged(nameof(PendingUpdateText));
        OnPropertyChanged(nameof(SidebarButtonLabel));
    }

    // ── Title / version ───────────────────────────────────────────────────────

    public string AppTitle   => "AVOCOR COMMANDER";
    public string AppVersion => "V3.0";

    public BaseViewModel CurrentViewModel => CurrentSection switch
    {
        "Devices"   => DevicesVM,
        "Control"   => ControlVM,
        "Remote"    => RemoteVM,
        "Scheduler" => SchedulerVM,
        "AuditLog"  => AuditLogVM,
        "Macros"    => MacroVM,
        "Database"  => DatabaseVM,
        "About"     => AboutVM,
        "Dashboard" => DashboardVM,
        "Groups"    => GroupsVM,
        "Panel"     => PanelVM,
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
        RemoteVM    = new RemoteViewModel(Database, Connections);
        StatusVM    = new StatusViewModel(Database, Connections);
        SchedulerVM = new SchedulerViewModel(Database, Scheduler);
        AuditLogVM  = new AuditLogViewModel(Database);
        DatabaseVM  = new DatabaseViewModel(Database);
        MacroVM     = new MacroViewModel(Database, MacroRunner);
        AboutVM     = new AboutViewModel();
        DashboardVM = new DashboardViewModel(Database, Connections);
        GroupsVM    = new GroupsViewModel(Database, Connections);
        PanelVM     = new PanelViewModel(Database, Connections);

        // Scheduler + MacroRunner → AuditLog live feed
        Scheduler.EntryLogged   += (_, _) =>
            System.Windows.Application.Current?.Dispatcher.Invoke(AuditLogVM.AppendFromDb);
        MacroRunner.EntryLogged += (_, _) =>
            System.Windows.Application.Current?.Dispatcher.Invoke(AuditLogVM.AppendFromDb);

        // DevicesVM history navigation
        DevicesVM.ViewHistoryRequested += (_, deviceName) =>
        {
            AuditLogVM.FilterDevice = deviceName;
            CurrentSection = "AuditLog";
        };

        // DevicesVM → Control shortcut
        DevicesVM.ControlDeviceRequested += (_, deviceId) =>
        {
            ControlVM.LoadData();
            ControlVM.SelectDevice(deviceId);
            CurrentSection = "Control";
        };

        // DashboardVM → Devices onboarding CTA
        DashboardVM.GoToDevicesRequested += (_, _) => CurrentSection = "Devices";

        // DashboardVM → Control shortcut
        DashboardVM.ControlDeviceRequested += (_, deviceId) =>
        {
            ControlVM.LoadData();
            ControlVM.SelectDevice(deviceId);
            CurrentSection = "Control";
        };

        // Wire events from section VMs to dialogs (raised in MainWindow code-behind)
        NavDevicesCommand   = new RelayCommand(() => CurrentSection = "Devices");
        NavControlCommand   = new RelayCommand(() => CurrentSection = "Control");
        NavRemoteCommand    = new RelayCommand(() => CurrentSection = "Remote");
        NavSchedulerCommand = new RelayCommand(() => CurrentSection = "Scheduler");
        NavAuditLogCommand  = new RelayCommand(() => CurrentSection = "AuditLog");
        NavMacroCommand     = new RelayCommand(() => CurrentSection = "Macros");
        NavDatabaseCommand  = new RelayCommand(() => CurrentSection = "Database");
        NavAboutCommand     = new RelayCommand(() => CurrentSection = "About");
        NavDashboardCommand = new RelayCommand(() => CurrentSection = "Dashboard");
        NavGroupsCommand    = new RelayCommand(() => CurrentSection = "Groups");
        NavPanelCommand     = new RelayCommand(() => CurrentSection = "Panel");

        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync, () => !IsUpdating);
        DismissBannerCommand   = new RelayCommand(() =>
        {
            if (_pendingUpdate != null)
            {
                _dismissedVersion = _pendingUpdate.Version;
                SaveDismissedVersion(_dismissedVersion);
            }
            SetPendingUpdate(null);
            UpdateStatus = "v" + UpdateService.CurrentVersion.ToString(3);
        });

        // Always show current version in sidebar; startup check will update if needed
        UpdateStatus = "v" + UpdateService.CurrentVersion.ToString(3);
    }

    /// <summary>
    /// Sidebar button handler.
    /// If an update is pending → confirm and install.
    /// If not → run a manual check and show banner if found.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        if (_pendingUpdate != null)
        {
            await InstallPendingUpdateAsync();
            return;
        }

        IsUpdating   = true;
        UpdateStatus = "Checking…";
        try
        {
            var info = await _updateSvc.CheckAsync();
            if (info?.Version == _dismissedVersion) info = null;
            SetPendingUpdate(info);
            UpdateStatus = info != null
                ? $"v{info.Version} available"
                : "Up to date  ✓";
        }
        catch
        {
            UpdateStatus = "Check failed";
        }
        finally
        {
            IsUpdating = false;
        }
    }

    /// <summary>Silent background check on startup. Shows banner on find; updates status on failure.</summary>
    private async Task RunStartupUpdateCheckAsync()
    {
        if (!_updateSvc.IsConfigured) return;
        try
        {
            var info = await _updateSvc.CheckAsync();
            if (info?.Version == _dismissedVersion) info = null;
            SetPendingUpdate(info);
            if (info != null)
                UpdateStatus = $"v{info.Version} available";
        }
        catch
        {
            UpdateStatus = "Check failed";
        }
    }

    /// <summary>Confirm dialog → download → install. Used by both the banner and sidebar button.</summary>
    private async Task InstallPendingUpdateAsync()
    {
        if (_pendingUpdate == null) return;

        var confirm = MessageBox.Show(
            $"Download and install v{_pendingUpdate.Version}?\n\n" +
            $"{_pendingUpdate.Notes}\n\nThe app will restart automatically.",
            "Install Update", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        IsUpdating       = true;
        DownloadProgress = 0;
        UpdateStatus     = "Downloading…  0%";

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
            MessageBox.Show(ex.Message, "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void Initialize()
    {
        DevicesVM.LoadDevices();
        DashboardVM.LoadData();
        DatabaseVM.Initialize();
        // Load other sections lazily when first navigated to

        // Startup auto-connect
        _ = StartupAutoConnectAsync();

        // Kick off silent background update check — no await, intentionally fire-and-forget
        _ = RunStartupUpdateCheckAsync();
    }

    private async Task StartupAutoConnectAsync()
    {
        var devices = Database.GetAutoConnectDevices();
        foreach (var d in devices)
            await Connections.ConnectAsync(d);
    }

    private void OnSectionChanged(string section)
    {
        switch (section)
        {
            case "Control":
                ControlVM.LoadData();
                break;
            case "Remote":
                RemoteVM.LoadDevices();
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
            case "Dashboard":
                DashboardVM.LoadData();
                break;
            case "Groups":
                GroupsVM.LoadData();
                break;
            case "Panel":
                PanelVM.LoadData();
                break;
        }
        GlobalStatus = $"Section: {section}";
    }

    public void Dispose()
    {
        DashboardVM.Dispose();
        PanelVM.Dispose();
        StatusVM.Dispose();
        Scheduler.Dispose();
        Connections.Dispose();
        Database.Dispose();
    }
}
