using System;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Models;

namespace TGK.Core.Services;

public enum SyncState
{
    /// <summary>Logged in and in sync with the server.</summary>
    Idle,

    /// <summary>A pull or push is in progress.</summary>
    Syncing,

    /// <summary>The last sync failed; local changes are kept and retried on the next change or sync. See <see cref="IVaultService.LastError"/>.</summary>
    Error,

    /// <summary>Not logged in.</summary>
    Offline,
}

public sealed record LoginResult(bool Success, string? Error)
{
    public static LoginResult Ok { get; } = new(true, null);
    public static LoginResult Fail(string error) => new(false, error);
}

/// <summary>
/// Client-side access to the user's vault (hosts, groups, identities, known host keys) held by the TGK server.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Current"/> is an immutable snapshot by convention: never modify it or the objects in it.
/// To edit, <c>Clone()</c> an entry, change the copy and pass it to a <c>Save*</c> method; every change
/// publishes a fresh snapshot, so a snapshot the UI is iterating never changes underneath it.
/// </para>
/// <para>
/// Mutations are applied to <see cref="Current"/> immediately (optimistically) and then pushed to the server;
/// the returned task completes once the push finished. A failed push does not throw: it sets
/// <see cref="Status"/> to <see cref="SyncState.Error"/> and is retried with the next change or sync.
/// Mutations throw <see cref="InvalidOperationException"/> when not logged in and <see cref="ArgumentException"/> for invalid input.
/// </para>
/// <para>All members are thread-safe and may be called from the UI thread; IO happens asynchronously.</para>
/// </remarks>
public interface IVaultService
{
    bool IsLoggedIn { get; }
    string? CurrentUser { get; }
    string? ServerUrl { get; }

    /// <summary>The latest vault snapshot (empty when logged out). Treat as read-only.</summary>
    VaultData Current { get; }

    SyncState Status { get; }
    DateTimeOffset? LastSync { get; }

    /// <summary>Human-readable reason for <see cref="SyncState.Error"/>, otherwise null.</summary>
    string? LastError { get; }

    /// <summary>
    /// Raised after <see cref="Current"/>, <see cref="Status"/> or the login state changed.
    /// May be raised on ANY thread (often a thread-pool thread): UI handlers must marshal to the UI thread
    /// (e.g. with <c>Browser.Post</c>) before touching UI state.
    /// </summary>
    event Action? Changed;

    /// <summary>Logs in and downloads the vault. Returns a failed result (never throws) for bad credentials or an unreachable server.</summary>
    Task<LoginResult> LoginAsync(string serverUrl, string username, string password, CancellationToken ct = default);

    /// <summary>Flushes pending changes, then clears all in-memory vault data.</summary>
    Task LogoutAsync();

    /// <summary>Pushes pending changes and pulls the latest vault from the server.</summary>
    Task SyncAsync(CancellationToken ct = default);

    /// <summary>Adds or replaces a host (matched by Id; an empty Id is assigned). The vault stores a copy.</summary>
    Task SaveHostAsync(HostEntry host);

    Task DeleteHostAsync(Guid hostId);

    /// <summary>Adds or replaces a group (matched by Id). The vault stores a copy.</summary>
    Task SaveGroupAsync(HostGroup group);

    /// <summary>Deletes a group; its hosts become ungrouped.</summary>
    Task DeleteGroupAsync(Guid groupId);

    /// <summary>Adds or replaces an identity (matched by Id). The vault stores a copy.</summary>
    Task SaveIdentityAsync(Identity identity);

    /// <summary>Deletes an identity; hosts that used it keep their other settings but lose the link.</summary>
    Task DeleteIdentityAsync(Guid identityId);

    /// <summary>Trusts a host key, replacing any previous key for the same host and port.</summary>
    Task AddKnownHostAsync(KnownHost knownHost);

    /// <summary>Records that a host was just connected to (sets <see cref="HostEntry.LastConnected"/>).</summary>
    Task TouchHostAsync(Guid hostId);
}
