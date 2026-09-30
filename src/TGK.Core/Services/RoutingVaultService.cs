using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Models;

namespace TGK.Core.Services;

/// <summary>
/// The client's vault: the server account (<see cref="RemoteVaultService"/>), the local vault (<see cref="Local"/>)
/// or, for <c>mock://</c> addresses, the dev <see cref="MockVaultService"/>. A login or registration picks the server
/// or mock service, <see cref="CreateLocalAsync"/> and <see cref="UnlockLocalAsync"/> the local vault (and
/// <see cref="VaultMigration"/> moves between them); switching logs out (or locks) the previous one. All other
/// members use the one picked last, and events are forwarded from it only.
/// </summary>
public sealed class RoutingVaultService : IVaultService, IVaultEditor
{
    private readonly IVaultService _mock;
    private volatile IVaultService _active;

    public RoutingVaultService(RemoteVaultService remote, IVaultService mock, LocalVaultService local)
    {
        Remote = remote;
        Local = local;
        _active = remote;
        _mock = mock;
        foreach (IVaultService service in new[] { remote, mock, local })
        {
            service.Changed += () =>
            {
                if (_active == service)
                    Changed?.Invoke();
            };
            service.SessionEnded += reason =>
            {
                if (_active == service)
                    SessionEnded?.Invoke(reason);
            };
        }
    }

    public event Action? Changed;
    public event Action<string>? SessionEnded;

    /// <summary>The local vault: <see cref="LocalVaultService.Exists"/>, <see cref="LocalVaultService.ChangeMasterPasswordAsync"/>, <see cref="LocalVaultService.DeleteVault"/>.</summary>
    public LocalVaultService Local { get; }

    internal RemoteVaultService Remote { get; }

    public VaultMode Mode => _active.Mode;
    public bool IsLoggedIn => _active.IsLoggedIn;
    public string? CurrentUser => _active.CurrentUser;
    public string? ServerUrl => _active.ServerUrl;
    public VaultData Current => _active.Current;
    public SyncState Status => _active.Status;
    public DateTimeOffset? LastSync => _active.LastSync;
    public string? LastError => _active.LastError;
    public int PendingChanges => _active.PendingChanges;

    public Task<ServerInfo?> GetServerInfoAsync(string serverUrl, CancellationToken ct = default) =>
        For(serverUrl).GetServerInfoAsync(serverUrl, ct);

    public TotpEnrollment BeginTotpEnrollment(string username) => Remote.BeginTotpEnrollment(username);

    public async Task<LoginResult> LoginAsync(string serverUrl, string username, string password, string? totpCode, bool keepSignedIn, CancellationToken ct = default) =>
        await (await SwitchToAsync(For(serverUrl)).ConfigureAwait(false)).LoginAsync(serverUrl, username, password, totpCode, keepSignedIn, ct).ConfigureAwait(false);

    public async Task<LoginResult> CompleteTotpSetupAsync(string serverUrl, string username, string password, string newSecret, string code, bool keepSignedIn, CancellationToken ct = default) =>
        await (await SwitchToAsync(For(serverUrl)).ConfigureAwait(false)).CompleteTotpSetupAsync(serverUrl, username, password, newSecret, code, keepSignedIn, ct).ConfigureAwait(false);

    public async Task<LoginResult> RegisterAsync(string serverUrl, string username, string password, string? inviteCode, string totpSecret, string totpCode, bool keepSignedIn, CancellationToken ct = default) =>
        await (await SwitchToAsync(For(serverUrl)).ConfigureAwait(false)).RegisterAsync(serverUrl, username, password, inviteCode, totpSecret, totpCode, keepSignedIn, ct).ConfigureAwait(false);

    /// <summary>Creates the local vault (see <see cref="LocalVaultService.CreateAsync(string, bool, CancellationToken)"/>) and, on success, switches to it.</summary>
    public async Task<LoginResult> CreateLocalAsync(string masterPassword, bool keepUnlocked, CancellationToken ct = default) =>
        await SwitchOnSuccessAsync(await Local.CreateAsync(masterPassword, keepUnlocked, ct).ConfigureAwait(false)).ConfigureAwait(false);

    /// <summary>Unlocks the local vault (see <see cref="LocalVaultService.UnlockAsync"/>) and, on success, switches to it.</summary>
    public async Task<LoginResult> UnlockLocalAsync(string masterPassword, bool keepUnlocked, CancellationToken ct = default) =>
        await SwitchOnSuccessAsync(await Local.UnlockAsync(masterPassword, keepUnlocked, ct).ConfigureAwait(false)).ConfigureAwait(false);

    private async Task<LoginResult> SwitchOnSuccessAsync(LoginResult result)
    {
        if (result.Success)
            await SwitchToAsync(Local).ConfigureAwait(false);
        return result;
    }

    /// <summary>Restores a kept server sign-in, else a kept local unlock (only one of them is ever kept).</summary>
    public async Task<bool> TryRestoreSessionAsync(CancellationToken ct = default)
    {
        if (_active.IsLoggedIn && _active != Remote)
            return false;
        foreach (IVaultService service in new IVaultService[] { Remote, Local })
        {
            _active = service;
            if (await service.TryRestoreSessionAsync(ct).ConfigureAwait(false))
                return true;
        }
        _active = Remote;
        return false;
    }

    public Task LogoutAsync() => _active.LogoutAsync();
    public Task SyncAsync(CancellationToken ct = default) => _active.SyncAsync(ct);
    public Task<IReadOnlyList<DeviceSession>> ListSessionsAsync(CancellationToken ct = default) => _active.ListSessionsAsync(ct);
    public Task RevokeSessionAsync(string sessionId, CancellationToken ct = default) => _active.RevokeSessionAsync(sessionId, ct);
    public Task<int> RevokeOtherSessionsAsync(CancellationToken ct = default) => _active.RevokeOtherSessionsAsync(ct);

    public Task ChangePasswordAsync(string currentPassword, string totpCode, string newPassword, bool revokeOtherSessions, CancellationToken ct = default) =>
        _active.ChangePasswordAsync(currentPassword, totpCode, newPassword, revokeOtherSessions, ct);

    public Task SaveHostAsync(HostEntry host) => _active.SaveHostAsync(host);
    public Task DeleteHostAsync(Guid hostId) => _active.DeleteHostAsync(hostId);
    public Task SaveGroupAsync(HostGroup group) => _active.SaveGroupAsync(group);
    public Task DeleteGroupAsync(Guid groupId) => _active.DeleteGroupAsync(groupId);
    public Task SaveIdentityAsync(Identity identity) => _active.SaveIdentityAsync(identity);
    public Task DeleteIdentityAsync(Guid identityId) => _active.DeleteIdentityAsync(identityId);
    public Task SaveDefaultsAsync(HostOptions defaults) => _active.SaveDefaultsAsync(defaults);

    public Task SaveWorkspaceAsync(Workspace workspace) => _active.SaveWorkspaceAsync(workspace);
    public Task AddKnownHostAsync(KnownHost knownHost) => _active.AddKnownHostAsync(knownHost);
    public Task TouchHostAsync(Guid hostId) => _active.TouchHostAsync(hostId);

    Task IVaultEditor.EditAsync(Action<VaultData> edit) => ((IVaultEditor)_active).EditAsync(edit);

    private IVaultService For(string serverUrl) => ServerAddress.IsMock(serverUrl) ? _mock : Remote;

    /// <summary>Makes <paramref name="target"/> active, then logs out (locks) the previous one if it is signed in.</summary>
    internal async Task<IVaultService> SwitchToAsync(IVaultService target)
    {
        IVaultService previous = _active;
        _active = target;
        if (previous != target && previous.IsLoggedIn)
            await previous.LogoutAsync().ConfigureAwait(false);
        if (previous != target)
            Changed?.Invoke(); // what the target did while inactive was not forwarded
        return target;
    }
}
