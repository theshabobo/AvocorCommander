using AvocorCommander.Core;
using AvocorCommander.Models;

namespace AvocorCommander.Services;

/// <summary>
/// Checks ScheduleRules every minute and fires commands at the configured time.
/// </summary>
public sealed class SchedulerService : IDisposable
{
    private readonly DatabaseService    _db;
    private readonly ConnectionManager  _connMgr;
    private readonly Timer              _timer;

    private readonly HashSet<string> _firedToday = [];
    private DateTime  _lastDate;

    public event EventHandler<string>? RuleFired;   // rule name
    public event EventHandler<string>? RuleFailed;  // rule name + reason
    public event EventHandler?         EntryLogged; // raised after each audit log write

    public SchedulerService(DatabaseService db, ConnectionManager connMgr)
    {
        _db      = db;
        _connMgr = connMgr;
        _lastDate = DateTime.Today;
        // Check every 30 seconds — granularity sufficient for minute-level scheduling
        _timer = new Timer(OnTick, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public bool IsRunning { get; private set; } = true;

    public void Start()
    {
        IsRunning = true;
        _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    public void Stop()
    {
        IsRunning = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void OnTick(object? _)
    {
        if (!IsRunning) return;

        var now  = DateTime.Now;
        var date = now.Date;

        // Reset fired-today set at midnight
        if (date != _lastDate)
        {
            _firedToday.Clear();
            _lastDate = date;
        }

        var timeStr = now.ToString("HH:mm");
        var dow     = now.DayOfWeek;

        var rules = _db.GetAllScheduleRules();
        foreach (var rule in rules)
        {
            if (!rule.IsEnabled)      continue;
            if (rule.ScheduleTime != timeStr) continue;

            // Recurrence check
            bool shouldFire = rule.Recurrence switch
            {
                "Daily"    => true,
                "Weekdays" => dow >= DayOfWeek.Monday && dow <= DayOfWeek.Friday,
                "Weekends" => dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday,
                "Once"     => !_firedToday.Contains($"rule:{rule.Id}:once"),
                _          => false,
            };

            if (!shouldFire) continue;

            var fireKey = $"rule:{rule.Id}:{timeStr}";
            if (_firedToday.Contains(fireKey)) continue;
            _firedToday.Add(fireKey);

            if (rule.Recurrence == "Once")
                _firedToday.Add($"rule:{rule.Id}:once");

            _ = FireRuleAsync(rule);
        }
    }

    private async Task FireRuleAsync(ScheduleRule rule)
    {
        try
        {
            // Load the command
            var cmd = _db.GetAllCommands().FirstOrDefault(c => c.Id == rule.CommandId);
            if (cmd == null)
            {
                RuleFailed?.Invoke(this, $"{rule.RuleName}: command ID {rule.CommandId} not found");
                return;
            }

            byte[] bytes = cmd.GetBytes();
            if (bytes.Length == 0)
            {
                RuleFailed?.Invoke(this, $"{rule.RuleName}: could not parse command bytes");
                return;
            }

            // Collect target device IDs
            var devices   = _db.GetAllDevices();
            var deviceIds = new List<int>();
            if (rule.DeviceId.HasValue)
            {
                deviceIds.Add(rule.DeviceId.Value);
            }
            else if (rule.GroupId.HasValue)
            {
                var group = _db.GetAllGroups().FirstOrDefault(g => g.Id == rule.GroupId.Value);
                if (group != null) deviceIds.AddRange(group.MemberDeviceIds);
            }

            foreach (var did in deviceIds)
            {
                var device = devices.FirstOrDefault(d => d.Id == did);

                // Auto-connect if not already connected
                bool autoConnected = false;
                if (!_connMgr.IsConnected(did))
                {
                    if (device == null)
                    {
                        RuleFailed?.Invoke(this, $"{rule.RuleName}: device ID {did} not found");
                        continue;
                    }
                    autoConnected = await _connMgr.ConnectAsync(device);
                    if (!autoConnected)
                    {
                        LogEntry(rule, cmd, bytes, device, null, "Could not connect to device", false);
                        continue;
                    }
                }

                try
                {
                    var response = await _connMgr.SendAsync(did, bytes);
                    var series   = device != null ? (_db.GetSeriesForModel(device.ModelNumber) ?? "") : "";
                    var parsed   = response != null
                        ? ResponseParser.Parse(response, bytes, cmd.CommandFormat, series)
                        : "No response";

                    LogEntry(rule, cmd, bytes, device, did, parsed, response != null);
                }
                finally
                {
                    // Disconnect if we auto-connected so we don't leave orphan connections
                    if (autoConnected)
                        await _connMgr.DisconnectAsync(did);
                }
            }

            RuleFired?.Invoke(this, rule.RuleName);
        }
        catch (Exception ex)
        {
            RuleFailed?.Invoke(this, $"{rule.RuleName}: {ex.Message}");
        }
    }

    /// <summary>Immediately fires a rule on demand (ignores recurrence/time checks).</summary>
    public Task RunRuleNowAsync(ScheduleRule rule) => FireRuleAsync(rule);

    private void LogEntry(ScheduleRule rule, CommandEntry cmd, byte[] bytes,
                          DeviceEntry? device, int? did, string response, bool success)
    {
        var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _db.LogCommand(new AuditLogEntry
        {
            Timestamp     = ts,
            DeviceName    = device?.DeviceName ?? $"Device {did}",
            DeviceAddress = device?.IPAddress  ?? string.Empty,
            CommandName   = $"[Scheduler] {rule.RuleName} → {cmd.CommandName}",
            CommandCode   = string.Join(" ", bytes.Select(b => b.ToString("X2"))),
            Response      = response,
            Success       = success,
        });
        _db.UpdateRuleHistory(rule.Id, ts, success ? response : $"FAILED: {response}");
        rule.LastFiredAt = ts;
        rule.LastResult  = success ? response : $"FAILED: {response}";
        EntryLogged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _timer.Dispose();
}
