using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace TGK.Server;

/// <summary>The SQLite database file: opens connections and applies schema migrations (tracked in <c>PRAGMA user_version</c>).</summary>
public sealed class Db
{
    // Append-only: each entry upgrades the schema by one version.
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE users(
            id TEXT PRIMARY KEY,
            username TEXT NOT NULL UNIQUE COLLATE NOCASE,
            verifier BLOB NOT NULL,
            verifier_salt BLOB NOT NULL,
            kdf_salt BLOB NOT NULL,
            kdf_json TEXT NOT NULL,
            wrapped_vault_key BLOB NOT NULL,
            totp_secret TEXT NULL,
            totp_last_step INTEGER NOT NULL DEFAULT 0,
            revision INTEGER NOT NULL DEFAULT 0,
            disabled INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL);
        CREATE TABLE sessions(
            id TEXT PRIMARY KEY,
            user_id TEXT NOT NULL REFERENCES users ON DELETE CASCADE,
            token_hash BLOB NOT NULL UNIQUE,
            device_name TEXT NOT NULL,
            platform TEXT NOT NULL,
            created_at TEXT NOT NULL,
            last_seen_at TEXT NOT NULL,
            last_ip TEXT NULL,
            expires_at TEXT NOT NULL,
            revoked_at TEXT NULL);
        CREATE INDEX sessions_user ON sessions(user_id);
        CREATE TABLE items(
            user_id TEXT NOT NULL REFERENCES users ON DELETE CASCADE,
            id TEXT NOT NULL,
            revision INTEGER NOT NULL,
            deleted INTEGER NOT NULL,
            data BLOB NULL,
            updated_at TEXT NOT NULL,
            PRIMARY KEY(user_id, id));
        CREATE INDEX items_user_revision ON items(user_id, revision);
        CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """,
        """
        CREATE TABLE invites(
            username TEXT PRIMARY KEY COLLATE NOCASE,
            code_hash BLOB NOT NULL,
            created_at TEXT NOT NULL,
            expires_at TEXT NOT NULL);
        """,
    ];

    private readonly string _connectionString;

    public Db(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        if (!File.Exists(Path))
        {
            IsNew = true;
            // The file holds TOTP secrets in plaintext: keep it private to the service account.
            File.Create(Path).Dispose();
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path, ForeignKeys = true }.ToString();
        Migrate();
    }

    public string Path { get; }

    /// <summary>True when this instance created the database file.</summary>
    public bool IsNew { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>Timestamps are stored as round-trip UTC strings, which also sort chronologically.</summary>
    public static string Format(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private void Migrate()
    {
        using var connection = Open();
        connection.Execute("PRAGMA journal_mode=WAL");
        long version = connection.Scalar<long>("PRAGMA user_version");
        if (version > Migrations.Length)
            throw new InvalidOperationException($"{Path} has schema version {version}; this tgk-server supports up to {Migrations.Length}.");

        for (long v = version; v < Migrations.Length; v++)
        {
            using var transaction = connection.BeginTransaction();
            connection.Execute(Migrations[v]);
            connection.Execute($"PRAGMA user_version = {v + 1}");
            transaction.Commit();
        }

        connection.Execute(
            "INSERT OR IGNORE INTO settings(key, value) VALUES('pepper', $pepper), ('registration_open', '1')",
            ("$pepper", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
    }
}

internal static class SqliteExtensions
{
    public static SqliteCommand Command(this SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public static int Execute(this SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Command(sql, parameters);
        return command.ExecuteNonQuery();
    }

    public static T Scalar<T>(this SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Command(sql, parameters);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }
}
