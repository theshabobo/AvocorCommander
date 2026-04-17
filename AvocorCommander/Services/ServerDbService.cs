using Microsoft.Data.Sqlite;
using System.IO;
using System.Security.Cryptography;

namespace AvocorCommander.Services;

/// <summary>
/// Manages server.db — users, API keys, and server configuration
/// for the embedded REST API / WebSocket layer.
/// </summary>
public sealed class ServerDbService : IDisposable
{
    private readonly string _dbPath;
    private readonly object _lock = new();

    public ServerDbService()
    {
        _dbPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppDomain.CurrentDomain.BaseDirectory, "server.db");
        EnsureSchema();
        SeedDefaults();
    }

    // ── Connection factory ───────────────────────────────────────────────────

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    // ── Schema ───────────────────────────────────────────────────────────────

    private void EnsureSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Users (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                Username     TEXT    NOT NULL UNIQUE,
                PasswordHash TEXT    NOT NULL,
                Salt         TEXT    NOT NULL,
                Role         TEXT    NOT NULL DEFAULT 'Admin',
                IsHidden     INTEGER NOT NULL DEFAULT 0,
                CreatedAt    TEXT    NOT NULL,
                LastLogin    TEXT    DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS ApiKeys (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                Label      TEXT    NOT NULL,
                KeyHash    TEXT    NOT NULL UNIQUE,
                KeyPrefix  TEXT    NOT NULL DEFAULT '',
                CreatedAt  TEXT    NOT NULL,
                LastUsedAt TEXT    DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS ServerConfig (
                id    INTEGER PRIMARY KEY AUTOINCREMENT,
                Key   TEXT    NOT NULL UNIQUE,
                Value TEXT    NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private void SeedDefaults()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();

        // Check if any users exist
        cmd.CommandText = "SELECT COUNT(*) FROM Users;";
        long count = (long)(cmd.ExecuteScalar() ?? 0);
        if (count > 0) return;

        // Seed admin/admin (visible)
        var (hash1, salt1) = HashPassword("admin");
        cmd.CommandText = """
            INSERT INTO Users (Username, PasswordHash, Salt, Role, IsHidden, CreatedAt)
            VALUES ($u, $h, $s, 'Admin', 0, $t);
            """;
        cmd.Parameters.AddWithValue("$u", "admin");
        cmd.Parameters.AddWithValue("$h", hash1);
        cmd.Parameters.AddWithValue("$s", salt1);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();

        // Seed Avocor/Avocor (hidden recovery account)
        var (hash2, salt2) = HashPassword("Avocor");
        cmd.Parameters.Clear();
        cmd.CommandText = """
            INSERT INTO Users (Username, PasswordHash, Salt, Role, IsHidden, CreatedAt)
            VALUES ($u, $h, $s, 'Admin', 1, $t);
            """;
        cmd.Parameters.AddWithValue("$u", "Avocor");
        cmd.Parameters.AddWithValue("$h", hash2);
        cmd.Parameters.AddWithValue("$s", salt2);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();

        // Seed default config
        cmd.Parameters.Clear();
        cmd.CommandText = """
            INSERT OR IGNORE INTO ServerConfig (Key, Value) VALUES ('api.port', '5105');
            INSERT OR IGNORE INTO ServerConfig (Key, Value) VALUES ('api.enabled', 'true');
            """;
        cmd.ExecuteNonQuery();

        // Generate a cryptographically random JWT secret if not already set
        cmd.Parameters.Clear();
        cmd.CommandText = "SELECT Value FROM ServerConfig WHERE Key='jwt.secret';";
        var existing = cmd.ExecuteScalar();
        if (existing == null || string.IsNullOrEmpty(existing as string))
        {
            byte[] secret = new byte[32];
            RandomNumberGenerator.Fill(secret);
            string secretB64 = Convert.ToBase64String(secret);
            cmd.CommandText = "INSERT OR REPLACE INTO ServerConfig (Key, Value) VALUES ('jwt.secret', $v);";
            cmd.Parameters.AddWithValue("$v", secretB64);
            cmd.ExecuteNonQuery();
        }
    }

    // ── Password hashing (SHA256 + random salt) ──────────────────────────────

    private static (string hash, string salt) HashPassword(string password)
    {
        byte[] saltBytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
            rng.GetBytes(saltBytes);
        string salt = Convert.ToBase64String(saltBytes);
        string hash = ComputeHash(password, saltBytes);
        return (hash, salt);
    }

    private static string ComputeHash(string password, byte[] saltBytes)
    {
        using var pbkdf2 = new System.Security.Cryptography.Rfc2898DeriveBytes(
            password, saltBytes, 310_000, System.Security.Cryptography.HashAlgorithmName.SHA256);
        return Convert.ToBase64String(pbkdf2.GetBytes(32));
    }

    private static bool VerifyPassword(string password, string storedHash, string storedSalt)
    {
        byte[] saltBytes = Convert.FromBase64String(storedSalt);
        string computed = ComputeHash(password, saltBytes);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(computed),
            System.Text.Encoding.UTF8.GetBytes(storedHash));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  USERS
    // ══════════════════════════════════════════════════════════════════════════

    public sealed class User
    {
        public int    Id       { get; set; }
        public string Username { get; set; } = "";
        public string Role     { get; set; } = "Admin";
        public bool   IsHidden { get; set; }
        public string CreatedAt { get; set; } = "";
        public string LastLogin { get; set; } = "";
    }

    /// <summary>Returns all visible users (excludes hidden recovery accounts).</summary>
    public List<User> GetAllUsers()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, Username, Role, IsHidden, CreatedAt, COALESCE(LastLogin,'')
                FROM Users WHERE IsHidden = 0 ORDER BY Username;
                """;
            var list = new List<User>();
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                list.Add(new User
                {
                    Id        = rdr.GetInt32(0),
                    Username  = rdr.GetString(1),
                    Role      = rdr.GetString(2),
                    IsHidden  = rdr.GetInt32(3) == 1,
                    CreatedAt = rdr.GetString(4),
                    LastLogin = rdr.GetString(5),
                });
            return list;
        }
    }

    public User? CreateUser(string username, string password, string role = "Admin")
    {
        lock (_lock)
        {
            var (hash, salt) = HashPassword(password);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Users (Username, PasswordHash, Salt, Role, IsHidden, CreatedAt)
                VALUES ($u, $h, $s, $r, 0, $t);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$u", username);
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.Parameters.AddWithValue("$s", salt);
            cmd.Parameters.AddWithValue("$r", role);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            try
            {
                int id = Convert.ToInt32(cmd.ExecuteScalar());
                return new User { Id = id, Username = username, Role = role, CreatedAt = DateTime.UtcNow.ToString("o") };
            }
            catch (SqliteException)
            {
                return null; // duplicate username
            }
        }
    }

    /// <summary>Validates username+password. Returns User on success, null on failure.</summary>
    public User? ValidateUser(string username, string password)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, Username, PasswordHash, Salt, Role, IsHidden, CreatedAt FROM Users WHERE Username=$u;";
            cmd.Parameters.AddWithValue("$u", username);
            using var rdr = cmd.ExecuteReader();
            if (!rdr.Read()) return null;

            string storedHash = rdr.GetString(2);
            string storedSalt = rdr.GetString(3);
            if (!VerifyPassword(password, storedHash, storedSalt)) return null;

            var user = new User
            {
                Id        = rdr.GetInt32(0),
                Username  = rdr.GetString(1),
                Role      = rdr.GetString(4),
                IsHidden  = rdr.GetInt32(5) == 1,
                CreatedAt = rdr.GetString(6),
            };

            // Update last login
            using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE Users SET LastLogin=$t WHERE id=$id;";
            upd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            upd.Parameters.AddWithValue("$id", user.Id);
            upd.ExecuteNonQuery();

            return user;
        }
    }

    public bool DeleteUser(int id)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            // Prevent deleting hidden recovery accounts
            cmd.CommandText = "DELETE FROM Users WHERE id=$id AND IsHidden=0;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public bool ChangePassword(int id, string newPassword)
    {
        lock (_lock)
        {
            var (hash, salt) = HashPassword(newPassword);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Users SET PasswordHash=$h, Salt=$s WHERE id=$id;";
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.Parameters.AddWithValue("$s", salt);
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  API KEYS
    // ══════════════════════════════════════════════════════════════════════════

    public sealed class ApiKeyInfo
    {
        public int    Id         { get; set; }
        public string Label      { get; set; } = "";
        public string KeyPrefix  { get; set; } = "";
        public string CreatedAt  { get; set; } = "";
        public string LastUsedAt { get; set; } = "";
    }

    /// <summary>Returns all API keys (with prefix for display, NOT the full key).</summary>
    public List<ApiKeyInfo> GetAllApiKeys()
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, Label, KeyPrefix, CreatedAt, COALESCE(LastUsedAt,'') FROM ApiKeys ORDER BY Label;";
            var list = new List<ApiKeyInfo>();
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                list.Add(new ApiKeyInfo
                {
                    Id         = rdr.GetInt32(0),
                    Label      = rdr.GetString(1),
                    KeyPrefix  = rdr.GetString(2),
                    CreatedAt  = rdr.GetString(3),
                    LastUsedAt = rdr.GetString(4),
                });
            return list;
        }
    }

    /// <summary>Creates a new API key. Returns the raw key (only shown once).</summary>
    public (string rawKey, ApiKeyInfo info) CreateApiKey(string label)
    {
        lock (_lock)
        {
            // Generate a random 32-byte key, Base64-URL-encoded
            byte[] keyBytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(keyBytes);
            string rawKey = Convert.ToBase64String(keyBytes)
                .Replace("+", "-").Replace("/", "_").TrimEnd('=');

            // Hash the key for storage
            byte[] hashBytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawKey));
            string keyHash = Convert.ToBase64String(hashBytes);

            string prefix = rawKey[..8] + "...";
            string now = DateTime.UtcNow.ToString("o");

            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ApiKeys (Label, KeyHash, KeyPrefix, CreatedAt)
                VALUES ($l, $h, $p, $t);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$l", label);
            cmd.Parameters.AddWithValue("$h", keyHash);
            cmd.Parameters.AddWithValue("$p", prefix);
            cmd.Parameters.AddWithValue("$t", now);
            int id = Convert.ToInt32(cmd.ExecuteScalar());

            return (rawKey, new ApiKeyInfo { Id = id, Label = label, KeyPrefix = prefix, CreatedAt = now });
        }
    }

    /// <summary>Validates an API key. Returns true if valid.</summary>
    public bool ValidateApiKey(string rawKey)
    {
        lock (_lock)
        {
            byte[] hashBytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawKey));
            string keyHash = Convert.ToBase64String(hashBytes);

            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM ApiKeys WHERE KeyHash=$h;";
            cmd.Parameters.AddWithValue("$h", keyHash);
            var result = cmd.ExecuteScalar();
            if (result == null) return false;

            // Update last used
            int id = Convert.ToInt32(result);
            UpdateApiKeyLastUsed(id, conn);
            return true;
        }
    }

    public bool DeleteApiKey(int id)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM ApiKeys WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    private void UpdateApiKeyLastUsed(int id, SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE ApiKeys SET LastUsedAt=$t WHERE id=$id;";
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  SERVER CONFIG
    // ══════════════════════════════════════════════════════════════════════════

    public string GetConfig(string key, string defaultValue = "")
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Value FROM ServerConfig WHERE Key=$k;";
            cmd.Parameters.AddWithValue("$k", key);
            var result = cmd.ExecuteScalar();
            return result as string ?? defaultValue;
        }
    }

    public void SetConfig(string key, string value)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ServerConfig (Key, Value) VALUES ($k, $v)
                ON CONFLICT(Key) DO UPDATE SET Value=$v;
                """;
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() { }
}
