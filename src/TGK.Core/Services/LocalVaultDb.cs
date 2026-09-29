using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TGK.Protocol.Dtos;

namespace TGK.Core.Services;

/// <summary>How the local vault's key is protected: the master password's KDF inputs and the wrapped vault key.</summary>
internal sealed record LocalVaultKeyInfo(string VaultId, byte[] Salt, KdfParams Kdf, byte[] WrappedVaultKey);

/// <summary>A sealed item (server format) and when this device last connected to it (hosts only).</summary>
internal sealed record LocalVaultRow(string Id, byte[] Data, DateTimeOffset? LastConnected);

/// <summary>
/// The local vault file (SQLite, WAL, 0600 on Unix): <c>meta</c> holds the format version, vault id, KDF salt and
/// parameters, wrapped vault key and creation time; <c>items</c> the sealed items keyed by their server item id.
/// Every write is one transaction. Connections are not pooled, so the file can be replaced or deleted at any time.
/// </summary>
internal sealed class LocalVaultDb(string path)
{
    private const string FormatVersion = "1";

    private const string Schema = """
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value NOT NULL);
        CREATE TABLE IF NOT EXISTS items(id TEXT PRIMARY KEY, data BLOB NOT NULL, updated_at TEXT NOT NULL, last_connected TEXT NULL);
        """;

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    /// <summary>The key info, or null when there is no vault (no file, or one that was never completed).</summary>
    /// <exception cref="SqliteException">Unreadable file.</exception>
    public LocalVaultKeyInfo? ReadKeyInfo()
    {
        if (!File.Exists(Path))
            return null;
        using SqliteConnection connection = Open(create: false);
        var meta = new Dictionary<string, object>();
        using (SqliteCommand command = Command(connection, "SELECT key, value FROM meta"))
        {
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
                meta[reader.GetString(0)] = reader.GetValue(1);
        }
        if (!meta.TryGetValue("version", out object? version))
            return null;
        if (version as string != FormatVersion)
            throw new InvalidDataException($"The local vault has format version {version}; this TGK supports {FormatVersion}.");
        return new LocalVaultKeyInfo(
            (string)meta["vault_id"],
            (byte[])meta["kdf_salt"],
            JsonSerializer.Deserialize<KdfParams>((string)meta["kdf"], TgkJson.Options) ?? throw new InvalidDataException("Missing KDF parameters."),
            (byte[])meta["wrapped_vault_key"]);
    }

    public List<LocalVaultRow> ReadItems()
    {
        using SqliteConnection connection = Open(create: false);
        using SqliteCommand command = Command(connection, "SELECT id, data, last_connected FROM items");
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<LocalVaultRow>();
        while (reader.Read())
            rows.Add(new LocalVaultRow(reader.GetString(0), (byte[])reader.GetValue(1), reader.IsDBNull(2) ? null : ParseTime(reader.GetString(2))));
        return rows;
    }

    /// <summary>
    /// Writes a new vault (key info and items) in one transaction. Returns false, changing nothing, when a vault
    /// exists and <paramref name="replace"/> is not set; with it, the old vault is dropped.
    /// </summary>
    public bool Create(LocalVaultKeyInfo key, IEnumerable<LocalVaultRow> rows, bool replace)
    {
        CreateFile();
        using SqliteConnection connection = Open(create: true);
        Execute(connection, Schema);
        using SqliteTransaction transaction = connection.BeginTransaction();
        if (Scalar(connection, "SELECT COUNT(*) FROM meta WHERE key = 'version'") > 0)
        {
            if (!replace)
                return false;
            Execute(connection, "DELETE FROM meta; DELETE FROM items;");
        }
        string now = Format(DateTimeOffset.UtcNow);
        foreach ((string name, object value) in new (string, object)[]
        {
            ("version", FormatVersion), ("vault_id", key.VaultId), ("kdf_salt", key.Salt), ("kdf", JsonSerializer.Serialize(key.Kdf, TgkJson.Options)),
            ("wrapped_vault_key", key.WrappedVaultKey), ("created_at", now),
        })
            Execute(connection, "INSERT INTO meta(key, value) VALUES($key, $value)", ("$key", name), ("$value", value));
        foreach (LocalVaultRow row in rows)
        {
            Execute(connection, "INSERT INTO items(id, data, updated_at, last_connected) VALUES($id, $data, $now, $at)",
                ("$id", row.Id), ("$data", row.Data), ("$now", now), ("$at", row.LastConnected is { } at ? Format(at) : null));
        }
        transaction.Commit();
        return true;
    }

    /// <summary>Stores upserts and deletes (null data) and last-connected times in one transaction.</summary>
    public void Write(IEnumerable<KeyValuePair<string, byte[]?>> changes, IEnumerable<KeyValuePair<string, DateTimeOffset>> touched)
    {
        using SqliteConnection connection = Open(create: false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        string now = Format(DateTimeOffset.UtcNow);
        foreach ((string id, byte[]? data) in changes)
        {
            if (data is null)
                Execute(connection, "DELETE FROM items WHERE id = $id", ("$id", id));
            else
                Execute(connection, "INSERT INTO items(id, data, updated_at) VALUES($id, $data, $now) ON CONFLICT(id) DO UPDATE SET data = excluded.data, updated_at = excluded.updated_at",
                    ("$id", id), ("$data", data), ("$now", now));
        }
        foreach ((string id, DateTimeOffset at) in touched)
            Execute(connection, "UPDATE items SET last_connected = $at WHERE id = $id", ("$id", id), ("$at", Format(at)));
        transaction.Commit();
    }

    /// <summary>Replaces the key info after a master password change (the vault key and items stay the same).</summary>
    public void UpdateKey(byte[] salt, KdfParams kdf, byte[] wrappedVaultKey)
    {
        using SqliteConnection connection = Open(create: false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        foreach ((string name, object value) in new (string, object)[]
        {
            ("kdf_salt", salt), ("kdf", JsonSerializer.Serialize(kdf, TgkJson.Options)), ("wrapped_vault_key", wrappedVaultKey),
        })
            Execute(connection, "UPDATE meta SET value = $value WHERE key = $key", ("$key", name), ("$value", value));
        transaction.Commit();
    }

    /// <summary>Deletes the file and its WAL files.</summary>
    public void Delete()
    {
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            File.Delete(Path + suffix);
    }

    /// <summary>Creates the (empty) file user-only before SQLite opens it; its WAL and shared-memory files inherit the mode.</summary>
    private void CreateFile()
    {
        if (File.Exists(Path))
            return;
        string directory = System.IO.Path.GetDirectoryName(Path)!;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            File.Create(Path).Dispose();
            return;
        }
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        new FileStream(Path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }).Dispose();
    }

    private SqliteConnection Open(bool create)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = Command(connection, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = Command(connection, sql);
        return (long)command.ExecuteScalar()!;
    }

    private static string Format(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
