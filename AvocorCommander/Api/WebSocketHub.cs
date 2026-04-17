using AvocorCommander.Services;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AvocorCommander.Api;

/// <summary>
/// Manages connected WebSocket clients and broadcasts real-time events.
/// Authenticates on connect via ?key= query parameter or expects a Bearer
/// token in the first text message.
/// </summary>
public sealed class WebSocketHub : IDisposable
{
    private readonly ConcurrentDictionary<string, ClientInfo> _clients = new();

    private sealed class ClientInfo
    {
        public WebSocket Socket { get; init; } = null!;
        public string Username { get; init; } = "";
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }
    private readonly ServerDbService _serverDb;
    private readonly JwtService      _jwt;
    private readonly ConnectionManager _connMgr;
    private readonly SchedulerService  _scheduler;
    private readonly MacroRunnerService _macroRunner;
    private HealthMonitorService? _healthMonitor;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public WebSocketHub(
        ServerDbService serverDb,
        JwtService jwt,
        ConnectionManager connMgr,
        SchedulerService scheduler,
        MacroRunnerService macroRunner)
    {
        _serverDb    = serverDb;
        _jwt         = jwt;
        _connMgr     = connMgr;
        _scheduler   = scheduler;
        _macroRunner = macroRunner;

        // Wire up events
        _connMgr.ConnectionChanged += OnConnectionChanged;
        _scheduler.RuleFired       += OnSchedulerRuleFired;
        _scheduler.RuleFailed      += OnSchedulerRuleFailed;
        _macroRunner.StepCompleted += OnMacroStepCompleted;
        _macroRunner.RunCompleted  += OnMacroRunCompleted;
        _macroRunner.RunFailed     += OnMacroRunFailed;
    }

    /// <summary>
    /// Subscribe to HealthMonitorService events for broadcasting device health changes.
    /// Called after the HealthMonitorService is created.
    /// </summary>
    public void SubscribeHealthMonitor(HealthMonitorService healthMonitor)
    {
        _healthMonitor = healthMonitor;
        _healthMonitor.DeviceStatusChanged += OnDeviceHealthChanged;
    }

    private void OnDeviceHealthChanged(object? sender, (int deviceId, string status) e)
    {
        _ = BroadcastAsync(new WsEvent("device.health", new
        {
            deviceId  = e.deviceId,
            status    = e.status,
            timestamp = DateTime.UtcNow.ToString("o"),
        }));
    }

    // ── Event handlers → broadcast ───────────────────────────────────────────

    private void OnConnectionChanged(object? sender, Models.DeviceEntry device)
    {
        _ = BroadcastAsync(new WsEvent("connection.changed", new
        {
            deviceId     = device.Id,
            deviceName   = device.DeviceName,
            isConnected  = device.IsConnected,
            isConnecting = device.IsConnecting,
        }));
    }

    private void OnSchedulerRuleFired(object? sender, string ruleName)
    {
        _ = BroadcastAsync(new WsEvent("scheduler.fired", new
        {
            ruleName,
            timestamp = DateTime.UtcNow.ToString("o"),
        }));
    }

    private void OnSchedulerRuleFailed(object? sender, string detail)
    {
        _ = BroadcastAsync(new WsEvent("scheduler.failed", new
        {
            detail,
            timestamp = DateTime.UtcNow.ToString("o"),
        }));
    }

    private void OnMacroStepCompleted(object? sender, string stepResult)
    {
        _ = BroadcastAsync(new WsEvent("macro.step", new
        {
            result    = stepResult,
            timestamp = DateTime.UtcNow.ToString("o"),
        }));
    }

    private void OnMacroRunCompleted(object? sender, EventArgs e)
    {
        _ = BroadcastAsync(new WsEvent("macro.completed", new
        {
            timestamp = DateTime.UtcNow.ToString("o"),
        }));
    }

    private void OnMacroRunFailed(object? sender, string reason)
    {
        _ = BroadcastAsync(new WsEvent("macro.failed", new
        {
            reason,
            timestamp = DateTime.UtcNow.ToString("o"),
        }));
    }

    // ── Macro prompt support for web clients ──────────────────────────────
    // When a macro hits a prompt step during an API-initiated run, we push
    // the prompt to all connected web clients and wait for a response via
    // POST /api/macros/prompt/{promptId}.

    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingPrompts = new();

    /// <summary>
    /// Called by the API macro-run handler when a prompt step is hit.
    /// Broadcasts the prompt to all WebSocket clients and returns a Task
    /// that completes when the web client responds via the prompt API.
    /// Times out after 5 minutes (auto-continues if no response).
    /// </summary>
    public async Task<bool> HandleWebPromptAsync(string message, string macroName, string deviceName)
    {
        var promptId = Guid.NewGuid().ToString("N")[..12];
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingPrompts[promptId] = tcs;

        _ = BroadcastAsync(new WsEvent("macro.prompt", new
        {
            promptId,
            message,
            macroName,
            deviceName,
            timestamp = DateTime.UtcNow.ToString("o"),
        }));

        try
        {
            // Wait up to 5 minutes for the web client to respond
            var timeout = Task.Delay(TimeSpan.FromMinutes(5));
            var completed = await Task.WhenAny(tcs.Task, timeout);
            return completed == tcs.Task && tcs.Task.Result;
        }
        finally
        {
            _pendingPrompts.TryRemove(promptId, out _);
        }
    }

    /// <summary>Called by POST /api/macros/prompt/{promptId} to resolve a pending prompt.</summary>
    public bool ResolvePrompt(string promptId, bool continueExecution)
    {
        if (_pendingPrompts.TryRemove(promptId, out var tcs))
        {
            tcs.TrySetResult(continueExecution);
            return true;
        }
        return false;
    }

    // ── WebSocket handling ────────────────────────────────────────────────────

    public async Task HandleAsync(Microsoft.AspNetCore.Http.HttpContext httpContext)
    {
        if (!httpContext.WebSockets.IsWebSocketRequest)
        {
            httpContext.Response.StatusCode = 400;
            return;
        }

        // Check API key in query string first
        bool authenticated = false;
        bool needsFirstMessageAuth = false;
        if (httpContext.Request.Query.TryGetValue("key", out var keyVal))
        {
            string? key = keyVal.FirstOrDefault();
            if (!string.IsNullOrEmpty(key) && _serverDb.ValidateApiKey(key))
                authenticated = true;
        }

        // Check Bearer token in query string
        if (!authenticated && httpContext.Request.Query.TryGetValue("token", out var tokenVal))
        {
            string? token = tokenVal.FirstOrDefault();
            if (!string.IsNullOrEmpty(token) && _jwt.ValidateToken(token) != null)
                authenticated = true;
        }

        // If no query-param auth at all, reject before accepting the WebSocket
        if (!authenticated)
        {
            // Allow first-message auth only if the client didn't supply any credentials in the query
            bool hasQueryKey   = httpContext.Request.Query.ContainsKey("key");
            bool hasQueryToken = httpContext.Request.Query.ContainsKey("token");
            if (hasQueryKey || hasQueryToken)
            {
                // They tried query auth and it failed — reject immediately
                httpContext.Response.StatusCode = 401;
                return;
            }
            // No query credentials at all — allow first-message auth path
            needsFirstMessageAuth = true;
        }

        var ws = await httpContext.WebSockets.AcceptWebSocketAsync();

        // If not authenticated via query param, expect auth in the first message (with timeout)
        if (needsFirstMessageAuth)
        {
            var buf = new byte[4096];
            using var authCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buf, authCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Timed out waiting for auth message
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Auth timeout", CancellationToken.None);
                return;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                string msg = Encoding.UTF8.GetString(buf, 0, result.Count);
                // Try as Bearer token
                if (msg.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    string token = msg["Bearer ".Length..].Trim();
                    if (_jwt.ValidateToken(token) != null)
                        authenticated = true;
                }
                // Try as raw API key
                else if (_serverDb.ValidateApiKey(msg.Trim()))
                {
                    authenticated = true;
                }
            }

            if (!authenticated)
            {
                var errorBytes = Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new { error = "Unauthorized" }, _jsonOpts));
                await ws.SendAsync(errorBytes, WebSocketMessageType.Text, true, CancellationToken.None);
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Unauthorized", CancellationToken.None);
                return;
            }
        }

        // Authenticated — register and send welcome
        string clientId = Guid.NewGuid().ToString("N");
        _clients[clientId] = new ClientInfo { Socket = ws };

        var welcomeBytes = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new WsEvent("connected", new { clientId }), _jsonOpts));
        await ws.SendAsync(welcomeBytes, WebSocketMessageType.Text, true, CancellationToken.None);

        // Keep-alive read loop
        try
        {
            var buffer = new byte[4096];
            while (ws.State == WebSocketState.Open)
            {
                var rx = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (rx.MessageType == WebSocketMessageType.Close)
                    break;
                // Currently no client-to-server commands beyond auth; just consume
            }
        }
        catch (WebSocketException) { }
        finally
        {
            _clients.TryRemove(clientId, out _);
            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", CancellationToken.None); }
                catch { }
            }
        }
    }

    // ── Broadcast ─────────────────────────────────────────────────────────────

    public async Task BroadcastAsync(WsEvent evt)
    {
        if (_clients.IsEmpty) return;

        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(evt, _jsonOpts));
        var segment = new ArraySegment<byte>(payload);

        var deadClients = new List<string>();

        foreach (var (id, info) in _clients)
        {
            if (info.Socket.State != WebSocketState.Open)
            {
                deadClients.Add(id);
                continue;
            }
            try
            {
                await info.SendLock.WaitAsync();
                try
                {
                    await info.Socket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                finally
                {
                    info.SendLock.Release();
                }
            }
            catch
            {
                deadClients.Add(id);
            }
        }

        foreach (var id in deadClients)
            _clients.TryRemove(id, out _);
    }

    public int ConnectedClientCount => _clients.Count;

    public void Dispose()
    {
        _connMgr.ConnectionChanged -= OnConnectionChanged;
        _scheduler.RuleFired       -= OnSchedulerRuleFired;
        _scheduler.RuleFailed      -= OnSchedulerRuleFailed;
        _macroRunner.StepCompleted -= OnMacroStepCompleted;
        _macroRunner.RunCompleted  -= OnMacroRunCompleted;
        _macroRunner.RunFailed     -= OnMacroRunFailed;

        if (_healthMonitor != null)
            _healthMonitor.DeviceStatusChanged -= OnDeviceHealthChanged;

        foreach (var (_, info) in _clients)
        {
            try { info.Socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "Server stopping", CancellationToken.None).GetAwaiter().GetResult(); }
            catch { }
        }
        _clients.Clear();
    }
}
