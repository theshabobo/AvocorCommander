using AvocorCommander.Core;
using AvocorCommander.Models;
using AvocorCommander.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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

            var dto = new DeviceDto(
                device.Id, device.DeviceName, device.ModelNumber, device.IPAddress,
                device.Port, device.BaudRate, device.ComPort, device.MacAddress,
                device.ConnectionType, device.Notes, device.LastSeenAt, device.AutoConnect,
                connMgr.IsConnected(device.Id),
                db.GetSeriesForModel(device.ModelNumber));
            return Results.Ok(dto);
        });

        app.MapPost("/api/devices/{id}/wake", async (int id) =>
        {
            var device = db.GetAllDevices().FirstOrDefault(d => d.Id == id);
            if (device == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Device {id} not found."));

            var result = await DeviceWakeService.WakeAsync(device, db);
            return Results.Ok(new WakeResponse(result.MagicPacketSent, result.PowerOnSent, result.PowerOnAckd, result.Detail));
        });

        app.MapPost("/api/devices/{id}/connect", async (int id) =>
        {
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

        app.MapPost("/api/devices/{id}/disconnect", async (int id) =>
        {
            var device = db.GetAllDevices().FirstOrDefault(d => d.Id == id);
            if (device == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Device {id} not found."));

            if (!connMgr.IsConnected(device.Id))
                return Results.Ok(new SuccessResponse(true, "Already disconnected."));

            await connMgr.DisconnectAsync(device);
            return Results.Ok(new SuccessResponse(true, $"Disconnected from {device.DeviceName}."));
        });

        app.MapPost("/api/devices/{id}/command", async (int id, CommandRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Command))
                return Results.BadRequest(new ErrorResponse("Bad request", "Command is required."));

            var device = db.GetAllDevices().FirstOrDefault(d => d.Id == id);
            if (device == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Device {id} not found."));

            string? series = db.GetSeriesForModel(device.ModelNumber);
            if (string.IsNullOrEmpty(series))
                return Results.Json(new ErrorResponse("No series", $"Cannot resolve series for model '{device.ModelNumber}'."), statusCode: 422);

            CommandEntry? cmd;
            if (!string.IsNullOrWhiteSpace(req.Category))
            {
                var commands = db.GetCommandsBySeriesAndCategory(series, req.Category);
                cmd = commands.FirstOrDefault(c =>
                    string.Equals(c.CommandName, req.Command, StringComparison.OrdinalIgnoreCase));
                if (cmd == null)
                    return Results.NotFound(new ErrorResponse("Command not found",
                        $"No command '{req.Command}' in category '{req.Category}' for series '{series}'."));
            }
            else
            {
                var commands = db.GetCommandsBySeries(series);
                cmd = commands.FirstOrDefault(c =>
                    string.Equals(c.CommandName, req.Command, StringComparison.OrdinalIgnoreCase));
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

        app.MapPost("/api/devices", (AddDeviceRequest req) =>
        {
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

        app.MapPut("/api/devices/{id}", (int id, UpdateDeviceRequest req) =>
        {
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

        app.MapDelete("/api/devices/{id}", (int id) =>
        {
            var existing = db.GetAllDevices().FirstOrDefault(d => d.Id == id);
            if (existing == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Device {id} not found."));

            db.DeleteDevice(id);
            return Results.Ok(new SuccessResponse(true, "Device deleted."));
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

        app.MapPost("/api/groups", (CreateGroupRequest req) =>
        {
            var g = new GroupEntry { GroupName = req.GroupName, Notes = req.Notes };
            int id = db.InsertGroup(g);
            g.Id = id;
            return Results.Ok(new GroupDto(g.Id, g.GroupName, g.Notes, g.MemberDeviceIds.ToList()));
        });

        app.MapPut("/api/groups/{id}", (int id, UpdateGroupRequest req) =>
        {
            var existing = db.GetAllGroups().FirstOrDefault(g => g.Id == id);
            if (existing == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Group {id} not found."));

            existing.GroupName = req.GroupName;
            existing.Notes     = req.Notes;
            db.UpdateGroup(existing);
            return Results.Ok(new GroupDto(existing.Id, existing.GroupName, existing.Notes, existing.MemberDeviceIds.ToList()));
        });

        app.MapDelete("/api/groups/{id}", (int id) =>
        {
            var existing = db.GetAllGroups().FirstOrDefault(g => g.Id == id);
            if (existing == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Group {id} not found."));

            db.DeleteGroup(id);
            return Results.Ok(new SuccessResponse(true, "Group deleted."));
        });

        app.MapPut("/api/groups/{id}/members", (int id, SetGroupMembersRequest req) =>
        {
            var existing = db.GetAllGroups().FirstOrDefault(g => g.Id == id);
            if (existing == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Group {id} not found."));

            db.SetGroupMembers(id, req.DeviceIds);
            return Results.Ok(new SuccessResponse(true, $"Group members updated ({req.DeviceIds.Count} devices)."));
        });

        app.MapPost("/api/groups/{id}/command", async (int id, GroupCommandRequest req) =>
        {
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

        app.MapPost("/api/macros/{id}/run", async (int id, MacroRunRequest req) =>
        {
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

        app.MapPost("/api/macros", (CreateMacroRequest req) =>
        {
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

        app.MapPut("/api/macros/{id}", (int id, CreateMacroRequest req) =>
        {
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

        app.MapDelete("/api/macros/{id}", (int id) =>
        {
            var existing = db.GetAllMacros().FirstOrDefault(m => m.Id == id);
            if (existing == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Macro {id} not found."));

            db.DeleteMacro(id);
            return Results.Ok(new SuccessResponse(true, "Macro deleted."));
        });

        // Prompt response — web client clicks Continue or Cancel on a macro prompt
        app.MapPost("/api/macros/prompt/{promptId}", (string promptId, PromptResponse req) =>
        {
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

        app.MapPost("/api/schedules", (CreateScheduleRequest req) =>
        {
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

        app.MapPut("/api/schedules/{id}", (int id, CreateScheduleRequest req) =>
        {
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

        app.MapDelete("/api/schedules/{id}", (int id) =>
        {
            var existing = db.GetAllScheduleRules().FirstOrDefault(r => r.Id == id);
            if (existing == null)
                return Results.NotFound(new ErrorResponse("Not found", $"Schedule rule {id} not found."));

            db.DeleteScheduleRule(id);
            return Results.Ok(new SuccessResponse(true, "Schedule rule deleted."));
        });

        app.MapPost("/api/schedules/{id}/run", async (int id) =>
        {
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

        app.MapGet("/api/oui", () =>
        {
            var entries = db.GetAllOuiEntries();
            return Results.Ok(entries);
        });

        // ══════════════════════════════════════════════════════════════════════
        //  SETTINGS
        // ══════════════════════════════════════════════════════════════════════

        app.MapGet("/api/settings/{key}", (string key) =>
        {
            var value = serverDb.GetConfig(key);
            return Results.Ok(new { key, value });
        });

        app.MapPut("/api/settings/{key}", (string key, SetConfigRequest req) =>
        {
            serverDb.SetConfig(key, req.Value);
            return Results.Ok(new SuccessResponse(true, $"Config '{key}' updated."));
        });

        app.MapPost("/api/settings/logo", async (HttpRequest httpReq) =>
        {
            if (!httpReq.HasFormContentType)
                return Results.BadRequest(new ErrorResponse("Bad request", "Expected multipart/form-data."));

            var form = await httpReq.ReadFormAsync();
            var file = form.Files.GetFile("logo") ?? form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file == null || file.Length == 0)
                return Results.BadRequest(new ErrorResponse("Bad request", "No file uploaded."));

            var imgDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "img");
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
    }
}
