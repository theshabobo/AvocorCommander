using AvocorCommander.Core;
using AvocorCommander.Models;

namespace AvocorCommander.Services;

/// <summary>
/// Executes macro steps in sequence against a target device or group.
/// </summary>
public sealed class MacroRunnerService
{
    private readonly DatabaseService   _db;
    private readonly ConnectionManager _connMgr;

    public event EventHandler<string>? StepCompleted;  // plain-English step result
    public event EventHandler<string>? RunFailed;
    public event EventHandler?         RunCompleted;
    public event EventHandler?         EntryLogged;

    /// <summary>
    /// Raised when a macro hits a "prompt" step. UI layer should show the
    /// PromptText to the user and set Response.TrySetResult(true) to continue
    /// or TrySetResult(false) to abort the whole run.
    /// </summary>
    public sealed class PromptRequest
    {
        public required string                    Message  { get; init; }
        public required string                    MacroName { get; init; }
        public required string                    DeviceName { get; init; }
        public required TaskCompletionSource<bool> Response { get; init; }
    }
    public event EventHandler<PromptRequest>? PromptRequested;

    public MacroRunnerService(DatabaseService db, ConnectionManager connMgr)
    {
        _db      = db;
        _connMgr = connMgr;
    }

    public async Task RunAsync(MacroEntry macro, int deviceId)
    {
        var devices = _db.GetAllDevices();
        var device  = devices.FirstOrDefault(d => d.Id == deviceId);

        bool autoConnected = false;
        if (!_connMgr.IsConnected(deviceId))
        {
            if (device == null) { RunFailed?.Invoke(this, "Device not found"); return; }
            autoConnected = await _connMgr.ConnectAsync(device);
            if (!autoConnected) { RunFailed?.Invoke(this, $"Could not connect to {device.DeviceName}"); return; }
        }

        try
        {
            var allCmds = _db.GetAllCommands();
            var series  = device != null ? (_db.GetSeriesForModel(device.ModelNumber) ?? "") : "";

            foreach (var step in macro.Steps.OrderBy(s => s.StepOrder))
            {
                // ── Prompt step: pause for user to click Continue / Cancel ──
                if (step.IsPrompt)
                {
                    string msg = string.IsNullOrWhiteSpace(step.PromptText)
                        ? "Macro paused. Click Continue to proceed or Cancel to abort."
                        : step.PromptText;

                    if (PromptRequested == null)
                    {
                        // No UI listener — can't prompt; treat as auto-continue.
                        StepCompleted?.Invoke(this, $"Step {step.StepOrder}: (prompt, no UI handler — continuing) {msg}");
                    }
                    else
                    {
                        var tcs = new TaskCompletionSource<bool>();
                        PromptRequested.Invoke(this, new PromptRequest
                        {
                            Message    = msg,
                            MacroName  = macro.MacroName,
                            DeviceName = device?.DeviceName ?? $"Device {deviceId}",
                            Response   = tcs,
                        });

                        bool keepGoing = await tcs.Task.ConfigureAwait(false);
                        if (!keepGoing)
                        {
                            StepCompleted?.Invoke(this, $"Step {step.StepOrder}: cancelled at prompt — aborting macro");
                            RunFailed?.Invoke(this, "Macro aborted at prompt step.");
                            return;
                        }
                        StepCompleted?.Invoke(this, $"Step {step.StepOrder}: prompt acknowledged — continuing");
                    }

                    if (step.DelayAfterMs > 0)
                        await Task.Delay(step.DelayAfterMs);
                    continue;
                }

                // ── Command step ────────────────────────────────────────────
                var cmd = allCmds.FirstOrDefault(c => c.Id == step.CommandId);
                if (cmd == null) { StepCompleted?.Invoke(this, $"Step {step.StepOrder}: command not found — skipped"); continue; }

                byte[] bytes = cmd.GetBytes();
                if (bytes.Length == 0) { StepCompleted?.Invoke(this, $"Step {step.StepOrder}: invalid bytes — skipped"); continue; }

                var response = await _connMgr.SendAsync(deviceId, bytes);
                var parsed   = response != null
                    ? ResponseParser.Parse(response, bytes, cmd.CommandFormat, series)
                    : "No response";

                _db.LogCommand(new AuditLogEntry
                {
                    Timestamp     = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    DeviceName    = device?.DeviceName ?? $"Device {deviceId}",
                    DeviceAddress = device?.IPAddress  ?? string.Empty,
                    CommandName   = $"[Macro] {macro.MacroName} \u2192 {cmd.CommandName}",
                    CommandCode   = string.Join(" ", bytes.Select(b => b.ToString("X2"))),
                    Response      = parsed,
                    Success       = response != null,
                });
                EntryLogged?.Invoke(this, EventArgs.Empty);
                StepCompleted?.Invoke(this, $"Step {step.StepOrder}: {cmd.CommandName} \u2192 {parsed}");

                if (step.DelayAfterMs > 0)
                    await Task.Delay(step.DelayAfterMs);
            }

            RunCompleted?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            if (autoConnected)
                await _connMgr.DisconnectAsync(deviceId);
        }
    }

    public async Task RunOnGroupAsync(MacroEntry macro, int groupId)
    {
        var group = _db.GetAllGroups().FirstOrDefault(g => g.Id == groupId);
        if (group == null) { RunFailed?.Invoke(this, "Group not found"); return; }

        foreach (var did in group.MemberDeviceIds)
            await RunAsync(macro, did);
    }
}
