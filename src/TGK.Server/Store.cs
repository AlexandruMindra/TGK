using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TGK.Protocol;
using TGK.Protocol.Dtos;

namespace TGK.Server;

public sealed record UserRecord(
    string Id,
    string Username,
    byte[] Verifier,
    byte[] VerifierSalt,
    byte[] KdfSalt,
    KdfParams Kdf,
    byte[] WrappedVaultKey,
    string? TotpSecret,
    long TotpLastStep,
    long Revision,
    bool Disabled,
    DateTimeOffset CreatedAt);

/// <summary>A username reserved by <c>tgk-server user create</c> for whoever holds the one-time code.</summary>
public sealed record InviteRecord(string Username, byte[] CodeHash, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public sealed record UserSummary(
    string Username, DateTimeOffset CreatedAt, bool TotpEnrolled, bool Disabled, long Items, long ActiveSessions, DateTimeOffset? LastSeenAt);

/// <summary>All persistence: users, sessions, vault items and settings. Every call uses its own pooled connection.</summary>
public sealed class Store(Db db, TimeProvider clock)
{
    /// <summary>PBKDF2 cost of the stored verifier; not persisted per user, so only the test assembly lowers it.</summary>
    public static int VerifierIterations { get; internal set; } = 100_000;
    public const long DefaultPullPageBytes = 4 * 1024 * 1024;
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);
    private static readonly byte[] DummyVerifierSalt = new byte[16];

    private const string UserColumns =
        "id, username, verifier, verifier_salt, kdf_salt, kdf_json, wrapped_vault_key, totp_secret, totp_last_step, revision, disabled, created_at";

    private byte[]? _pepper;

    private string Now => Db.Format(clock.GetUtcNow());

    // ---- settings ----

    /// <summary>Read on every call so <c>tgk-server registration on|off</c> applies to a running server immediately.</summary>
    public bool RegistrationOpen
    {
        get => GetSetting("registration_open") == "1";
        set => SetSetting("registration_open", value ? "1" : "0");
    }

    /// <summary>Server secret keying the fake prelogin salts of unknown users.</summary>
    public byte[] Pepper => _pepper ??= Convert.FromBase64String(GetSetting("pepper")!);

    private string? GetSetting(string key)
    {
        using var connection = db.Open();
        using var command = connection.Command("SELECT value FROM settings WHERE key = $key", ("$key", key));
        return command.ExecuteScalar() as string;
    }

    private void SetSetting(string key, string value)
    {
        using var connection = db.Open();
        connection.Execute(
            "INSERT INTO settings(key, value) VALUES($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("$key", key), ("$value", value));
    }

    // ---- users ----

    public static byte[] ComputeVerifier(ReadOnlySpan<byte> authKey, ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(authKey, salt, VerifierIterations, HashAlgorithmName.SHA256, 32);

    /// <summary>Checks <paramref name="authKey"/>; an unknown user still costs one PBKDF2 run so both cases take similar time.</summary>
    public static bool CheckAuthKey(UserRecord? user, byte[] authKey)
    {
        byte[] computed = ComputeVerifier(authKey, user?.VerifierSalt ?? DummyVerifierSalt);
        return user is not null && CryptographicOperations.FixedTimeEquals(computed, user.Verifier);
    }

    public UserRecord? FindUser(string username) => QueryUser("username = $value", username);

    public UserRecord? GetUser(string id) => QueryUser("id = $value", id);

    /// <summary>Creates a user and consumes any invite for the name; returns null when the username is taken (case-insensitively).</summary>
    public UserRecord? CreateUser(
        string username, byte[] authKey, byte[] kdfSalt, KdfParams kdf, byte[] wrappedVaultKey, string? totpSecret, long totpStep)
    {
        var now = clock.GetUtcNow();
        byte[] verifierSalt = RandomNumberGenerator.GetBytes(16);
        var user = new UserRecord(Guid.CreateVersion7().ToString(), username, ComputeVerifier(authKey, verifierSalt), verifierSalt,
            kdfSalt, kdf, wrappedVaultKey, totpSecret, totpStep, 0, false, now);
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            connection.Execute(
                $"INSERT INTO users({UserColumns}, updated_at) VALUES($id, $username, $verifier, $verifierSalt, $kdfSalt, $kdf, $wrapped, $totp, $step, 0, 0, $now, $now)",
                ("$id", user.Id), ("$username", username), ("$verifier", user.Verifier), ("$verifierSalt", verifierSalt),
                ("$kdfSalt", kdfSalt), ("$kdf", JsonSerializer.Serialize(kdf, ProtocolJson.Options)), ("$wrapped", wrappedVaultKey),
                ("$totp", totpSecret), ("$step", totpStep), ("$now", Db.Format(now)));
            connection.Execute("DELETE FROM invites WHERE username = $username", ("$username", username));
            transaction.Commit();
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) // SQLITE_CONSTRAINT: username taken
        {
            return null;
        }
        return user;
    }

    /// <summary>Records an accepted TOTP step; false if an equal or later step was already used (a replay race).</summary>
    public bool AdvanceTotpStep(string userId, long step) => Update(
        "UPDATE users SET totp_last_step = $step WHERE id = $id AND totp_last_step < $step", ("$id", userId), ("$step", step)) == 1;

    /// <summary>Enrolls an authenticator for a user without one (after <see cref="ResetTotp"/>).</summary>
    public bool EnrollTotp(string userId, string secret, long step) => Update(
        "UPDATE users SET totp_secret = $secret, totp_last_step = $step, updated_at = $now WHERE id = $id AND totp_secret IS NULL",
        ("$id", userId), ("$secret", secret), ("$step", step), ("$now", Now)) == 1;

    public void ResetTotp(string userId) => Update(
        "UPDATE users SET totp_secret = NULL, totp_last_step = 0, updated_at = $now WHERE id = $id", ("$id", userId), ("$now", Now));

    public void SetDisabled(string userId, bool disabled) => Update(
        "UPDATE users SET disabled = $disabled, updated_at = $now WHERE id = $id", ("$id", userId), ("$disabled", disabled ? 1 : 0), ("$now", Now));

    public void DeleteUser(string userId) => Update("DELETE FROM users WHERE id = $id", ("$id", userId));

    /// <summary>Swaps the login verifier and the re-wrapped vault key; the vault key itself (and every item) is unchanged.</summary>
    public void ChangePassword(string userId, byte[] authKey, byte[] kdfSalt, KdfParams kdf, byte[] wrappedVaultKey)
    {
        byte[] verifierSalt = RandomNumberGenerator.GetBytes(16);
        Update(
            """
            UPDATE users SET verifier = $verifier, verifier_salt = $verifierSalt, kdf_salt = $kdfSalt, kdf_json = $kdf,
                wrapped_vault_key = $wrapped, updated_at = $now WHERE id = $id
            """,
            ("$id", userId), ("$verifier", ComputeVerifier(authKey, verifierSalt)), ("$verifierSalt", verifierSalt), ("$kdfSalt", kdfSalt),
            ("$kdf", JsonSerializer.Serialize(kdf, ProtocolJson.Options)), ("$wrapped", wrappedVaultKey), ("$now", Now));
    }

    public IReadOnlyList<UserSummary> ListUsers()
    {
        using var connection = db.Open();
        using var command = connection.Command(
            """
            SELECT u.username, u.created_at, u.totp_secret IS NOT NULL, u.disabled,
                (SELECT COUNT(*) FROM items i WHERE i.user_id = u.id AND i.deleted = 0),
                (SELECT COUNT(*) FROM sessions s WHERE s.user_id = u.id AND s.revoked_at IS NULL AND s.expires_at > $now),
                (SELECT MAX(s.last_seen_at) FROM sessions s WHERE s.user_id = u.id)
            FROM users u ORDER BY u.username
            """,
            ("$now", Now));
        using var reader = command.ExecuteReader();
        var users = new List<UserSummary>();
        while (reader.Read())
        {
            users.Add(new UserSummary(reader.GetString(0), Db.Parse(reader.GetString(1)), reader.GetBoolean(2), reader.GetBoolean(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : Db.Parse(reader.GetString(6))));
        }
        return users;
    }

    /// <summary>Live and deleted (tombstone) item counts.</summary>
    public (long Live, long Deleted) CountItems(string userId)
    {
        using var connection = db.Open();
        using var command = connection.Command(
            "SELECT COUNT(*) - COALESCE(SUM(deleted), 0), COALESCE(SUM(deleted), 0) FROM items WHERE user_id = $user", ("$user", userId));
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private UserRecord? QueryUser(string where, string value)
    {
        using var connection = db.Open();
        using var command = connection.Command($"SELECT {UserColumns} FROM users WHERE {where}", ("$value", value));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new UserRecord(
            reader.GetString(0), reader.GetString(1), reader.GetFieldValue<byte[]>(2), reader.GetFieldValue<byte[]>(3),
            reader.GetFieldValue<byte[]>(4), JsonSerializer.Deserialize<KdfParams>(reader.GetString(5), ProtocolJson.Options)!,
            reader.GetFieldValue<byte[]>(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt64(8), reader.GetInt64(9),
            reader.GetBoolean(10), Db.Parse(reader.GetString(11)));
    }

    // ---- invites ----

    /// <summary>
    /// Reserves <paramref name="username"/> (replacing an earlier invite) and returns the one-time code that lets the
    /// user create the account from the client. Only the code's hash is stored.
    /// </summary>
    public string CreateInvite(string username)
    {
        string code = Base32.Encode(RandomNumberGenerator.GetBytes(10)); // 16 characters, 80 bits
        var now = clock.GetUtcNow();
        Update(
            """
            INSERT INTO invites(username, code_hash, created_at, expires_at) VALUES($username, $hash, $now, $expires)
            ON CONFLICT(username) DO UPDATE SET code_hash = excluded.code_hash, created_at = excluded.created_at, expires_at = excluded.expires_at
            """,
            ("$username", username), ("$hash", HashInviteCode(code)), ("$now", Db.Format(now)), ("$expires", Db.Format(now + InviteLifetime)));
        return string.Join('-', code.Chunk(4).Select(chunk => new string(chunk)));
    }

    /// <summary>The unexpired invite for <paramref name="username"/>, if any.</summary>
    public InviteRecord? FindInvite(string username) => ListInvites(username).FirstOrDefault();

    public IReadOnlyList<InviteRecord> ListInvites() => ListInvites(null);

    public bool DeleteInvite(string username) => Update("DELETE FROM invites WHERE username = $username", ("$username", username)) == 1;

    /// <summary>Compares a code as typed (any case, with or without dashes and spaces) with the invite's.</summary>
    public static bool CheckInviteCode(InviteRecord invite, string code) =>
        CryptographicOperations.FixedTimeEquals(HashInviteCode(code), invite.CodeHash);

    private static byte[] HashInviteCode(string code) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(code.Where(char.IsLetterOrDigit)).ToUpperInvariant()));

    private IReadOnlyList<InviteRecord> ListInvites(string? username)
    {
        using var connection = db.Open();
        using var command = connection.Command(
            """
            SELECT username, code_hash, created_at, expires_at FROM invites
            WHERE expires_at > $now AND ($username IS NULL OR username = $username) ORDER BY username
            """,
            ("$now", Now), ("$username", username));
        using var reader = command.ExecuteReader();
        var invites = new List<InviteRecord>();
        while (reader.Read())
        {
            invites.Add(new InviteRecord(reader.GetString(0), reader.GetFieldValue<byte[]>(1), Db.Parse(reader.GetString(2)),
                Db.Parse(reader.GetString(3))));
        }
        return invites;
    }

    // ---- sessions ----

    /// <summary>Starts a session; only the SHA-256 of the returned bearer token is stored. Also prunes the user's dead sessions.</summary>
    public (string Token, string SessionId) CreateSession(string userId, string deviceName, string platform, string? ip)
    {
        string token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        string id = Guid.NewGuid().ToString("N");
        var now = clock.GetUtcNow();
        using var connection = db.Open();
        connection.Execute(
            "DELETE FROM sessions WHERE user_id = $user AND (revoked_at IS NOT NULL OR expires_at <= $now)",
            ("$user", userId), ("$now", Db.Format(now)));
        connection.Execute(
            """
            INSERT INTO sessions(id, user_id, token_hash, device_name, platform, created_at, last_seen_at, last_ip, expires_at)
            VALUES($id, $user, $hash, $device, $platform, $now, $now, $ip, $expires)
            """,
            ("$id", id), ("$user", userId), ("$hash", HashToken(token)), ("$device", deviceName), ("$platform", platform),
            ("$now", Db.Format(now)), ("$ip", ip), ("$expires", Db.Format(now + SessionLifetime)));
        return (token, id);
    }

    /// <summary>Resolves a bearer token to a live session of an enabled user, sliding its expiry (at most once a minute).</summary>
    public AuthSession? Authenticate(string token, string? ip)
    {
        var now = clock.GetUtcNow();
        using var connection = db.Open();
        AuthSession session;
        DateTimeOffset lastSeen;
        using (var command = connection.Command(
            """
            SELECT s.id, s.user_id, u.username, s.last_seen_at FROM sessions s JOIN users u ON u.id = s.user_id
            WHERE s.token_hash = $hash AND s.revoked_at IS NULL AND s.expires_at > $now AND u.disabled = 0
            """,
            ("$hash", HashToken(token)), ("$now", Db.Format(now))))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
                return null;
            session = new AuthSession(reader.GetString(0), reader.GetString(1), reader.GetString(2));
            lastSeen = Db.Parse(reader.GetString(3));
        }

        if (now - lastSeen >= TouchInterval)
        {
            connection.Execute(
                "UPDATE sessions SET last_seen_at = $now, last_ip = $ip, expires_at = $expires WHERE id = $id",
                ("$id", session.SessionId), ("$now", Db.Format(now)), ("$ip", ip), ("$expires", Db.Format(now + SessionLifetime)));
        }
        return session;
    }

    /// <summary>The user's live sessions, most recently used first.</summary>
    public IReadOnlyList<SessionInfo> ListSessions(string userId, string? currentSessionId)
    {
        using var connection = db.Open();
        using var command = connection.Command(
            """
            SELECT id, device_name, platform, created_at, last_seen_at, last_ip FROM sessions
            WHERE user_id = $user AND revoked_at IS NULL AND expires_at > $now ORDER BY last_seen_at DESC
            """,
            ("$user", userId), ("$now", Now));
        using var reader = command.ExecuteReader();
        var sessions = new List<SessionInfo>();
        while (reader.Read())
        {
            string id = reader.GetString(0);
            sessions.Add(new SessionInfo(id, reader.GetString(1), reader.GetString(2), Db.Parse(reader.GetString(3)),
                Db.Parse(reader.GetString(4)), reader.IsDBNull(5) ? null : reader.GetString(5), id == currentSessionId));
        }
        return sessions;
    }

    /// <summary>Revokes one of the user's live sessions; false if there is no such session.</summary>
    public bool RevokeSession(string userId, string sessionId) => Update(
        "UPDATE sessions SET revoked_at = $now WHERE id = $id AND user_id = $user AND revoked_at IS NULL AND expires_at > $now",
        ("$id", sessionId), ("$user", userId), ("$now", Now)) == 1;

    /// <summary>Revokes all of the user's live sessions except <paramref name="exceptSessionId"/>; returns how many.</summary>
    public int RevokeSessions(string userId, string? exceptSessionId = null) => Update(
        """
        UPDATE sessions SET revoked_at = $now
        WHERE user_id = $user AND revoked_at IS NULL AND expires_at > $now AND ($except IS NULL OR id <> $except)
        """,
        ("$user", userId), ("$except", exceptSessionId), ("$now", Now));

    private static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    // ---- vault ----

    /// <summary>
    /// Items changed after <paramref name="since"/>, oldest first. A page stops after about <paramref name="maxBytes"/> of
    /// data; then <see cref="VaultPullResponse.HasMore"/> is set and its revision is that of the last item returned.
    /// </summary>
    public VaultPullResponse Pull(string userId, long since, long maxBytes = DefaultPullPageBytes)
    {
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction(deferred: true); // one snapshot for the revision and the items
        long revision = connection.Scalar<long>("SELECT revision FROM users WHERE id = $user", ("$user", userId));
        var items = new List<VaultItem>();
        long bytes = 0;
        bool hasMore = false;
        using (var command = connection.Command(
            "SELECT id, revision, deleted, data, updated_at FROM items WHERE user_id = $user AND revision > $since ORDER BY revision",
            ("$user", userId), ("$since", since)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                long size = (reader.IsDBNull(3) ? 0 : reader.GetBytes(3, 0, null, 0, 0)) + 64;
                if (items.Count > 0 && bytes + size > maxBytes)
                {
                    hasMore = true;
                    break;
                }
                bytes += size;
                items.Add(new VaultItem(reader.GetString(0), reader.GetInt64(1), reader.GetBoolean(2),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<byte[]>(3), Db.Parse(reader.GetString(4))));
            }
        }
        transaction.Commit();
        return new VaultPullResponse(hasMore ? items[^1].Revision : revision, items, hasMore);
    }

    /// <summary>
    /// Applies the changes in order, last write wins; each one takes the next user revision. All or nothing: returns
    /// null (and changes nothing) when the vault would grow beyond <paramref name="maxItems"/> items (tombstones
    /// included) or <paramref name="maxBytes"/> of data.
    /// </summary>
    public VaultPushResponse? Push(string userId, IReadOnlyList<VaultChange> changes, long maxItems, long maxBytes)
    {
        string now = Now;
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();
        var before = Usage(connection, userId);
        long revision = connection.Scalar<long>("SELECT revision FROM users WHERE id = $user", ("$user", userId));
        var applied = new List<AppliedChange>(changes.Count);
        foreach (var change in changes)
        {
            revision++;
            connection.Execute(
                """
                INSERT INTO items(user_id, id, revision, deleted, data, updated_at) VALUES($user, $id, $revision, $deleted, $data, $now)
                ON CONFLICT(user_id, id) DO UPDATE SET
                    revision = excluded.revision, deleted = excluded.deleted, data = excluded.data, updated_at = excluded.updated_at
                """,
                ("$user", userId), ("$id", change.Id), ("$revision", revision), ("$deleted", change.Data is null ? 1 : 0),
                ("$data", change.Data), ("$now", now));
            applied.Add(new AppliedChange(change.Id, revision));
        }
        var after = Usage(connection, userId);
        // Over the limit only refuses growth, so a full vault can still be edited down.
        if ((after.Items > maxItems && after.Items > before.Items) || (after.Bytes > maxBytes && after.Bytes > before.Bytes))
            return null;
        connection.Execute("UPDATE users SET revision = $revision WHERE id = $user", ("$user", userId), ("$revision", revision));
        transaction.Commit();
        return new VaultPushResponse(revision, applied);
    }

    private static (long Items, long Bytes) Usage(SqliteConnection connection, string userId)
    {
        using var command = connection.Command(
            "SELECT COUNT(*), COALESCE(SUM(LENGTH(data)), 0) FROM items WHERE user_id = $user", ("$user", userId));
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private int Update(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = db.Open();
        return connection.Execute(sql, parameters);
    }
}
