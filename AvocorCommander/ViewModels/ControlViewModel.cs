using AvocorCommander.Core;
using AvocorCommander.Models;
using AvocorCommander.Services;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Input;

namespace AvocorCommander.ViewModels;

public enum SendTarget { SelectedDevice, SelectedGroup, AllConnected }

public sealed class ControlViewModel : BaseViewModel
{
    private readonly DatabaseService   _db;
    private readonly ConnectionManager _connMgr;

    // ── Target selection ─────────────────────────────────────────────────────

    public ObservableCollection<DeviceEntry>  AvailableDevices { get; } = [];
    public ObservableCollection<GroupEntry>   AvailableGroups  { get; } = [];

    private DeviceEntry? _selectedDevice;
    public DeviceEntry? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            Set(ref _selectedDevice, value);
            if (SendTarget == SendTarget.SelectedDevice) RefreshCategories();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private GroupEntry? _selectedGroup;
    public GroupEntry? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            Set(ref _selectedGroup, value);
            RefreshCategories();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private SendTarget _sendTarget = SendTarget.SelectedDevice;
    public SendTarget SendTarget
    {
        get => _sendTarget;
        set
        {
            Set(ref _sendTarget, value);
            OnPropertyChanged(nameof(TargetDevice));
            OnPropertyChanged(nameof(TargetGroup));
            OnPropertyChanged(nameof(TargetAll));
            OnPropertyChanged(nameof(IsRawCodeEditable));
            RefreshCategories();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>RawCode override only makes sense for a single-device target.
    /// Group / AllConnected send the per-device command as resolved from the DB.</summary>
    public bool IsRawCodeEditable => SendTarget == SendTarget.SelectedDevice;

    public bool TargetDevice { get => SendTarget == SendTarget.SelectedDevice; set { if (value) SendTarget = SendTarget.SelectedDevice; } }
    public bool TargetGroup  { get => SendTarget == SendTarget.SelectedGroup;  set { if (value) SendTarget = SendTarget.SelectedGroup; } }
    public bool TargetAll    { get => SendTarget == SendTarget.AllConnected;   set { if (value) SendTarget = SendTarget.AllConnected; } }

    // ── Command selection ─────────────────────────────────────────────────────

    public ObservableCollection<string>       AvailableCategories { get; } = [];
    public ObservableCollection<CommandEntry> AvailableCommands   { get; } = [];

    private string? _selectedCategory;
    public string? SelectedCategory
    {
        get => _selectedCategory;
        set { Set(ref _selectedCategory, value); RefreshCommands(); }
    }

    private CommandEntry? _selectedCommand;
    public CommandEntry? SelectedCommand
    {
        get => _selectedCommand;
        set
        {
            Set(ref _selectedCommand, value);
            RawCode = value?.CommandCode ?? string.Empty;
            OnPropertyChanged(nameof(HasVariableBytes));
            OnPropertyChanged(nameof(HasCommand));
        }
    }

    public bool HasVariableBytes => _selectedCommand?.HasVariableBytes ?? false;
    public bool HasCommand       => _selectedCommand != null;

    // ── Raw code ──────────────────────────────────────────────────────────────

    private string _rawCode = string.Empty;
    public string RawCode { get => _rawCode; set => Set(ref _rawCode, value); }

    // ── Log (in-memory for this session) ─────────────────────────────────────

    public ObservableCollection<AuditLogEntry> SessionLog { get; } = [];

    // ── Status bar ───────────────────────────────────────────────────────────

    private string _statusMessage = "Ready.";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    // ── Test all commands ─────────────────────────────────────────────────────

    private bool _isTesting;
    public bool IsTesting
    {
        get => _isTesting;
        set
        {
            Set(ref _isTesting, value);
            OnPropertyChanged(nameof(IsNotTesting));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool IsNotTesting => !_isTesting;

    private CancellationTokenSource? _testCts;

    // ── Commands ──────────────────────────────────────────────────────────────

    public ICommand SendCommand        { get; }
    public ICommand WakeOnLanCommand   { get; }
    public ICommand ClearLogCommand    { get; }
    public ICommand CopyLogCommand     { get; }
    public ICommand ExportLogCommand   { get; }
    public ICommand RefreshCommand     { get; }
    public ICommand TestAllCommand     { get; }
    public ICommand CancelTestCommand  { get; }

    public ControlViewModel(DatabaseService db, ConnectionManager connMgr)
    {
        _db      = db;
        _connMgr = connMgr;

        SendCommand       = new AsyncRelayCommand(SendAsync,      () => SelectedCommand != null && !IsTesting);
        WakeOnLanCommand  = new AsyncRelayCommand(WakeOnLanAsync, CanWakeOnLan);
        ClearLogCommand   = new RelayCommand(() => SessionLog.Clear());
        CopyLogCommand    = new RelayCommand(CopyLog, () => SessionLog.Count > 0);
        TestAllCommand    = new AsyncRelayCommand(TestAllAsync,   () => !IsTesting);
        CancelTestCommand = new RelayCommand(() => _testCts?.Cancel(), () => IsTesting);
        ExportLogCommand = new RelayCommand(ExportLog);
        RefreshCommand   = new RelayCommand(LoadData);
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    public void LoadData()
    {
        var devices = _db.GetAllDevices();
        AvailableDevices.Clear();
        foreach (var d in devices)
        {
            d.IsConnected = _connMgr.IsConnected(d.Id);
            AvailableDevices.Add(d);
        }

        var groups = _db.GetAllGroups();
        AvailableGroups.Clear();
        foreach (var g in groups) AvailableGroups.Add(g);

        SelectedDevice = AvailableDevices.FirstOrDefault(d => d.IsConnected)
                      ?? AvailableDevices.FirstOrDefault();
    }

    /// <summary>Pre-select a device by ID after LoadData() has been called.</summary>
    public void SelectDevice(int deviceId)
    {
        var device = AvailableDevices.FirstOrDefault(d => d.Id == deviceId);
        if (device != null) SelectedDevice = device;
    }

    // ── Category / command refresh ────────────────────────────────────────────

    /// <summary>Returns the distinct series patterns for the current target scope.
    /// Single-device → that device's series. Group → every member's series.
    /// AllConnected → every connected device's series.</summary>
    private List<string> GetTargetSeriesList()
    {
        IEnumerable<DeviceEntry> devices = SendTarget switch
        {
            SendTarget.SelectedDevice => SelectedDevice != null ? [SelectedDevice] : Array.Empty<DeviceEntry>(),
            SendTarget.SelectedGroup  => SelectedGroup == null
                ? Array.Empty<DeviceEntry>()
                : AvailableDevices.Where(d => SelectedGroup.MemberDeviceIds.Contains(d.Id)),
            SendTarget.AllConnected   => AvailableDevices.Where(d => _connMgr.IsConnected(d.Id)),
            _                         => Array.Empty<DeviceEntry>(),
        };

        return devices
            .Select(d => _db.GetSeriesForModel(d.ModelNumber))
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .Distinct()
            .ToList();
    }

    private void RefreshCategories()
    {
        AvailableCategories.Clear();
        SelectedCategory = null;

        var seriesList = GetTargetSeriesList();
        if (seriesList.Count == 0) { RefreshCommands(); return; }

        // Intersection of category sets across all target series — only show
        // categories that exist for every device we'd send to.
        var sets = seriesList
            .Select(s => _db.GetCommandsBySeries(s)
                .Select(c => c.CommandCategory)
                .ToHashSet(StringComparer.OrdinalIgnoreCase))
            .ToList();

        var common = new HashSet<string>(sets[0], StringComparer.OrdinalIgnoreCase);
        foreach (var s in sets.Skip(1)) common.IntersectWith(s);

        foreach (var c in common.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            AvailableCategories.Add(c);
        SelectedCategory = AvailableCategories.FirstOrDefault();
    }

    private void RefreshCommands()
    {
        AvailableCommands.Clear();
        SelectedCommand = null;
        if (SelectedCategory == null) return;

        var seriesList = GetTargetSeriesList();
        if (seriesList.Count == 0) return;

        // For each target series, list command names in the chosen category.
        var perSeriesCmds = seriesList
            .Select(s => _db.GetCommandsBySeries(s)
                .Where(c => string.Equals(c.CommandCategory, SelectedCategory, StringComparison.OrdinalIgnoreCase))
                .ToList())
            .ToList();

        // Intersect command names so the dropdown only shows commands every
        // target device can actually execute.
        var commonNames = new HashSet<string>(
            perSeriesCmds[0].Select(c => c.CommandName),
            StringComparer.OrdinalIgnoreCase);
        foreach (var list in perSeriesCmds.Skip(1))
            commonNames.IntersectWith(list.Select(c => c.CommandName));

        // Display template is the first series's command — the *display name* is
        // what matters; the actual bytes are re-resolved per-device at send time.
        foreach (var name in commonNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var template = perSeriesCmds[0].First(c =>
                string.Equals(c.CommandName, name, StringComparison.OrdinalIgnoreCase));
            AvailableCommands.Add(template);
        }
        SelectedCommand = AvailableCommands.FirstOrDefault();
    }

    // ── Send ──────────────────────────────────────────────────────────────────

    private async Task SendAsync()
    {
        if (SelectedCommand == null) return;

        var targets = GetTargetDevices();
        if (targets.Count == 0)
        {
            StatusMessage = "No connected devices to send to.";
            return;
        }

        if (targets.Count > 1)
            StatusMessage = $"Sending to {targets.Count} devices…";

        string commandName     = SelectedCommand.CommandName;
        string commandCategory = SelectedCommand.CommandCategory;

        // For single-device sends, honour the raw-code edit box. For group /
        // all-connected, we re-resolve the command per-device so each series
        // gets the correct wire bytes, and the raw-code override is ignored
        // (one hex blob can't be valid across mixed series).
        bool perDeviceResolve = SendTarget != SendTarget.SelectedDevice;

        foreach (var device in targets)
        {
            if (!_connMgr.IsConnected(device.Id))
            {
                Log(device.DeviceName, "Not connected — skipped.", false);
                continue;
            }

            var series = _db.GetSeriesForModel(device.ModelNumber) ?? "";
            if (string.IsNullOrEmpty(series))
            {
                Log(device.DeviceName, $"Unknown series for model '{device.ModelNumber}' — skipped.", false);
                continue;
            }

            // Look up the command for THIS device's series, keyed by the
            // canonical CommandName (consistent across series thanks to the
            // normalised seed).
            CommandEntry? deviceCmd = _db.GetCommandsBySeries(series)
                .FirstOrDefault(c =>
                    string.Equals(c.CommandCategory, commandCategory, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(c.CommandName,     commandName,     StringComparison.OrdinalIgnoreCase));

            if (deviceCmd == null)
            {
                Log(device.DeviceName, $"'{commandName}' not defined for {series} — skipped.", false);
                continue;
            }

            string? overrideCode = perDeviceResolve ? null : RawCode;
            byte[]  bytes        = deviceCmd.GetBytes(overrideCode);
            if (bytes.Length == 0)
            {
                Log(device.DeviceName, $"Cannot parse bytes for '{commandName}' ({series}) — skipped.", false);
                continue;
            }

            string hexStr = string.Join(" ", bytes.Select(b => b.ToString("X2")));
            Log(device.DeviceName, $"▶ {commandName}  [{hexStr}]", true);

            var response = await _connMgr.SendAsync(device.Id, bytes);
            bool ok = response != null;

            string parsedResponse = response is { Length: > 0 }
                ? ResponseParser.Parse(response, bytes, deviceCmd.CommandFormat, series)
                : ok ? "No response data." : "No response (timeout).";

            _db.LogCommand(new AuditLogEntry
            {
                Timestamp     = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                DeviceName    = device.DeviceName,
                DeviceAddress = device.IPAddress,
                CommandName   = commandName,
                CommandCode   = hexStr,
                Response      = parsedResponse,
                Success       = ok,
            });

            Log(device.DeviceName, $"◀ {parsedResponse}", ok);
        }

        StatusMessage = $"Sent to {targets.Count} device(s) at {DateTime.Now:HH:mm:ss}";
    }

    // ── Test all commands ─────────────────────────────────────────────────────

    /// <summary>
    /// Iterates every command in every category for every target device and
    /// sends each one, logging SUCCEEDED / FAILED per send. Ends with a tally
    /// line. Used to characterise a display's actual response behaviour so we
    /// can refine the ResponseParser over time.
    /// </summary>
    private async Task TestAllAsync()
    {
        var targets = GetTargetDevices();
        if (targets.Count == 0)
        {
            StatusMessage = "No connected devices to test.";
            return;
        }

        _testCts = new CancellationTokenSource();
        IsTesting = true;

        int pass = 0, fail = 0, skipped = 0;

        try
        {
            Log("—", $"══ Testing all commands on {targets.Count} device(s) ══", true);

            foreach (var device in targets)
            {
                if (_testCts.IsCancellationRequested) break;
                if (!_connMgr.IsConnected(device.Id))
                {
                    Log(device.DeviceName, "Not connected — skipped.", false);
                    continue;
                }

                var series = _db.GetSeriesForModel(device.ModelNumber) ?? "";
                if (string.IsNullOrEmpty(series))
                {
                    Log(device.DeviceName, $"Unknown series '{device.ModelNumber}' — skipped.", false);
                    continue;
                }

                // Ordering: alphabetical by category, then command — but push
                // the "Power" category to the END so a Power Off mid-test
                // doesn't cut the connection and fail the rest.
                var allCmds = _db.GetCommandsBySeries(series)
                    .OrderBy(c => string.Equals(c.CommandCategory, "Power", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenBy(c => c.CommandCategory, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(c => string.Equals(c.CommandName, "Power Off", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenBy(c => c.CommandName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                Log(device.DeviceName,
                    $"── {device.DeviceName} ({series}) — {allCmds.Count} commands ──", true);

                string? currentCategory = null;

                foreach (var cmd in allCmds)
                {
                    if (_testCts.IsCancellationRequested) break;

                    // Skip destructive commands — these aren't safe to run in
                    // an automated sweep.
                    if (IsDangerousCommand(cmd.CommandName))
                    {
                        Log(device.DeviceName, $"SKIP  [{cmd.CommandCategory}] {cmd.CommandName} — destructive command, test loop will not send", false);
                        skipped++;
                        continue;
                    }

                    // Skip variable-byte commands — they need a parameter we
                    // don't have in a bulk test run.
                    if (cmd.HasVariableBytes)
                    {
                        Log(device.DeviceName, $"SKIP  [{cmd.CommandCategory}] {cmd.CommandName} — has variable bytes (XX/YY)", false);
                        skipped++;
                        continue;
                    }

                    byte[] bytes = cmd.GetBytes();
                    if (bytes.Length == 0)
                    {
                        Log(device.DeviceName, $"SKIP  [{cmd.CommandCategory}] {cmd.CommandName} — cannot parse bytes", false);
                        skipped++;
                        continue;
                    }

                    if (currentCategory != cmd.CommandCategory)
                    {
                        currentCategory = cmd.CommandCategory;
                        Log(device.DeviceName, $"   · Category: {currentCategory}", true);
                    }

                    var response = await _connMgr.SendAsync(device.Id, bytes);

                    string hexStr = string.Join(" ", bytes.Select(b => b.ToString("X2")));
                    string parsed = response is { Length: > 0 }
                        ? ResponseParser.Parse(response, bytes, cmd.CommandFormat, series)
                        : "No response (timeout).";

                    // "SUCCEEDED" means we got a usable reply. A device reply
                    // like "Rejected …" / "Error" / NAK is a negative ACK, so
                    // even though bytes came back, the command itself failed.
                    bool ok = response is { Length: > 0 }
                              && !parsed.StartsWith("Rejected",  StringComparison.OrdinalIgnoreCase)
                              && !parsed.StartsWith("Error",     StringComparison.OrdinalIgnoreCase)
                              && !parsed.StartsWith("NAK",       StringComparison.OrdinalIgnoreCase);

                    string verdict = ok ? "SUCCEEDED" : "FAILED";
                    if (ok) pass++; else fail++;

                    Log(device.DeviceName,
                        $"{verdict}  [{cmd.CommandCategory}] {cmd.CommandName}  [{hexStr}]  ◀ {parsed}",
                        ok);

                    // Persist to the audit log too
                    _db.LogCommand(new AuditLogEntry
                    {
                        Timestamp     = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        DeviceName    = device.DeviceName,
                        DeviceAddress = device.IPAddress,
                        CommandName   = $"[TEST] {cmd.CommandName}",
                        CommandCode   = hexStr,
                        Response      = parsed,
                        Success       = ok,
                    });

                    // 3-second pacing delay so each command's effect is visible
                    // on the display before the next one is sent.
                    try { await Task.Delay(3000, _testCts.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        finally
        {
            string cancelledMsg = _testCts?.IsCancellationRequested == true ? " (cancelled)" : "";
            Log("—",
                $"Testing all commands complete{cancelledMsg}. Pass = {pass}, Fail = {fail}" +
                (skipped > 0 ? $", Skipped = {skipped}" : ""),
                fail == 0 && pass > 0);

            _testCts?.Dispose();
            _testCts = null;
            IsTesting = false;
        }
    }

    /// <summary>
    /// Commands that must never be sent during an automated Test All sweep.
    /// - Factory Reset wipes user configuration.
    /// - Power Off / Standby / Backlight Off / Screen Off / Sleep all put the
    ///   display into a state where subsequent commands either fail or the
    ///   operator has to walk over and turn the display back on.
    /// More destructive commands can be added here as we discover them.
    /// </summary>
    private static bool IsDangerousCommand(string commandName)
    {
        if (string.IsNullOrWhiteSpace(commandName)) return false;
        string[] danger = [
            "Factory Reset",
            "Power Off",
            "Standby",
            "Backlight Off",
            "Screen Off",
        ];
        return danger.Any(d => commandName.Contains(d, StringComparison.OrdinalIgnoreCase));
    }

    private List<DeviceEntry> GetTargetDevices()
    {
        return SendTarget switch
        {
            SendTarget.SelectedDevice => SelectedDevice != null ? [SelectedDevice] : [],
            SendTarget.SelectedGroup  => SelectedGroup == null ? [] :
                AvailableDevices
                    .Where(d => SelectedGroup.MemberDeviceIds.Contains(d.Id) && _connMgr.IsConnected(d.Id))
                    .ToList(),
            SendTarget.AllConnected => AvailableDevices
                    .Where(d => _connMgr.IsConnected(d.Id)).ToList(),
            _ => []
        };
    }

    private void Log(string deviceName, string message, bool success)
    {
        var entry = new AuditLogEntry
        {
            Timestamp   = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            DeviceName  = deviceName,
            CommandName = message,
            Success     = success,
        };
        SessionLog.Add(entry);
        while (SessionLog.Count > 500)
            SessionLog.RemoveAt(0);
        StatusMessage = message;
    }

    // ── Wake on LAN ───────────────────────────────────────────────────────────

    // Enabled whenever there's a selected device — we try native Power-On via
    // TCP first and fall back to WOL magic packet, so we don't strictly need a
    // MAC to attempt a wake.
    private bool CanWakeOnLan() => SelectedDevice != null;

    private async Task WakeOnLanAsync()
    {
        if (SelectedDevice == null) return;

        try
        {
            var result = await DeviceWakeService.WakeAsync(SelectedDevice, _db);
            bool ok = result.PowerOnAckd || result.MagicPacketSent;

            Log(SelectedDevice.DeviceName, result.Detail, ok);

            _db.LogCommand(new AuditLogEntry
            {
                Timestamp     = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                DeviceName    = SelectedDevice.DeviceName,
                DeviceAddress = SelectedDevice.IPAddress,
                CommandName   = "Wake on LAN",
                CommandCode   = SelectedDevice.MacAddress,
                Response      = result.Detail,
                Success       = ok,
                Notes         = result.Detail,
            });
        }
        catch (Exception ex)
        {
            Log(SelectedDevice?.DeviceName ?? "—", $"Wake failed: {ex.Message}", false);
        }
    }

    private void CopyLog()
    {
        if (SessionLog.Count == 0) return;
        try
        {
            var sb = new StringBuilder();
            foreach (var e in SessionLog)
                sb.AppendLine($"{e.TimeString}  {e.DeviceName,-20}  {e.CommandName}");
            System.Windows.Clipboard.SetText(sb.ToString());
            StatusMessage = $"Copied {SessionLog.Count} log line(s) to clipboard.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Copy failed: {ex.Message}";
        }
    }

    private void ExportLog()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Export Session Log",
            Filter     = "Text File (*.txt)|*.txt|CSV (*.csv)|*.csv",
            DefaultExt = ".txt",
            FileName   = $"AvocorLog_{DateTime.Now:yyyyMMdd_HHmmss}"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var sb = new StringBuilder();
            string ext = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();
            if (ext == ".csv")
            {
                sb.AppendLine("Timestamp,Device,Message,Success");
                foreach (var e in SessionLog)
                    sb.AppendLine($"\"{e.Timestamp}\",\"{e.DeviceName}\",\"{e.CommandName}\",{e.Success}");
            }
            else
            {
                foreach (var e in SessionLog)
                    sb.AppendLine($"{e.TimeString}  {e.DeviceName,-20}  {e.CommandName}");
            }
            System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            StatusMessage = $"Log exported to {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }
}
