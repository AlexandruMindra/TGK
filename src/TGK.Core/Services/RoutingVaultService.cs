using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Models;

namespace TGK.Core.Services;

/// <summary>
/// The client's vault: <c>mock://</c> server addresses go to the dev <see cref="MockVaultService"/>, everything
/// else to the real <see cref="RemoteVaultService"/>. A login or registration picks the service; all other
/// members use the one picked last. Events are forwarded from the active service only.
/// </summary>
public sealed class RoutingVaultService : IVaultService
{
    private readonly IVaultService _remote;
    private readonly IVaultService _mock;
    private volatile IVaultService _active;

    public RoutingVaultService(IVaultService remote, IVaultService mock)
    {
        _remote = _active = remote;
        _mock = mock;
        foreach (IVaultService service in new[] { remote, mock })
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

    public TotpEnrollment BeginTotpEnrollment(string username) => _remote.BeginTotpEnrollment(username);

    public async Task<LoginResult> LoginAsync(string serverUrl, string username, string password, string? totpCode, bool keepSignedIn, CancellationToken ct = default) =>
        await (await SwitchToAsync(serverUrl).ConfigureAwait(false)).LoginAsync(serverUrl, username, password, totpCode, keepSignedIn, ct).ConfigureAwait(false);

    public async Task<LoginResult> CompleteTotpSetupAsync(string serverUrl, string username, string password, string newSecret, string code, bool keepSignedIn, CancellationToken ct = default) =>
        await (await SwitchToAsync(serverUrl).ConfigureAwait(false)).CompleteTotpSetupAsync(serverUrl, username, password, newSecret, code, keepSignedIn, ct).ConfigureAwait(false);

    public async Task<LoginResult> RegisterAsync(string serverUrl, string username, string password, string? inviteCode, string totpSecret, string totpCode, bool keepSignedIn, CancellationToken ct = default) =>
        await (await SwitchToAsync(serverUrl).ConfigureAwait(false)).RegisterAsync(serverUrl, username, password, inviteCode, totpSecret, totpCode, keepSignedIn, ct).ConfigureAwait(false);

    public async Task<bool> TryRestoreSessionAsync(CancellationToken ct = default)
    {
        if (_active != _remote && _active.IsLoggedIn)
            return false;
        _active = _remote;
        return await _remote.TryRestoreSessionAsync(ct).ConfigureAwait(false);
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
    public Task AddKnownHostAsync(KnownHost knownHost) => _active.AddKnownHostAsync(knownHost);
    public Task TouchHostAsync(Guid hostId) => _active.TouchHostAsync(hostId);

    private IVaultService For(string serverUrl) => ServerAddress.IsMock(serverUrl) ? _mock : _remote;

    /// <summary>Makes the service for <paramref name="serverUrl"/> active, logging out the other one first.</summary>
    private async Task<IVaultService> SwitchToAsync(string serverUrl)
    {
        IVaultService target = For(serverUrl);
        IVaultService previous = _active;
        if (previous != target && previous.IsLoggedIn)
            await previous.LogoutAsync().ConfigureAwait(false);
        _active = target;
        return target;
    }
}
