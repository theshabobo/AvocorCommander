using AvocorCommander.Models;
using Microsoft.Data.Sqlite;
using System.IO;

namespace AvocorCommander.Services;

/// <summary>
/// Central data access layer — wraps all SQLite operations.
/// Command/model data lives in commands.db; user data lives in userdata.db.
/// </summary>
public sealed class DatabaseService : IDisposable
{
    private readonly string _commandsDbPath;
    private readonly string _userDataDbPath;
    private readonly string _legacyDbPath;
    private readonly object _lock = new();

    public DatabaseService()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        _commandsDbPath = Path.Combine(baseDir, "commands.db");
        _userDataDbPath = Path.Combine(baseDir, "userdata.db");
        _legacyDbPath   = Path.Combine(baseDir, "AvocorCommander.db");
        MigrateFromLegacyIfNeeded();
        EnsureUserDataSchema();
        EnsureCommandsSchema();
        ApplyCommandDbUpdate();
    }

    // ── Connection factories ─────────────────────────────────────────────────

    private SqliteConnection OpenCommandsConnection()
    {
        var conn = new SqliteConnection($"Data Source={_commandsDbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    private SqliteConnection OpenUserDataConnection()
    {
        var conn = new SqliteConnection($"Data Source={_userDataDbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    // Cross-DB lookups (MacroSteps → DeviceList, ScheduleRules → DeviceList)
    // are handled by querying each DB separately rather than using ATTACH,
    // which avoids thread-safety issues with the 'cmd' alias on pooled connections.

    // ── Legacy migration ─────────────────────────────────────────────────────

    private void MigrateFromLegacyIfNeeded()
    {
        // If userdata.db already exists, nothing to migrate
        if (File.Exists(_userDataDbPath)) return;

        // If the legacy DB doesn't exist either, nothing to migrate
        if (!File.Exists(_legacyDbPath)) return;

        // Copy legacy → userdata.db
        File.Copy(_legacyDbPath, _userDataDbPath);

        // Strip command-side tables from the new userdata.db
        try
        {
            using var conn = new SqliteConnection($"Data Source={_userDataDbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DROP TABLE IF EXISTS DeviceList;
                DROP TABLE IF EXISTS Models;
                DROP TABLE IF EXISTS OUITable;
                """;
            cmd.ExecuteNonQuery();
        }
        catch { /* best-effort cleanup */ }

        // Rename legacy DB to .bak so it won't be re-migrated
        try
        {
            File.Move(_legacyDbPath, _legacyDbPath + ".bak");
        }
        catch { /* if rename fails, the File.Exists guard above prevents re-run */ }
    }

    // ── Schema bootstrap ─────────────────────────────────────────────────────

    /// <summary>
    /// If a pending db update file exists (placed by the update batch script),
    /// merges DeviceList, Models, and OUITable from it into commands.db, then deletes it.
    /// User data tables are untouched.
    /// </summary>
    private void ApplyCommandDbUpdate()
    {
        var updatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AvocorCommander_update.db");
        if (!File.Exists(updatePath)) return;

        try
        {
            using var conn = OpenCommandsConnection();
            using var cmd  = conn.CreateCommand();

            // Attach the update db and replace DeviceList + Models + OUITable atomically
            cmd.CommandText = $"ATTACH DATABASE '{updatePath.Replace("'", "''")}' AS upd;";
            cmd.ExecuteNonQuery();

            cmd.CommandText = @"
                BEGIN;
                DELETE FROM DeviceList;
                INSERT INTO DeviceList SELECT * FROM upd.DeviceList;
                DELETE FROM Models;
                INSERT INTO Models SELECT * FROM upd.Models;
                DELETE FROM OUITable;
                INSERT INTO OUITable SELECT * FROM upd.OUITable;
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

    private void EnsureUserDataSchema()
    {
        using var conn = OpenUserDataConnection();
        using var cmd  = conn.CreateCommand();

        // Create StoredDevices if it doesn't exist (legacy migration may have carried it over,
        // but fresh installs need it created from scratch)
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS StoredDevices (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                DeviceName     TEXT    NOT NULL,
                ModelNumber    TEXT    DEFAULT '',
                IPAddress      TEXT    DEFAULT '',
                Port           INTEGER DEFAULT 0,
                BaudRate       INTEGER DEFAULT 9600,
                Notes          TEXT    DEFAULT '',
                ComPort        TEXT    DEFAULT '',
                MacAddress     TEXT    DEFAULT '',
                ConnectionType TEXT    DEFAULT 'TCP',
                LastSeenAt     TEXT    DEFAULT '',
                AutoConnect    INTEGER DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();

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

        // Create remaining user-data tables
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
                Notes        TEXT    DEFAULT '',
                LastFiredAt  TEXT    DEFAULT '',
                LastResult   TEXT    DEFAULT ''
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
                StepType     TEXT    NOT NULL DEFAULT 'command',
                PromptText   TEXT    NOT NULL DEFAULT '',
                FOREIGN KEY(MacroId) REFERENCES Macros(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS MACFilter (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                MACAddress TEXT    NOT NULL UNIQUE,
                DeviceName TEXT    DEFAULT '',
                Notes      TEXT    DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS PanelScenes (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                Name        TEXT    NOT NULL DEFAULT 'Scene',
                Description TEXT    NOT NULL DEFAULT '',
                SortOrder   INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS PanelPages (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                SceneId   INTEGER NOT NULL,
                Name      TEXT    NOT NULL DEFAULT 'Page',
                SortOrder INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY(SceneId) REFERENCES PanelScenes(Id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS PanelButtons (
                Id         INTEGER PRIMARY KEY AUTOINCREMENT,
                PageId     INTEGER,
                SceneId    INTEGER,
                ButtonType TEXT    NOT NULL DEFAULT 'grid',
                Label      TEXT    NOT NULL DEFAULT '',
                Icon       TEXT    NOT NULL DEFAULT '▶',
                Color      TEXT    NOT NULL DEFAULT '#3A7BD5',
                IsToggle   INTEGER NOT NULL DEFAULT 0,
                GridRow    INTEGER NOT NULL DEFAULT 0,
                GridCol    INTEGER NOT NULL DEFAULT 0,
                SortOrder  INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS PanelButtonActions (
                Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                ButtonId      INTEGER NOT NULL,
                Phase         INTEGER NOT NULL DEFAULT 0,
                DeviceId      INTEGER NOT NULL,
                DeviceName    TEXT    NOT NULL DEFAULT '',
                CommandCode   TEXT    NOT NULL DEFAULT '',
                CommandName   TEXT    NOT NULL DEFAULT '',
                CommandFormat TEXT    NOT NULL DEFAULT 'HEX',
                SortOrder     INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY(ButtonId) REFERENCES PanelButtons(Id) ON DELETE CASCADE
            );
            """;
        cmd.ExecuteNonQuery();

        // Migrate CommandLog — add Response column if it doesn't exist
        try
        {
            cmd.CommandText = "ALTER TABLE CommandLog ADD COLUMN Response TEXT DEFAULT '';";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException) { /* column already exists */ }

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

        // Migrate MacroSteps — add StepType + PromptText for "pause for user" steps
        foreach (var (col, def) in new[]
        {
            ("StepType",   "TEXT DEFAULT 'command'"),
            ("PromptText", "TEXT DEFAULT ''"),
        })
        {
            try
            {
                cmd.CommandText = $"ALTER TABLE MacroSteps ADD COLUMN {col} {def};";
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException) { /* column already exists */ }
        }
    }

    private void EnsureCommandsSchema()
    {
        using var conn = OpenCommandsConnection();
        using var cmd  = conn.CreateCommand();

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

        // Migrate Models — add per-model RS232 baud rate
        try
        {
            cmd.CommandText = "ALTER TABLE Models ADD COLUMN BaudRate INTEGER DEFAULT 9600;";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException) { /* column already exists */ }

        // One-time backfill: set BaudRate per SeriesPattern (only rows still at 0 or default 9600
        // which haven't been explicitly set — safe to re-run because NULL/0 rows get corrected
        // and previously-correct rows stay untouched where BaudRate already matches).
        foreach (var (series, baud) in new[]
        {
            ("A-Series", 38400),
            ("K-Series", 38400),
            ("X-Series", 38400),
            ("B-Series", 115200),
            ("E-50",     115200),
            ("AVE-9200", 9600),
            ("S-Series", 9600),
            ("H-Series", 9600),
        })
        {
            cmd.Parameters.Clear();
            cmd.CommandText = "UPDATE Models SET BaudRate=$b WHERE SeriesPattern=$s AND (BaudRate IS NULL OR BaudRate=0 OR BaudRate=9600);";
            cmd.Parameters.AddWithValue("$b", baud);
            cmd.Parameters.AddWithValue("$s", series);
            cmd.ExecuteNonQuery();
        }
        cmd.Parameters.Clear();

        // Seed known Avocor OUI prefixes. INSERT OR IGNORE keeps the table
        // stable across launches (won't overwrite user-added prefixes).
        foreach (var (prefix, label, series) in new[]
        {
            ("38:54:39", "Avocor S-Series (AVS-xx10)", "S-Series"),
            ("1C:D1:D7", "Avocor B-Series (AVB-xx10)", "B-Series"),
            ("44:37:0B", "Avocor H-Series (AVH-xx20)", "H-Series"),
        })
        {
            cmd.Parameters.Clear();
            cmd.CommandText = "INSERT OR IGNORE INTO OUITable (OUIPrefix, SeriesLabel, SeriesPattern, Notes) VALUES ($p,$l,$s,'');";
            cmd.Parameters.AddWithValue("$p", prefix);
            cmd.Parameters.AddWithValue("$l", label);
            cmd.Parameters.AddWithValue("$s", series);
            cmd.ExecuteNonQuery();
        }

        // One-time label correction: a prior build mislabeled the H-Series OUI as xx10.
        cmd.Parameters.Clear();
        cmd.CommandText = "UPDATE OUITable SET SeriesLabel='Avocor H-Series (AVH-xx20)' WHERE OUIPrefix='44:37:0B' AND SeriesLabel='Avocor H-Series (AVH-xx10)';";
        cmd.ExecuteNonQuery();
        cmd.Parameters.Clear();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  STORED DEVICES  (userdata.db)
    // ══════════════════════════════════════════════════════════════════════════

    public List<DeviceEntry> GetAllDevices()
    {
        using var conn = OpenUserDataConnection();
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
        using var conn = OpenUserDataConnection();
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
        using var conn = OpenUserDataConnection();
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
        using var conn = OpenUserDataConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM StoredDevices WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public List<DeviceEntry> GetAutoConnectDevices() =>
        GetAllDevices().Where(d => d.AutoConnect).ToList();

    // ══════════════════════════════════════════════════════════════════════════
    //  COMMANDS (DeviceList)  (commands.db)
    // ══════════════════════════════════════════════════════════════════════════

    public List<CommandEntry> GetCommandsBySeries(string seriesPattern)
    {
        using var conn = OpenCommandsConnection();
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

    public List<string> GetCategoriesBySeries(string seriesPattern)
    {
        using var conn = OpenCommandsConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT CommandCategory FROM DeviceList WHERE SeriesPattern=$s ORDER BY CommandCategory;";
        cmd.Parameters.AddWithValue("$s", seriesPattern);
        var list = new List<string>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) list.Add(rdr.GetString(0));
        return list;
    }

    public List<CommandEntry> GetCommandsBySeriesAndCategory(string seriesPattern, string category)
    {
        using var conn = OpenCommandsConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, SeriesPattern, CommandCategory, CommandName,
                   CommandCode, COALESCE(Notes,''), Port, CommandFormat
            FROM   DeviceList
            WHERE  SeriesPattern=$s AND CommandCategory=$cat
            ORDER  BY CommandName;
            """;
        cmd.Parameters.AddWithValue("$s",   seriesPattern);
        cmd.Parameters.AddWithValue("$cat", category);
        return ReadCommands(cmd);
    }

    public List<CommandEntry> GetAllCommands()
    {
        using var conn = OpenCommandsConnection();
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
        using var conn = OpenCommandsConnection();
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
        using var conn = OpenCommandsConnection();
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
        using var conn = OpenCommandsConnection();
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
    //  MODELS  (commands.db)
    // ══════════════════════════════════════════════════════════════════════════

    public List<ModelEntry> GetAllModels()
    {
        using var conn = OpenCommandsConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, ModelNumber, SeriesPattern, COALESCE(BaudRate, 9600)
            FROM   Models
            ORDER  BY ModelNumber;
            """;
        var list = new List<ModelEntry>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            list.Add(new ModelEntry
            {
                Id            = rdr.GetInt32(0),
                ModelNumber   = rdr.GetString(1),
                SeriesPattern = rdr.GetString(2),
                BaudRate      = rdr.GetInt32(3),
            });
        return list;
    }

    public string? GetSeriesForModel(string modelNumber)
    {
        using var conn = OpenCommandsConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT SeriesPattern FROM Models WHERE ModelNumber=$m LIMIT 1;";
        cmd.Parameters.AddWithValue("$m", modelNumber);
        var result = cmd.ExecuteScalar();
        return result as string;
    }

    /// <summary>
    /// Returns the default RS232 baud rate for the given model, from the Models table.
    /// Falls back to 9600 if the model isn't found or BaudRate is null/0.
    /// </summary>
    public int GetBaudRateForModel(string modelNumber)
    {
        using var conn = OpenCommandsConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(BaudRate, 9600) FROM Models WHERE ModelNumber=$m LIMIT 1;";
        cmd.Parameters.AddWithValue("$m", modelNumber);
        var result = cmd.ExecuteScalar();
        if (result is long l && l > 0)  return (int)l;
        if (result is int  i && i > 0)  return i;
        return 9600;
    }

    public void InsertModel(ModelEntry m)
    {
        using var conn = OpenCommandsConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO Models (ModelNumber, SeriesPattern, BaudRate) VALUES ($m, $s, $b);";
        cmd.Parameters.AddWithValue("$m", m.ModelNumber);
        cmd.Parameters.AddWithValue("$s", m.SeriesPattern);
        cmd.Parameters.AddWithValue("$b", m.BaudRate);
        cmd.ExecuteNonQuery();
    }

    public void UpdateModel(ModelEntry m)
    {
        using var conn = OpenCommandsConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "UPDATE Models SET ModelNumber=$m, SeriesPattern=$s, BaudRate=$b WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", m.Id);
        cmd.Parameters.AddWithValue("$m",  m.ModelNumber);
        cmd.Parameters.AddWithValue("$b",  m.BaudRate);
        cmd.Parameters.AddWithValue("$s",  m.SeriesPattern);
        cmd.ExecuteNonQuery();
    }

    public void DeleteModel(int id)
    {
        using var conn = OpenCommandsConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Models WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  OUI TABLE  (commands.db)
    // ══════════════════════════════════════════════════════════════════════════

    public List<OuiEntry> GetAllOuiEntries()
    {
        using var conn = OpenCommandsConnection();
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
        using var conn = OpenCommandsConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT SeriesPattern FROM OUITable WHERE OUIPrefix=$p LIMIT 1;";
        cmd.Parameters.AddWithValue("$p", prefix);
        return cmd.ExecuteScalar() as string;
    }

    public void InsertOuiEntry(OuiEntry e)
    {
        using var conn = OpenCommandsConnection();
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
        using var conn = OpenCommandsConnection();
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
        using var conn = OpenCommandsConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM OUITable WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  GROUPS  (userdata.db)
    // ══════════════════════════════════════════════════════════════════════════

    public List<GroupEntry> GetAllGroups()
    {
        using var conn = OpenUserDataConnection();
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
        using var conn = OpenUserDataConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Groups (GroupName, Notes) VALUES ($n,$notes); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n",     g.GroupName);
        cmd.Parameters.AddWithValue("$notes", g.Notes);
        return System.Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void UpdateGroup(GroupEntry g)
    {
        using var conn = OpenUserDataConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "UPDATE Groups SET GroupName=$n, Notes=$notes WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id",    g.Id);
        cmd.Parameters.AddWithValue("$n",     g.GroupName);
        cmd.Parameters.AddWithValue("$notes", g.Notes);
        cmd.ExecuteNonQuery();
    }

    public void DeleteGroup(int id)
    {
        using var conn = OpenUserDataConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Groups WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetGroupMembers(int groupId, IEnumerable<int> deviceIds)
    {
        using var conn = OpenUserDataConnection();
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
    //  SCHEDULE RULES  (userdata.db, with commands.db attached for JOINs)
    // ══════════════════════════════════════════════════════════════════════════

    public List<ScheduleRule> GetAllScheduleRules()
    {
        // Query rules + target names from userdata.db (no cross-DB ATTACH needed)
        using var conn = OpenUserDataConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.RuleName,
                   r.DeviceId, r.GroupId, r.CommandId,
                   r.ScheduleTime, r.Recurrence, r.IsEnabled,
                   COALESCE(r.Notes,''),
                   COALESCE(sd.DeviceName, g.GroupName, 'Unknown') AS TargetName,
                   COALESCE(r.LastFiredAt,''),
                   COALESCE(r.LastResult,'')
            FROM   ScheduleRules r
            LEFT   JOIN StoredDevices   sd ON sd.id = r.DeviceId
            LEFT   JOIN Groups          g  ON g.id  = r.GroupId
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
                TargetName   = rdr.GetString(9),
                LastFiredAt  = rdr.GetString(10),
                LastResult   = rdr.GetString(11),
            });
        }

        // Resolve CommandId → CommandName from commands.db (separate connection)
        var commandIds = list.Where(r => r.CommandId > 0).Select(r => r.CommandId).Distinct().ToList();
        if (commandIds.Count > 0)
        {
            using var cmdConn = OpenCommandsConnection();
            using var lookup  = cmdConn.CreateCommand();
            lookup.CommandText = $"SELECT id, CommandName FROM DeviceList WHERE id IN ({string.Join(",", commandIds)})";
            var nameMap = new Dictionary<int, string>();
            using var lr = lookup.ExecuteReader();
            while (lr.Read())
                nameMap[lr.GetInt32(0)] = lr.GetString(1);

            foreach (var rule in list)
                rule.CommandName = nameMap.TryGetValue(rule.CommandId, out var n) ? n : "";
        }

        return list;
    }

    public int InsertScheduleRule(ScheduleRule r)
    {
        using var conn = OpenUserDataConnection();
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
        using var conn = OpenUserDataConnection();
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
        using var conn = OpenUserDataConnection();
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
    //  AUDIT / COMMAND LOG  (userdata.db)
    // ══════════════════════════════════════════════════════════════════════════

    public void LogCommand(AuditLogEntry entry)
    {
        using var conn = OpenUserDataConnection();
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
        using var conn = OpenUserDataConnection();
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
        using var conn = OpenUserDataConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM CommandLog;";
        cmd.ExecuteNonQuery();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  MAC FILTER  (userdata.db)
    // ══════════════════════════════════════════════════════════════════════════

    public List<(int Id, string Mac, string DeviceName, string Notes)> GetMacFilters()
    {
        using var conn = OpenUserDataConnection();
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
        using var conn = OpenUserDataConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO MACFilter (MACAddress, DeviceName, Notes) VALUES ($m,$d,$n);";
        cmd.Parameters.AddWithValue("$m", mac);
        cmd.Parameters.AddWithValue("$d", deviceName);
        cmd.Parameters.AddWithValue("$n", notes);
        cmd.ExecuteNonQuery();
    }

    public void DeleteMacFilter(int id)
    {
        using var conn = OpenUserDataConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM MACFilter WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  HELPERS  (routed to correct db)
    // ══════════════════════════════════════════════════════════════════════════

    public List<string> GetDistinctSeries()
    {
        using var conn = OpenCommandsConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT SeriesPattern FROM DeviceList ORDER BY SeriesPattern;";
        var list = new List<string>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) list.Add(rdr.GetString(0));
        return list;
    }

    public List<string> GetDistinctModelNumbers()
    {
        using var conn = OpenCommandsConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT ModelNumber FROM Models ORDER BY ModelNumber;";
        var list = new List<string>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) list.Add(rdr.GetString(0));
        return list;
    }

    public void UpdateDeviceLastSeen(int deviceId, string timestamp)
    {
        using var conn = OpenUserDataConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "UPDATE StoredDevices SET LastSeenAt=$ts WHERE id=$id;";
        cmd.Parameters.AddWithValue("$ts", timestamp);
        cmd.Parameters.AddWithValue("$id", deviceId);
        cmd.ExecuteNonQuery();
    }

    public void UpdateRuleHistory(int ruleId, string firedAt, string result)
    {
        using var conn = OpenUserDataConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "UPDATE ScheduleRules SET LastFiredAt=$fa, LastResult=$res WHERE id=$id;";
        cmd.Parameters.AddWithValue("$fa",  firedAt);
        cmd.Parameters.AddWithValue("$res", result);
        cmd.Parameters.AddWithValue("$id",  ruleId);
        cmd.ExecuteNonQuery();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  MACROS  (userdata.db; GetMacroSteps uses attached commands.db for JOIN)
    // ══════════════════════════════════════════════════════════════════════════

    public List<MacroEntry> GetAllMacros()
    {
        using var conn = OpenUserDataConnection();
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
        // Query steps from userdata.db
        using var conn = OpenUserDataConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ms.id, ms.MacroId, ms.StepOrder, ms.CommandId, ms.DelayAfterMs,
                   COALESCE(ms.StepType,'command'), COALESCE(ms.PromptText,'')
            FROM   MacroSteps ms
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
                StepType      = rdr.GetString(5),
                PromptText    = rdr.GetString(6),
            });

        // Resolve CommandId → CommandName + SeriesPattern from commands.db
        var commandIds = list.Where(s => s.IsCommand && s.CommandId > 0)
                             .Select(s => s.CommandId).Distinct().ToList();
        if (commandIds.Count > 0)
        {
            using var cmdConn = OpenCommandsConnection();
            using var lookup  = cmdConn.CreateCommand();
            lookup.CommandText = $"SELECT id, CommandName, SeriesPattern FROM DeviceList WHERE id IN ({string.Join(",", commandIds)})";
            var map = new Dictionary<int, (string name, string series)>();
            using var lr = lookup.ExecuteReader();
            while (lr.Read())
                map[lr.GetInt32(0)] = (lr.GetString(1), lr.GetString(2));

            foreach (var step in list)
            {
                if (map.TryGetValue(step.CommandId, out var info))
                {
                    step.CommandName   = info.name;
                    step.SeriesPattern = info.series;
                }
            }
        }

        return list;
    }

    public int InsertMacro(MacroEntry m)
    {
        using var conn = OpenUserDataConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Macros (MacroName, Notes) VALUES ($n,$notes); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n",     m.MacroName);
        cmd.Parameters.AddWithValue("$notes", m.Notes);
        return System.Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void UpdateMacro(MacroEntry m)
    {
        using var conn = OpenUserDataConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "UPDATE Macros SET MacroName=$n, Notes=$notes WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id",    m.Id);
        cmd.Parameters.AddWithValue("$n",     m.MacroName);
        cmd.Parameters.AddWithValue("$notes", m.Notes);
        cmd.ExecuteNonQuery();
    }

    public void DeleteMacro(int id)
    {
        using var conn = OpenUserDataConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Macros WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetMacroSteps(int macroId, IEnumerable<MacroStep> steps)
    {
        using var conn = OpenUserDataConnection();
        using var tx   = conn.BeginTransaction();
        var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM MacroSteps WHERE MacroId=$mid;";
        del.Parameters.AddWithValue("$mid", macroId);
        del.ExecuteNonQuery();

        int order = 1;
        foreach (var s in steps)
        {
            var ins = conn.CreateCommand();
            ins.CommandText = """
                INSERT INTO MacroSteps (MacroId, StepOrder, CommandId, DelayAfterMs, StepType, PromptText)
                VALUES ($mid, $ord, $cid, $del, $type, $prompt);
                """;
            ins.Parameters.AddWithValue("$mid",    macroId);
            ins.Parameters.AddWithValue("$ord",    order++);
            ins.Parameters.AddWithValue("$cid",    s.CommandId);
            ins.Parameters.AddWithValue("$del",    s.DelayAfterMs);
            ins.Parameters.AddWithValue("$type",   string.IsNullOrWhiteSpace(s.StepType) ? "command" : s.StepType);
            ins.Parameters.AddWithValue("$prompt", s.PromptText ?? string.Empty);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Returns the OUI prefix -> series mapping for use by NetworkScanService.</summary>
    public Dictionary<string, string> GetOuiSeriesMap()
    {
        using var conn = OpenCommandsConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT OUIPrefix, SeriesPattern FROM OUITable;";
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            dict[rdr.GetString(0)] = rdr.GetString(1);
        return dict;
    }

    public void Dispose() { /* connections are opened/closed per call — nothing to dispose */ }
}
