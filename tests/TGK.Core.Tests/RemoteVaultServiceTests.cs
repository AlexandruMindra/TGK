using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Models;
using TGK.Core.Services;
using TGK.Protocol;
using TGK.Protocol.Dtos;
using Xunit;

namespace TGK.Core.Tests;

public sealed class RemoteVaultServiceTests : IDisposable
{
    private const string User = "alice", Password = "correct horse";
    private readonly TempDirectory _dir = new();
    private readonly FakeTgkServer _server = new();
    private readonly MemoryCredentialStore _store = new();
    private readonly List<RemoteVaultService> _services = [];
    private readonly string _secret = Totp.GenerateSecret();
    private readonly byte[] _vaultKey;

    public RemoteVaultServiceTests()
    {
        _vaultKey = _server.AddUser(User, Password, _secret);
    }

    public void Dispose()
    {
        foreach (RemoteVaultService service in _services)
            service.Dispose();
        _dir.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string CachePath => Path.Combine(_dir.Path, VaultCache.FileName);

    private RemoteVaultService NewService(MemoryCredentialStore? store = null)
    {
        var service = new RemoteVaultService(new RemoteVaultOptions
        {
            BaseDirectory = _dir.Path,
            Credentials = store ?? _store,
            HttpHandler = _server,
            PushDelay = TimeSpan.FromMilliseconds(10),
            PullInterval = TimeSpan.FromHours(1), // tests pull explicitly with SyncAsync
            MinRetryDelay = TimeSpan.FromMilliseconds(50),
            MaxRetryDelay = TimeSpan.FromMilliseconds(200),
            DeviceName = "test-device",
            NewKdf = FakeTgkServer.TestKdf,
        });
        _services.Add(service);
        return service;
    }

    private async Task<RemoteVaultService> LoggedInAsync(bool keep = false, MemoryCredentialStore? store = null)
    {
        RemoteVaultService vault = NewService(store);
        LoginResult result = await vault.LoginAsync(FakeTgkServer.Url, User, Password, FakeTgkServer.CurrentCode(_secret), keep, Ct);
        Assert.True(result.Success, result.Error);
        return vault;
    }

    private VaultItems Codec => new(_vaultKey);

    private byte[] Seal(object entity) => Codec.Encrypt(Codec.IdOf(entity), entity);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 250 && !condition(); i++)
            await Task.Delay(20, Ct);
        Assert.True(condition(), "Condition not met within 5 s.");
    }

    [Fact]
    public async Task Login_DownloadsAndDecryptsVault_SkippingUnreadableItems()
    {
        var host = new HostEntry { Name = "web", Host = "web.example.com" };
        var group = new HostGroup { Name = "Prod" };
        _server.PutItem(User, Codec.IdOf(host), Seal(host));
        _server.PutItem(User, Codec.IdOf(group), Seal(group));
        _server.PutItem(User, Guid.NewGuid().ToString(), [1, 2, 3]); // garbage from a broken client
        _server.PutItem(User, Codec.IdOf(group), null); // group deleted again (tombstone)

        RemoteVaultService vault = await LoggedInAsync();

        Assert.True(vault.IsLoggedIn);
        Assert.Equal(User, vault.CurrentUser);
        Assert.Equal(FakeTgkServer.Url, vault.ServerUrl);
        Assert.Equal(SyncState.Idle, vault.Status);
        Assert.NotNull(vault.LastSync);
        HostEntry loaded = Assert.Single(vault.Current.Hosts);
        Assert.Equal(host.Id, loaded.Id);
        Assert.Equal("web.example.com", loaded.Host);
        Assert.Empty(vault.Current.Groups);
        // Not kept signed in: nothing stored on the device.
        Assert.Null(_store.Stored);
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public async Task Defaults_SyncToAnotherDevice_AndUnknownKindsAreSkipped()
    {
        var host = new HostEntry { Name = "web", Host = "web.example.com" };
        _server.PutItem(User, Codec.IdOf(host), Seal(host));
        string futureId = Guid.NewGuid().ToString();
        _server.PutItem(User, futureId, VaultCrypto.EncryptItem(_vaultKey, futureId, "futureKind", new { x = 1 }, TgkJson.Options));

        RemoteVaultService deviceA = await LoggedInAsync();
        Assert.Single(deviceA.Current.Hosts); // the item of an unknown kind did not break the login
        Assert.True(deviceA.Current.Defaults.IsEmpty); // no settings item yet: built-in defaults

        await deviceA.SaveDefaultsAsync(new HostOptions { KeepAliveSeconds = 5, LegacyAlgorithms = true });
        Assert.Equal(0, deviceA.PendingChanges);
        VaultItem? stored = _server.GetItem(User, Codec.SettingsId);
        Assert.NotNull(stored);
        Assert.Equal(ItemKinds.Settings, VaultCrypto.DecryptItem(_vaultKey, Codec.SettingsId, stored.Data!).Kind);

        RemoteVaultService deviceB = await LoggedInAsync();
        Assert.Equal(5, deviceB.Current.Defaults.KeepAliveSeconds);
        Assert.True(deviceB.Current.Defaults.LegacyAlgorithms);
        Assert.Single(deviceB.Current.Hosts);
    }

    [Fact]
    public async Task Workspace_SyncsToAnotherDevice()
    {
        RemoteVaultService deviceA = await LoggedInAsync();
        Assert.False(deviceA.Current.Workspace.RestoreTabs); // off until the user turns it on

        Workspace workspace = WorkspaceTests.Sample();
        await deviceA.SaveWorkspaceAsync(workspace);
        Assert.Equal(0, deviceA.PendingChanges);
        VaultItem? stored = _server.GetItem(User, Codec.WorkspaceId);
        Assert.NotNull(stored);
        Assert.Equal(ItemKinds.Workspace, VaultCrypto.DecryptItem(_vaultKey, Codec.WorkspaceId, stored.Data!).Kind);

        RemoteVaultService deviceB = await LoggedInAsync();
        Assert.Equivalent(workspace, deviceB.Current.Workspace);

        // Turned off on B: A sees it on its next pull.
        await deviceB.SaveWorkspaceAsync(new Workspace());
        await deviceA.SyncAsync(Ct);
        Assert.True(deviceA.Current.Workspace.IsEmpty);
    }

    [Fact]
    public async Task Unsent_saved_tabs_never_replace_tabs_saved_later_on_another_device()
    {
        RemoteVaultService deviceA = await LoggedInAsync();
        _server.Offline = true;
        Workspace stale = WorkspaceTests.Sample();
        stale.SavedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        stale.SavedOn = "A";
        await deviceA.SaveWorkspaceAsync(stale); // queued: the server can't be reached
        Assert.True(deviceA.PendingChanges > 0);

        // Meanwhile device B saves newer tabs.
        Workspace newer = WorkspaceTests.Sample();
        newer.SavedAt = DateTimeOffset.UtcNow;
        newer.SavedOn = "B";
        _server.PutItem(User, Codec.WorkspaceId, Seal(newer));

        _server.Offline = false;
        await deviceA.SyncAsync(Ct);

        Assert.Equal("B", deviceA.Current.Workspace.SavedOn);
        Assert.Equal(0, deviceA.PendingChanges);
        VaultItem? stored = _server.GetItem(User, Codec.WorkspaceId);
        var onServer = new VaultData();
        Codec.Apply(onServer, Codec.WorkspaceId, stored!.Data);
        Assert.Equal("B", onServer.Workspace.SavedOn);

        // A later save on A still goes through.
        Workspace latest = WorkspaceTests.Sample();
        latest.SavedAt = DateTimeOffset.UtcNow.AddSeconds(5);
        latest.SavedOn = "A";
        await deviceA.SaveWorkspaceAsync(latest);
        await deviceA.SyncAsync(Ct);
        Codec.Apply(onServer, Codec.WorkspaceId, _server.GetItem(User, Codec.WorkspaceId)!.Data);
        Assert.Equal("A", onServer.Workspace.SavedOn);
    }

    [Theory]
    [InlineData("wrong-password", VaultError.InvalidCredentials)]
    [InlineData("unknown-user", VaultError.InvalidCredentials)]
    [InlineData("no-code", VaultError.TotpRequired)]
    [InlineData("wrong-code", VaultError.TotpInvalid)]
    [InlineData("totp-reset", VaultError.TotpSetupRequired)]
    [InlineData("disabled", VaultError.AccountDisabled)]
    [InlineData("rate-limited", VaultError.RateLimited)]
    [InlineData("offline", VaultError.Network)]
    [InlineData("insecure", VaultError.InsecureUrl)]
    [InlineData("weak-kdf", VaultError.IncompatibleServer)]
    [InlineData("not-tgk", VaultError.IncompatibleServer)]
    public async Task Login_Failures_ReportErrorCode(string scenario, VaultError expected)
    {
        string url = FakeTgkServer.Url, user = User, password = Password;
        string? code = FakeTgkServer.CurrentCode(_secret);
        switch (scenario)
        {
            case "wrong-password": password = "nope"; break;
            case "unknown-user": user = "mallory"; break;
            case "no-code": code = null; break;
            case "wrong-code": code = code == "000000" ? "111111" : "000000"; break;
            case "totp-reset": _server.GetUser(User).TotpSecret = null; break;
            case "disabled": _server.GetUser(User).Disabled = true; break;
            case "rate-limited": _server.Intercept = _ => FakeTgkServer.Error((HttpStatusCode)429, ErrorCodes.RateLimited); break;
            case "offline": _server.Offline = true; break;
            case "insecure": url = "http://tgk.test"; break;
            case "weak-kdf": _server.GetUser(User).Kdf = new KdfParams(KdfParams.Pbkdf2Sha256, FastCrypto.KdfIterations - 1); break;
            case "not-tgk": _server.Intercept = _ => new System.Net.Http.HttpResponseMessage(HttpStatusCode.NotFound); break;
        }
        RemoteVaultService vault = NewService();

        LoginResult result = await vault.LoginAsync(url, user, password, code, keepSignedIn: true, Ct);

        Assert.False(result.Success);
        Assert.Equal(expected, result.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.False(vault.IsLoggedIn);
        Assert.Null(_store.Stored);
        if (scenario is "insecure" or "weak-kdf")
            Assert.DoesNotContain("POST /api/login", _server.Requests);
    }

    [Fact]
    public async Task Login_AllowsPlainHttpOnLoopback()
    {
        RemoteVaultService vault = NewService();

        LoginResult result = await vault.LoginAsync("http://localhost:5080", User, Password, FakeTgkServer.CurrentCode(_secret), false, Ct);

        Assert.True(result.Success, result.Error);
        Assert.Equal("http://localhost:5080", vault.ServerUrl);
    }

    [Fact]
    public async Task TotpSetupRequired_CompletedWithNewSecret()
    {
        _server.GetUser(User).TotpSecret = null;
        RemoteVaultService vault = NewService();
        TotpEnrollment enrollment = vault.BeginTotpEnrollment(User);
        Assert.StartsWith("otpauth://totp/TGK:alice?secret=" + enrollment.Secret, enrollment.OtpAuthUri);

        LoginResult wrong = await vault.CompleteTotpSetupAsync(FakeTgkServer.Url, User, Password, enrollment.Secret, "000000" == FakeTgkServer.CurrentCode(enrollment.Secret) ? "111111" : "000000", false, Ct);
        LoginResult result = await vault.CompleteTotpSetupAsync(FakeTgkServer.Url, User, Password, enrollment.Secret, FakeTgkServer.CurrentCode(enrollment.Secret), false, Ct);

        Assert.Equal(VaultError.TotpSetupRequired, wrong.ErrorCode);
        Assert.True(result.Success, result.Error);
        Assert.Equal(enrollment.Secret, _server.GetUser(User).TotpSecret);
    }

    [Fact]
    public async Task Register_ThenEditsReachAnotherDevice()
    {
        RemoteVaultService deviceA = NewService();
        TotpEnrollment enrollment = deviceA.BeginTotpEnrollment("bob");
        Assert.Equal(VaultError.ValidationFailed, (await deviceA.RegisterAsync(FakeTgkServer.Url, "b", "long enough", null, enrollment.Secret, "123456", false, Ct)).ErrorCode);
        Assert.Equal(VaultError.ValidationFailed, (await deviceA.RegisterAsync(FakeTgkServer.Url, "bob", "short", null, enrollment.Secret, "123456", false, Ct)).ErrorCode);

        LoginResult registered = await deviceA.RegisterAsync(FakeTgkServer.Url, "bob", "bob's password", null, enrollment.Secret, FakeTgkServer.CurrentCode(enrollment.Secret), false, Ct);
        Assert.True(registered.Success, registered.Error);
        Assert.Equal(VaultError.UsernameTaken,
            (await NewService().RegisterAsync(FakeTgkServer.Url, "BOB", "bob's password", null, enrollment.Secret, FakeTgkServer.CurrentCode(enrollment.Secret), false, Ct)).ErrorCode);
        Assert.Empty(deviceA.Current.Hosts);

        var host = new HostEntry { Name = "db", Host = "db.internal", Port = 2222 };
        await deviceA.SaveHostAsync(host);
        await deviceA.AddKnownHostAsync(new KnownHost { Host = "DB.internal", Port = 2222, KeyType = "ssh-ed25519", FingerprintSha256 = "SHA256:a" });
        await deviceA.AddKnownHostAsync(new KnownHost { Host = "db.internal", Port = 2222, KeyType = "ssh-ed25519", FingerprintSha256 = "SHA256:b" });
        Assert.Equal(SyncState.Idle, deviceA.Status);
        Assert.Equal(3, _server.GetUser("bob").Revision); // the known host is one item, replaced

        RemoteVaultService deviceB = NewService();
        LoginResult login = await deviceB.LoginAsync(FakeTgkServer.Url, "bob", "bob's password", FakeTgkServer.CurrentCode(enrollment.Secret), false, Ct);
        Assert.True(login.Success, login.Error);
        Assert.Equal("db.internal", Assert.Single(deviceB.Current.Hosts).Host);
        Assert.Equal("SHA256:b", Assert.Single(deviceB.Current.KnownHosts).FingerprintSha256);
    }

    [Fact]
    public async Task Pull_AppliesRemoteChangesAndTombstones()
    {
        var keep = new HostEntry { Name = "keep", Host = "a" };
        var gone = new HostEntry { Name = "gone", Host = "b" };
        _server.PutItem(User, Codec.IdOf(keep), Seal(keep));
        _server.PutItem(User, Codec.IdOf(gone), Seal(gone));
        RemoteVaultService vault = await LoggedInAsync();
        int changes = 0;
        vault.Changed += () => Interlocked.Increment(ref changes);

        var added = new Identity { Name = "ops", Username = "root", Password = "s3cret" };
        _server.PutItem(User, Codec.IdOf(added), Seal(added));
        _server.PutItem(User, Codec.IdOf(gone), null);
        await vault.SyncAsync(Ct);

        Assert.Equal(["keep"], vault.Current.Hosts.Select(h => h.Name));
        Assert.Equal("s3cret", Assert.Single(vault.Current.Identities).Password);
        Assert.True(changes > 0);
        Assert.Equal(SyncState.Idle, vault.Status);
    }

    [Fact]
    public async Task FailedPush_IsRetriedFromOutbox_AndLocalEditWinsOverRemote()
    {
        var host = new HostEntry { Name = "web", Host = "old" };
        _server.PutItem(User, Codec.IdOf(host), Seal(host));
        RemoteVaultService vault = await LoggedInAsync();

        _server.Offline = true;
        HostEntry edit = vault.Current.Hosts[0].Clone();
        edit.Host = "local";
        await vault.SaveHostAsync(edit); // completes after the (failed) push attempt
        Assert.Equal(SyncState.Offline, vault.Status);
        Assert.NotNull(vault.LastError);
        Assert.True(vault.IsLoggedIn);
        Assert.Equal("local", vault.Current.Hosts[0].Host);

        // Another device changes the same host meanwhile; our queued edit is newer and wins.
        HostEntry remote = host.Clone();
        remote.Host = "remote";
        _server.PutItem(User, Codec.IdOf(host), Seal(remote));
        _server.Offline = false;

        await WaitUntilAsync(() => vault.Status == SyncState.Idle); // backoff retry
        await vault.SyncAsync(Ct);
        Assert.Equal("local", vault.Current.Hosts[0].Host);
        VaultItem stored = _server.GetItem(User, Codec.IdOf(host))!;
        Assert.Equal("local", VaultItems_Decrypt<HostEntry>(stored).Host);
        Assert.Null(vault.LastError);
    }

    [Fact]
    public async Task EditBurst_IsPushedAsOneBatch_AndDeletingAGroupLeavesItsHostsAlone()
    {
        RemoteVaultService vault = await LoggedInAsync();
        var group = new HostGroup { Name = "g" };
        await vault.SaveGroupAsync(group);
        int pushesBefore = _server.Requests.Count(r => r == "POST /api/vault");

        Task[] saves = Enumerable.Range(0, 5).Select(i => vault.SaveHostAsync(new HostEntry { Name = $"h{i}", Host = $"h{i}", GroupId = group.Id })).ToArray();
        await Task.WhenAll(saves);
        Assert.Equal(pushesBefore + 1, _server.Requests.Count(r => r == "POST /api/vault"));

        // Only the group is deleted: its hosts read as ungrouped, and no (possibly stale) copy of them is re-uploaded.
        long revision = _server.GetUser(User).Revision;
        await vault.DeleteGroupAsync(group.Id);
        Assert.All(vault.Current.Hosts, h => Assert.Null(vault.Current.FindGroup(h.GroupId)));
        Assert.True(_server.GetItem(User, Codec.IdOf(group))!.Deleted);
        Assert.Equal(revision + 1, _server.GetUser(User).Revision);
    }

    [Fact]
    public async Task TouchHost_IsKeptOnThisDevice_AndNeverOverwritesRemoteEdits()
    {
        var host = new HostEntry { Name = "web", Host = "old" };
        _server.PutItem(User, Codec.IdOf(host), Seal(host));
        RemoteVaultService vault = await LoggedInAsync(keep: true);
        long revision = _server.GetUser(User).Revision;

        await vault.TouchHostAsync(host.Id); // before this device pulled another device's rename
        HostEntry renamed = host.Clone();
        renamed.Name = "renamed";
        _server.PutItem(User, Codec.IdOf(host), Seal(renamed));
        await vault.SyncAsync(Ct);

        Assert.Equal(revision + 1, _server.GetUser(User).Revision); // only the rename: connecting pushed nothing
        HostEntry local = Assert.Single(vault.Current.Hosts);
        Assert.Equal("renamed", local.Name);
        Assert.NotNull(local.LastConnected);

        // An edit of the host syncs it without the device-local time, which survives a restart.
        HostEntry edit = local.Clone();
        edit.Notes = "edited";
        await vault.SaveHostAsync(edit);
        Assert.Null(VaultItems_Decrypt<HostEntry>(_server.GetItem(User, Codec.IdOf(host))!).LastConnected);
        vault.Dispose();
        RemoteVaultService restarted = NewService();
        Assert.True(await restarted.TryRestoreSessionAsync(Ct));
        Assert.Equal(local.LastConnected, Assert.Single(restarted.Current.Hosts).LastConnected);
    }

    [Fact]
    public async Task RevokedSession_KeepsUnsentChanges_ForTheNextSignIn()
    {
        RemoteVaultService vault = await LoggedInAsync();
        _server.Offline = true;
        await vault.SaveHostAsync(new HostEntry { Name = "offline edit", Host = "x" });
        Assert.Equal(1, vault.PendingChanges);
        string? reason = null;
        vault.SessionEnded += r => reason = r;

        _server.Offline = false;
        _server.RevokeAll(); // e.g. "sign out all other devices" from another device
        await vault.SyncAsync(Ct);

        Assert.False(vault.IsLoggedIn);
        Assert.Contains("1 unsynced change is kept", reason);
        Assert.Empty(_server.GetUser(User).Items);
        Assert.True(File.Exists(CachePath));

        RemoteVaultService again = await LoggedInAsync();
        Assert.Equal("offline edit", Assert.Single(again.Current.Hosts).Name);
        await WaitUntilAsync(() => _server.GetUser(User).Items.Count == 1 && again.PendingChanges == 0);
        Assert.False(File.Exists(CachePath)); // not kept signed in: nothing stays on the device
    }

    [Fact]
    public async Task ServerRevisionGoingBack_ResyncsAndRestoresWhatTheServerLost()
    {
        var kept = new HostEntry { Name = "kept", Host = "a" };
        var lost = new HostEntry { Name = "lost", Host = "b" };
        _server.PutItem(User, Codec.IdOf(kept), Seal(kept));
        _server.PutItem(User, Codec.IdOf(lost), Seal(lost));
        RemoteVaultService vault = await LoggedInAsync();

        // The admin restores a backup taken before "lost" was added.
        string lostId = Codec.IdOf(lost);
        _server.Change(User, user =>
        {
            user.Items.Remove(lostId);
            user.Revision = 1;
        });
        await vault.SyncAsync(Ct);

        await WaitUntilAsync(() => _server.GetItem(User, Codec.IdOf(lost)) is { Deleted: false } && vault.PendingChanges == 0);
        Assert.Equal(["kept", "lost"], vault.Current.Hosts.Select(h => h.Name).Order());
    }

    [Fact]
    public async Task RevokedSession_RaisesSessionEnded_AndWipesDevice()
    {
        RemoteVaultService vault = await LoggedInAsync(keep: true);
        await vault.SaveHostAsync(new HostEntry { Host = "x" });
        Assert.NotNull(_store.Stored);
        Assert.True(File.Exists(CachePath));
        string? reason = null;
        vault.SessionEnded += r => reason = r;

        _server.RevokeAll();
        await vault.SyncAsync(Ct);

        Assert.NotNull(reason);
        Assert.False(vault.IsLoggedIn);
        Assert.Empty(vault.Current.Hosts);
        Assert.Equal(SyncState.Offline, vault.Status);
        Assert.Null(_store.Stored);
        Assert.False(File.Exists(CachePath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.SaveHostAsync(new HostEntry { Host = "y" }));
    }

    [Fact]
    public async Task Restore_ShowsCachedVaultOffline_ThenPushesQueuedEdits()
    {
        RemoteVaultService first = await LoggedInAsync(keep: true);
        await first.SaveHostAsync(new HostEntry { Name = "synced", Host = "a" });
        if (OperatingSystem.IsLinux())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(CachePath));
        _server.Offline = true;
        await first.SaveHostAsync(new HostEntry { Name = "queued", Host = "b" });
        first.Dispose(); // app closed while offline

        RemoteVaultService second = NewService();
        Assert.True(await second.TryRestoreSessionAsync(Ct));

        Assert.True(second.IsLoggedIn);
        Assert.Equal(User, second.CurrentUser);
        Assert.Equal(["queued", "synced"], second.Current.Hosts.Select(h => h.Name).Order());
        await WaitUntilAsync(() => second.Status == SyncState.Offline && second.LastError is not null);

        _server.Offline = false;
        await WaitUntilAsync(() => second.Status == SyncState.Idle);
        Assert.Equal(2, _server.GetUser(User).Items.Count);
    }

    [Fact]
    public async Task Restore_ReturnsFalse_WhenNothingStored()
    {
        RemoteVaultService vault = NewService();

        Assert.False(await vault.TryRestoreSessionAsync(Ct));
        Assert.False(vault.IsLoggedIn);
    }

    [Fact]
    public async Task Logout_RevokesSessionAndWipesDevice()
    {
        RemoteVaultService vault = await LoggedInAsync(keep: true);
        Assert.Equal(1, _server.SessionCount(User));

        await vault.LogoutAsync();

        Assert.False(vault.IsLoggedIn);
        Assert.Equal(0, _server.SessionCount(User));
        Assert.Null(_store.Stored);
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public async Task Sessions_ListAndRevokeOthers()
    {
        RemoteVaultService other = await LoggedInAsync(store: new MemoryCredentialStore());
        RemoteVaultService vault = await LoggedInAsync();
        string? otherEnded = null;
        other.SessionEnded += r => otherEnded = r;

        IReadOnlyList<DeviceSession> sessions = await vault.ListSessionsAsync(Ct);
        Assert.Equal(2, sessions.Count);
        Assert.Single(sessions, s => s.Current);

        Assert.Equal(1, await vault.RevokeOtherSessionsAsync(Ct));
        await other.SyncAsync(Ct);
        Assert.NotNull(otherEnded);
        Assert.True(vault.IsLoggedIn);

        await Assert.ThrowsAsync<VaultException>(() => vault.RevokeSessionAsync("nope", Ct));
        string current = sessions.Single(s => s.Current).Id;
        await vault.RevokeSessionAsync(current, Ct);
        Assert.False(vault.IsLoggedIn);
    }

    [Fact]
    public async Task ChangePassword_RewrapsVaultKey()
    {
        var host = new HostEntry { Host = "kept" };
        _server.PutItem(User, Codec.IdOf(host), Seal(host));
        RemoteVaultService vault = await LoggedInAsync();
        string code = FakeTgkServer.CurrentCode(_secret);

        VaultException wrong = await Assert.ThrowsAsync<VaultException>(() => vault.ChangePasswordAsync("nope", code, "new password", false, Ct));
        Assert.Equal(VaultError.InvalidCredentials, wrong.Code);
        await vault.ChangePasswordAsync(Password, code, "new password", false, Ct);

        RemoteVaultService again = NewService();
        Assert.Equal(VaultError.InvalidCredentials, (await again.LoginAsync(FakeTgkServer.Url, User, Password, code, false, Ct)).ErrorCode);
        Assert.True((await again.LoginAsync(FakeTgkServer.Url, User, "new password", code, false, Ct)).Success);
        Assert.Equal("kept", Assert.Single(again.Current.Hosts).Host);
    }

    [Fact]
    public async Task GetServerInfo_ReportsServer_OrNull()
    {
        RemoteVaultService vault = NewService();

        ServerInfo? info = await vault.GetServerInfoAsync("tgk.test", Ct);
        Assert.NotNull(info);
        Assert.True(info.RegistrationOpen);
        Assert.Null(await vault.GetServerInfoAsync("http://tgk.test", Ct));
        _server.Offline = true;
        Assert.Null(await vault.GetServerInfoAsync(FakeTgkServer.Url, Ct));
    }

    private T VaultItems_Decrypt<T>(VaultItem item) =>
        VaultCrypto.DecryptItem(_vaultKey, item.Id, item.Data!).Data.Deserialize<T>(TgkJson.Options)!;
}
