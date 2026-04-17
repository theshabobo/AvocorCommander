using AvocorCommander.Core;
using AvocorCommander.Models;
using AvocorCommander.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace AvocorCommander.Api;

/// <summary>
/// Maps all REST API endpoints using ASP.NET Core Minimal APIs.
/// </summary>
public static class ApiRoutes
{
    static IResult? RequireAdmin(HttpContext ctx) =>
        ctx.Items["AuthRole"] as string == "Admin" ? null
        : Results.Json(new { success = false, error = "Admin role required." }, statusCode: 403);

    static IResult? RequireOperatorOrAdmin(HttpContext ctx)
    {
        var role = ctx.Items["AuthRole"] as string;
        return role == "Admin" || role == "Operator"
            ? null
            : Results.Json(new { success = false, error = "Operator or Admin role required." }, statusCode: 403);
    }

    // In-memory scan state for async network scans
    private static readonly ConcurrentDictionary<string, ScanState> _scans = new();

    private sealed class ScanState
    {
        public string Status { get; set; } = "running";
        public List<ScanResultDto> Results { get; } = new();
        public (int done, int total) Progress { get; set; }
    }

    public static void MapApiRoutes(
        this WebApplication app,
        DatabaseService     db,
        ConnectionManager   connMgr,
        MacroRunnerService  macroRunner,
        SchedulerService    scheduler,
        ServerDbService     serverDb,
        JwtService          jwt,
        WebSocketHub        wsHub)
    {
        // ══════════════════════════════════════════════════════════════════════
        //  AUTH
        // ══════════════════════════════════════════════════════════════════════

        app.MapPost("/api/auth/login", (LoginRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new ErrorResponse("Bad request", "Username and password are required."));

            var user = serverDb.ValidateUser(req.Username, req.Password);
            if (user == null)
                return Results.Json(new ErrorResponse("Unauthorized", "Invalid username or password."), statusCode: 401);

            string token = jwt.GenerateToken(user.Username, user.Role);
            return Results.Ok(new LoginResponse(token, user.Username, user.Role));
        });

        // ══════════════════════════════════════════════════════════════════════
        //  DEVICES
        // ══════════════════════════════════════════════════════════════════════

        app.MapGet("/api/devices", () =>
        {
            var devices = db.GetAllDevices();
            var dtos = devices.Select(d => new DeviceDto(
                d.Id, d.DeviceName, d.ModelNumber, d.IPAddress, d.Port, d.BaudRate,
                d.ComPort, d.MacAddress, d.ConnectionType, d.Notes, d.LastSeenAt,
                d.AutoConnect,
                connMgr.IsConnected(d.Id),
                db.GetSeriesForModel(d.ModelNumber)
            )).ToList();
            return Results.Ok(dtos);
        });

        app.MapGet("/api/devices/{id}", (int id) =>
        {
            var device = db.GetAllDevices().FirstOrDefault(d => d.Id == id);
            if (device == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Device {id} not found."));

            var deviceCommands = db.GetDeviceCommands(id);
            var dto = new DeviceDetailDto(
                device.Id, device.DeviceName, device.ModelNumber, device.IPAddress,
                device.Port, device.BaudRate, device.ComPort, device.MacAddress,
                device.ConnectionType, device.Notes, device.LastSeenAt, device.AutoConnect,
                connMgr.IsConnected(device.Id),
                db.GetSeriesForModel(device.ModelNumber),
                deviceCommands);
            return Results.Ok(dto);
        });

        app.MapPost("/api/devices/{id}/wake", async (HttpContext ctx, int id) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            var device = db.GetAllDevices().FirstOrDefault(d => d.Id == id);
            if (device == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Device {id} not found."));

            var result = await DeviceWakeService.WakeAsync(device, db);
            return Results.Ok(new WakeResponse(result.MagicPacketSent, result.PowerOnSent, result.PowerOnAckd, result.Detail));
        });

        app.MapPost("/api/devices/{id}/connect", async (HttpContext ctx, int id) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            var device = db.GetAllDevices().FirstOrDefault(d => d.Id == id);
            if (device == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Device {id} not found."));

            if (connMgr.IsConnected(device.Id))
                return Results.Ok(new SuccessResponse(true, "Already connected."));

            bool ok = await connMgr.ConnectAsync(device);
            if (!ok)
                return Results.Json(new ErrorResponse("Connection failed", connMgr.LastError), statusCode: 502);

            return Results.Ok(new SuccessResponse(true, $"Connected to {device.DeviceName}."));
        });

        app.MapPost("/api/devices/{id}/disconnect", async (HttpContext ctx, int id) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            var device = db.GetAllDevices().FirstOrDefault(d => d.Id == id);
            if (device == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Device {id} not found."));

            if (!connMgr.IsConnected(device.Id))
                return Results.Ok(new SuccessResponse(true, "Already disconnected."));

            await connMgr.DisconnectAsync(device);
            return Results.Ok(new SuccessResponse(true, $"Disconnected from {device.DeviceName}."));
        });

        app.MapPost("/api/devices/{id}/command", async (HttpContext ctx, int id, CommandRequest req) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            if (string.IsNullOrWhiteSpace(req.Command))
                return Results.BadRequest(new ErrorResponse("Bad request", "Command is required."));

            var device = db.GetAllDevices().FirstOrDefault(d => d.Id == id);
            if (device == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Device {id} not found."));

            string? series = db.GetSeriesForModel(device.ModelNumber);
            if (string.IsNullOrEmpty(series))
                return Results.Json(new ErrorResponse("No series", $"Cannot resolve series for model '{device.ModelNumber}'."), statusCode: 422);

            // Search series commands first, then fall back to device-specific commands
            CommandEntry? cmd;
            if (!string.IsNullOrWhiteSpace(req.Category))
            {
                var commands = db.GetCommandsBySeriesAndCategory(series, req.Category);
                cmd = commands.FirstOrDefault(c =>
                    string.Equals(c.CommandName, req.Command, StringComparison.OrdinalIgnoreCase));
                // Fall back to device-specific commands
                if (cmd == null)
                {
                    var devCmds = db.GetDeviceCommands(id);
                    cmd = devCmds.FirstOrDefault(c =>
                        string.Equals(c.CommandCategory, req.Category, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.CommandName, req.Command, StringComparison.OrdinalIgnoreCase));
                }
                if (cmd == null)
                    return Results.NotFound(new ErrorResponse("Command not found",
                        $"No command '{req.Command}' in category '{req.Category}' for series '{series}'."));
            }
            else
            {
                var commands = db.GetCommandsBySeries(series);
                cmd = commands.FirstOrDefault(c =>
                    string.Equals(c.CommandName, req.Command, StringComparison.OrdinalIgnoreCase));
                // Fall back to device-specific commands
                if (cmd == null)
                {
                    var devCmds = db.GetDeviceCommands(id);
                    cmd = devCmds.FirstOrDefault(c =>
                        string.Equals(c.CommandName, req.Command, StringComparison.OrdinalIgnoreCase));
                }
                if (cmd == null)
                    return Results.NotFound(new ErrorResponse("Command not found",
                        $"No command '{req.Command}' for series '{series}'."));
            }

            byte[] bytes = cmd.GetBytes();
            if (bytes.Length == 0)
                return Results.Json(new ErrorResponse("Invalid command", "Could not encode command bytes."), statusCode: 422);

            // Auto-connect if needed
            bool autoConnected = false;
            if (!connMgr.IsConnected(device.Id))
            {
                autoConnected = await connMgr.ConnectAsync(device);
                if (!autoConnected)
                    return Results.Json(new ErrorResponse("Connection failed", connMgr.LastError), statusCode: 502);
            }

            try
            {
                var response = await connMgr.SendAsync(device.Id, bytes);
                string parsed = response != null
                    ? ResponseParser.Parse(response, bytes, cmd.CommandFormat, series)
                    : "No response";

                string hex = response != null
                    ? string.Join(" ", response.Select(b => b.ToString("X2")))
                    : "";

                // Log to audit
                db.LogCommand(new AuditLogEntry
                {
                    Timestamp     = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    DeviceName    = device.DeviceName,
                    DeviceAddress = device.IPAddress,
                    CommandName   = $"[API] {cmd.CommandName}",
                    CommandCode   = string.Join(" ", bytes.Select(b => b.ToString("X2"))),
                    Response      = parsed,
                    Success       = response != null,
                });

                return Results.Ok(new CommandResponse(response != null, parsed, hex));
            }
            finally
            {
                if (autoConnected)
                    await connMgr.DisconnectAsync(device);
            }
        });

        app.MapPost("/api/devices", (HttpContext ctx, AddDeviceRequest req) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            var d = new DeviceEntry
            {
                DeviceName     = req.DeviceName,
                ModelNumber    = req.ModelNumber,
                IPAddress      = req.IpAddress,
                Port           = req.Port,
                BaudRate       = req.BaudRate,
                ComPort        = req.ComPort,
                MacAddress     = req.MacAddress,
                ConnectionType = req.ConnectionType,
                Notes          = req.Notes,
                AutoConnect    = req.AutoConnect,
            };
            int id = db.InsertDevice(d);
            d.Id = id;
            return Results.Ok(new DeviceDto(
                d.Id, d.DeviceName, d.ModelNumber, d.IPAddress, d.Port, d.BaudRate,
                d.ComPort, d.MacAddress, d.ConnectionType, d.Notes, d.LastSeenAt,
                d.AutoConnect, false, db.GetSeriesForModel(d.ModelNumber)));
        });

        app.MapPut("/api/devices/{id}", (HttpContext ctx, int id, UpdateDeviceRequest req) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            var existing = db.GetAllDevices().FirstOrDefault(d => d.Id == id);
            if (existing == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Device {id} not found."));

            existing.DeviceName     = req.DeviceName;
            existing.ModelNumber    = req.ModelNumber;
            existing.IPAddress      = req.IpAddress;
            existing.Port           = req.Port;
            existing.BaudRate       = req.BaudRate;
            existing.ComPort        = req.ComPort;
            existing.MacAddress     = req.MacAddress;
            existing.ConnectionType = req.ConnectionType;
            existing.Notes          = req.Notes;
            existing.AutoConnect    = req.AutoConnect;
            db.UpdateDevice(existing);

            return Results.Ok(new DeviceDto(
                existing.Id, existing.DeviceName, existing.ModelNumber, existing.IPAddress,
                existing.Port, existing.BaudRate, existing.ComPort, existing.MacAddress,
                existing.ConnectionType, existing.Notes, existing.LastSeenAt, existing.AutoConnect,
                connMgr.IsConnected(existing.Id), db.GetSeriesForModel(existing.ModelNumber)));
        });

        app.MapDelete("/api/devices/{id}", (HttpContext ctx, int id) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            var existing = db.GetAllDevices().FirstOrDefault(d => d.Id == id);
            if (existing == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Device {id} not found."));

            db.DeleteDevice(id);
            return Results.Ok(new SuccessResponse(true, "Device deleted."));
        });

        // ══════════════════════════════════════════════════════════════════════
        //  DEVICE COMMANDS (series + device-specific merged)
        // ══════════════════════════════════════════════════════════════════════

        app.MapGet("/api/devices/{id}/commands", (int id) =>
        {
            var device = db.GetAllDevices().FirstOrDefault(d => d.Id == id);
            if (device == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Device {id} not found."));

            string? series = db.GetSeriesForModel(device.ModelNumber);
            var seriesCommands = !string.IsNullOrEmpty(series)
                ? db.GetCommandsBySeries(series)
                : new List<Models.CommandEntry>();

            var deviceCommands = db.GetDeviceCommands(id);

            // If device has discovered Application commands, hide seed XYZ placeholders
            bool hasRealApps = deviceCommands.Any(c =>
                string.Equals(c.CommandCategory, "Application", StringComparison.OrdinalIgnoreCase));

            if (hasRealApps)
            {
                seriesCommands = seriesCommands.Where(c =>
                    !string.Equals(c.CommandCategory, "Application", StringComparison.OrdinalIgnoreCase) ||
                    !c.CommandName.Contains("XYZ", StringComparison.OrdinalIgnoreCase)
                ).ToList();
            }

            var merged = seriesCommands.Concat(deviceCommands).ToList();
            return Results.Ok(merged);
        });

        app.MapPost("/api/devices/{id}/discover", async (HttpContext ctx, int id) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            var device = db.GetAllDevices().FirstOrDefault(d => d.Id == id);
            if (device == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Device {id} not found."));

            string? series = db.GetSeriesForModel(device.ModelNumber);
            if (series != "B-Series")
                return Results.Json(new ErrorResponse("Not supported", "App discovery is only supported on B-Series displays."), statusCode: 422);

            if (!connMgr.IsConnected(device.Id))
                return Results.Json(new ErrorResponse("Not connected", "Device must be connected to discover apps."), statusCode: 422);

            // Clear existing device commands so we get a fresh discovery
            db.ClearDeviceCommands(id);

            var discovered = await AppDiscoveryService.DiscoverAppsAsync(connMgr, db, id);
            if (discovered.Count > 0)
                db.SetDeviceCommands(id, discovered);

            var deviceCommands = db.GetDeviceCommands(id);
            return Results.Ok(new { success = true, count = deviceCommands.Count, commands = deviceCommands });
        });

        // ══════════════════════════════════════════════════════════════════════
        //  GROUPS
        // ══════════════════════════════════════════════════════════════════════

        app.MapGet("/api/groups", () =>
        {
            var groups = db.GetAllGroups();
            var dtos = groups.Select(g => new GroupDto(
                g.Id, g.GroupName, g.Notes, g.MemberDeviceIds.ToList()
            )).ToList();
            return Results.Ok(dtos);
        });

        app.MapPost("/api/groups", (HttpContext ctx, CreateGroupRequest req) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            var g = new GroupEntry { GroupName = req.GroupName, Notes = req.Notes };
            int id = db.InsertGroup(g);
            g.Id = id;
            return Results.Ok(new GroupDto(g.Id, g.GroupName, g.Notes, g.MemberDeviceIds.ToList()));
        });

        app.MapPut("/api/groups/{id}", (HttpContext ctx, int id, UpdateGroupRequest req) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            var existing = db.GetAllGroups().FirstOrDefault(g => g.Id == id);
            if (existing == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Group {id} not found."));

            existing.GroupName = req.GroupName;
            existing.Notes     = req.Notes;
            db.UpdateGroup(existing);
            return Results.Ok(new GroupDto(existing.Id, existing.GroupName, existing.Notes, existing.MemberDeviceIds.ToList()));
        });

        app.MapDelete("/api/groups/{id}", (HttpContext ctx, int id) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            var existing = db.GetAllGroups().FirstOrDefault(g => g.Id == id);
            if (existing == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Group {id} not found."));

            db.DeleteGroup(id);
            return Results.Ok(new SuccessResponse(true, "Group deleted."));
        });

        app.MapPut("/api/groups/{id}/members", (HttpContext ctx, int id, SetGroupMembersRequest req) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            var existing = db.GetAllGroups().FirstOrDefault(g => g.Id == id);
            if (existing == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Group {id} not found."));

            db.SetGroupMembers(id, req.DeviceIds);
            return Results.Ok(new SuccessResponse(true, $"Group members updated ({req.DeviceIds.Count} devices)."));
        });

        app.MapPost("/api/groups/{id}/command", async (HttpContext ctx, int id, GroupCommandRequest req) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            if (string.IsNullOrWhiteSpace(req.Command))
                return Results.BadRequest(new ErrorResponse("Bad request", "Command name is required."));

            var group = db.GetAllGroups().FirstOrDefault(g => g.Id == id);
            if (group == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Group {id} not found."));

            if (group.MemberDeviceIds.Count == 0)
                return Results.Json(new ErrorResponse("Empty group", "Group has no member devices."), statusCode: 422);

            var allDevices = db.GetAllDevices();
            var results = new List<GroupCommandResult>();

            foreach (int deviceId in group.MemberDeviceIds)
            {
                var device = allDevices.FirstOrDefault(d => d.Id == deviceId);
                if (device == null)
                {
                    results.Add(new GroupCommandResult(deviceId, $"Device {deviceId}", false, "Device not found"));
                    continue;
                }

                string? series = db.GetSeriesForModel(device.ModelNumber);
                if (string.IsNullOrEmpty(series))
                {
                    results.Add(new GroupCommandResult(deviceId, device.DeviceName, false, "Unknown series"));
                    continue;
                }

                // Find the command by name across all categories for this series
                var commands = db.GetCommandsBySeries(series);
                var cmd = commands.FirstOrDefault(c =>
                    string.Equals(c.CommandName, req.Command, StringComparison.OrdinalIgnoreCase));
                if (cmd == null)
                {
                    results.Add(new GroupCommandResult(deviceId, device.DeviceName, false,
                        $"Command '{req.Command}' not found for series '{series}'"));
                    continue;
                }

                byte[] bytes = cmd.GetBytes();
                if (bytes.Length == 0)
                {
                    results.Add(new GroupCommandResult(deviceId, device.DeviceName, false, "Invalid command bytes"));
                    continue;
                }

                bool autoConnected = false;
                if (!connMgr.IsConnected(device.Id))
                {
                    autoConnected = await connMgr.ConnectAsync(device);
                    if (!autoConnected)
                    {
                        results.Add(new GroupCommandResult(deviceId, device.DeviceName, false, "Connection failed"));
                        continue;
                    }
                }

                try
                {
                    var response = await connMgr.SendAsync(device.Id, bytes);
                    string parsed = response != null
                        ? ResponseParser.Parse(response, bytes, cmd.CommandFormat, series)
                        : "No response";

                    db.LogCommand(new AuditLogEntry
                    {
                        Timestamp     = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        DeviceName    = device.DeviceName,
                        DeviceAddress = device.IPAddress,
                        CommandName   = $"[API/Group] {cmd.CommandName}",
                        CommandCode   = string.Join(" ", bytes.Select(b => b.ToString("X2"))),
                        Response      = parsed,
                        Success       = response != null,
                    });

                    results.Add(new GroupCommandResult(deviceId, device.DeviceName, response != null, parsed));
                }
                finally
                {
                    if (autoConnected)
                        await connMgr.DisconnectAsync(device);
                }
            }

            return Results.Ok(results);
        });

        // ══════════════════════════════════════════════════════════════════════
        //  MACROS
        // ══════════════════════════════════════════════════════════════════════

        app.MapGet("/api/macros", () =>
        {
            var macros = db.GetAllMacros();
            var dtos = macros.Select(m => new
            {
                m.Id,
                m.MacroName,
                m.Notes,
                stepCount = m.Steps.Count,
                steps = m.Steps.Select(s => new
                {
                    s.Id, s.StepOrder, s.CommandId, s.CommandName,
                    s.SeriesPattern, s.DelayAfterMs, s.StepType, s.PromptText
                })
            }).ToList();
            return Results.Ok(dtos);
        });

        app.MapPost("/api/macros/{id}/run", async (HttpContext ctx, int id, MacroRunRequest req) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            var macro = db.GetAllMacros().FirstOrDefault(m => m.Id == id);
            if (macro == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Macro {id} not found."));

            if (req.DeviceId == null && req.GroupId == null)
                return Results.BadRequest(new ErrorResponse("Bad request", "Either deviceId or groupId is required."));

            try
            {
                if (req.GroupId.HasValue)
                {
                    var group = db.GetAllGroups().FirstOrDefault(g => g.Id == req.GroupId.Value);
                    if (group == null)
                        return Results.NotFound(new ErrorResponse("Not found", $"Group {req.GroupId} not found."));
                    await macroRunner.RunOnGroupAsync(macro, req.GroupId.Value);
                }
                else
                {
                    var device = db.GetAllDevices().FirstOrDefault(d => d.Id == req.DeviceId!.Value);
                    if (device == null)
                        return Results.NotFound(new ErrorResponse("Not found", $"Device {req.DeviceId} not found."));
                    // autoPrompts: true — auto-continue prompt steps so the WPF
                    // MessageBox doesn't block API-initiated macro runs.
                    await macroRunner.RunAsync(macro, req.DeviceId!.Value, autoPrompts: true);
                }
                return Results.Ok(new SuccessResponse(true, $"Macro '{macro.MacroName}' executed."));
            }
            catch (Exception ex)
            {
                return Results.Json(new ErrorResponse("Macro failed", ex.Message), statusCode: 500);
            }
        });

        app.MapPost("/api/macros", (HttpContext ctx, CreateMacroRequest req) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            var m = new MacroEntry { MacroName = req.MacroName, Notes = req.Notes };
            int id = db.InsertMacro(m);
            m.Id = id;

            if (req.Steps?.Count > 0)
            {
                var steps = req.Steps.Select((s, i) => new MacroStep
                {
                    MacroId      = id,
                    StepOrder    = i + 1,
                    CommandId    = s.CommandId,
                    DelayAfterMs = s.DelayAfterMs,
                    StepType     = s.StepType,
                    PromptText   = s.PromptText,
                });
                db.SetMacroSteps(id, steps);
            }

            return Results.Ok(new MacroDto(m.Id, m.MacroName, m.Notes, req.Steps?.Count ?? 0));
        });

        app.MapPut("/api/macros/{id}", (HttpContext ctx, int id, CreateMacroRequest req) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            var existing = db.GetAllMacros().FirstOrDefault(m => m.Id == id);
            if (existing == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Macro {id} not found."));

            existing.MacroName = req.MacroName;
            existing.Notes     = req.Notes;
            db.UpdateMacro(existing);

            if (req.Steps != null)
            {
                var steps = req.Steps.Select((s, i) => new MacroStep
                {
                    MacroId      = id,
                    StepOrder    = i + 1,
                    CommandId    = s.CommandId,
                    DelayAfterMs = s.DelayAfterMs,
                    StepType     = s.StepType,
                    PromptText   = s.PromptText,
                });
                db.SetMacroSteps(id, steps);
            }

            return Results.Ok(new MacroDto(existing.Id, existing.MacroName, existing.Notes, req.Steps?.Count ?? existing.Steps.Count));
        });

        app.MapDelete("/api/macros/{id}", (HttpContext ctx, int id) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            var existing = db.GetAllMacros().FirstOrDefault(m => m.Id == id);
            if (existing == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Macro {id} not found."));

            db.DeleteMacro(id);
            return Results.Ok(new SuccessResponse(true, "Macro deleted."));
        });

        // Prompt response — web client clicks Continue or Cancel on a macro prompt
        app.MapPost("/api/macros/prompt/{promptId}", (HttpContext ctx, string promptId, PromptResponse req) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            bool resolved = wsHub.ResolvePrompt(promptId, req.Continue);
            return resolved
                ? Results.Ok(new SuccessResponse(true, req.Continue ? "Continuing macro." : "Macro aborted."))
                : Results.NotFound(new ErrorResponse("Not found", "Prompt not found or already resolved."));
        });

        // ══════════════════════════════════════════════════════════════════════
        //  SCHEDULES
        // ══════════════════════════════════════════════════════════════════════

        app.MapGet("/api/schedules", () =>
        {
            var rules = db.GetAllScheduleRules();
            return Results.Ok(rules);
        });

        app.MapPost("/api/schedules", (HttpContext ctx, CreateScheduleRequest req) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            var r = new ScheduleRule
            {
                RuleName     = req.RuleName,
                DeviceId     = req.DeviceId,
                GroupId      = req.GroupId,
                CommandId    = req.CommandId,
                ScheduleTime = req.ScheduleTime,
                Recurrence   = req.Recurrence,
                IsEnabled    = req.IsEnabled,
                Notes        = req.Notes,
            };
            int id = db.InsertScheduleRule(r);
            r.Id = id;
            return Results.Ok(r);
        });

        app.MapPut("/api/schedules/{id}", (HttpContext ctx, int id, CreateScheduleRequest req) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            var existing = db.GetAllScheduleRules().FirstOrDefault(r => r.Id == id);
            if (existing == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Schedule rule {id} not found."));

            existing.RuleName     = req.RuleName;
            existing.DeviceId     = req.DeviceId;
            existing.GroupId      = req.GroupId;
            existing.CommandId    = req.CommandId;
            existing.ScheduleTime = req.ScheduleTime;
            existing.Recurrence   = req.Recurrence;
            existing.IsEnabled    = req.IsEnabled;
            existing.Notes        = req.Notes;
            db.UpdateScheduleRule(existing);
            return Results.Ok(existing);
        });

        app.MapDelete("/api/schedules/{id}", (HttpContext ctx, int id) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            var existing = db.GetAllScheduleRules().FirstOrDefault(r => r.Id == id);
            if (existing == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Schedule rule {id} not found."));

            db.DeleteScheduleRule(id);
            return Results.Ok(new SuccessResponse(true, "Schedule rule deleted."));
        });

        app.MapPost("/api/schedules/{id}/run", async (HttpContext ctx, int id) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            var rule = db.GetAllScheduleRules().FirstOrDefault(r => r.Id == id);
            if (rule == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Schedule rule {id} not found."));

            var allCommands = db.GetAllCommands();
            var cmd = allCommands.FirstOrDefault(c => c.Id == rule.CommandId);
            if (cmd == null)
                return Results.NotFound(new ErrorResponse("Command not found", $"Command {rule.CommandId} not found."));

            byte[] bytes = cmd.GetBytes();
            if (bytes.Length == 0)
                return Results.Json(new ErrorResponse("Invalid command", "Could not encode command bytes."), statusCode: 422);

            // Determine target devices
            var targetDevices = new List<DeviceEntry>();
            if (rule.DeviceId.HasValue)
            {
                var dev = db.GetAllDevices().FirstOrDefault(d => d.Id == rule.DeviceId.Value);
                if (dev != null) targetDevices.Add(dev);
            }
            else if (rule.GroupId.HasValue)
            {
                var group = db.GetAllGroups().FirstOrDefault(g => g.Id == rule.GroupId.Value);
                if (group != null)
                {
                    var allDevices = db.GetAllDevices();
                    foreach (int devId in group.MemberDeviceIds)
                    {
                        var dev = allDevices.FirstOrDefault(d => d.Id == devId);
                        if (dev != null) targetDevices.Add(dev);
                    }
                }
            }

            if (targetDevices.Count == 0)
                return Results.Json(new ErrorResponse("No target", "No target device or group members found."), statusCode: 422);

            var results = new List<GroupCommandResult>();
            foreach (var device in targetDevices)
            {
                bool autoConnected = false;
                if (!connMgr.IsConnected(device.Id))
                {
                    autoConnected = await connMgr.ConnectAsync(device);
                    if (!autoConnected)
                    {
                        results.Add(new GroupCommandResult(device.Id, device.DeviceName, false, "Connection failed"));
                        continue;
                    }
                }

                try
                {
                    var response = await connMgr.SendAsync(device.Id, bytes);
                    string? series = db.GetSeriesForModel(device.ModelNumber);
                    string parsed = response != null && !string.IsNullOrEmpty(series)
                        ? ResponseParser.Parse(response, bytes, cmd.CommandFormat, series)
                        : response != null ? "OK" : "No response";

                    results.Add(new GroupCommandResult(device.Id, device.DeviceName, response != null, parsed));
                }
                finally
                {
                    if (autoConnected)
                        await connMgr.DisconnectAsync(device);
                }
            }

            return Results.Ok(results);
        });

        // ══════════════════════════════════════════════════════════════════════
        //  COMMANDS BROWSER
        // ══════════════════════════════════════════════════════════════════════

        app.MapGet("/api/commands", (string? series) =>
        {
            var commands = db.GetAllCommands();
            if (!string.IsNullOrWhiteSpace(series))
                commands = commands.Where(c => string.Equals(c.SeriesPattern, series, StringComparison.OrdinalIgnoreCase)).ToList();
            return Results.Ok(commands);
        });

        app.MapGet("/api/commands/series", () =>
        {
            var seriesList = db.GetDistinctSeries();
            return Results.Ok(seriesList);
        });

        app.MapGet("/api/commands/categories", (string? series) =>
        {
            if (string.IsNullOrWhiteSpace(series))
            {
                // Return distinct categories across all series
                var allCommands = db.GetAllCommands();
                var categories = allCommands.Select(c => c.CommandCategory).Distinct().OrderBy(c => c).ToList();
                return Results.Ok(categories);
            }
            var cats = db.GetCategoriesBySeries(series);
            return Results.Ok(cats);
        });

        app.MapGet("/api/models", () =>
        {
            var models = db.GetAllModels();
            return Results.Ok(models);
        });

        // ── Commands CRUD ───────────────────────────────────────────────────

        app.MapPost("/api/commands", (HttpContext ctx, CommandCrudRequest req) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            if (string.IsNullOrWhiteSpace(req.SeriesPattern) || string.IsNullOrWhiteSpace(req.CommandName))
                return Results.BadRequest(new ErrorResponse("Bad request", "SeriesPattern and CommandName are required."));

            var entry = new CommandEntry
            {
                SeriesPattern   = req.SeriesPattern,
                CommandCategory = req.CommandCategory ?? "",
                CommandName     = req.CommandName,
                CommandCode     = req.CommandCode ?? "",
                Notes           = req.Notes ?? "",
                Port            = req.Port,
                CommandFormat   = req.CommandFormat ?? "HEX"
            };
            db.InsertCommand(entry);
            return Results.Ok(new SuccessResponse(true, "Command created."));
        });

        app.MapPut("/api/commands/{id}", (HttpContext ctx, int id, CommandCrudRequest req) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            if (string.IsNullOrWhiteSpace(req.SeriesPattern) || string.IsNullOrWhiteSpace(req.CommandName))
                return Results.BadRequest(new ErrorResponse("Bad request", "SeriesPattern and CommandName are required."));

            var entry = new CommandEntry
            {
                Id              = id,
                SeriesPattern   = req.SeriesPattern,
                CommandCategory = req.CommandCategory ?? "",
                CommandName     = req.CommandName,
                CommandCode     = req.CommandCode ?? "",
                Notes           = req.Notes ?? "",
                Port            = req.Port,
                CommandFormat   = req.CommandFormat ?? "HEX"
            };
            db.UpdateCommand(entry);
            return Results.Ok(new SuccessResponse(true, "Command updated."));
        });

        app.MapDelete("/api/commands/{id}", (HttpContext ctx, int id) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;
            db.DeleteCommand(id);
            return Results.Ok(new SuccessResponse(true, "Command deleted."));
        });

        // ── Models CRUD ─────────────────────────────────────────────────────

        app.MapPost("/api/models", (HttpContext ctx, ModelCrudRequest req) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            if (string.IsNullOrWhiteSpace(req.ModelNumber) || string.IsNullOrWhiteSpace(req.SeriesPattern))
                return Results.BadRequest(new ErrorResponse("Bad request", "ModelNumber and SeriesPattern are required."));

            var entry = new ModelEntry
            {
                ModelNumber   = req.ModelNumber,
                SeriesPattern = req.SeriesPattern,
                BaudRate      = req.BaudRate > 0 ? req.BaudRate : 9600
            };
            db.InsertModel(entry);
            return Results.Ok(new SuccessResponse(true, "Model created."));
        });

        app.MapPut("/api/models/{id}", (HttpContext ctx, int id, ModelCrudRequest req) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            if (string.IsNullOrWhiteSpace(req.ModelNumber) || string.IsNullOrWhiteSpace(req.SeriesPattern))
                return Results.BadRequest(new ErrorResponse("Bad request", "ModelNumber and SeriesPattern are required."));

            var entry = new ModelEntry
            {
                Id            = id,
                ModelNumber   = req.ModelNumber,
                SeriesPattern = req.SeriesPattern,
                BaudRate      = req.BaudRate > 0 ? req.BaudRate : 9600
            };
            db.UpdateModel(entry);
            return Results.Ok(new SuccessResponse(true, "Model updated."));
        });

        app.MapDelete("/api/models/{id}", (HttpContext ctx, int id) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;
            db.DeleteModel(id);
            return Results.Ok(new SuccessResponse(true, "Model deleted."));
        });

        // ── CSV Export/Import ───────────────────────────────────────────────

        app.MapGet("/api/commands/export", (HttpContext ctx) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            var commands = db.GetAllCommands();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("SeriesPattern,CommandCategory,CommandName,CommandCode,Notes,Port,CommandFormat");
            foreach (var c in commands)
            {
                sb.Append(CsvEscape(c.SeriesPattern)).Append(',');
                sb.Append(CsvEscape(c.CommandCategory)).Append(',');
                sb.Append(CsvEscape(c.CommandName)).Append(',');
                sb.Append(CsvEscape(c.CommandCode)).Append(',');
                sb.Append(CsvEscape(c.Notes)).Append(',');
                sb.Append(c.Port).Append(',');
                sb.AppendLine(CsvEscape(c.CommandFormat));
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            return Results.File(bytes, "text/csv", "commands_export.csv");
        });

        app.MapPost("/api/commands/import", async (HttpContext ctx) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            if (!ctx.Request.HasFormContentType)
                return Results.BadRequest(new ErrorResponse("Bad request", "Expected multipart/form-data."));

            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file == null || file.Length == 0)
                return Results.BadRequest(new ErrorResponse("Bad request", "No CSV file provided."));

            var existing = db.GetAllCommands();
            var existingKeys = new HashSet<string>(
                existing.Select(c => $"{c.SeriesPattern}||{c.CommandCategory}||{c.CommandName}"),
                StringComparer.OrdinalIgnoreCase);

            int imported = 0, skipped = 0, errors = 0;

            using var reader = new StreamReader(file.OpenReadStream());
            string? line;
            bool headerSkipped = false;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (!headerSkipped) { headerSkipped = true; continue; }
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var fields = ParseCsvLine(line);
                    if (fields.Count < 7) { errors++; continue; }

                    var seriesPattern   = fields[0].Trim();
                    var commandCategory = fields[1].Trim();
                    var commandName     = fields[2].Trim();
                    var commandCode     = fields[3].Trim();
                    var notes           = fields[4].Trim();
                    var portStr         = fields[5].Trim();
                    var commandFormat   = fields[6].Trim();

                    if (string.IsNullOrEmpty(seriesPattern) || string.IsNullOrEmpty(commandName))
                    { errors++; continue; }

                    var key = $"{seriesPattern}||{commandCategory}||{commandName}";
                    if (existingKeys.Contains(key))
                    { skipped++; continue; }

                    int port = 0;
                    int.TryParse(portStr, out port);

                    db.InsertCommand(new CommandEntry
                    {
                        SeriesPattern   = seriesPattern,
                        CommandCategory = commandCategory,
                        CommandName     = commandName,
                        CommandCode     = commandCode,
                        Notes           = notes,
                        Port            = port,
                        CommandFormat   = string.IsNullOrEmpty(commandFormat) ? "HEX" : commandFormat
                    });
                    existingKeys.Add(key);
                    imported++;
                }
                catch
                {
                    errors++;
                }
            }

            return Results.Ok(new { imported, skipped, errors });
        });

        app.MapGet("/api/oui", () =>
        {
            var entries = db.GetAllOuiEntries();
            return Results.Ok(entries);
        });

        app.MapPost("/api/oui", (HttpContext ctx, OuiCrudRequest req) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            if (string.IsNullOrWhiteSpace(req.OuiPrefix))
                return Results.BadRequest(new ErrorResponse("Bad request", "OuiPrefix is required."));

            var entry = new Models.OuiEntry
            {
                OUIPrefix     = req.OuiPrefix.Trim(),
                SeriesLabel   = req.SeriesLabel?.Trim() ?? string.Empty,
                SeriesPattern = req.SeriesPattern?.Trim() ?? string.Empty,
                Notes         = req.Notes?.Trim() ?? string.Empty,
            };
            db.InsertOuiEntry(entry);
            return Results.Ok(new SuccessResponse(true, $"OUI entry '{entry.OUIPrefix}' created."));
        });

        app.MapPut("/api/oui/{id}", (HttpContext ctx, int id, OuiCrudRequest req) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            if (string.IsNullOrWhiteSpace(req.OuiPrefix))
                return Results.BadRequest(new ErrorResponse("Bad request", "OuiPrefix is required."));

            var entry = new Models.OuiEntry
            {
                Id            = id,
                OUIPrefix     = req.OuiPrefix.Trim(),
                SeriesLabel   = req.SeriesLabel?.Trim() ?? string.Empty,
                SeriesPattern = req.SeriesPattern?.Trim() ?? string.Empty,
                Notes         = req.Notes?.Trim() ?? string.Empty,
            };
            db.UpdateOuiEntry(entry);
            return Results.Ok(new SuccessResponse(true, $"OUI entry '{entry.OUIPrefix}' updated."));
        });

        app.MapDelete("/api/oui/{id}", (HttpContext ctx, int id) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            db.DeleteOuiEntry(id);
            return Results.Ok(new SuccessResponse(true, "OUI entry deleted."));
        });

        // ══════════════════════════════════════════════════════════════════════
        //  SETTINGS
        // ══════════════════════════════════════════════════════════════════════

        app.MapGet("/api/settings/{key}", (string key) =>
        {
            var value = serverDb.GetConfig(key);
            return Results.Ok(new { key, value });
        });

        app.MapPut("/api/settings/{key}", (HttpContext ctx, string key, SetConfigRequest req) =>
        {
            var denied = RequireAdmin(ctx);
            if (denied != null) return denied;

            serverDb.SetConfig(key, req.Value);
            return Results.Ok(new SuccessResponse(true, $"Config '{key}' updated."));
        });

        app.MapPost("/api/settings/logo", async (HttpContext ctx, HttpRequest httpReq) =>
        {
            var denied = RequireAdmin(ctx);
            if (denied != null) return denied;

            if (!httpReq.HasFormContentType)
                return Results.BadRequest(new ErrorResponse("Bad request", "Expected multipart/form-data."));

            var form = await httpReq.ReadFormAsync();
            var file = form.Files.GetFile("logo") ?? form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file == null || file.Length == 0)
                return Results.BadRequest(new ErrorResponse("Bad request", "No file uploaded."));

            var imgDir = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "img");
            Directory.CreateDirectory(imgDir);
            var filePath = Path.Combine(imgDir, "logo.png");

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            return Results.Ok(new SuccessResponse(true, "Logo uploaded."));
        });

        // ══════════════════════════════════════════════════════════════════════
        //  STATUS
        // ══════════════════════════════════════════════════════════════════════

        app.MapGet("/api/status", async () =>
        {
            var devices = db.GetAllDevices();
            var statusList = new List<DeviceStatusDto>();

            foreach (var device in devices)
            {
                bool isConnected = connMgr.IsConnected(device.Id);
                string? powerState = null;
                string? source = null;
                string? volume = null;

                if (isConnected)
                {
                    string? series = db.GetSeriesForModel(device.ModelNumber);
                    if (!string.IsNullOrEmpty(series))
                    {
                        var commands = db.GetCommandsBySeries(series);

                        // Query Power State
                        var powerCmd = commands.FirstOrDefault(c =>
                            string.Equals(c.CommandName, "Get Power State", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c.CommandName, "Power Query", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c.CommandName, "Get Power", StringComparison.OrdinalIgnoreCase));

                        if (powerCmd != null)
                        {
                            byte[] bytes = powerCmd.GetBytes();
                            if (bytes.Length > 0)
                            {
                                var response = await connMgr.SendAsync(device.Id, bytes);
                                if (response != null)
                                    powerState = ResponseParser.Parse(response, bytes, powerCmd.CommandFormat, series);
                            }
                        }

                        // Query Source
                        var sourceCmd = commands.FirstOrDefault(c =>
                            string.Equals(c.CommandName, "Get Source", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c.CommandName, "Source Query", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c.CommandName, "Get Input", StringComparison.OrdinalIgnoreCase));

                        if (sourceCmd != null)
                        {
                            byte[] bytes = sourceCmd.GetBytes();
                            if (bytes.Length > 0)
                            {
                                var response = await connMgr.SendAsync(device.Id, bytes);
                                if (response != null)
                                    source = ResponseParser.Parse(response, bytes, sourceCmd.CommandFormat, series);
                            }
                        }

                        // Query Volume
                        var volCmd = commands.FirstOrDefault(c =>
                            string.Equals(c.CommandName, "Get Volume", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c.CommandName, "Volume Query", StringComparison.OrdinalIgnoreCase));

                        if (volCmd != null)
                        {
                            byte[] bytes = volCmd.GetBytes();
                            if (bytes.Length > 0)
                            {
                                var response = await connMgr.SendAsync(device.Id, bytes);
                                if (response != null)
                                    volume = ResponseParser.Parse(response, bytes, volCmd.CommandFormat, series);
                            }
                        }
                    }
                }

                statusList.Add(new DeviceStatusDto(
                    device.Id, device.DeviceName, isConnected, powerState, source, volume));
            }

            return Results.Ok(statusList);
        });

        // ══════════════════════════════════════════════════════════════════════
        //  USERS (admin only)
        // ══════════════════════════════════════════════════════════════════════

        app.MapGet("/api/users", (HttpContext ctx) =>
        {
            var denied = RequireAdmin(ctx);
            if (denied != null) return denied;

            var users = serverDb.GetAllUsers();
            var dtos = users.Select(u => new UserDto(u.Id, u.Username, u.Role, u.CreatedAt, u.LastLogin)).ToList();
            return Results.Ok(dtos);
        });

        app.MapPost("/api/users", (HttpContext ctx, CreateUserRequest req) =>
        {
            var denied = RequireAdmin(ctx);
            if (denied != null) return denied;

            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new ErrorResponse("Bad request", "Username and password are required."));

            var user = serverDb.CreateUser(req.Username, req.Password, req.Role);
            if (user == null)
                return Results.Json(new ErrorResponse("Conflict", $"Username '{req.Username}' already exists."), statusCode: 409);

            return Results.Ok(new UserDto(user.Id, user.Username, user.Role, user.CreatedAt, user.LastLogin));
        });

        app.MapDelete("/api/users/{id}", (HttpContext ctx, int id) =>
        {
            var denied = RequireAdmin(ctx);
            if (denied != null) return denied;

            bool ok = serverDb.DeleteUser(id);
            if (!ok)
                return Results.NotFound(new ErrorResponse("Not found", "User not found or cannot be deleted."));
            return Results.Ok(new SuccessResponse(true, "User deleted."));
        });

        // ══════════════════════════════════════════════════════════════════════
        //  API KEYS
        // ══════════════════════════════════════════════════════════════════════

        app.MapGet("/api/keys", (HttpContext ctx) =>
        {
            var denied = RequireAdmin(ctx);
            if (denied != null) return denied;

            var keys = serverDb.GetAllApiKeys();
            var dtos = keys.Select(k => new ApiKeyDto(k.Id, k.Label, k.KeyPrefix, k.CreatedAt, k.LastUsedAt)).ToList();
            return Results.Ok(dtos);
        });

        app.MapPost("/api/keys", (HttpContext ctx, CreateKeyRequest req) =>
        {
            var denied = RequireAdmin(ctx);
            if (denied != null) return denied;

            if (string.IsNullOrWhiteSpace(req.Label))
                return Results.BadRequest(new ErrorResponse("Bad request", "Label is required."));

            var (rawKey, info) = serverDb.CreateApiKey(req.Label);
            return Results.Ok(new ApiKeyCreatedDto(info.Id, info.Label, rawKey, info.CreatedAt));
        });

        app.MapDelete("/api/keys/{id}", (HttpContext ctx, int id) =>
        {
            var denied = RequireAdmin(ctx);
            if (denied != null) return denied;

            bool ok = serverDb.DeleteApiKey(id);
            if (!ok)
                return Results.NotFound(new ErrorResponse("Not found", "API key not found."));
            return Results.Ok(new SuccessResponse(true, "API key deleted."));
        });

        // ══════════════════════════════════════════════════════════════════════
        //  AUDIT LOG
        // ══════════════════════════════════════════════════════════════════════

        app.MapGet("/api/logs", (string? device, string? from, string? to, bool? success, string? search, int? limit, int? offset) =>
        {
            var allLogs = db.GetCommandLog(50000);

            IEnumerable<AuditLogEntry> filtered = allLogs;

            if (!string.IsNullOrWhiteSpace(device))
                filtered = filtered.Where(e => e.DeviceName.Contains(device, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(from) && DateTime.TryParse(from, out var fromDt))
                filtered = filtered.Where(e => DateTime.TryParse(e.Timestamp, out var ts) && ts >= fromDt);

            if (!string.IsNullOrWhiteSpace(to) && DateTime.TryParse(to, out var toDt))
                filtered = filtered.Where(e => DateTime.TryParse(e.Timestamp, out var ts) && ts < toDt.AddDays(1));

            if (success.HasValue)
                filtered = filtered.Where(e => e.Success == success.Value);

            if (!string.IsNullOrWhiteSpace(search))
                filtered = filtered.Where(e =>
                    e.CommandName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    e.Response.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    e.Notes.Contains(search, StringComparison.OrdinalIgnoreCase));

            var materialized = filtered.ToList();
            int total = materialized.Count;

            int skip = offset ?? 0;
            int take = limit ?? 100;
            var page = materialized.Skip(skip).Take(take);

            var items = page.Select(e => new AuditLogDto(
                e.Timestamp, e.DeviceName, e.DeviceAddress,
                e.CommandName, e.CommandCode, e.Response,
                e.Success, e.Notes)).ToList();

            return Results.Ok(new AuditLogPagedResponse(total, items));
        });

        app.MapDelete("/api/logs", (HttpContext ctx) =>
        {
            var denied = RequireAdmin(ctx);
            if (denied != null) return denied;

            db.ClearCommandLog();
            return Results.Ok(new SuccessResponse(true, "Audit log cleared."));
        });

        // ══════════════════════════════════════════════════════════════════════
        //  FIRMWARE
        // ══════════════════════════════════════════════════════════════════════

        app.MapGet("/api/devices/{id}/firmware", async (int id) =>
        {
            var device = db.GetAllDevices().FirstOrDefault(d => d.Id == id);
            if (device == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Device {id} not found."));

            if (!connMgr.IsConnected(id))
                return Results.Ok(new FirmwareResponse(id, null, "Not connected"));

            string? fw = await FirmwareService.QueryFirmwareVersionAsync(connMgr, db, id);
            return Results.Ok(new FirmwareResponse(id, fw, fw == null ? "No version command available for this series" : null));
        });

        // ══════════════════════════════════════════════════════════════════════
        //  NETWORK SCAN
        // ══════════════════════════════════════════════════════════════════════

        app.MapPost("/api/scan", (HttpContext ctx, ScanRequest req) =>
        {
            var denied = RequireOperatorOrAdmin(ctx);
            if (denied != null) return denied;

            if (string.IsNullOrWhiteSpace(req.StartIp) || string.IsNullOrWhiteSpace(req.EndIp))
                return Results.BadRequest(new ErrorResponse("Bad request", "startIp and endIp are required."));

            string scanId = Guid.NewGuid().ToString("N")[..12];
            var state = new ScanState();
            _scans[scanId] = state;

            var ouiMap = db.GetOuiSeriesMap();
            var models = db.GetAllModels();
            var modelSeriesMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in models)
            {
                if (!modelSeriesMap.ContainsKey(m.ModelNumber))
                    modelSeriesMap[m.ModelNumber] = m.SeriesPattern;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var progress = new Progress<(int done, int total)>(p =>
                    {
                        state.Progress = p;
                    });

                    await NetworkScanService.ScanAsync(
                        req.StartIp, req.EndIp, ouiMap, modelSeriesMap,
                        result =>
                        {
                            var dto = new ScanResultDto(
                                result.IpAddress, result.MacAddress,
                                result.SeriesPattern, result.ModelNumber,
                                result.Hostname, result.IsOnline);
                            lock (state.Results) state.Results.Add(dto);

                            _ = wsHub.BroadcastAsync(new WsEvent("scan.result", new
                            {
                                scanId,
                                result = dto,
                            }));
                        },
                        progress,
                        CancellationToken.None);
                }
                catch { /* scan failed — mark complete with partial results */ }
                finally
                {
                    state.Status = "complete";
                }
            });

            return Results.Ok(new ScanStartResponse(scanId));
        });

        app.MapGet("/api/scan/{scanId}", (string scanId) =>
        {
            if (!_scans.TryGetValue(scanId, out var state))
                return Results.NotFound(new ErrorResponse("Not found", $"Scan '{scanId}' not found."));

            List<ScanResultDto> results;
            lock (state.Results) results = state.Results.ToList();

            var progress = new ScanProgressDto(state.Progress.done, state.Progress.total);
            return Results.Ok(new ScanStatusResponse(state.Status, results, progress));
        });
    }

    // ── CSV helpers ─────────────────────────────────────────────────────────

    private static string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        int i = 0;
        while (i <= line.Length)
        {
            if (i == line.Length) { fields.Add(""); break; }

            if (line[i] == '"')
            {
                // Quoted field
                var sb = new System.Text.StringBuilder();
                i++; // skip opening quote
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            sb.Append('"');
                            i += 2;
                        }
                        else
                        {
                            i++; // skip closing quote
                            break;
                        }
                    }
                    else
                    {
                        sb.Append(line[i]);
                        i++;
                    }
                }
                fields.Add(sb.ToString());
                // skip comma after closing quote
                if (i < line.Length && line[i] == ',') i++;
            }
            else
            {
                // Unquoted field
                int start = i;
                while (i < line.Length && line[i] != ',') i++;
                fields.Add(line[start..i]);
                if (i < line.Length) i++; // skip comma
            }
        }
        return fields;
    }
}
