using System;
using System.Collections.Generic;
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

    /// <summary>Not logged in, or logged in but the server is unreachable (then <see cref="IVaultService.LastError"/> is set and local changes are kept for later).</summary>
    Offline,
}

/// <summary>Where the vault lives: a TGK server account, the local vault on this device, or the dev mock.</summary>
public enum VaultMode
{
    Server,
    Local,
    Mock,
}

/// <summary>Outcome of a login or registration. <see cref="Error"/> is a message for the user.</summary>
public sealed record LoginResult(bool Success, string? Error, VaultError ErrorCode = VaultError.None)
{
    public static LoginResult Ok { get; } = new(true, null);
    public static LoginResult Fail(VaultError code, string error) => new(false, error, code);
}

/// <summary>What <c>GET /api/info</c> reports about a server.</summary>
public sealed record ServerInfo(string Name, string Version, int Protocol, bool RegistrationOpen);

/// <summary>A new authenticator secret: show <see cref="OtpAuthUri"/> as a QR code and <see cref="Secret"/> for manual entry.</summary>
public sealed record TotpEnrollment(string Secret, string OtpAuthUri);

/// <summary>A signed-in device of the current user; <see cref="Current"/> marks this device.</summary>
public sealed record DeviceSession(string Id, string DeviceName, string Platform, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, string? LastIp, bool Current);

/// <summary>
/// Client-side access to the user's vault (hosts, groups, identities, known host keys) held by the TGK server, or in
/// local mode by this device (<see cref="LocalVaultService"/>).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Current"/> is an immutable snapshot by convention: never modify it or the objects in it.
/// To edit, <c>Clone()</c> an entry, change the copy and pass it to a <c>Save*</c> method; every change
/// publishes a fresh snapshot, so a snapshot the UI is iterating never changes underneath it.
/// </para>
/// <para>
/// Mutations are applied to <see cref="Current"/> immediately (optimistically) and then pushed to the server;
/// the returned task completes once the next push attempt finished. A failed push does not throw: it sets
/// <see cref="Status"/> to <see cref="SyncState.Error"/> (or <see cref="SyncState.Offline"/>) and is retried
/// with backoff, with the next change or sync.
/// Mutations throw <see cref="InvalidOperationException"/> when not logged in and <see cref="ArgumentException"/> for invalid input.
/// </para>
/// <para>All members are thread-safe and may be called from the UI thread; IO happens asynchronously.</para>
/// </remarks>
public interface IVaultService
{
    /// <summary>
    /// Which kind of vault this is. Server-only members (sessions, account password, <see cref="GetServerInfoAsync"/>)
    /// are harmless no-ops or throw <see cref="NotSupportedException"/> for <see cref="VaultMode.Local"/>.
    /// </summary>
    VaultMode Mode { get; }

    /// <summary>Signed in (server), or unlocked (local vault).</summary>
    bool IsLoggedIn { get; }
    string? CurrentUser { get; }
    string? ServerUrl { get; }

    /// <summary>The latest vault snapshot (empty when logged out). Treat as read-only.</summary>
    VaultData Current { get; }

    SyncState Status { get; }
    DateTimeOffset? LastSync { get; }

    /// <summary>Human-readable reason for <see cref="SyncState.Error"/>, otherwise null.</summary>
    string? LastError { get; }

    /// <summary>Local changes the server has not accepted yet (0 when logged out).</summary>
    int PendingChanges { get; }

    /// <summary>
    /// Raised after <see cref="Current"/>, <see cref="Status"/> or the login state changed.
    /// May be raised on ANY thread (often a thread-pool thread): UI handlers must marshal to the UI thread
    /// (e.g. with <c>Browser.Post</c>) before touching UI state.
    /// </summary>
    event Action? Changed;

    /// <summary>
    /// Raised once when the server ended this device's session (signed out from another device, expired, account
    /// disabled). The vault is already cleared and the stored sign-in wiped; the argument is a message for the user.
    /// Changes that were not pushed yet are kept (encrypted) on this device and pushed after the next sign-in to the same account.
    /// Raised on a thread-pool thread, after the <see cref="Changed"/> that reports the logout.
    /// </summary>
    event Action<string>? SessionEnded;

    /// <summary>Returns the server's info, or null when it is unreachable, not a TGK server or the address is unusable.</summary>
    Task<ServerInfo?> GetServerInfoAsync(string serverUrl, CancellationToken ct = default);

    /// <summary>
    /// Logs in and downloads the vault. Returns a failed result (never throws) for bad credentials, a missing or wrong
    /// authenticator code (<see cref="VaultError.TotpRequired"/>, <see cref="VaultError.TotpInvalid"/>), an account
    /// that must enroll a new authenticator (<see cref="VaultError.TotpSetupRequired"/>: use <see cref="CompleteTotpSetupAsync"/>),
    /// an insecure address or an unreachable server. With <paramref name="keepSignedIn"/> the session and an
    /// encrypted vault cache are stored on this device for <see cref="TryRestoreSessionAsync"/>.
    /// </summary>
    Task<LoginResult> LoginAsync(string serverUrl, string username, string password, string? totpCode, bool keepSignedIn, CancellationToken ct = default);

    /// <summary>Logs in to an account whose authenticator was reset, enrolling <paramref name="newSecret"/> (from <see cref="BeginTotpEnrollment"/>) with a current <paramref name="code"/> for it.</summary>
    Task<LoginResult> CompleteTotpSetupAsync(string serverUrl, string username, string password, string newSecret, string code, bool keepSignedIn, CancellationToken ct = default);

    /// <summary>Creates a new authenticator secret for <paramref name="username"/> (generated locally; nothing is sent).</summary>
    TotpEnrollment BeginTotpEnrollment(string username);

    /// <summary>
    /// Creates an account (the code proves the authenticator holds <paramref name="totpSecret"/>) and logs in to its
    /// empty vault. <paramref name="inviteCode"/> (from the server admin) claims an invited username, also when
    /// registration is closed.
    /// </summary>
    Task<LoginResult> RegisterAsync(string serverUrl, string username, string password, string? inviteCode, string totpSecret, string totpCode, bool keepSignedIn, CancellationToken ct = default);

    /// <summary>
    /// Restores a "keep me signed in" session at startup: logs in from the stored device credentials and shows the
    /// cached vault immediately, then syncs in the background (an ended session raises <see cref="SessionEnded"/>).
    /// Returns false when nothing is stored.
    /// </summary>
    Task<bool> TryRestoreSessionAsync(CancellationToken ct = default);

    /// <summary>
    /// Flushes pending changes (best effort), signs this device out on the server and clears all local vault data and
    /// stored credentials. Changes the flush could not push are discarded: check <see cref="PendingChanges"/> first.
    /// </summary>
    Task LogoutAsync();

    /// <summary>The account's signed-in devices. Account methods throw <see cref="VaultException"/> on failure.</summary>
    Task<IReadOnlyList<DeviceSession>> ListSessionsAsync(CancellationToken ct = default);

    /// <summary>Signs a device out. Revoking this device's own session logs out (and raises <see cref="SessionEnded"/>).</summary>
    Task RevokeSessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Signs out every other device; returns how many sessions were revoked.</summary>
    Task<int> RevokeOtherSessionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Changes the account password (the vault key is re-wrapped; items are untouched). Throws
    /// <see cref="VaultException"/> with <see cref="VaultError.InvalidCredentials"/> for a wrong current password
    /// or <see cref="VaultError.TotpInvalid"/> for a wrong code.
    /// </summary>
    Task ChangePasswordAsync(string currentPassword, string totpCode, string newPassword, bool revokeOtherSessions, CancellationToken ct = default);

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

    /// <summary>
    /// Replaces the connection defaults every host inherits (<see cref="VaultData.Defaults"/>; see <see cref="EffectiveOptions"/>).
    /// The vault stores a copy.
    /// </summary>
    Task SaveDefaultsAsync(HostOptions defaults);

    /// <summary>
    /// Replaces the workspace: the "reopen my tabs" preference and the saved tabs and split views
    /// (<see cref="VaultData.Workspace"/>). The vault stores a copy.
    /// </summary>
    Task SaveWorkspaceAsync(Workspace workspace);

    /// <summary>Trusts a host key, replacing any previous key for the same host and port.</summary>
    Task AddKnownHostAsync(KnownHost knownHost);

    /// <summary>Records that a host was just connected to (sets <see cref="HostEntry.LastConnected"/>, which is kept per device and not synced).</summary>
    Task TouchHostAsync(Guid hostId);
}

/// <summary>Applies an arbitrary edit (e.g. an import) to the vault like the <c>Save*</c> methods do.</summary>
internal interface IVaultEditor
{
    /// <summary>
    /// Runs <paramref name="edit"/> on a shallow copy of the current snapshot (it must replace entities, never modify
    /// them), publishes it and stores/pushes the changed items. Completes like a <c>Save*</c> call.
    /// </summary>
    Task EditAsync(Action<VaultData> edit);
}
