using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TGK.Core.Models;
using TGK.Protocol;
using TGK.Protocol.Dtos;

namespace TGK.Core.Services;

public sealed class LocalVaultOptions
{
    /// <summary>Where the vault file (and the credentials file fallback) live; defaults to <see cref="AppPaths.ConfigDirectory"/>.</summary>
    public string? BaseDirectory { get; init; }

    /// <summary>
    /// Holds the vault key for "keep unlocked on this device". Defaults to a <see cref="DeviceCredentialStore"/> in
    /// <see cref="BaseDirectory"/> using the local vault's own entry, separate from the server sign-in.
    /// </summary>
    public IDeviceCredentialStore? Credentials { get; init; }

    /// <summary>KDF for new vaults, master password changes and backups; tests lower it to the minimum accepted.</summary>
    public KdfParams NewKdf { get; init; } = KdfParams.Default;

    /// <summary>Wait after the third and each later wrong master password in a row; doubles each time, up to 30 s.</summary>
    public TimeSpan WrongPasswordDelay { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// The vault kept only on this device, for using TGK without a server: an SQLite file (<see cref="FileName"/>)
/// protected by a master password. Items are sealed exactly like on a server (same codec, item ids and vault key
/// wrapping), so a local vault can become a server account's vault as it is. Nothing is synced: a change is written
/// to the file right after it is published. Logging out (<see cref="LogoutAsync"/>) locks the vault.
/// </summary>
/// <remarks>
/// Server-only members are harmless: <see cref="GetServerInfoAsync"/> returns null, <see cref="ListSessionsAsync"/> is
/// empty, <see cref="SyncAsync"/> waits for pending writes; login, registration, TOTP, session revocation and the
/// account password throw <see cref="NotSupportedException"/> (use <see cref="ChangeMasterPasswordAsync"/>).
/// <see cref="Status"/> is <see cref="SyncState.Idle"/> while unlocked, <see cref="SyncState.Syncing"/> while a write is
/// in progress and <see cref="SyncState.Error"/> after a failed write (retried with the next change).
/// </remarks>
public sealed class LocalVaultService : IVaultService, IVaultEditor, IDisposable
{
    public const string FileName = "local-vault.db";

    /// <summary>Minimum master (and backup) password length.</summary>
    public const int MinPasswordLength = 10;

    private const string LocalMarker = "local";
    private static readonly TimeSpan MaxWrongPasswordDelay = TimeSpan.FromSeconds(30);

    private readonly LocalVaultDb _db;
    private readonly IDeviceCredentialStore _credentials;
    private readonly Lock _gate = new();

    // Guarded by _gate.
    private Unlocked? _vault;
    private VaultData _current = new();
    private string? _lastError;
    private DateTimeOffset? _lastSaved;
    private int _wrongPasswords;

    public LocalVaultService(LocalVaultOptions? options = null)
    {
        Options = options ?? new LocalVaultOptions();
        string directory = Options.BaseDirectory ?? AppPaths.ConfigDirectory;
        _db = new LocalVaultDb(Path.Combine(directory, FileName));
        _credentials = Options.Credentials ?? new DeviceCredentialStore(directory, localVault: true);
    }

    public event Action? Changed;

    // A local vault is never ended from elsewhere.
    public event Action<string>? SessionEnded { add { } remove { } }

    public LocalVaultOptions Options { get; }

    public string FilePath => _db.Path;

    /// <summary>True when this device has a local vault (to unlock) rather than none (to create). Reads the file.</summary>
    public bool Exists
    {
        get
        {
            try
            {
                return _db.ReadKeyInfo() is not null;
            }
            catch (Exception ex) when (ex is SqliteException or InvalidDataException or IOException or InvalidCastException or KeyNotFoundException or JsonException)
            {
                return File.Exists(FilePath); // there is a (damaged) vault: don't offer to create one over it
            }
        }
    }

    public VaultMode Mode => VaultMode.Local;
    public bool IsLoggedIn { get { lock (_gate) return _vault is not null; } }
    public string? CurrentUser => null;
    public string? ServerUrl => null;
    public VaultData Current { get { lock (_gate) return _current; } }
    public DateTimeOffset? LastSync { get { lock (_gate) return _lastSaved; } }
    public string? LastError { get { lock (_gate) return _lastError; } }
    public int PendingChanges { get { lock (_gate) return _vault is null ? 0 : _vault.Unsaved.Count + _vault.Writing; } }

    public SyncState Status
    {
        get
        {
            lock (_gate)
            {
                return _vault is null ? SyncState.Offline
                    : _lastError is not null ? SyncState.Error
                    : _vault.Writing > 0 ? SyncState.Syncing
                    : SyncState.Idle;
            }
        }
    }

    /// <summary>Waits for pending writes (call on exit).</summary>
    public void Dispose()
    {
        Task? writes;
        lock (_gate)
            writes = _vault?.Writes;
        if (writes is not null)
            Task.WhenAny(writes, Task.Delay(TimeSpan.FromSeconds(5))).Wait();
    }

    // ---- Create, unlock, lock ----

    /// <summary>
    /// Creates an empty local vault protected by <paramref name="masterPassword"/> and unlocks it. Fails (without
    /// changing anything) when this device already has one. With <paramref name="keepUnlocked"/> the vault key is
    /// stored on this device and <see cref="TryRestoreSessionAsync"/> unlocks the vault at the next start.
    /// </summary>
    public Task<LoginResult> CreateAsync(string masterPassword, bool keepUnlocked, CancellationToken ct = default) =>
        CreateAsync(masterPassword, new VaultData(), replace: false, unlock: true, keepUnlocked, ct);

    /// <summary>
    /// Writes a new local vault holding <paramref name="data"/> under a new vault key, replacing an existing one only
    /// with <paramref name="replace"/>; then unlocks it, or with <paramref name="unlock"/> false leaves it locked.
    /// </summary>
    internal async Task<LoginResult> CreateAsync(string masterPassword, VaultData data, bool replace, bool unlock, bool keepUnlocked, CancellationToken ct)
    {
        if (CheckNewPassword(masterPassword) is { } problem)
            return LoginResult.Fail(VaultError.ValidationFailed, problem);
        if (IsLoggedIn)
            return LoginResult.Fail(VaultError.ValidationFailed, "Lock the local vault first.");

        byte[] salt = VaultCrypto.NewSalt();
        KdfParams kdf = Options.NewKdf;
        byte[] vaultKey = VaultCrypto.NewVaultKey();
        var codec = new VaultItems(vaultKey);
        var rows = new List<LocalVaultRow>();
        foreach (object entity in Entities(data))
        {
            string id = codec.IdOf(entity);
            rows.Add(new LocalVaultRow(id, codec.Encrypt(id, entity), (entity as HostEntry)?.LastConnected));
        }
        byte[] kek = await Task.Run(() => VaultCrypto.DeriveKeys(masterPassword, salt, kdf).Kek, ct).ConfigureAwait(false);
        var key = new LocalVaultKeyInfo(Guid.NewGuid().ToString("D"), salt, kdf, VaultCrypto.WrapVaultKey(kek, vaultKey));
        CryptographicOperations.ZeroMemory(kek);
        try
        {
            if (!await Task.Run(() => _db.Create(key, rows, replace), ct).ConfigureAwait(false))
                return LoginResult.Fail(VaultError.ValidationFailed, "This device already has a local vault.");
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return LoginResult.Fail(VaultError.Storage, $"Could not create the local vault: {ex.Message}");
        }

        if (!unlock)
        {
            ClearKeepUnlocked(); // a key kept for the replaced vault
            CryptographicOperations.ZeroMemory(vaultKey);
            return LoginResult.Ok;
        }
        Activate(new Unlocked(key.VaultId, vaultKey, keepUnlocked), Load(codec, rows), persist: true);
        return LoginResult.Ok;
    }

    /// <summary>
    /// Unlocks the vault with its master password. A wrong password returns <see cref="VaultError.InvalidCredentials"/>
    /// (after a growing delay from the third wrong one in a row); items that cannot be read are skipped and logged.
    /// </summary>
    public async Task<LoginResult> UnlockAsync(string masterPassword, bool keepUnlocked, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(masterPassword))
            return LoginResult.Fail(VaultError.ValidationFailed, "Enter your master password.");
        await LogoutAsync().ConfigureAwait(false);
        LocalVaultKeyInfo? key;
        List<LocalVaultRow> rows;
        try
        {
            key = await Task.Run(_db.ReadKeyInfo, ct).ConfigureAwait(false);
            if (key is null)
                return LoginResult.Fail(VaultError.NotFound, "This device has no local vault yet.");
            if (key.Salt.Length != VaultCrypto.SaltSize || !key.Kdf.IsAcceptable())
                return LoginResult.Fail(VaultError.IncompatibleServer, "The local vault file asks for unsafe password-hashing parameters.");
            rows = await Task.Run(_db.ReadItems, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException or IOException or UnauthorizedAccessException or InvalidCastException or KeyNotFoundException or JsonException)
        {
            return LoginResult.Fail(VaultError.Storage, $"Could not read the local vault: {ex.Message}");
        }

        byte[]? vaultKey = await Task.Run(() => TryUnwrap(masterPassword, key), ct).ConfigureAwait(false);
        if (vaultKey is null)
        {
            int failures;
            lock (_gate)
                failures = ++_wrongPasswords;
            if (failures >= 3)
            {
                TimeSpan delay = Options.WrongPasswordDelay * Math.Pow(2, Math.Min(failures - 3, 5));
                await Task.Delay(delay < MaxWrongPasswordDelay ? delay : MaxWrongPasswordDelay, ct).ConfigureAwait(false);
            }
            return LoginResult.Fail(VaultError.InvalidCredentials, "Wrong master password.");
        }
        lock (_gate)
            _wrongPasswords = 0;

        VaultData data = await Task.Run(() => Load(new VaultItems(vaultKey), rows), ct).ConfigureAwait(false);
        Activate(new Unlocked(key.VaultId, vaultKey, keepUnlocked), data, persist: true);
        return LoginResult.Ok;
    }

    /// <summary>Unlocks with the vault key kept on this device ("keep unlocked"); false when none is kept (or it is stale).</summary>
    public async Task<bool> TryRestoreSessionAsync(CancellationToken ct = default)
    {
        if (!File.Exists(FilePath))
            return false; // don't touch the keyring for devices that never used a local vault
        (Unlocked Vault, VaultData Data)? restored = await Task.Run(() =>
        {
            DeviceCredentials? stored = _credentials.Load();
            if (stored is null)
                return null;
            try
            {
                LocalVaultKeyInfo? key = _db.ReadKeyInfo();
                if (key is null || stored.ServerUrl != LocalMarker || stored.UserId != key.VaultId || stored.VaultKey.Length != VaultCrypto.KeySize)
                {
                    _credentials.Clear(); // kept for a vault that was replaced or deleted
                    return null;
                }
                return ((Unlocked, VaultData)?)(new Unlocked(key.VaultId, stored.VaultKey, keep: true), Load(new VaultItems(stored.VaultKey), _db.ReadItems()));
            }
            catch (Exception ex) when (ex is SqliteException or InvalidDataException or IOException or UnauthorizedAccessException or InvalidCastException or KeyNotFoundException or JsonException)
            {
                CoreLog.Warn($"Could not open the local vault: {ex.Message}");
                return null;
            }
        }, ct).ConfigureAwait(false);
        if (restored is not { } r)
            return false;
        Activate(r.Vault, r.Data, persist: false);
        return true;
    }

    /// <summary>Locks the vault: waits for pending writes, forgets the key and the "keep unlocked" entry.</summary>
    public async Task LogoutAsync()
    {
        Unlocked? vault;
        lock (_gate)
            vault = _vault;
        if (vault is null)
            return;
        await Task.WhenAny(vault.Writes, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        lock (_gate)
        {
            if (_vault != vault)
                return;
            _vault = null;
            _current = new VaultData { Revision = _current.Revision + 1 };
            _lastError = null;
            _lastSaved = null;
        }
        CryptographicOperations.ZeroMemory(vault.Key);
        if (vault.Keep)
            await Task.Run(ClearKeepUnlocked).ConfigureAwait(false);
        RaiseChanged();
    }

    /// <summary>
    /// Changes the master password: only the vault key's wrapping changes (items and a kept unlock stay valid).
    /// Throws <see cref="VaultException"/> with <see cref="VaultError.InvalidCredentials"/> for a wrong current
    /// password, <see cref="VaultError.ValidationFailed"/> for a too short new one.
    /// </summary>
    public async Task ChangeMasterPasswordAsync(string currentPassword, string newPassword, CancellationToken ct = default)
    {
        if (CheckNewPassword(newPassword) is { } problem)
            throw new VaultException(VaultError.ValidationFailed, problem);
        Unlocked vault = RequireVault();
        byte[] salt = VaultCrypto.NewSalt();
        KdfParams kdf = Options.NewKdf;
        await Task.Run(() =>
        {
            LocalVaultKeyInfo key = _db.ReadKeyInfo() ?? throw new VaultException(VaultError.NotFound, "The local vault file is missing.");
            byte[]? current = TryUnwrap(currentPassword ?? "", key);
            if (current is null || !CryptographicOperations.FixedTimeEquals(current, vault.Key))
                throw new VaultException(VaultError.InvalidCredentials, "The current master password is wrong.");
            CryptographicOperations.ZeroMemory(current);
            byte[] kek = VaultCrypto.DeriveKeys(newPassword, salt, kdf).Kek;
            _db.UpdateKey(salt, kdf, VaultCrypto.WrapVaultKey(kek, vault.Key));
            CryptographicOperations.ZeroMemory(kek);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes the local vault file (and a kept unlock). The vault must be locked.</summary>
    /// <exception cref="IOException">The file could not be deleted.</exception>
    public void DeleteVault()
    {
        if (IsLoggedIn)
            throw new InvalidOperationException("Lock the local vault first.");
        ClearKeepUnlocked();
        _db.Delete();
    }

    /// <summary>The vault key and every stored item as it is (sealed), after pending writes. The vault must be unlocked.</summary>
    internal async Task<(byte[] VaultKey, List<KeyValuePair<string, byte[]>> Items)> ExportSealedAsync()
    {
        Unlocked vault = RequireVault();
        await vault.Writes.ConfigureAwait(false);
        lock (_gate)
        {
            if (vault.Unsaved.Count > 0)
                throw new VaultException(VaultError.Storage, $"Some changes could not be saved to the local vault: {_lastError}");
        }
        List<LocalVaultRow> rows = await Task.Run(_db.ReadItems).ConfigureAwait(false);
        return ((byte[])vault.Key.Clone(), rows.ConvertAll(r => new KeyValuePair<string, byte[]>(r.Id, r.Data)));
    }

    // ---- Server-only members ----

    public Task<ServerInfo?> GetServerInfoAsync(string serverUrl, CancellationToken ct = default) => Task.FromResult<ServerInfo?>(null);

    public Task<LoginResult> LoginAsync(string serverUrl, string username, string password, string? totpCode, bool keepSignedIn, CancellationToken ct = default) =>
        throw NotServer();

    public Task<LoginResult> CompleteTotpSetupAsync(string serverUrl, string username, string password, string newSecret, string code, bool keepSignedIn, CancellationToken ct = default) =>
        throw NotServer();

    public TotpEnrollment BeginTotpEnrollment(string username) => throw NotServer();

    public Task<LoginResult> RegisterAsync(string serverUrl, string username, string password, string? inviteCode, string totpSecret, string totpCode, bool keepSignedIn, CancellationToken ct = default) =>
        throw NotServer();

    public Task<IReadOnlyList<DeviceSession>> ListSessionsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DeviceSession>>([]);

    public Task RevokeSessionAsync(string sessionId, CancellationToken ct = default) => throw NotServer();

    public Task<int> RevokeOtherSessionsAsync(CancellationToken ct = default) => Task.FromResult(0);

    public Task ChangePasswordAsync(string currentPassword, string totpCode, string newPassword, bool revokeOtherSessions, CancellationToken ct = default) =>
        throw new NotSupportedException("The local vault has a master password: use ChangeMasterPasswordAsync.");

    /// <summary>Nothing to sync: waits until pending changes are written (and retries a failed write).</summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        Task writes;
        lock (_gate)
        {
            Unlocked vault = _vault ?? throw new InvalidOperationException("The local vault is locked.");
            writes = vault.Unsaved.Count > 0 ? Save(vault, []) : vault.Writes;
        }
        await writes.WaitAsync(ct).ConfigureAwait(false);
    }

    // ---- Vault mutations ----

    public Task SaveHostAsync(HostEntry host) => MutateAsync(VaultEdits.SaveHost(host));

    public Task DeleteHostAsync(Guid hostId) => MutateAsync(VaultEdits.DeleteHost(hostId));

    public Task SaveGroupAsync(HostGroup group) => MutateAsync(VaultEdits.SaveGroup(group));

    public Task DeleteGroupAsync(Guid groupId) => MutateAsync(VaultEdits.DeleteGroup(groupId));

    public Task SaveIdentityAsync(Identity identity) => MutateAsync(VaultEdits.SaveIdentity(identity));

    public Task DeleteIdentityAsync(Guid identityId) => MutateAsync(VaultEdits.DeleteIdentity(identityId));

    public Task SaveDefaultsAsync(HostOptions defaults) => MutateAsync(VaultEdits.SaveDefaults(defaults));

    public Task AddKnownHostAsync(KnownHost knownHost) => MutateAsync(VaultEdits.AddKnownHost(knownHost));

    Task IVaultEditor.EditAsync(Action<VaultData> edit) => MutateAsync(edit);

    public Task TouchHostAsync(Guid hostId)
    {
        Unlocked vault;
        Task written;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            vault = _vault ?? throw new InvalidOperationException("The local vault is locked.");
            if (_current.FindHost(hostId) is null)
                return Task.CompletedTask;
            VaultData next = _current.ShallowCopy();
            VaultEdits.SetLastConnected(next, hostId, now);
            Publish(next);
            written = Save(vault, [], (VaultItems.IdOf(hostId), now));
        }
        RaiseChanged();
        return written;
    }

    /// <summary>Publishes the edited snapshot and writes the changed items (sealed) to the file.</summary>
    private Task MutateAsync(Action<VaultData> edit)
    {
        Task written;
        lock (_gate)
        {
            Unlocked vault = _vault ?? throw new InvalidOperationException("The local vault is locked.");
            VaultData next = _current.ShallowCopy();
            edit(next);
            var changes = new List<KeyValuePair<string, byte[]?>>();
            foreach (ItemChange change in vault.Codec.Diff(_current, next))
            {
                byte[]? data = change.Entity is null ? null : vault.Codec.Encrypt(change.Id, change.Entity);
                // The server's limit: a local vault must stay uploadable.
                if (data?.Length > ProtocolConstants.MaxItemBytes)
                    throw new ArgumentException($"This entry is too large to save (limit {ProtocolConstants.MaxItemBytes / 1024} KB).");
                changes.Add(new(change.Id, data));
            }
            if (changes.Count == 0)
                return Task.CompletedTask;
            Publish(next);
            written = Save(vault, changes);
        }
        RaiseChanged();
        return written;
    }

    /// <summary>
    /// Queues a write of <paramref name="changes"/> (plus what an earlier failed write left unsaved) after the writes
    /// already queued, so the file always ends up with the latest state. Caller holds <see cref="_gate"/>.
    /// </summary>
    private Task Save(Unlocked vault, List<KeyValuePair<string, byte[]?>> changes, (string Id, DateTimeOffset At)? touched = null)
    {
        vault.Writing++;
        vault.Writes = vault.Writes.ContinueWith(_ => Write(vault, changes, touched), CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default);
        return vault.Writes;
    }

    private void Write(Unlocked vault, List<KeyValuePair<string, byte[]?>> changes, (string Id, DateTimeOffset At)? touched)
    {
        Dictionary<string, byte[]?> batch;
        lock (_gate)
        {
            batch = new Dictionary<string, byte[]?>(vault.Unsaved);
            foreach ((string id, byte[]? data) in changes)
                batch[id] = data;
        }
        string? error = null;
        try
        {
            _db.Write(batch, touched is { } t ? [new(t.Id, t.At)] : []);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            error = $"Could not save to the local vault: {ex.Message}";
        }
        lock (_gate)
        {
            vault.Writing--;
            if (error is null)
                vault.Unsaved.Clear();
            else
            {
                if (vault.Unsaved.Count == 0)
                    CoreLog.Warn(error);
                foreach ((string id, byte[]? data) in batch)
                    vault.Unsaved[id] = data;
            }
            if (_vault == vault)
            {
                _lastError = error;
                if (error is null)
                    _lastSaved = DateTimeOffset.UtcNow;
            }
        }
        RaiseChanged();
    }

    // ---- Helpers ----

    /// <summary>Makes <paramref name="vault"/> the unlocked one and, with <paramref name="persist"/>, stores or clears the kept unlock.</summary>
    private void Activate(Unlocked vault, VaultData data, bool persist)
    {
        if (persist)
        {
            try
            {
                if (vault.Keep)
                    _credentials.Save(new DeviceCredentials(LocalMarker, "", vault.VaultId, "", "", vault.Key));
                else
                    _credentials.Clear();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CoreLog.Warn($"Could not keep the local vault unlocked on this device: {ex.Message}");
            }
        }
        lock (_gate)
        {
            data.Revision = _current.Revision + 1;
            data.UpdatedAt = DateTimeOffset.UtcNow;
            _vault = vault;
            _current = data;
            _lastError = null;
            _lastSaved = DateTimeOffset.UtcNow;
        }
        RaiseChanged();
    }

    /// <summary>Decrypts the stored items (unreadable ones are skipped and logged) and applies the last-connected times.</summary>
    private static VaultData Load(VaultItems codec, List<LocalVaultRow> rows)
    {
        var data = new VaultData();
        foreach (LocalVaultRow row in rows)
        {
            try
            {
                if (codec.Apply(data, row.Id, row.Data) is HostEntry host)
                    host.LastConnected = row.LastConnected;
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            {
                CoreLog.Warn($"Skipping unreadable local vault item {row.Id}: {ex.Message}");
            }
        }
        return data;
    }

    /// <summary>Every entity of <paramref name="data"/> that is stored as an item (the defaults only when set).</summary>
    internal static IEnumerable<object> Entities(VaultData data)
    {
        foreach (HostGroup group in data.Groups)
            yield return group;
        foreach (Identity identity in data.Identities)
            yield return identity;
        foreach (HostEntry host in data.Hosts)
            yield return host;
        foreach (KnownHost known in data.KnownHosts)
            yield return known;
        if (!VaultMerge.IsUnset(data.Defaults))
            yield return data.Defaults;
    }

    /// <summary>The vault key, or null for a wrong password (or a damaged wrapped key).</summary>
    private static byte[]? TryUnwrap(string password, LocalVaultKeyInfo key)
    {
        byte[] kek = VaultCrypto.DeriveKeys(password, key.Salt, key.Kdf).Kek;
        try
        {
            return VaultCrypto.UnwrapVaultKey(kek, key.WrappedVaultKey);
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    internal static string? CheckNewPassword(string? password) =>
        password is null || password.Length < MinPasswordLength ? $"Use at least {MinPasswordLength} characters." : null;

    private void ClearKeepUnlocked()
    {
        try
        {
            _credentials.Clear();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CoreLog.Warn($"Could not clear the kept local vault key: {ex.Message}");
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void Publish(VaultData next)
    {
        next.Revision = _current.Revision + 1;
        next.UpdatedAt = DateTimeOffset.UtcNow;
        _current = next;
    }

    private Unlocked RequireVault()
    {
        lock (_gate)
            return _vault ?? throw new InvalidOperationException("The local vault is locked.");
    }

    private static NotSupportedException NotServer() => new("The local vault has no server account.");

    private void RaiseChanged() => Changed?.Invoke();

    /// <summary>One unlock of the vault; replaced on lock/unlock so late writes of an old one are ignored.</summary>
    private sealed class Unlocked(string vaultId, byte[] key, bool keep)
    {
        public string VaultId { get; } = vaultId;
        public byte[] Key { get; } = key;
        public bool Keep { get; } = keep;
        public VaultItems Codec { get; } = new(key);

        // Guarded by the service's _gate.
        public Task Writes { get; set; } = Task.CompletedTask;
        public int Writing { get; set; }
        public Dictionary<string, byte[]?> Unsaved { get; } = [];
    }
}
