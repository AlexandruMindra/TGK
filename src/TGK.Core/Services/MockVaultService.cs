using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Models;

namespace TGK.Core.Services;

/// <summary>
/// DEV ONLY — stands in for the TGK server until it exists. Simulates network latency, accepts any
/// non-empty username and password except the password <c>wrong</c>, and keeps the "server-side" vault
/// in <c>dev-vault-&lt;user&gt;.json</c> in the config directory, seeded with sample data on first login.
/// A server URL containing <c>offline</c> simulates an unreachable server.
/// </summary>
/// <remarks>
/// TODO: replace with the real server client. The real server will own secret storage; this mock writes
/// identities (including any passwords and private keys entered) to its dev file in plain JSON (0600 on Unix).
/// </remarks>
public sealed class MockVaultService : IVaultService
{
    public static readonly TimeSpan DefaultLatency = TimeSpan.FromMilliseconds(300);

    private readonly string _baseDirectory;
    private readonly TimeSpan _latency;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _io = new(1, 1); // serializes access to the dev file

    // All fields below are guarded by _gate.
    private VaultData _current = new();
    private string? _user;
    private string? _serverUrl;
    private string? _filePath;
    private SyncState _status = SyncState.Offline;
    private DateTimeOffset? _lastSync;
    private string? _lastError;
    private long _persistedRevision;
    private int _pendingPushes;
    private int _generation; // bumped on login/logout so late completions of an old session are ignored

    /// <param name="baseDirectory">Where dev vault files live; defaults to <see cref="AppPaths.ConfigDirectory"/>.</param>
    /// <param name="latency">Simulated round-trip time; defaults to <see cref="DefaultLatency"/>.</param>
    public MockVaultService(string? baseDirectory = null, TimeSpan? latency = null)
    {
        _baseDirectory = baseDirectory ?? AppPaths.ConfigDirectory;
        _latency = latency ?? DefaultLatency;
    }

    public event Action? Changed;

    public bool IsLoggedIn { get { lock (_gate) return _user is not null; } }
    public string? CurrentUser { get { lock (_gate) return _user; } }
    public string? ServerUrl { get { lock (_gate) return _serverUrl; } }
    public VaultData Current { get { lock (_gate) return _current; } }
    public SyncState Status { get { lock (_gate) return _status; } }
    public DateTimeOffset? LastSync { get { lock (_gate) return _lastSync; } }
    public string? LastError { get { lock (_gate) return _lastError; } }

    /// <summary>Path of the dev vault file for <paramref name="username"/>.</summary>
    public string GetVaultFilePath(string username)
    {
        var name = new StringBuilder();
        foreach (char c in username.Trim().ToLowerInvariant())
            name.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        return Path.Combine(_baseDirectory, $"dev-vault-{name}.json");
    }

    public async Task<LoginResult> LoginAsync(string serverUrl, string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
            return LoginResult.Fail("Enter the server address.");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return LoginResult.Fail("Enter your username and password.");

        await Task.Delay(_latency, ct).ConfigureAwait(false);

        if (serverUrl.Contains("offline", StringComparison.OrdinalIgnoreCase))
            return LoginResult.Fail($"Cannot reach the server at {serverUrl.Trim()}.");
        if (password == "wrong")
            return LoginResult.Fail("Invalid username or password.");

        if (IsLoggedIn)
            await LogoutAsync().ConfigureAwait(false);

        string user = username.Trim();
        string path = GetVaultFilePath(user);
        VaultData data;
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            VaultData? stored = await TgkJson.ReadFileAsync<VaultData>(path, ct).ConfigureAwait(false);
            if (stored is null)
            {
                stored = SampleVault.Create(DateTimeOffset.UtcNow);
                await TgkJson.WriteFileAtomicAsync(path, stored, ct).ConfigureAwait(false);
            }
            data = stored;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return LoginResult.Fail($"Could not load your vault: {ex.Message}");
        }
        finally
        {
            _io.Release();
        }

        lock (_gate)
        {
            _generation++;
            _user = user;
            _serverUrl = serverUrl.Trim();
            _filePath = path;
            _current = data;
            _persistedRevision = data.Revision;
            _pendingPushes = 0;
            _status = SyncState.Idle;
            _lastError = null;
            _lastSync = DateTimeOffset.UtcNow;
        }
        RaiseChanged();
        return LoginResult.Ok;
    }

    public async Task LogoutAsync()
    {
        int generation;
        lock (_gate)
            generation = _generation;

        await _io.WaitAsync().ConfigureAwait(false);
        try
        {
            // Push anything not yet written so a quick logout never loses an edit.
            await PushIfDirtyAsync(generation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging out anyway; the unsaved change is lost, as it would be with an unreachable server.
        }
        finally
        {
            lock (_gate)
            {
                _generation++;
                _user = null;
                _serverUrl = null;
                _filePath = null;
                _current = new VaultData();
                _persistedRevision = 0;
                _pendingPushes = 0;
                _status = SyncState.Offline;
                _lastError = null;
                _lastSync = null;
            }
            _io.Release();
        }
        RaiseChanged();
    }

    public async Task SyncAsync(CancellationToken ct = default)
    {
        int generation;
        lock (_gate)
        {
            EnsureLoggedIn();
            generation = _generation;
            _status = SyncState.Syncing;
        }
        RaiseChanged();

        try
        {
            await Task.Delay(_latency, ct).ConfigureAwait(false);
            await _io.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await PushIfDirtyAsync(generation).ConfigureAwait(false);
                string? path;
                lock (_gate)
                    path = generation == _generation ? _filePath : null;
                if (path is null)
                    return;

                VaultData? pulled = await TgkJson.ReadFileAsync<VaultData>(path, ct).ConfigureAwait(false);
                lock (_gate)
                {
                    if (generation != _generation)
                        return;
                    // Never let the pull clobber a local edit made while it was in flight.
                    if (pulled is not null && pulled.Revision >= _current.Revision)
                    {
                        _current = pulled;
                        _persistedRevision = pulled.Revision;
                    }
                    _lastError = null;
                    _lastSync = DateTimeOffset.UtcNow;
                }
            }
            finally
            {
                _io.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            lock (_gate)
            {
                if (generation == _generation)
                    _lastError = $"Sync failed: {ex.Message}";
            }
        }
        finally
        {
            lock (_gate)
            {
                if (generation == _generation)
                    _status = CurrentStatus();
            }
            RaiseChanged();
        }
    }

    public Task SaveHostAsync(HostEntry host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (string.IsNullOrWhiteSpace(host.Host))
            throw new ArgumentException("A host needs an address.", nameof(host));
        if (host.Port is < 1 or > 65535)
            throw new ArgumentException("Port must be between 1 and 65535.", nameof(host));
        if (host.Id == Guid.Empty)
            host.Id = Guid.NewGuid();

        HostEntry copy = host.Clone();
        return MutateAsync(vault => Upsert(vault.Hosts, copy, h => h.Id == copy.Id));
    }

    public Task DeleteHostAsync(Guid hostId) =>
        MutateAsync(vault => vault.Hosts.RemoveAll(h => h.Id == hostId));

    public Task SaveGroupAsync(HostGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (string.IsNullOrWhiteSpace(group.Name))
            throw new ArgumentException("A group needs a name.", nameof(group));
        if (group.Id == Guid.Empty)
            group.Id = Guid.NewGuid();

        HostGroup copy = group.Clone();
        return MutateAsync(vault => Upsert(vault.Groups, copy, g => g.Id == copy.Id));
    }

    public Task DeleteGroupAsync(Guid groupId) =>
        MutateAsync(vault =>
        {
            vault.Groups.RemoveAll(g => g.Id == groupId);
            foreach (HostEntry host in vault.Hosts.Where(h => h.GroupId == groupId))
                host.GroupId = null;
        });

    public Task SaveIdentityAsync(Identity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.Name))
            throw new ArgumentException("An identity needs a name.", nameof(identity));
        if (identity.Id == Guid.Empty)
            identity.Id = Guid.NewGuid();

        Identity copy = identity.Clone();
        return MutateAsync(vault => Upsert(vault.Identities, copy, i => i.Id == copy.Id));
    }

    public Task DeleteIdentityAsync(Guid identityId) =>
        MutateAsync(vault =>
        {
            vault.Identities.RemoveAll(i => i.Id == identityId);
            foreach (HostEntry host in vault.Hosts.Where(h => h.IdentityId == identityId))
                host.IdentityId = null;
        });

    public Task AddKnownHostAsync(KnownHost knownHost)
    {
        ArgumentNullException.ThrowIfNull(knownHost);
        if (string.IsNullOrWhiteSpace(knownHost.Host) || string.IsNullOrWhiteSpace(knownHost.FingerprintSha256))
            throw new ArgumentException("A known host needs a host name and fingerprint.", nameof(knownHost));

        KnownHost copy = knownHost.Clone();
        if (copy.AddedAt == default)
            copy.AddedAt = DateTimeOffset.UtcNow;
        return MutateAsync(vault => Upsert(vault.KnownHosts, copy, k => k.Matches(copy.Host, copy.Port)));
    }

    public Task TouchHostAsync(Guid hostId)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return MutateAsync(vault =>
        {
            if (vault.FindHost(hostId) is { } host)
                host.LastConnected = now;
        });
    }

    /// <summary>Applies <paramref name="edit"/> to a copy of the vault, publishes it, then pushes it to the dev file.</summary>
    private async Task MutateAsync(Action<VaultData> edit)
    {
        int generation;
        lock (_gate)
        {
            EnsureLoggedIn();
            VaultData next = _current.Clone();
            edit(next);
            next.Revision = _current.Revision + 1;
            next.UpdatedAt = DateTimeOffset.UtcNow;
            _current = next;
            _pendingPushes++;
            _status = SyncState.Syncing;
            generation = _generation;
        }
        RaiseChanged();

        try
        {
            await Task.Delay(_latency).ConfigureAwait(false);
            await _io.WaitAsync().ConfigureAwait(false);
            try
            {
                await PushIfDirtyAsync(generation).ConfigureAwait(false);
            }
            finally
            {
                _io.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                if (generation == _generation)
                    _lastError = $"Could not save changes: {ex.Message}";
            }
        }
        finally
        {
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _pendingPushes--;
                    _status = CurrentStatus();
                }
            }
            RaiseChanged();
        }
    }

    /// <summary>Writes the current snapshot if it is newer than the file. Caller must hold <see cref="_io"/>.</summary>
    private async Task PushIfDirtyAsync(int generation)
    {
        VaultData snapshot;
        string path;
        lock (_gate)
        {
            if (generation != _generation || _filePath is null || _current.Revision <= _persistedRevision)
                return;
            snapshot = _current;
            path = _filePath;
        }

        // Snapshots are never mutated after publication, so serializing outside the lock is safe.
        await TgkJson.WriteFileAtomicAsync(path, snapshot).ConfigureAwait(false);

        lock (_gate)
        {
            if (generation != _generation)
                return;
            _persistedRevision = Math.Max(_persistedRevision, snapshot.Revision);
            _lastError = null;
            _lastSync = DateTimeOffset.UtcNow;
        }
    }

    private SyncState CurrentStatus() =>
        _user is null ? SyncState.Offline
        : _pendingPushes > 0 ? SyncState.Syncing
        : _lastError is not null ? SyncState.Error
        : SyncState.Idle;

    private void EnsureLoggedIn()
    {
        if (_user is null)
            throw new InvalidOperationException("Not logged in.");
    }

    private static void Upsert<T>(List<T> list, T item, Predicate<T> match)
    {
        int index = list.FindIndex(match);
        if (index >= 0)
            list[index] = item;
        else
            list.Add(item);
    }

    private void RaiseChanged() => Changed?.Invoke();
}
