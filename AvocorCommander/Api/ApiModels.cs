namespace AvocorCommander.Api;

// ── Auth ─────────────────────────────────────────────────────────────────────
public record LoginRequest(string Username, string Password);
public record LoginResponse(string Token, string Username, string Role);

// ── Devices ──────────────────────────────────────────────────────────────────
public record DeviceDto(
    int    Id,
    string DeviceName,
    string ModelNumber,
    string IPAddress,
    int    Port,
    int    BaudRate,
    string ComPort,
    string MacAddress,
    string ConnectionType,
    string Notes,
    string LastSeenAt,
    bool   AutoConnect,
    bool   IsConnected,
    string? Series);

public record DeviceDetailDto(
    int    Id,
    string DeviceName,
    string ModelNumber,
    string IPAddress,
    int    Port,
    int    BaudRate,
    string ComPort,
    string MacAddress,
    string ConnectionType,
    string Notes,
    string LastSeenAt,
    bool   AutoConnect,
    bool   IsConnected,
    string? Series,
    List<AvocorCommander.Models.CommandEntry> DeviceCommands);

public record CommandRequest(string? Category, string Command);

public record CommandResponse(bool Success, string Response, string Hex, string? Error = null);

// ── Groups ───────────────────────────────────────────────────────────────────
public record GroupDto(int Id, string GroupName, string Notes, List<int> MemberDeviceIds);

public record GroupCommandRequest(string Command);

public record GroupCommandResult(int DeviceId, string DeviceName, bool Success, string Response);

// ── Macros ───────────────────────────────────────────────────────────────────
public record MacroDto(int Id, string MacroName, string Notes, int StepCount);

public record MacroRunRequest(int? DeviceId, int? GroupId);

// ── Status ───────────────────────────────────────────────────────────────────
public record DeviceStatusDto(
    int    DeviceId,
    string DeviceName,
    bool   IsConnected,
    string? PowerState,
    string? Source,
    string? Volume);

// ── Users ────────────────────────────────────────────────────────────────────
public record CreateUserRequest(string Username, string Password, string Role = "Operator");

public record UserDto(int Id, string Username, string Role, string CreatedAt, string LastLogin);

// ── API Keys ─────────────────────────────────────────────────────────────────
public record CreateKeyRequest(string Label);

public record ApiKeyDto(int Id, string Label, string KeyPrefix, string CreatedAt, string LastUsedAt);

public record ApiKeyCreatedDto(int Id, string Label, string Key, string CreatedAt);

// ── Wake ─────────────────────────────────────────────────────────────────────
public record WakeResponse(bool MagicPacketSent, bool PowerOnSent, bool PowerOnAckd, string Detail);

// ── Generic ──────────────────────────────────────────────────────────────────
public record ErrorResponse(string Error, string? Message = null);

public record SuccessResponse(bool Success, string? Message = null);

// ── Device CRUD ─────────────────────────────────────────────────────────────
public record AddDeviceRequest(string DeviceName, string ModelNumber, string IpAddress, int Port, int BaudRate, string ComPort, string MacAddress, string ConnectionType, string Notes, bool AutoConnect);
public record UpdateDeviceRequest(string DeviceName, string ModelNumber, string IpAddress, int Port, int BaudRate, string ComPort, string MacAddress, string ConnectionType, string Notes, bool AutoConnect);

// ── Group CRUD ──────────────────────────────────────────────────────────────
public record CreateGroupRequest(string GroupName, string Notes);
public record UpdateGroupRequest(string GroupName, string Notes);
public record SetGroupMembersRequest(List<int> DeviceIds);

// ── Macro CRUD ──────────────────────────────────────────────────────────────
public record CreateMacroRequest(string MacroName, string Notes, List<MacroStepRequest> Steps);
public record MacroStepRequest(int CommandId, int DelayAfterMs, string StepType, string PromptText);

// ── Schedule CRUD ───────────────────────────────────────────────────────────
public record CreateScheduleRequest(string RuleName, int? DeviceId, int? GroupId, int CommandId, string ScheduleTime, string Recurrence, bool IsEnabled, string Notes);

// ── Settings ────────────────────────────────────────────────────────────────
public record SetConfigRequest(string Value);
public record PromptResponse(bool Continue);

// ── Audit Logs ──────────────────────────────────────────────────────────────
public record AuditLogDto(string Timestamp, string DeviceName, string DeviceAddress, string CommandName, string CommandCode, string Response, bool Success, string Notes);

public record AuditLogPagedResponse(int Total, List<AuditLogDto> Items);

// ── Firmware ────────────────────────────────────────────────────────────────
public record FirmwareResponse(int DeviceId, string? Firmware, string? Error = null);

// ── Scan ────────────────────────────────────────────────────────────────────
public record ScanRequest(string StartIp, string EndIp);

public record ScanStartResponse(string ScanId);

public record ScanProgressDto(int Done, int Total);

public record ScanResultDto(string Ip, string Mac, string Series, string Model, string Hostname, bool Online);

public record ScanStatusResponse(string Status, List<ScanResultDto> Results, ScanProgressDto Progress);

// ── Command / Model CRUD ────────────────────────────────────────────────────
public record CommandCrudRequest(string SeriesPattern, string? CommandCategory, string CommandName, string? CommandCode, string? Notes, int Port, string? CommandFormat);
public record ModelCrudRequest(string ModelNumber, string SeriesPattern, int BaudRate);

// ── OUI CRUD ────────────────────────────────────────────────────────────────
public record OuiCrudRequest(string OuiPrefix, string SeriesLabel, string SeriesPattern, string Notes);

// ── WebSocket events ─────────────────────────────────────────────────────────
public record WsEvent(string Type, object Data);
