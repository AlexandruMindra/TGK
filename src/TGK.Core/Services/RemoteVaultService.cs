using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Models;
using TGK.Protocol;
using TGK.Protocol.Dtos;

namespace TGK.Core.Services;

public sealed class RemoteVaultOptions
{
    /// <summary>Where the vault cache (and the credentials file fallback) live; defaults to <see cref="AppPaths.ConfigDirectory"/>.</summary>
    public string? BaseDirectory { get; init; }

    /// <summary>Defaults to a <see cref="DeviceCredentialStore"/> in <see cref="BaseDirectory"/>.</summary>
    public IDeviceCredentialStore? Credentials { get; init; }

    /// <summary>For tests; defaults to the system handler.</summary>
    public HttpMessageHandler? HttpHandler { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long after a change the push starts, so a burst of edits goes out as one request.</summary>
    public TimeSpan PushDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    public TimeSpan PullInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>KDF for new accounts and password changes; tests lower it to the minimum the protocol accepts.</summary>
    public KdfParams NewKdf { get; init; } = KdfParams.Default;

    /// <summary>First retry delay after a failed sync; doubles per failure up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan MinRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(1);

    public string DeviceName { get; init; } = Environment.MachineName;

    public string Platform { get; init; } =
        $"{(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux")} {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
}

/// <summary>
/// The vault on a TGK server, end-to-end encrypted: the password never leaves this process (only a derived auth
/// key does), and every group, host, identity, known host and the settings item travels and rests sealed with the vault key.
/// </summary>
/// <remarks>
/// Local edits go to an outbox that is pushed ~300 ms later in one batch and retried with backoff; the vault is
/// pulled every 30 s. Conflicts are last-write-wins per item, and a local edit wins over remote changes until it
/// has been pushed. With "keep me signed in" the session is stored by <see cref="IDeviceCredentialStore"/> and
/// the sealed items plus the outbox by a cache file, so the vault opens offline. The sealed outbox belongs to the
/// account, not the session: when a session ends before it was pushed (revoked, expired, app closed), it stays in
/// the cache file and is pushed after the next sign-in to the same account.
/// </remarks>
public sealed class RemoteVaultService : IVaultService, IVaultEditor, IDisposable
{
    public const int MinPasswordLength = 8;

    private const string SessionEndedMessage = "You were signed out: this device's session was revoked from another device or has expired.";
    private static readonly TimeSpan LogoutTimeout = TimeSpan.FromSeconds(5);

    private readonly RemoteVaultOptions _options;
    private readonly VaultApiClient _api;
    private readonly IDeviceCredentialStore _credentials;
    private readonly VaultCache _cache;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _syncLock = new(1, 1); // one push/pull at a time

    // Guarded by _gate.
    private Session? _session;
    private VaultData _current = new();
    private DateTimeOffset? _lastSync;
    private string? _lastError;
    private bool _unreachable;
    private int _busy;

    public RemoteVaultService(RemoteVaultOptions? options = null)
    {
        _options = options ?? new RemoteVaultOptions();
        string directory = _options.BaseDirectory ?? AppPaths.ConfigDirectory;
        _api = new VaultApiClient(_options.HttpHandler, _options.RequestTimeout);
        _credentials = _options.Credentials ?? new DeviceCredentialStore(directory);
        _cache = new VaultCache(directory);
    }

    public event Action? Changed;
    public event Action<string>? SessionEnded;

    public VaultMode Mode => VaultMode.Server;
    public bool IsLoggedIn { get { lock (_gate) return _session is not null; } }
    public string? CurrentUser { get { lock (_gate) return _session?.Username; } }
    public string? ServerUrl { get { lock (_gate) return _session?.ServerUrl; } }
    public VaultData Current { get { lock (_gate) return _current; } }
    public DateTimeOffset? LastSync { get { lock (_gate) return _lastSync; } }
    public string? LastError { get { lock (_gate) return _lastError; } }
    public int PendingChanges { get { lock (_gate) return _session?.Pending.Count ?? 0; } }

    public SyncState Status
    {
        get
        {
            lock (_gate)
            {
                return _session is null ? SyncState.Offline
                    : _busy > 0 ? SyncState.Syncing
                    : _lastError is not null ? (_unreachable ? SyncState.Offline : SyncState.Error)
                    : _session.Pending.Count > 0 ? SyncState.Syncing
                    : SyncState.Idle;
            }
        }
    }

    /// <summary>Stops syncing and writes what is not saved yet (call on exit): the cache, or the unsent changes for the next sign-in.</summary>
    public void Dispose()
    {
        Session? session;
        lock (_gate)
            session = _session;
        if (session is not null)
        {
            session.Stop.Cancel();
            SaveCache(session, exiting: true);
        }
        _api.Dispose();
    }

    // ---- Server, login and registration ----

    public async Task<ServerInfo?> GetServerInfoAsync(string serverUrl, CancellationToken ct = default)
    {
        if (!ServerAddress.TryParse(serverUrl, out Uri? baseUri, out _))
            return null;
        try
        {
            InfoResponse info = await _api.SendAsync<InfoResponse>(baseUri, HttpMethod.Get, "info", null, null, ct).ConfigureAwait(false);
            return info.Name == ProtocolConstants.ServerName ? new ServerInfo(info.Name, info.Version, info.Protocol, info.RegistrationOpen) : null;
        }
        catch (VaultException)
        {
            return null;
        }
    }

    public TotpEnrollment BeginTotpEnrollment(string username)
    {
        string secret = Totp.GenerateSecret();
        return new TotpEnrollment(secret, Totp.BuildUri(username.Trim(), secret));
    }

    public Task<LoginResult> LoginAsync(string serverUrl, string username, string password, string? totpCode, bool keepSignedIn, CancellationToken ct = default) =>
        AuthenticateAsync(serverUrl, username, password, totpCode, null, keepSignedIn, ct);

    public Task<LoginResult> CompleteTotpSetupAsync(string serverUrl, string username, string password, string newSecret, string code, bool keepSignedIn, CancellationToken ct = default) =>
        Totp.IsValidSecret(newSecret)
            ? AuthenticateAsync(serverUrl, username, password, code, newSecret, keepSignedIn, ct)
            : Task.FromResult(LoginResult.Fail(VaultError.ValidationFailed, "Invalid authenticator secret."));

    private async Task<LoginResult> AuthenticateAsync(string serverUrl, string username, string password, string? totpCode, string? newTotpSecret, bool keep, CancellationToken ct)
    {
        if (!ServerAddress.TryParse(serverUrl, out Uri? baseUri, out string? addressError))
            return LoginResult.Fail(VaultError.InsecureUrl, addressError);
        username = username?.Trim() ?? "";
        if (username.Length == 0 || string.IsNullOrEmpty(password))
            return LoginResult.Fail(VaultError.ValidationFailed, "Enter your username and password.");

        try
        {
            PreloginResponse prelogin = await _api.SendAsync<PreloginResponse>(baseUri, HttpMethod.Post, "prelogin", new PreloginRequest(username), null, ct).ConfigureAwait(false);
            CheckKdf(prelogin.Salt, prelogin.Kdf);
            (byte[] authKey, byte[] kek) = await Task.Run(() => VaultCrypto.DeriveKeys(password, prelogin.Salt, prelogin.Kdf), ct).ConfigureAwait(false);
            try
            {
                var request = new LoginRequest(username, authKey, _options.DeviceName, _options.Platform,
                    string.IsNullOrWhiteSpace(totpCode) ? null : totpCode.Trim(), newTotpSecret);
                LoginResponse login = await _api.SendAsync<LoginResponse>(baseUri, HttpMethod.Post, "login", request, null, ct).ConfigureAwait(false);

                byte[] vaultKey;
                try
                {
                    vaultKey = VaultCrypto.UnwrapVaultKey(kek, login.WrappedVaultKey);
                }
                catch (CryptographicException)
                {
                    await RevokeQuietlyAsync(baseUri, login.Token).ConfigureAwait(false);
                    return LoginResult.Fail(VaultError.Server, "The server accepted your password but your vault key could not be decrypted.");
                }

                var session = new Session(baseUri, login.Username, login.UserId, login.SessionId, login.Token, vaultKey, keep);
                VaultData data;
                try
                {
                    LoadCache(session, withItems: false); // unsent changes an earlier session of this account left behind
                    VaultPullResponse pull;
                    do
                    {
                        pull = await FetchAsync(session, session.Revision, ct).ConfigureAwait(false);
                        ApplyPulled(session, null, pull);
                    }
                    while (pull.HasMore);
                    data = BuildVault(session);
                }
                catch
                {
                    await RevokeQuietlyAsync(baseUri, login.Token).ConfigureAwait(false);
                    throw;
                }
                await ActivateAsync(session, data, persist: true, pullNow: false).ConfigureAwait(false);
                return LoginResult.Ok;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(authKey);
                CryptographicOperations.ZeroMemory(kek);
            }
        }
        catch (VaultException ex)
        {
            return LoginResult.Fail(ex.Code, ex.Message);
        }
    }

    public Task<LoginResult> RegisterAsync(string serverUrl, string username, string password, string? inviteCode, string totpSecret, string totpCode, bool keepSignedIn, CancellationToken ct = default) =>
        RegisterAsync(serverUrl, username, password, inviteCode, totpSecret, totpCode, keepSignedIn, null, [], ct);

    /// <summary>
    /// Registers with <paramref name="vaultKey"/> (null = a new one) as the account's vault key and queues
    /// <paramref name="items"/> (sealed with that key) for upload as they are; they show at once and are pushed right
    /// after. Used to turn a local vault into a server account.
    /// </summary>
    internal async Task<LoginResult> RegisterAsync(string serverUrl, string username, string password, string? inviteCode, string totpSecret, string totpCode,
        bool keepSignedIn, byte[]? vaultKey, IReadOnlyCollection<KeyValuePair<string, byte[]>> items, CancellationToken ct)
    {
        if (!ServerAddress.TryParse(serverUrl, out Uri? baseUri, out string? addressError))
            return LoginResult.Fail(VaultError.InsecureUrl, addressError);
        username = username?.Trim() ?? "";
        if (UsernameRules.Validate(username) is { } problem)
            return LoginResult.Fail(VaultError.ValidationFailed, problem);
        if (password is null || password.Length < MinPasswordLength)
            return LoginResult.Fail(VaultError.ValidationFailed, $"Use a password of at least {MinPasswordLength} characters.");
        if (!Totp.IsValidSecret(totpSecret))
            return LoginResult.Fail(VaultError.ValidationFailed, "Invalid authenticator secret.");
        if (string.IsNullOrWhiteSpace(totpCode))
            return LoginResult.Fail(VaultError.TotpRequired, "Enter the code from your authenticator app.");

        byte[] salt = VaultCrypto.NewSalt();
        KdfParams kdf = _options.NewKdf;
        (byte[] authKey, byte[] kek) = await Task.Run(() => VaultCrypto.DeriveKeys(password, salt, kdf), ct).ConfigureAwait(false);
        vaultKey = vaultKey is null ? VaultCrypto.NewVaultKey() : (byte[])vaultKey.Clone();
        try
        {
            var request = new RegisterRequest(username, authKey, salt, kdf, VaultCrypto.WrapVaultKey(kek, vaultKey), totpSecret,
                totpCode.Trim(), _options.DeviceName, _options.Platform, string.IsNullOrWhiteSpace(inviteCode) ? null : inviteCode.Trim());
            RegisterResponse response = await _api.SendAsync<RegisterResponse>(baseUri, HttpMethod.Post, "register", request, null, ct).ConfigureAwait(false);
            var session = new Session(baseUri, response.Username, response.UserId, response.SessionId, response.Token, vaultKey, keepSignedIn)
            {
                Revision = response.Revision,
            };
            foreach ((string id, byte[] data) in items)
                session.Pending[id] = data;
            await ActivateAsync(session, BuildVault(session), persist: true, pullNow: false).ConfigureAwait(false);
            return LoginResult.Ok;
        }
        catch (VaultException ex)
        {
            return LoginResult.Fail(ex.Code, ex.Message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(authKey);
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public async Task<bool> TryRestoreSessionAsync(CancellationToken ct = default)
    {
        // Keyring access, file IO and decryption: off the caller's (UI) thread.
        (Session Session, VaultData Data)? stored = await Task.Run(LoadStoredSession, ct).ConfigureAwait(false);
        if (stored is not { } restored)
            return false;
        await ActivateAsync(restored.Session, restored.Data, persist: false, pullNow: true).ConfigureAwait(false);
        return true;
    }

    private (Session, VaultData)? LoadStoredSession()
    {
        DeviceCredentials? credentials = _credentials.Load();
        if (credentials is null)
            return null;
        if (!ServerAddress.TryParse(credentials.ServerUrl, out Uri? baseUri, out _) || credentials.VaultKey.Length != VaultCrypto.KeySize)
        {
            _credentials.Clear();
            return null;
        }

        var session = new Session(baseUri, credentials.Username, credentials.UserId, credentials.SessionId, credentials.Token, credentials.VaultKey, keep: true);
        LoadCache(session, withItems: true);
        return (session, BuildVault(session));
    }

    /// <summary>
    /// Loads this device's cache for the session's account (not yet active): the unsent changes and last-connected
    /// times, plus with <paramref name="withItems"/> the server items and revision (a restore works offline from them).
    /// </summary>
    private void LoadCache(Session session, bool withItems)
    {
        if (_cache.Load(session.ServerUrl, session.UserId) is not { } cache)
            return;
        if (withItems)
        {
            session.Revision = cache.Revision;
            foreach (CachedItem item in cache.Items)
                session.Items[item.Id] = item;
        }
        foreach (PendingChange change in cache.Pending)
            session.Pending[change.Id] = change.Data;
        foreach ((string id, DateTimeOffset at) in cache.LastConnected)
            session.LastConnected[id] = at;
    }

    /// <summary>The vault of a session that is not active yet: its server items overlaid with its unsent changes.</summary>
    private static VaultData BuildVault(Session session)
    {
        var data = new VaultData();
        foreach (CachedItem item in session.Items.Values)
        {
            if (!session.Pending.ContainsKey(item.Id))
                TryApply(data, session, item.Id, item.Data);
        }
        foreach ((string id, byte[]? change) in session.Pending)
            TryApply(data, session, id, change);
        return data;
    }

    /// <summary>Makes <paramref name="session"/> the current one (logging out any other) and starts its sync loop.</summary>
    private async Task ActivateAsync(Session session, VaultData data, bool persist, bool pullNow)
    {
        if (IsLoggedIn)
            await LogoutAsync().ConfigureAwait(false);

        if (persist)
        {
            try
            {
                if (session.Keep)
                {
                    _credentials.Save(new DeviceCredentials(session.ServerUrl, session.Username, session.UserId, session.SessionId, session.Token, session.VaultKey));
                    _cache.Save(CacheState(session, withItems: true));
                }
                else
                {
                    _credentials.Clear();
                    _cache.Delete();
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                CoreLog.Warn($"Could not store the sign-in on this device: {ex.Message}");
            }
        }

        lock (_gate)
        {
            data.Revision = _current.Revision + 1;
            data.UpdatedAt = DateTimeOffset.UtcNow;
            _session = session;
            _current = data;
            _lastError = null;
            _unreachable = false;
            _lastSync = pullNow ? null : DateTimeOffset.UtcNow;
            _busy = 0;
        }
        if (session.Pending.Count > 0)
            session.Wake.Release(); // push what an earlier session left unsent right away
        _ = Task.Run(() => RunSyncLoopAsync(session, pullNow));
        RaiseChanged();
    }

    public async Task LogoutAsync()
    {
        Session? session;
        bool pending;
        lock (_gate)
        {
            session = _session;
            if (session is null)
                return;
            session.SigningOut = true;
            pending = session.Pending.Count > 0;
        }

        if (pending)
        {
            // Best effort, with its own time budget so a slow push never keeps the sign-out off the server.
            using var timeout = new CancellationTokenSource(LogoutTimeout);
            try
            {
                await SyncOnceAsync(session, pull: false, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        await RevokeQuietlyAsync(session.BaseUri, session.Token).ConfigureAwait(false);
        EndSession(session, null, keepOutbox: false);
    }

    // ---- Account ----

    public Task<IReadOnlyList<DeviceSession>> ListSessionsAsync(CancellationToken ct = default) =>
        AccountCallAsync<IReadOnlyList<DeviceSession>>(async session =>
        {
            SessionInfo[] sessions = await _api.SendAsync<SessionInfo[]>(session.BaseUri, HttpMethod.Get, "sessions", null, session.Token, ct).ConfigureAwait(false);
            return sessions.Select(s => new DeviceSession(s.Id, s.DeviceName, s.Platform, s.CreatedAt, s.LastSeenAt, s.LastIp, s.Current)).ToList();
        });

    public Task RevokeSessionAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        return AccountCallAsync(async session =>
        {
            await _api.SendAsync(session.BaseUri, HttpMethod.Delete, "sessions/" + Uri.EscapeDataString(sessionId), null, session.Token, ct).ConfigureAwait(false);
            if (sessionId == session.SessionId)
                EndSession(session, "This device was signed out.", keepOutbox: true);
            return true;
        });
    }

    public Task<int> RevokeOtherSessionsAsync(CancellationToken ct = default) =>
        AccountCallAsync(async session =>
        {
            RevokeOthersResponse response = await _api.SendAsync<RevokeOthersResponse>(session.BaseUri, HttpMethod.Post, "sessions/revoke-others", null, session.Token, ct).ConfigureAwait(false);
            return response.Revoked;
        });

    public Task ChangePasswordAsync(string currentPassword, string totpCode, string newPassword, bool revokeOtherSessions, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(currentPassword))
            throw new VaultException(VaultError.ValidationFailed, "Enter your current password.");
        if (newPassword is null || newPassword.Length < MinPasswordLength)
            throw new VaultException(VaultError.ValidationFailed, $"Use a password of at least {MinPasswordLength} characters.");
        if (string.IsNullOrWhiteSpace(totpCode))
            throw new VaultException(VaultError.TotpRequired, "Enter the code from your authenticator app.");

        return AccountCallAsync(async session =>
        {
            PreloginResponse prelogin = await _api.SendAsync<PreloginResponse>(session.BaseUri, HttpMethod.Post, "prelogin", new PreloginRequest(session.Username), null, ct).ConfigureAwait(false);
            CheckKdf(prelogin.Salt, prelogin.Kdf);
            byte[] newSalt = VaultCrypto.NewSalt();
            KdfParams newKdf = _options.NewKdf;
            (byte[] currentAuthKey, byte[] currentKek, byte[] newAuthKey, byte[] newKek) = await Task.Run(() =>
            {
                (byte[] a, byte[] k) = VaultCrypto.DeriveKeys(currentPassword, prelogin.Salt, prelogin.Kdf);
                (byte[] na, byte[] nk) = VaultCrypto.DeriveKeys(newPassword, newSalt, newKdf);
                return (a, k, na, nk);
            }, ct).ConfigureAwait(false);
            try
            {
                var request = new ChangePasswordRequest(currentAuthKey, totpCode.Trim(), newAuthKey, newSalt, newKdf,
                    VaultCrypto.WrapVaultKey(newKek, session.VaultKey), revokeOtherSessions);
                await _api.SendAsync(session.BaseUri, HttpMethod.Post, "account/password", request, session.Token, ct).ConfigureAwait(false);
                return true;
            }
            finally
            {
                foreach (byte[] key in new[] { currentAuthKey, currentKek, newAuthKey, newKek })
                    CryptographicOperations.ZeroMemory(key);
            }
        });
    }

    /// <summary>Runs an authenticated call; a revoked session or disabled account ends the session before the exception propagates.</summary>
    private async Task<T> AccountCallAsync<T>(Func<Session, Task<T>> call)
    {
        Session session = RequireSession();
        try
        {
            return await call(session).ConfigureAwait(false);
        }
        catch (VaultException ex) when (ex.Code is VaultError.Unauthorized or VaultError.AccountDisabled)
        {
            EndSession(session, ex.Code == VaultError.AccountDisabled ? ex.Message : SessionEndedMessage, keepOutbox: true);
            throw;
        }
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

    /// <summary>Kept on this device only (see <see cref="VaultItems"/>): nothing is pushed, so the task is already complete.</summary>
    public Task TouchHostAsync(Guid hostId)
    {
        Session session;
        lock (_gate)
        {
            session = _session ?? throw new InvalidOperationException("Not logged in.");
            if (_current.FindHost(hostId) is null)
                return Task.CompletedTask;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            session.LastConnected[VaultItems.IdOf(hostId)] = now;
            session.CacheDirty = true;
            VaultData next = _current.ShallowCopy();
            VaultEdits.SetLastConnected(next, hostId, now);
            Publish(next);
        }
        _ = Task.Run(() => SaveCache(session));
        RaiseChanged();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Takes over when this device last connected to <paramref name="hosts"/> (the local vault moving to this account)
    /// for the vault's hosts that have no later time. Kept on this device only, like <see cref="TouchHostAsync"/>.
    /// </summary>
    internal void ImportLastConnected(IEnumerable<HostEntry> hosts)
    {
        Session session;
        lock (_gate)
        {
            session = _session ?? throw new InvalidOperationException("Not logged in.");
            VaultData? next = null;
            foreach (HostEntry host in hosts)
            {
                string id = VaultItems.IdOf(host.Id);
                if (host.LastConnected is not { } at || _current.FindHost(host.Id) is null
                    || (session.LastConnected.TryGetValue(id, out DateTimeOffset known) && known >= at))
                    continue;
                session.LastConnected[id] = at;
                next ??= _current.ShallowCopy();
                VaultEdits.SetLastConnected(next, host.Id, at);
            }
            if (next is null)
                return;
            session.CacheDirty = true;
            Publish(next);
        }
        _ = Task.Run(() => SaveCache(session));
        RaiseChanged();
    }

    /// <summary>Publishes the edited snapshot, queues the changed items (sealed), saves the outbox and wakes the sync loop.</summary>
    private Task MutateAsync(Action<VaultData> edit)
    {
        Session session;
        Task pushed;
        lock (_gate)
        {
            session = _session ?? throw new InvalidOperationException("Not logged in.");
            VaultData next = _current.ShallowCopy();
            edit(next);
            var sealedChanges = new List<(string Id, byte[]? Data)>();
            foreach (ItemChange change in session.Codec.Diff(_current, next))
            {
                byte[]? data = change.Entity is null ? null : session.Codec.Encrypt(change.Id, change.Entity);
                if (data?.Length > ProtocolConstants.MaxItemBytes)
                    throw new ArgumentException($"This entry is too large to sync (limit {ProtocolConstants.MaxItemBytes / 1024} KB).");
                sealedChanges.Add((change.Id, data));
            }
            if (sealedChanges.Count == 0)
                return Task.CompletedTask;

            foreach ((string id, byte[]? data) in sealedChanges)
                session.Pending[id] = data;
            session.CacheDirty = true;
            Publish(next);
            pushed = session.PushDone.Task;
        }
        session.Wake.Release();
        // On disk right away, not only when the next sync runs: a sync stuck on a hanging server must not hold it back.
        _ = Task.Run(() => SaveCache(session));
        RaiseChanged();
        return pushed;
    }

    // ---- Sync ----

    public async Task SyncAsync(CancellationToken ct = default)
    {
        Session session = RequireSession();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Stop.Token);
        try
        {
            await SyncOnceAsync(session, pull: true, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Logged out meanwhile.
        }
    }

    private async Task RunSyncLoopAsync(Session session, bool pullNow)
    {
        CancellationToken ct = session.Stop.Token;
        DateTimeOffset nextPull = pullNow ? DateTimeOffset.MinValue : DateTimeOffset.UtcNow + _options.PullInterval;
        try
        {
            while (true)
            {
                TimeSpan wait = session.Failures > 0
                    ? RetryDelay(session.Failures)
                    : Max(TimeSpan.Zero, nextPull - DateTimeOffset.UtcNow);
                bool woken = await session.Wake.WaitAsync(wait, ct).ConfigureAwait(false);
                if (woken)
                {
                    // A local change: let the burst finish, then push it in one request.
                    await Task.Delay(_options.PushDelay, ct).ConfigureAwait(false);
                    while (session.Wake.Wait(0))
                    {
                    }
                }
                bool pull = !woken || DateTimeOffset.UtcNow >= nextPull;
                if (await SyncOnceAsync(session, pull, ct).ConfigureAwait(false) && pull)
                    nextPull = DateTimeOffset.UtcNow + _options.PullInterval;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Session ended.
        }
    }

    /// <summary>Pushes the outbox, then optionally pulls. Returns false on failure (recorded in <see cref="LastError"/>).</summary>
    private async Task<bool> SyncOnceAsync(Session session, bool pull, CancellationToken ct)
    {
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Mutations waiting for this push complete only once its outcome (Status, LastError) is recorded.
            TaskCompletionSource pushed;
            lock (_gate)
            {
                if (_session != session)
                    return false;
                _busy++;
                pushed = session.PushDone;
                session.PushDone = NewCompletion();
            }
            RaiseChanged();
            SaveCache(session); // an edit survives a crash while its push is in flight

            try
            {
                await PushAsync(session, ct).ConfigureAwait(false);
                if (pull)
                    await PullAsync(session, ct).ConfigureAwait(false);
                session.Failures = 0;
                lock (_gate)
                {
                    if (_session == session)
                    {
                        _lastError = null;
                        _unreachable = false;
                        _lastSync = DateTimeOffset.UtcNow;
                    }
                }
                return true;
            }
            catch (VaultException ex) when (ex.Code is VaultError.Unauthorized or VaultError.AccountDisabled)
            {
                bool signingOut;
                lock (_gate)
                    signingOut = session.SigningOut;
                EndSession(session, signingOut ? null : ex.Code == VaultError.AccountDisabled ? ex.Message : SessionEndedMessage, keepOutbox: !signingOut);
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                bool unreachable = ex is VaultException { Code: VaultError.Network };
                if (session.Failures++ == 0)
                    CoreLog.Warn($"Vault sync failed: {ex.Message}");
                lock (_gate)
                {
                    if (_session == session)
                    {
                        _lastError = unreachable ? ex.Message + " Your changes are kept and synced when it is reachable." : ex.Message;
                        _unreachable = unreachable;
                    }
                }
                return false;
            }
            finally
            {
                lock (_gate)
                {
                    if (_session == session)
                        _busy--;
                }
                SaveCache(session);
                RaiseChanged();
                pushed.TrySetResult();
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>Sends the outbox in batches.</summary>
    private async Task PushAsync(Session session, CancellationToken ct)
    {
        while (true)
        {
            List<VaultChange> batch;
            lock (_gate)
            {
                if (_session != session || session.Pending.Count == 0)
                    return;
                batch = TakeBatch(session.Pending);
            }

            VaultPushResponse response = await _api.SendAsync<VaultPushResponse>(session.BaseUri, HttpMethod.Post, "vault", new VaultPushRequest(batch), session.Token, ct).ConfigureAwait(false);

            var sent = batch.ToDictionary(c => c.Id, c => c.Data);
            lock (_gate)
            {
                if (_session != session)
                    return;
                foreach (AppliedChange applied in response.Applied)
                {
                    if (!sent.TryGetValue(applied.Id, out byte[]? data))
                        continue;
                    // Changed again while the push was in flight? Then the newer edit stays queued.
                    if (session.Pending.TryGetValue(applied.Id, out byte[]? queued) && ReferenceEquals(queued, data))
                        session.Pending.Remove(applied.Id);
                    if (data is null)
                        session.Items.Remove(applied.Id);
                    else
                        session.Items[applied.Id] = new CachedItem(applied.Id, applied.Revision, data);
                }
                session.CacheDirty = true;
            }
        }
    }

    private static List<VaultChange> TakeBatch(Dictionary<string, byte[]?> pending)
    {
        const long budget = ProtocolConstants.MaxRequestBytes / 2; // base64 grows data by a third
        var batch = new List<VaultChange>();
        long bytes = 0;
        foreach ((string id, byte[]? data) in pending)
        {
            long size = (data?.Length ?? 0) + id.Length + 64;
            if (batch.Count == ProtocolConstants.MaxChangesPerRequest || (batch.Count > 0 && bytes + size > budget))
                break;
            batch.Add(new VaultChange(id, data));
            bytes += size;
        }
        return batch;
    }

    /// <summary>Downloads the changes since the last pull, page by page, and shows those without a pending local edit.</summary>
    private async Task PullAsync(Session session, CancellationToken ct)
    {
        bool reset = false;
        while (true)
        {
            long since;
            lock (_gate)
                since = session.Revision;
            VaultPullResponse pull = await FetchAsync(session, since, ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (_session != session)
                    return;
                if (pull.Revision < since)
                {
                    // The server's revision went backwards: its database was restored from a backup. Download it all
                    // again and push our copy of every item back, so the changes the backup lacks are restored.
                    CoreLog.Warn($"The server's vault revision went back from {since} to {pull.Revision}; resyncing everything.");
                    foreach (CachedItem item in session.Items.Values)
                        session.Pending.TryAdd(item.Id, item.Data);
                    session.Items.Clear();
                    session.Revision = 0;
                    session.CacheDirty = true;
                    reset = true;
                    continue;
                }
                if (ApplyPulled(session, _current, pull) is { } next)
                    Publish(next);
            }
            if (!pull.HasMore)
                break;
        }
        if (reset)
            session.Wake.Release();
    }

    private Task<VaultPullResponse> FetchAsync(Session session, long since, CancellationToken ct) =>
        _api.SendAsync<VaultPullResponse>(session.BaseUri, HttpMethod.Get, $"vault?since={since}", null, session.Token, ct);

    /// <summary>
    /// Records pulled items as server state and applies those without a queued local edit (which wins until pushed)
    /// to a copy of <paramref name="vault"/>. Returns the copy, or null when nothing visible changed (or no vault was given).
    /// </summary>
    private static VaultData? ApplyPulled(Session session, VaultData? vault, VaultPullResponse pull)
    {
        VaultData? next = null;
        foreach (VaultItem item in pull.Items)
        {
            if (session.Items.TryGetValue(item.Id, out CachedItem? known) && known.Revision >= item.Revision)
                continue; // our own push, already applied
            byte[]? data = item.Deleted ? null : item.Data;
            if (data is null)
                session.Items.Remove(item.Id);
            else
                session.Items[item.Id] = new CachedItem(item.Id, item.Revision, data);
            if (vault is null || session.Pending.ContainsKey(item.Id))
                continue;
            next ??= vault.ShallowCopy();
            TryApply(next, session, item.Id, data);
        }
        session.Revision = Math.Max(session.Revision, pull.Revision);
        session.CacheDirty = true;
        return next;
    }

    /// <summary>Applies one item; a host gets this device's last-connected time (which is not synced).</summary>
    private static void TryApply(VaultData vault, Session session, string id, byte[]? data)
    {
        try
        {
            if (session.Codec.Apply(vault, id, data) is HostEntry host && session.LastConnected.TryGetValue(id, out DateTimeOffset at))
                host.LastConnected = at; // a fresh entity, not shared with any snapshot yet
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            CoreLog.Warn($"Skipping unreadable vault item {id}: {ex.Message}");
        }
    }

    // ---- Session state ----

    /// <summary>
    /// Clears the session (once), stops its loop and wipes what it stored on the device, except, with
    /// <paramref name="keepOutbox"/>, the changes it could not push yet (for the next sign-in). Caller must not hold <see cref="_gate"/>.
    /// </summary>
    private void EndSession(Session session, string? reason, bool keepOutbox)
    {
        VaultCacheState? outbox;
        lock (_gate)
        {
            if (_session != session)
                return;
            _session = null;
            _current = new VaultData { Revision = _current.Revision + 1 };
            _lastError = null;
            _unreachable = false;
            _lastSync = null;
            _busy = 0;
            outbox = keepOutbox && session.Pending.Count > 0 ? CacheState(session, withItems: false) : null;
            session.PushDone.TrySetResult();
        }
        session.Stop.Cancel();
        lock (session.FileLock)
        {
            if (session.Keep)
                _credentials.Clear();
            if (outbox is not null)
                _cache.Save(outbox);
            else if (session.Keep)
                _cache.Delete();
        }
        RaiseChanged();
        if (reason is null)
            return;
        int unsent = outbox?.Pending.Count ?? 0;
        SessionEnded?.Invoke(unsent == 0 ? reason
            : $"{reason} {unsent} unsynced change{(unsent == 1 ? " is" : "s are")} kept on this device and will be uploaded when you sign in again.");
    }

    /// <summary>
    /// Writes the cache file: for "keep me signed in" the whole state (when it changed); otherwise, only on
    /// <paramref name="exiting"/>, the unsent changes for the next sign-in. The snapshot is taken under
    /// <see cref="Session.FileLock"/>, so concurrent saves never write an older state last.
    /// </summary>
    private void SaveCache(Session session, bool exiting = false)
    {
        if (!session.Keep && !exiting)
            return;
        lock (session.FileLock)
        {
            VaultCacheState state;
            lock (_gate)
            {
                if (_session != session)
                    return; // ended: EndSession owns the file now
                if (session.Keep)
                {
                    if (!session.CacheDirty)
                        return;
                    session.CacheDirty = false;
                    state = CacheState(session, withItems: true);
                }
                else
                {
                    if (session.Pending.Count == 0)
                        return;
                    state = CacheState(session, withItems: false);
                }
            }
            _cache.Save(state);
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private static VaultCacheState CacheState(Session session, bool withItems) => new()
    {
        ServerUrl = session.ServerUrl,
        UserId = session.UserId,
        Revision = withItems ? session.Revision : 0,
        Items = withItems ? [.. session.Items.Values] : [],
        Pending = [.. session.Pending.Select(p => new PendingChange(p.Key, p.Value))],
        LastConnected = new Dictionary<string, DateTimeOffset>(session.LastConnected),
    };

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void Publish(VaultData next)
    {
        next.Revision = _current.Revision + 1;
        next.UpdatedAt = DateTimeOffset.UtcNow;
        _current = next;
    }

    private Session RequireSession()
    {
        lock (_gate)
            return _session ?? throw new InvalidOperationException("Not logged in.");
    }

    private async Task RevokeQuietlyAsync(Uri baseUri, string token)
    {
        try
        {
            using var timeout = new CancellationTokenSource(LogoutTimeout);
            await _api.SendAsync(baseUri, HttpMethod.Post, "logout", null, token, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is VaultException or OperationCanceledException)
        {
        }
    }

    private static void CheckKdf(byte[] salt, KdfParams kdf)
    {
        if (salt.Length != VaultCrypto.SaltSize || !kdf.IsAcceptable())
            throw new VaultException(VaultError.IncompatibleServer, "The server asked for unsafe password-hashing parameters; your password was not used.");
    }

    private TimeSpan RetryDelay(int failures) =>
        TimeSpan.FromTicks(Math.Min(_options.MaxRetryDelay.Ticks, _options.MinRetryDelay.Ticks << Math.Min(failures - 1, 16)));

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void RaiseChanged() => Changed?.Invoke();

    /// <summary>One signed-in session; replaced wholesale on login/logout so late completions of an old one are ignored.</summary>
    private sealed class Session(Uri baseUri, string username, string userId, string sessionId, string token, byte[] vaultKey, bool keep)
    {
        public VaultItems Codec { get; } = new(vaultKey);
        public Uri BaseUri { get; } = baseUri;
        public string ServerUrl { get; } = baseUri.ToString().TrimEnd('/');
        public string Username { get; } = username;
        public string UserId { get; } = userId;
        public string SessionId { get; } = sessionId;
        public string Token { get; } = token;
        public byte[] VaultKey { get; } = vaultKey;
        public bool Keep { get; } = keep;

        public CancellationTokenSource Stop { get; } = new();
        public SemaphoreSlim Wake { get; } = new(0);
        public int Failures { get; set; } // sync loop / _syncLock only

        // Guarded by the service's _gate.
        public long Revision { get; set; }
        public Dictionary<string, CachedItem> Items { get; } = [];
        public Dictionary<string, byte[]?> Pending { get; } = [];
        public Dictionary<string, DateTimeOffset> LastConnected { get; } = []; // item id -> when this device last connected
        public TaskCompletionSource PushDone { get; set; } = NewCompletion();
        public bool CacheDirty { get; set; }
        public bool SigningOut { get; set; }

        /// <summary>Serializes the cache file writes of this session (taken before <see cref="_gate"/>, never inside it).</summary>
        public Lock FileLock { get; } = new();
    }
}
