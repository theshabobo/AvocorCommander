using AvocorCommander.Models;
using Microsoft.Data.Sqlite;
using System.IO;

namespace AvocorCommander.Services;

/// <summary>
/// Central data access layer — wraps all SQLite operations for AvocorCommander.db.
/// </summary>
public sealed class DatabaseService : IDisposable
{
    private readonly string _dbPath;
    private readonly object _lock = new();

    public DatabaseService()
    {
        _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AvocorCommander.db");
        EnsureSchema();
        ApplyCommandDbUpdate();
    }

    // ── Connection factory ────────────────────────────────────────────────────

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    // ── Schema bootstrap ──────────────────────────────────────────────────────

    /// <summary>
    /// If a pending db update file exists (placed by the update batch script),
    /// merges DeviceList and Models from it into the live database, then deletes it.
    /// User data tables (Groups, ScheduleRules, Macros, etc.) are untouched.
    /// </summary>
    private void ApplyCommandDbUpdate()
    {
        var updatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AvocorCommander_update.db");
        if (!File.Exists(updatePath)) return;

        try
        {
            using var conn = OpenConnection();
            using var cmd  = conn.CreateCommand();

            // Attach the update db and replace DeviceList + Models atomically
            cmd.CommandText = $"ATTACH DATABASE '{updatePath.Replace("'", "''")}' AS upd;";
            cmd.ExecuteNonQuery();

            cmd.CommandText = @"
                BEGIN;
                DELETE FROM DeviceList;
                INSERT INTO DeviceList SELECT * FROM upd.DeviceList;
                DELETE FROM Models;
                INSERT INTO Models SELECT * FROM upd.Models;
                COMMIT;
                DETACH DATABASE upd;";
            cmd.ExecuteNonQuery();
        }
        catch { /* swallow — stale/corrupt update file */ }
        finally
        {
            try { File.Delete(updatePath); } catch { }
        }
    }

    private void EnsureSchema()
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();

        // Migrate StoredDevices — add columns that may not exist in older DB files
        foreach (var (col, def) in new[]
        {
            ("ComPort",        "TEXT    DEFAULT ''"),
            ("MacAddress",     "TEXT    DEFAULT ''"),
            ("ConnectionType", "TEXT    DEFAULT 'TCP'"),
            ("LastSeenAt",     "TEXT    DEFAULT ''"),
            ("AutoConnect",    "INTEGER DEFAULT 0"),
        })
        {
            try
            {
                cmd.CommandText = $"ALTER TABLE StoredDevices ADD COLUMN {col} {def};";
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException) { /* column already exists — safe to ignore */ }
        }

        // Migrate CommandLog — add Response column if it doesn't exist
        try
        {
            cmd.CommandText = "ALTER TABLE CommandLog ADD COLUMN Response TEXT DEFAULT '';";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException) { /* column already exists */ }

        // B-Series uses ASCII protocol — fix any rows that were inserted with the default HEX format
        cmd.CommandText = "UPDATE DeviceList SET CommandFormat='ASCII' WHERE SeriesPattern='B-Series' AND CommandFormat='HEX';";
        cmd.ExecuteNonQuery();

        // B-Series IR commands: correct codes use short form (!IR Up, not !IR Cursor Up)
        foreach (var (wrong, correct) in new[]
        {
            ("!IR Cursor Up",    "!IR Up"),
            ("!IR Cursor Down",  "!IR Down"),
            ("!IR Cursor Left",  "!IR Left"),
            ("!IR Cursor Right", "!IR Right"),
        })
        {
            cmd.CommandText = "UPDATE DeviceList SET CommandCode=$correct WHERE SeriesPattern='B-Series' AND CommandCode=$wrong;";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$correct", correct);
            cmd.Parameters.AddWithValue("$wrong",   wrong);
            cmd.ExecuteNonQuery();
        }

        // Migrate ScheduleRules — add history columns
        foreach (var (col, def) in new[]
        {
            ("LastFiredAt", "TEXT DEFAULT ''"),
            ("LastResult",  "TEXT DEFAULT ''"),
        })
        {
            try
            {
                cmd.CommandText = $"ALTER TABLE ScheduleRules ADD COLUMN {col} {def};";
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException) { }
        }

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Groups (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                GroupName TEXT    NOT NULL UNIQUE,
                Notes     TEXT    DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS GroupMembers (
                id       INTEGER PRIMARY KEY AUTOINCREMENT,
                GroupId  INTEGER NOT NULL,
                DeviceId INTEGER NOT NULL,
                FOREIGN KEY(GroupId)  REFERENCES Groups(id)        ON DELETE CASCADE,
                FOREIGN KEY(DeviceId) REFERENCES StoredDevices(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS ScheduleRules (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                RuleName     TEXT    NOT NULL,
                DeviceId     INTEGER,
                GroupId      INTEGER,
                CommandId    INTEGER NOT NULL,
                ScheduleTime TEXT    NOT NULL,
                Recurrence   TEXT    NOT NULL DEFAULT 'Daily',
                IsEnabled    INTEGER NOT NULL DEFAULT 1,
                Notes        TEXT    DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS CommandLog (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp     TEXT    NOT NULL,
                DeviceName    TEXT    DEFAULT '',
                DeviceAddress TEXT    DEFAULT '',
                CommandName   TEXT    DEFAULT '',
                CommandCode   TEXT    DEFAULT '',
                Response      TEXT    DEFAULT '',
                Success       INTEGER NOT NULL DEFAULT 1,
                Notes         TEXT    DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS Macros (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                MacroName TEXT    NOT NULL,
                Notes     TEXT    DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS MacroSteps (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                MacroId      INTEGER NOT NULL,
                StepOrder    INTEGER NOT NULL DEFAULT 0,
                CommandId    INTEGER NOT NULL,
                DelayAfterMs INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY(MacroId) REFERENCES Macros(id) ON DELETE CASCADE
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  STORED DEVICES
    // ══════════════════════════════════════════════════════════════════════════

    public List<DeviceEntry> GetAllDevices()
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, DeviceName, ModelNumber, IPAddress, Port,
                   BaudRate, Notes,
                   COALESCE(ComPort,'')           AS ComPort,
                   COALESCE(MacAddress,'')        AS MacAddress,
                   COALESCE(ConnectionType,'TCP') AS ConnectionType,
                   COALESCE(LastSeenAt,'')        AS LastSeenAt,
                   COALESCE(AutoConnect,0)        AS AutoConnect
            FROM   StoredDevices
            ORDER  BY DeviceName;
            """;
        var list = new List<DeviceEntry>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            list.Add(new DeviceEntry
            {
                Id             = rdr.GetInt32(0),
                DeviceName     = rdr.GetString(1),
                ModelNumber    = rdr.IsDBNull(2) ? "" : rdr.GetString(2),
                IPAddress      = rdr.IsDBNull(3) ? "" : rdr.GetString(3),
                Port           = rdr.IsDBNull(4) ? 0  : rdr.GetInt32(4),
                BaudRate       = rdr.IsDBNull(5) ? 9600 : rdr.GetInt32(5),
                Notes          = rdr.IsDBNull(6) ? "" : rdr.GetString(6),
                ComPort        = rdr.GetString(7),
                MacAddress     = rdr.GetString(8),
                ConnectionType = rdr.GetString(9),
                LastSeenAt     = rdr.GetString(10),
                AutoConnect    = rdr.GetInt32(11) == 1,
            });
        }
        return list;
    }

    public int InsertDevice(DeviceEntry d)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO StoredDevices
                (DeviceName, ModelNumber, IPAddress, Port, BaudRate,
                 Notes, ComPort, MacAddress, ConnectionType, AutoConnect)
            VALUES
                ($name, $model, $ip, $port, $baud,
                 $notes, $com, $mac, $ctype, $autoconnect);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$name",  d.DeviceName);
        cmd.Parameters.AddWithValue("$model", d.ModelNumber);
        cmd.Parameters.AddWithValue("$ip",    d.IPAddress);
        cmd.Parameters.AddWithValue("$port",  d.Port);
        cmd.Parameters.AddWithValue("$baud",  d.BaudRate);
        cmd.Parameters.AddWithValue("$notes", d.Notes);
        cmd.Parameters.AddWithValue("$com",         d.ComPort);
        cmd.Parameters.AddWithValue("$mac",         d.MacAddress);
        cmd.Parameters.AddWithValue("$ctype",       d.ConnectionType);
        cmd.Parameters.AddWithValue("$autoconnect", d.AutoConnect ? 1 : 0);
        return System.Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void UpdateDevice(DeviceEntry d)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE StoredDevices
            SET    DeviceName=$name, ModelNumber=$model, IPAddress=$ip,
                   Port=$port, BaudRate=$baud, Notes=$notes,
                   ComPort=$com, MacAddress=$mac, ConnectionType=$ctype,
                   AutoConnect=$autoconnect
            WHERE  id=$id;
            """;
        cmd.Parameters.AddWithValue("$id",    d.Id);
        cmd.Parameters.AddWithValue("$name",  d.DeviceName);
        cmd.Parameters.AddWithValue("$model", d.ModelNumber);
        cmd.Parameters.AddWithValue("$ip",    d.IPAddress);
        cmd.Parameters.AddWithValue("$port",  d.Port);
        cmd.Parameters.AddWithValue("$baud",  d.BaudRate);
        cmd.Parameters.AddWithValue("$notes", d.Notes);
        cmd.Parameters.AddWithValue("$com",         d.ComPort);
        cmd.Parameters.AddWithValue("$mac",         d.MacAddress);
        cmd.Parameters.AddWithValue("$ctype",       d.ConnectionType);
        cmd.Parameters.AddWithValue("$autoconnect", d.AutoConnect ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void DeleteDevice(int id)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM StoredDevices WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public List<DeviceEntry> GetAutoConnectDevices() =>
        GetAllDevices().Where(d => d.AutoConnect).ToList();

    // ══════════════════════════════════════════════════════════════════════════
    //  COMMANDS (DeviceList)
    // ══════════════════════════════════════════════════════════════════════════

    public List<CommandEntry> GetCommandsBySeries(string seriesPattern)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, SeriesPattern, CommandCategory, CommandName,
                   CommandCode, COALESCE(Notes,''), Port, CommandFormat
            FROM   DeviceList
            WHERE  SeriesPattern = $series
            ORDER  BY CommandCategory, CommandName;
            """;
        cmd.Parameters.AddWithValue("$series", seriesPattern);
        return ReadCommands(cmd);
    }

    public List<CommandEntry> GetAllCommands()
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, SeriesPattern, CommandCategory, CommandName,
                   CommandCode, COALESCE(Notes,''), Port, CommandFormat
            FROM   DeviceList
            ORDER  BY SeriesPattern, CommandCategory, CommandName;
            """;
        return ReadCommands(cmd);
    }

    public void InsertCommand(CommandEntry c)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO DeviceList
                (SeriesPattern, CommandCategory, CommandName, CommandCode, Notes, Port, CommandFormat)
            VALUES ($series, $cat, $name, $code, $notes, $port, $fmt);
            """;
        BindCommand(cmd, c);
        cmd.ExecuteNonQuery();
    }

    public void UpdateCommand(CommandEntry c)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE DeviceList
            SET SeriesPattern=$series, CommandCategory=$cat, CommandName=$name,
                CommandCode=$code, Notes=$notes, Port=$port, CommandFormat=$fmt
            WHERE id=$id;
            """;
        cmd.Parameters.AddWithValue("$id", c.Id);
        BindCommand(cmd, c);
        cmd.ExecuteNonQuery();
    }

    public void DeleteCommand(int id)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM DeviceList WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static List<CommandEntry> ReadCommands(SqliteCommand cmd)
    {
        var list = new List<CommandEntry>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            list.Add(new CommandEntry
            {
                Id              = rdr.GetInt32(0),
                SeriesPattern   = rdr.GetString(1),
                CommandCategory = rdr.GetString(2),
                CommandName     = rdr.GetString(3),
                CommandCode     = rdr.GetString(4),
                Notes           = rdr.GetString(5),
                Port            = rdr.GetInt32(6),
                CommandFormat   = rdr.GetString(7),
            });
        }
        return list;
    }

    private static void BindCommand(SqliteCommand cmd, CommandEntry c)
    {
        cmd.Parameters.AddWithValue("$series", c.SeriesPattern);
        cmd.Parameters.AddWithValue("$cat",    c.CommandCategory);
        cmd.Parameters.AddWithValue("$name",   c.CommandName);
        cmd.Parameters.AddWithValue("$code",   c.CommandCode);
        cmd.Parameters.AddWithValue("$notes",  c.Notes);
        cmd.Parameters.AddWithValue("$port",   c.Port);
        cmd.Parameters.AddWithValue("$fmt",    c.CommandFormat);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  MODELS
    // ══════════════════════════════════════════════════════════════════════════

    public List<ModelEntry> GetAllModels()
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, ModelNumber, SeriesPattern
            FROM   Models
            ORDER  BY ModelNumber;
            """;
        var list = new List<ModelEntry>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            list.Add(new ModelEntry { Id = rdr.GetInt32(0), ModelNumber = rdr.GetString(1), SeriesPattern = rdr.GetString(2) });
        return list;
    }

    public string? GetSeriesForModel(string modelNumber)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT SeriesPattern FROM Models WHERE ModelNumber=$m LIMIT 1;";
        cmd.Parameters.AddWithValue("$m", modelNumber);
        var result = cmd.ExecuteScalar();
        return result as string;
    }

    public void InsertModel(ModelEntry m)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO Models (ModelNumber, SeriesPattern) VALUES ($m, $s);";
        cmd.Parameters.AddWithValue("$m", m.ModelNumber);
        cmd.Parameters.AddWithValue("$s", m.SeriesPattern);
        cmd.ExecuteNonQuery();
    }

    public void UpdateModel(ModelEntry m)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "UPDATE Models SET ModelNumber=$m, SeriesPattern=$s WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", m.Id);
        cmd.Parameters.AddWithValue("$m",  m.ModelNumber);
        cmd.Parameters.AddWithValue("$s",  m.SeriesPattern);
        cmd.ExecuteNonQuery();
    }

    public void DeleteModel(int id)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Models WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  OUI TABLE
    // ══════════════════════════════════════════════════════════════════════════

    public List<OuiEntry> GetAllOuiEntries()
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, OUIPrefix, SeriesLabel, SeriesPattern, COALESCE(Notes,'')
            FROM   OUITable
            ORDER  BY OUIPrefix;
            """;
        var list = new List<OuiEntry>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            list.Add(new OuiEntry
            {
                Id            = rdr.GetInt32(0),
                OUIPrefix     = rdr.GetString(1),
                SeriesLabel   = rdr.GetString(2),
                SeriesPattern = rdr.GetString(3),
                Notes         = rdr.GetString(4),
            });
        return list;
    }

    /// <summary>Lookup the series for a MAC address (first 8 chars = XX:XX:XX).</summary>
    public string? LookupSeriesByMac(string macAddress)
    {
        if (macAddress.Length < 8) return null;
        string prefix = macAddress[..8].ToUpperInvariant().Replace("-", ":");
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT SeriesPattern FROM OUITable WHERE OUIPrefix=$p LIMIT 1;";
        cmd.Parameters.AddWithValue("$p", prefix);
        return cmd.ExecuteScalar() as string;
    }

    public void InsertOuiEntry(OuiEntry e)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO OUITable (OUIPrefix, SeriesLabel, SeriesPattern, Notes) VALUES ($p,$l,$s,$n);";
        cmd.Parameters.AddWithValue("$p", e.OUIPrefix);
        cmd.Parameters.AddWithValue("$l", e.SeriesLabel);
        cmd.Parameters.AddWithValue("$s", e.SeriesPattern);
        cmd.Parameters.AddWithValue("$n", e.Notes);
        cmd.ExecuteNonQuery();
    }

    public void UpdateOuiEntry(OuiEntry e)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "UPDATE OUITable SET OUIPrefix=$p, SeriesLabel=$l, SeriesPattern=$s, Notes=$n WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", e.Id);
        cmd.Parameters.AddWithValue("$p",  e.OUIPrefix);
        cmd.Parameters.AddWithValue("$l",  e.SeriesLabel);
        cmd.Parameters.AddWithValue("$s",  e.SeriesPattern);
        cmd.Parameters.AddWithValue("$n",  e.Notes);
        cmd.ExecuteNonQuery();
    }

    public void DeleteOuiEntry(int id)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM OUITable WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  GROUPS
    // ══════════════════════════════════════════════════════════════════════════

    public List<GroupEntry> GetAllGroups()
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT id, GroupName, COALESCE(Notes,'') FROM Groups ORDER BY GroupName;";
        var list = new List<GroupEntry>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            list.Add(new GroupEntry { Id = rdr.GetInt32(0), GroupName = rdr.GetString(1), Notes = rdr.GetString(2) });

        // Load members
        foreach (var g in list)
        {
            using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = "SELECT DeviceId FROM GroupMembers WHERE GroupId=$gid;";
            cmd2.Parameters.AddWithValue("$gid", g.Id);
            using var rdr2 = cmd2.ExecuteReader();
            while (rdr2.Read())
                g.MemberDeviceIds.Add(rdr2.GetInt32(0));
        }
        return list;
    }

    public int InsertGroup(GroupEntry g)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Groups (GroupName, Notes) VALUES ($n,$notes); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n",     g.GroupName);
        cmd.Parameters.AddWithValue("$notes", g.Notes);
        return System.Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void UpdateGroup(GroupEntry g)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "UPDATE Groups SET GroupName=$n, Notes=$notes WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id",    g.Id);
        cmd.Parameters.AddWithValue("$n",     g.GroupName);
        cmd.Parameters.AddWithValue("$notes", g.Notes);
        cmd.ExecuteNonQuery();
    }

    public void DeleteGroup(int id)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Groups WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetGroupMembers(int groupId, IEnumerable<int> deviceIds)
    {
        using var conn = OpenConnection();
        using var tx   = conn.BeginTransaction();
        var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM GroupMembers WHERE GroupId=$gid;";
        del.Parameters.AddWithValue("$gid", groupId);
        del.ExecuteNonQuery();

        foreach (var did in deviceIds)
        {
            var ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO GroupMembers (GroupId, DeviceId) VALUES ($gid,$did);";
            ins.Parameters.AddWithValue("$gid", groupId);
            ins.Parameters.AddWithValue("$did", did);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  SCHEDULE RULES
    // ══════════════════════════════════════════════════════════════════════════

    public List<ScheduleRule> GetAllScheduleRules()
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.RuleName,
                   r.DeviceId, r.GroupId, r.CommandId,
                   r.ScheduleTime, r.Recurrence, r.IsEnabled,
                   COALESCE(r.Notes,''),
                   COALESCE(d.CommandName, '') AS CmdName,
                   COALESCE(sd.DeviceName, g.GroupName, 'Unknown') AS TargetName,
                   COALESCE(r.LastFiredAt,''),
                   COALESCE(r.LastResult,'')
            FROM   ScheduleRules r
            LEFT   JOIN DeviceList     d  ON d.id  = r.CommandId
            LEFT   JOIN StoredDevices  sd ON sd.id = r.DeviceId
            LEFT   JOIN Groups         g  ON g.id  = r.GroupId
            ORDER  BY r.ScheduleTime;
            """;
        var list = new List<ScheduleRule>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            list.Add(new ScheduleRule
            {
                Id           = rdr.GetInt32(0),
                RuleName     = rdr.GetString(1),
                DeviceId     = rdr.IsDBNull(2) ? null : rdr.GetInt32(2),
                GroupId      = rdr.IsDBNull(3) ? null : rdr.GetInt32(3),
                CommandId    = rdr.GetInt32(4),
                ScheduleTime = rdr.GetString(5),
                Recurrence   = rdr.GetString(6),
                IsEnabled    = rdr.GetInt32(7) == 1,
                Notes        = rdr.GetString(8),
                CommandName  = rdr.GetString(9),
                TargetName   = rdr.GetString(10),
                LastFiredAt  = rdr.GetString(11),
                LastResult   = rdr.GetString(12),
            });
        }
        return list;
    }

    public int InsertScheduleRule(ScheduleRule r)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ScheduleRules
                (RuleName, DeviceId, GroupId, CommandId, ScheduleTime, Recurrence, IsEnabled, Notes)
            VALUES ($name, $did, $gid, $cid, $time, $rec, $en, $notes);
            SELECT last_insert_rowid();
            """;
        BindRule(cmd, r);
        return System.Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void UpdateScheduleRule(ScheduleRule r)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE ScheduleRules
            SET RuleName=$name, DeviceId=$did, GroupId=$gid, CommandId=$cid,
                ScheduleTime=$time, Recurrence=$rec, IsEnabled=$en, Notes=$notes
            WHERE id=$id;
            """;
        cmd.Parameters.AddWithValue("$id", r.Id);
        BindRule(cmd, r);
        cmd.ExecuteNonQuery();
    }

    public void DeleteScheduleRule(int id)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ScheduleRules WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static void BindRule(SqliteCommand cmd, ScheduleRule r)
    {
        cmd.Parameters.AddWithValue("$name",  r.RuleName);
        cmd.Parameters.AddWithValue("$did",   (object?)r.DeviceId  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gid",   (object?)r.GroupId   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cid",   r.CommandId);
        cmd.Parameters.AddWithValue("$time",  r.ScheduleTime);
        cmd.Parameters.AddWithValue("$rec",   r.Recurrence);
        cmd.Parameters.AddWithValue("$en",    r.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$notes", r.Notes);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  AUDIT / COMMAND LOG
    // ══════════════════════════════════════════════════════════════════════════

    public void LogCommand(AuditLogEntry entry)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO CommandLog
                (Timestamp, DeviceName, DeviceAddress, CommandName, CommandCode, Response, Success, Notes)
            VALUES ($ts, $dn, $da, $cn, $cc, $resp, $ok, $notes);
            """;
        cmd.Parameters.AddWithValue("$ts",    entry.Timestamp);
        cmd.Parameters.AddWithValue("$dn",    entry.DeviceName);
        cmd.Parameters.AddWithValue("$da",    entry.DeviceAddress);
        cmd.Parameters.AddWithValue("$cn",    entry.CommandName);
        cmd.Parameters.AddWithValue("$cc",    entry.CommandCode);
        cmd.Parameters.AddWithValue("$resp",  entry.Response);
        cmd.Parameters.AddWithValue("$ok",    entry.Success ? 1 : 0);
        cmd.Parameters.AddWithValue("$notes", entry.Notes);
        cmd.ExecuteNonQuery();
    }

    public List<AuditLogEntry> GetCommandLog(int limit = 1000)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, Timestamp, COALESCE(DeviceName,''), COALESCE(DeviceAddress,''),
                   COALESCE(CommandName,''), COALESCE(CommandCode,''),
                   COALESCE(Response,''), Success, COALESCE(Notes,'')
            FROM   CommandLog
            ORDER  BY id ASC
            LIMIT  $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<AuditLogEntry>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            list.Add(new AuditLogEntry
            {
                Id            = rdr.GetInt32(0),
                Timestamp     = rdr.GetString(1),
                DeviceName    = rdr.GetString(2),
                DeviceAddress = rdr.GetString(3),
                CommandName   = rdr.GetString(4),
                CommandCode   = rdr.GetString(5),
                Response      = rdr.GetString(6),
                Success       = rdr.GetInt32(7) == 1,
                Notes         = rdr.GetString(8),
            });
        }
        return list;
    }

    public void ClearCommandLog()
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM CommandLog;";
        cmd.ExecuteNonQuery();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  MAC FILTER
    // ══════════════════════════════════════════════════════════════════════════

    public List<(int Id, string Mac, string DeviceName, string Notes)> GetMacFilters()
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, MACAddress,
                   COALESCE(DeviceName,''), COALESCE(Notes,'')
            FROM   MACFilter ORDER BY MACAddress;
            """;
        var list = new List<(int, string, string, string)>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            list.Add((rdr.GetInt32(0), rdr.GetString(1), rdr.GetString(2), rdr.GetString(3)));
        return list;
    }

    public void InsertMacFilter(string mac, string deviceName, string notes)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO MACFilter (MACAddress, DeviceName, Notes) VALUES ($m,$d,$n);";
        cmd.Parameters.AddWithValue("$m", mac);
        cmd.Parameters.AddWithValue("$d", deviceName);
        cmd.Parameters.AddWithValue("$n", notes);
        cmd.ExecuteNonQuery();
    }

    public void DeleteMacFilter(int id)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM MACFilter WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  HELPERS
    // ══════════════════════════════════════════════════════════════════════════

    public List<string> GetDistinctSeries()
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT SeriesPattern FROM DeviceList ORDER BY SeriesPattern;";
        var list = new List<string>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) list.Add(rdr.GetString(0));
        return list;
    }

    public List<string> GetDistinctModelNumbers()
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT ModelNumber FROM Models ORDER BY ModelNumber;";
        var list = new List<string>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) list.Add(rdr.GetString(0));
        return list;
    }

    public void UpdateDeviceLastSeen(int deviceId, string timestamp)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "UPDATE StoredDevices SET LastSeenAt=$ts WHERE id=$id;";
        cmd.Parameters.AddWithValue("$ts", timestamp);
        cmd.Parameters.AddWithValue("$id", deviceId);
        cmd.ExecuteNonQuery();
    }

    public void UpdateRuleHistory(int ruleId, string firedAt, string result)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "UPDATE ScheduleRules SET LastFiredAt=$fa, LastResult=$res WHERE id=$id;";
        cmd.Parameters.AddWithValue("$fa",  firedAt);
        cmd.Parameters.AddWithValue("$res", result);
        cmd.Parameters.AddWithValue("$id",  ruleId);
        cmd.ExecuteNonQuery();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  MACROS
    // ══════════════════════════════════════════════════════════════════════════

    public List<MacroEntry> GetAllMacros()
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT id, MacroName, COALESCE(Notes,'') FROM Macros ORDER BY MacroName;";
        var list = new List<MacroEntry>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            list.Add(new MacroEntry { Id = rdr.GetInt32(0), MacroName = rdr.GetString(1), Notes = rdr.GetString(2) });
        foreach (var m in list)
            m.Steps = GetMacroSteps(m.Id);
        return list;
    }

    public List<MacroStep> GetMacroSteps(int macroId)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ms.id, ms.MacroId, ms.StepOrder, ms.CommandId, ms.DelayAfterMs,
                   COALESCE(dl.CommandName,''), COALESCE(dl.SeriesPattern,'')
            FROM   MacroSteps ms
            LEFT   JOIN DeviceList dl ON dl.id = ms.CommandId
            WHERE  ms.MacroId = $mid
            ORDER  BY ms.StepOrder;
            """;
        cmd.Parameters.AddWithValue("$mid", macroId);
        var list = new List<MacroStep>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            list.Add(new MacroStep
            {
                Id            = rdr.GetInt32(0),
                MacroId       = rdr.GetInt32(1),
                StepOrder     = rdr.GetInt32(2),
                CommandId     = rdr.GetInt32(3),
                DelayAfterMs  = rdr.GetInt32(4),
                CommandName   = rdr.GetString(5),
                SeriesPattern = rdr.GetString(6),
            });
        return list;
    }

    public int InsertMacro(MacroEntry m)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Macros (MacroName, Notes) VALUES ($n,$notes); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n",     m.MacroName);
        cmd.Parameters.AddWithValue("$notes", m.Notes);
        return System.Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void UpdateMacro(MacroEntry m)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "UPDATE Macros SET MacroName=$n, Notes=$notes WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id",    m.Id);
        cmd.Parameters.AddWithValue("$n",     m.MacroName);
        cmd.Parameters.AddWithValue("$notes", m.Notes);
        cmd.ExecuteNonQuery();
    }

    public void DeleteMacro(int id)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Macros WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetMacroSteps(int macroId, IEnumerable<MacroStep> steps)
    {
        using var conn = OpenConnection();
        using var tx   = conn.BeginTransaction();
        var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM MacroSteps WHERE MacroId=$mid;";
        del.Parameters.AddWithValue("$mid", macroId);
        del.ExecuteNonQuery();

        int order = 1;
        foreach (var s in steps)
        {
            var ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO MacroSteps (MacroId, StepOrder, CommandId, DelayAfterMs) VALUES ($mid,$ord,$cid,$del);";
            ins.Parameters.AddWithValue("$mid", macroId);
            ins.Parameters.AddWithValue("$ord", order++);
            ins.Parameters.AddWithValue("$cid", s.CommandId);
            ins.Parameters.AddWithValue("$del", s.DelayAfterMs);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Returns the OUI prefix → series mapping for use by NetworkScanService.</summary>
    public Dictionary<string, string> GetOuiSeriesMap()
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT OUIPrefix, SeriesPattern FROM OUITable;";
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            dict[rdr.GetString(0)] = rdr.GetString(1);
        return dict;
    }

    public void Dispose() { /* connection is opened/closed per call — nothing to dispose */ }
}
