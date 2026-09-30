using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TGK.Core.Models;
using TGK.Core.Services;
using TGK.Protocol;
using Xunit;

namespace TGK.Core.Tests;

public sealed class LocalVaultServiceTests : IDisposable
{
    private const string Master = "correct horse 42";
    private const string HostName = "local-db-01.internal.example";
    private const string KeyMaterial = "LOCAL-PRIVATE-KEY-MATERIAL-9c1e";

    private readonly TempDirectory _dir = new();
    private readonly MemoryCredentialStore _store = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private LocalVaultService NewService() => new(new LocalVaultOptions
    {
        BaseDirectory = _dir.Path,
        Credentials = _store,
        NewKdf = FakeTgkServer.TestKdf,
        WrongPasswordDelay = TimeSpan.Zero,
    });

    private static void AssertOk(LoginResult result) => Assert.True(result.Success, $"{result.ErrorCode}: {result.Error}");

    /// <summary>A group, an identity with a private key, a host in both, a known host and defaults.</summary>
    private static async Task<(HostGroup Group, Identity Identity, HostEntry Host)> FillAsync(IVaultService vault)
    {
        var group = new HostGroup { Name = "Production" };
        var identity = new Identity { Name = "Deploy key", Username = "deploy", AuthKind = AuthKind.PrivateKey, PrivateKey = KeyMaterial };
        var host = new HostEntry { Name = "db-01", Host = HostName, Port = 2222, GroupId = group.Id, IdentityId = identity.Id };
        await vault.SaveGroupAsync(group);
        await vault.SaveIdentityAsync(identity);
        await vault.SaveHostAsync(host);
        await vault.AddKnownHostAsync(new KnownHost { Host = HostName, Port = 2222, KeyType = "ssh-ed25519", FingerprintSha256 = "SHA256:local" });
        await vault.SaveDefaultsAsync(new HostOptions { KeepAliveSeconds = 42 });
        await vault.SaveWorkspaceAsync(new Workspace { RestoreTabs = true, Tabs = [new WorkspaceTab { HostId = host.Id }], ActiveTab = 0 });
        return (group, identity, host);
    }

    [Fact]
    public async Task CreateLockUnlock_WrongAndShortPasswords()
    {
        LocalVaultService vault = NewService();
        Assert.False(vault.Exists);
        Assert.Equal(VaultError.NotFound, (await vault.UnlockAsync(Master, false, Ct)).ErrorCode);
        Assert.Equal(VaultError.ValidationFailed, (await vault.CreateAsync("too short", false, Ct)).ErrorCode);
        Assert.False(vault.Exists);

        AssertOk(await vault.CreateAsync(Master, false, Ct));
        Assert.True(vault.Exists);
        Assert.True(vault.IsLoggedIn);
        Assert.Equal(VaultMode.Local, vault.Mode);
        Assert.Equal(SyncState.Idle, vault.Status);
        Assert.Empty(vault.Current.Hosts);
        await FillAsync(vault);

        await vault.LogoutAsync();
        Assert.False(vault.IsLoggedIn);
        Assert.Empty(vault.Current.Hosts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.SaveGroupAsync(new HostGroup { Name = "x" }));

        LoginResult wrong = await vault.UnlockAsync("not the password", false, Ct);
        Assert.Equal(VaultError.InvalidCredentials, wrong.ErrorCode);
        Assert.False(vault.IsLoggedIn);
        Assert.Equal(VaultError.ValidationFailed, (await vault.CreateAsync(Master + "!", false, Ct)).ErrorCode); // one exists already

        AssertOk(await vault.UnlockAsync(Master, false, Ct));
        Assert.Equal(HostName, Assert.Single(vault.Current.Hosts).Host);
        Assert.Equal(KeyMaterial, Assert.Single(vault.Current.Identities).PrivateKey);
        Assert.Equal(42, vault.Current.Defaults.KeepAliveSeconds);
        Assert.Equal("SHA256:local", Assert.Single(vault.Current.KnownHosts).FingerprintSha256);
    }

    [Fact]
    public async Task ChangeMasterPassword_RewrapsOnly()
    {
        LocalVaultService vault = NewService();
        AssertOk(await vault.CreateAsync(Master, false, Ct));
        await FillAsync(vault);

        var wrong = await Assert.ThrowsAsync<VaultException>(() => vault.ChangeMasterPasswordAsync("wrong password", "new master pass", Ct));
        Assert.Equal(VaultError.InvalidCredentials, wrong.Code);
        var tooShort = await Assert.ThrowsAsync<VaultException>(() => vault.ChangeMasterPasswordAsync(Master, "short", Ct));
        Assert.Equal(VaultError.ValidationFailed, tooShort.Code);

        await vault.ChangeMasterPasswordAsync(Master, "new master pass", Ct);
        Assert.True(vault.IsLoggedIn);
        await vault.LogoutAsync();

        LocalVaultService other = NewService();
        Assert.Equal(VaultError.InvalidCredentials, (await other.UnlockAsync(Master, false, Ct)).ErrorCode);
        AssertOk(await other.UnlockAsync("new master pass", false, Ct));
        Assert.Equal(KeyMaterial, Assert.Single(other.Current.Identities).PrivateKey);
    }

    [Fact]
    public async Task Changes_PersistAcrossInstances()
    {
        LocalVaultService first = NewService();
        AssertOk(await first.CreateAsync(Master, false, Ct));
        (HostGroup group, _, HostEntry host) = await FillAsync(first);
        var second = new HostEntry { Name = "web", Host = "web.example" };
        await first.SaveHostAsync(second);
        await first.TouchHostAsync(host.Id);
        await first.DeleteGroupAsync(group.Id);
        HostEntry renamed = first.Current.FindHost(second.Id)!.Clone();
        renamed.Name = "web-renamed";
        await first.SaveHostAsync(renamed);
        DateTimeOffset? touched = first.Current.FindHost(host.Id)!.LastConnected;
        Assert.NotNull(touched);
        first.Dispose();

        LocalVaultService reopened = NewService();
        AssertOk(await reopened.UnlockAsync(Master, false, Ct));
        VaultData data = reopened.Current;
        Assert.Empty(data.Groups);
        Assert.Equal(new[] { "db-01", "web-renamed" }, data.Hosts.Select(h => h.Name).Order());
        Assert.Equal(touched, data.FindHost(host.Id)!.LastConnected);
        Assert.Equal(42, data.Defaults.KeepAliveSeconds);
        Assert.Single(data.KnownHosts);
        Assert.True(data.Workspace.RestoreTabs);
        Assert.Equal(host.Id, Assert.Single(data.Workspace.Tabs).HostId);
    }

    [Fact]
    public async Task KeepUnlocked_RestoresAtStartup_LockWipesIt()
    {
        LocalVaultService vault = NewService();
        AssertOk(await vault.CreateAsync(Master, keepUnlocked: true, Ct));
        await FillAsync(vault);
        Assert.NotNull(_store.Stored);

        LocalVaultService restarted = NewService();
        Assert.True(await restarted.TryRestoreSessionAsync(Ct));
        Assert.True(restarted.IsLoggedIn);
        Assert.Equal(HostName, Assert.Single(restarted.Current.Hosts).Host);

        await restarted.LogoutAsync(); // lock
        Assert.Null(_store.Stored);
        Assert.False(await NewService().TryRestoreSessionAsync(Ct));

        // A key kept for another (e.g. replaced) vault is ignored and cleared.
        AssertOk(await vault.UnlockAsync(Master, keepUnlocked: true, Ct));
        DeviceCredentials kept = _store.Stored!;
        await vault.LogoutAsync();
        _store.Stored = kept with { UserId = Guid.NewGuid().ToString("D") };
        Assert.False(await NewService().TryRestoreSessionAsync(Ct));
        Assert.Null(_store.Stored);
    }

    [Fact]
    public async Task DatabaseFile_HoldsOnlyCiphertext_InTheServerItemFormat()
    {
        LocalVaultService vault = NewService();
        AssertOk(await vault.CreateAsync(Master, keepUnlocked: true, Ct));
        (HostGroup group, Identity identity, HostEntry host) = await FillAsync(vault);
        byte[] vaultKey = _store.Stored!.VaultKey;
        string path = Path.Combine(_dir.Path, LocalVaultService.FileName);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

        // Every row is a server item: the server-path codec opens it under the same id.
        var codec = new VaultItems(vaultKey);
        var decoded = new VaultData();
        await using (var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"))
        {
            await db.OpenAsync(Ct);
            await using SqliteCommand command = db.CreateCommand();
            command.CommandText = "SELECT id, data FROM items";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
                codec.Apply(decoded, reader.GetString(0), (byte[])reader.GetValue(1));
        }
        Assert.Equal(group.Id, Assert.Single(decoded.Groups).Id);
        Assert.Equal(KeyMaterial, Assert.Single(decoded.Identities).PrivateKey);
        Assert.Equal((host.Id, HostName, identity.Id), (decoded.Hosts[0].Id, decoded.Hosts[0].Host, decoded.Hosts[0].IdentityId));
        Assert.Equal(42, decoded.Defaults.KeepAliveSeconds);
        Assert.Single(decoded.KnownHosts);

        await vault.LogoutAsync();
        byte[] stored = [.. ReadShared(path), .. ReadShared(path + "-wal")];
        foreach (string plaintext in new[] { HostName, KeyMaterial, "Deploy key", "Production", "SHA256:local", Master })
        {
            Assert.False(stored.AsSpan().IndexOf(Encoding.UTF8.GetBytes(plaintext)) >= 0, $"'{plaintext}' is stored in plaintext");
            Assert.False(stored.AsSpan().IndexOf(Encoding.Unicode.GetBytes(plaintext)) >= 0, $"'{plaintext}' is stored as UTF-16");
        }
    }

    [Fact]
    public async Task ServerOnlyMembers_AreHarmlessOrUnsupported()
    {
        LocalVaultService vault = NewService();
        AssertOk(await vault.CreateAsync(Master, false, Ct));
        Assert.Null(await vault.GetServerInfoAsync("https://tgk.test", Ct));
        Assert.Empty(await vault.ListSessionsAsync(Ct));
        Assert.Equal(0, await vault.RevokeOtherSessionsAsync(Ct));
        await vault.SyncAsync(Ct);
        await Assert.ThrowsAsync<NotSupportedException>(() => vault.ChangePasswordAsync(Master, "123456", "new master pass", false, Ct));
        Assert.Throws<NotSupportedException>(() => vault.BeginTotpEnrollment("alice"));
        Assert.Throws<InvalidOperationException>(vault.DeleteVault); // unlocked

        await vault.LogoutAsync();
        vault.DeleteVault();
        Assert.False(vault.Exists);
        Assert.False(File.Exists(vault.FilePath));
    }

    [Fact]
    public async Task Routing_SwitchesBetweenServerAndLocal_AndRestoresTheKeptOne()
    {
        var server = new FakeTgkServer();
        string secret = Totp.GenerateSecret();
        server.AddUser("alice", "correct horse", secret);
        RoutingVaultService NewRouting() => new(
            new RemoteVaultService(new RemoteVaultOptions { BaseDirectory = _dir.Path, Credentials = new MemoryCredentialStore(), HttpHandler = server, PullInterval = TimeSpan.FromHours(1) }),
            new MockVaultService(_dir.Path, TimeSpan.Zero),
            NewService());

        RoutingVaultService routing = NewRouting();
        int changes = 0;
        routing.Changed += () => Interlocked.Increment(ref changes);
        AssertOk(await routing.CreateLocalAsync(Master, keepUnlocked: true, Ct));
        Assert.Equal(VaultMode.Local, routing.Mode);
        await routing.SaveHostAsync(new HostEntry { Host = HostName });
        Assert.True(changes > 0);

        // A new start restores the kept local unlock.
        RoutingVaultService restarted = NewRouting();
        Assert.True(await restarted.TryRestoreSessionAsync(Ct));
        Assert.Equal(VaultMode.Local, restarted.Mode);
        Assert.Equal(HostName, Assert.Single(restarted.Current.Hosts).Host);

        // Signing in to a server locks the local vault (and forgets the kept key), without restarting.
        AssertOk(await restarted.LoginAsync(FakeTgkServer.Url, "alice", "correct horse", FakeTgkServer.CurrentCode(secret), false, Ct));
        Assert.Equal(VaultMode.Server, restarted.Mode);
        Assert.False(restarted.Local.IsLoggedIn);
        Assert.Empty(restarted.Current.Hosts);
        Assert.Null(_store.Stored);

        // And back.
        AssertOk(await restarted.UnlockLocalAsync(Master, false, Ct));
        Assert.Equal(VaultMode.Local, restarted.Mode);
        Assert.Equal(HostName, Assert.Single(restarted.Current.Hosts).Host);
        Assert.Equal(0, server.SessionCount("alice"));
    }

    private static byte[] ReadShared(string path)
    {
        if (!File.Exists(path))
            return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
