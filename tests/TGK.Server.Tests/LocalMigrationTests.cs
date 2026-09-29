using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TGK.Core.Models;
using TGK.Core.Services;
using TGK.Protocol.Dtos;
using Xunit;

namespace TGK.Server.Tests;

/// <summary>
/// The local vault and the real server (in-process): a local vault becomes a new account as it is, merges into an
/// existing account, and an account is copied to a local vault.
/// </summary>
public sealed class LocalMigrationTests : IAsyncLifetime
{
    private const string ServerUrl = "http://localhost";
    private const string Password = "correct horse battery";
    private const string Master = "local master 42";

    private static readonly KdfParams TestKdf = new(KdfParams.Pbkdf2Sha256, FastCrypto.KdfIterations);

    private readonly ServerFixture _server = new();
    private readonly string _clientDir = Path.Combine(Path.GetTempPath(), "tgk-migration-clients", Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (IDisposable disposable in _disposables)
            disposable.Dispose();
        await _server.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_clientDir, recursive: true); } catch (IOException) { }
    }

    private RemoteVaultService NewRemote(string name)
    {
        var remote = new RemoteVaultService(new RemoteVaultOptions
        {
            BaseDirectory = Path.Combine(_clientDir, name),
            Credentials = new MemoryCredentialStore(),
            HttpHandler = _server.Server.CreateHandler(),
            DeviceName = name,
            NewKdf = TestKdf,
            PushDelay = TimeSpan.FromMilliseconds(10),
            PullInterval = TimeSpan.FromHours(1),
        });
        _disposables.Add(remote);
        return remote;
    }

    private (RoutingVaultService Vault, VaultMigration Migration) NewClient(string name)
    {
        var local = new LocalVaultService(new LocalVaultOptions { BaseDirectory = Path.Combine(_clientDir, name), Credentials = new MemoryCredentialStore(), NewKdf = TestKdf });
        _disposables.Add(local);
        var vault = new RoutingVaultService(NewRemote(name), new MockVaultService(Path.Combine(_clientDir, name), TimeSpan.Zero), local);
        return (vault, new VaultMigration(vault));
    }

    private static void AssertOk(LoginResult result) => Assert.True(result.Success, $"{result.ErrorCode}: {result.Error}");

    /// <summary>Registers <paramref name="username"/> from another device; returns it (signed in) and the TOTP secret.</summary>
    private async Task<(RemoteVaultService Device, string Secret)> RegisterAsync(string username)
    {
        RemoteVaultService device = NewRemote("other-" + username);
        string secret = device.BeginTotpEnrollment(username).Secret;
        AssertOk(await device.RegisterAsync(ServerUrl, username, Password, null, secret, _server.NextCode(secret), false));
        return (device, secret);
    }

    private Dictionary<string, byte[]> ServerItems()
    {
        using var db = new SqliteConnection($"Data Source={_server.DbPath};Mode=ReadOnly;Pooling=False");
        db.Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT id, data FROM items WHERE data IS NOT NULL";
        using SqliteDataReader reader = command.ExecuteReader();
        var items = new Dictionary<string, byte[]>();
        while (reader.Read())
            items[reader.GetString(0)] = (byte[])reader.GetValue(1);
        return items;
    }

    private static Dictionary<string, byte[]> LocalItems(string path)
    {
        using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        db.Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT id, data FROM items";
        using SqliteDataReader reader = command.ExecuteReader();
        var items = new Dictionary<string, byte[]>();
        while (reader.Read())
            items[reader.GetString(0)] = (byte[])reader.GetValue(1);
        return items;
    }

    [Fact]
    public async Task LocalToNewAccount_UploadsTheItemsAsTheyAre_AndAnotherDeviceSeesEverything()
    {
        (RoutingVaultService vault, VaultMigration migration) = NewClient("laptop");
        AssertOk(await vault.CreateLocalAsync(Master, keepUnlocked: true));
        var group = new HostGroup { Name = "Production" };
        var identity = new Identity { Name = "Deploy key", Username = "deploy", AuthKind = AuthKind.PrivateKey, PrivateKey = "PRIVATE-KEY-MATERIAL" };
        var host = new HostEntry { Name = "db-01", Host = "db.internal.example", Port = 2222, GroupId = group.Id, IdentityId = identity.Id };
        await vault.SaveGroupAsync(group);
        await vault.SaveIdentityAsync(identity);
        await vault.SaveHostAsync(host);
        await vault.AddKnownHostAsync(new KnownHost { Host = "db.internal.example", Port = 2222, KeyType = "ssh-ed25519", FingerprintSha256 = "SHA256:abc" });
        await vault.SaveDefaultsAsync(new HostOptions { KeepAliveSeconds = 30 });
        await vault.TouchHostAsync(host.Id);
        Dictionary<string, byte[]> local = LocalItems(vault.Local.FilePath);
        Assert.Equal(5, local.Count);

        string secret = vault.BeginTotpEnrollment("carol").Secret;
        MigrationResult result = await migration.LocalToNewAccountAsync(ServerUrl, "carol", Password, null, secret, _server.NextCode(secret),
            keepSignedIn: false, removeLocalVault: true);

        AssertOk(result.Login);
        Assert.True(result.Uploaded);
        Assert.True(result.LocalVaultRemoved);
        Assert.Equal(new MergeCounts(5, 0, 0), result.Counts);
        Assert.Equal(VaultMode.Server, vault.Mode);
        Assert.Equal("carol", vault.CurrentUser);
        Assert.False(vault.Local.IsLoggedIn);
        Assert.False(vault.Local.Exists);
        Assert.Equal(host.Id, Assert.Single(vault.Current.Hosts).Id);
        Assert.NotNull(vault.Current.Hosts[0].LastConnected); // this device's "recent" hosts came along

        // The server holds exactly the local blobs, under the same ids.
        Dictionary<string, byte[]> server = ServerItems();
        Assert.Equal(local.Keys.Order(), server.Keys.Order());
        foreach ((string id, byte[] data) in local)
            Assert.Equal(data, server[id]);

        // A second device signs in with the account password and decrypts everything.
        RemoteVaultService phone = NewRemote("phone");
        AssertOk(await phone.LoginAsync(ServerUrl, "carol", Password, _server.NextCode(secret), false));
        VaultData synced = phone.Current;
        Assert.Equal(group.Id, Assert.Single(synced.Groups).Id);
        Assert.Equal("PRIVATE-KEY-MATERIAL", Assert.Single(synced.Identities).PrivateKey);
        Assert.Equal((host.Id, "db.internal.example", 2222, group.Id, identity.Id),
            (synced.Hosts[0].Id, synced.Hosts[0].Host, synced.Hosts[0].Port, synced.Hosts[0].GroupId, synced.Hosts[0].IdentityId));
        Assert.Equal("SHA256:abc", synced.FindKnownHost("db.internal.example", 2222)!.FingerprintSha256);
        Assert.Equal(30, synced.Defaults.KeepAliveSeconds);
    }

    [Fact]
    public async Task LocalToExistingAccount_MergesWithTheAccountWinningDefaultsAndHostKeys()
    {
        (RemoteVaultService account, string secret) = await RegisterAsync("dave");
        var accountHost = new HostEntry { Name = "account-host", Host = "account.example" };
        await account.SaveHostAsync(accountHost);
        await account.SaveDefaultsAsync(new HostOptions { KeepAliveSeconds = 60 });
        await account.AddKnownHostAsync(new KnownHost { Host = "shared.example", Port = 22, KeyType = "ssh-ed25519", FingerprintSha256 = "SHA256:account" });
        await account.SyncAsync();

        (RoutingVaultService vault, VaultMigration migration) = NewClient("laptop");
        AssertOk(await vault.CreateLocalAsync(Master, keepUnlocked: false));
        var group = new HostGroup { Name = "Local group" };
        var identity = new Identity { Name = "Local key", Password = "pw" };
        var localHost = new HostEntry { Name = "local-host", Host = "local.example", GroupId = group.Id };
        await vault.SaveGroupAsync(group);
        await vault.SaveIdentityAsync(identity);
        await vault.SaveHostAsync(localHost);
        await vault.SaveDefaultsAsync(new HostOptions { KeepAliveSeconds = 5 });
        await vault.AddKnownHostAsync(new KnownHost { Host = "shared.example", Port = 22, KeyType = "ssh-ed25519", FingerprintSha256 = "SHA256:local" });
        await vault.AddKnownHostAsync(new KnownHost { Host = "local-only.example", Port = 22, KeyType = "ssh-ed25519", FingerprintSha256 = "SHA256:only" });

        MigrationResult wrongCode = await migration.LocalToExistingAccountAsync(ServerUrl, "dave", Password, _server.WrongCode(secret), false, true);
        Assert.Equal(VaultError.TotpInvalid, wrongCode.Login.ErrorCode);
        Assert.Equal(VaultMode.Local, vault.Mode);
        Assert.True(vault.Local.IsLoggedIn);

        MigrationResult result = await migration.LocalToExistingAccountAsync(ServerUrl, "dave", Password, _server.NextCode(secret), false, removeLocalVault: false);
        AssertOk(result.Login);
        Assert.True(result.Uploaded);
        Assert.False(result.LocalVaultRemoved);
        Assert.Equal(new MergeCounts(4, 0, 2), result.Counts); // + group, identity, host, local-only key; account's defaults and shared key win
        Assert.Equal(VaultMode.Server, vault.Mode);
        Assert.True(vault.Local.Exists);
        Assert.False(vault.Local.IsLoggedIn);

        await account.SyncAsync();
        VaultData merged = account.Current;
        Assert.Equal(new[] { "account-host", "local-host" }, merged.Hosts.Select(h => h.Name).Order());
        Assert.Equal(group.Id, Assert.Single(merged.Groups).Id);
        Assert.Equal(identity.Id, Assert.Single(merged.Identities).Id);
        Assert.Equal(60, merged.Defaults.KeepAliveSeconds);
        Assert.Equal("SHA256:account", merged.FindKnownHost("shared.example", 22)!.FingerprintSha256);
        Assert.Equal("SHA256:only", merged.FindKnownHost("local-only.example", 22)!.FingerprintSha256);

        // The local vault was left as it was.
        AssertOk(await vault.UnlockLocalAsync(Master, false));
        Assert.Equal(5, vault.Current.Defaults.KeepAliveSeconds);
        Assert.Equal("SHA256:local", vault.Current.FindKnownHost("shared.example", 22)!.FingerprintSha256);
    }

    [Fact]
    public async Task LocalToExistingAccount_KeepsTheAccountsVersionOfAConflictingEntry_AndTheLocalVault()
    {
        (RemoteVaultService account, string secret) = await RegisterAsync("gina");
        var host = new HostEntry { Name = "web", Host = "web.example", Username = "new-user" };
        await account.SaveHostAsync(host);
        await account.SyncAsync();

        // The local vault has an older copy of the account's host (e.g. an offline copy edited elsewhere since).
        (RoutingVaultService vault, VaultMigration migration) = NewClient("laptop");
        AssertOk(await vault.CreateLocalAsync(Master, keepUnlocked: false));
        HostEntry stale = host.Clone();
        stale.Username = "old-user";
        var mine = new HostEntry { Name = "mine", Host = "mine.example" };
        await vault.SaveHostAsync(stale);
        await vault.SaveHostAsync(mine);
        await vault.TouchHostAsync(stale.Id);
        await vault.TouchHostAsync(mine.Id);

        MigrationResult result = await migration.LocalToExistingAccountAsync(ServerUrl, "gina", Password, _server.NextCode(secret), false, removeLocalVault: true);
        AssertOk(result.Login);
        Assert.Equal(new MergeCounts(1, 1, 0), result.Counts);
        Assert.True(result.Uploaded);
        Assert.False(result.LocalVaultRemoved); // its version of "web" exists only there
        Assert.True(vault.Local.Exists);
        Assert.All(vault.Current.Hosts, h => Assert.NotNull(h.LastConnected)); // this device's times came along

        await account.SyncAsync();
        Assert.Equal("new-user", account.Current.FindHost(host.Id)!.Username);
        Assert.Equal(2, account.Current.Hosts.Count);
    }

    [Fact]
    public async Task LocalToExistingAccount_UsesTheLocalDefaults_WhenTheAccountHasNone()
    {
        (RemoteVaultService account, string secret) = await RegisterAsync("erin");
        (RoutingVaultService vault, VaultMigration migration) = NewClient("laptop");
        AssertOk(await vault.CreateLocalAsync(Master, keepUnlocked: false));
        await vault.SaveDefaultsAsync(new HostOptions { KeepAliveSeconds = 7 });

        MigrationResult result = await migration.LocalToExistingAccountAsync(ServerUrl, "erin", Password, _server.NextCode(secret), false, removeLocalVault: true);
        AssertOk(result.Login);
        Assert.Equal(new MergeCounts(1, 0, 0), result.Counts);
        Assert.True(result.LocalVaultRemoved);

        await account.SyncAsync();
        Assert.Equal(7, account.Current.Defaults.KeepAliveSeconds);
    }

    [Fact]
    public async Task ServerToLocal_SavesAnOfflineCopyUnderANewKey_AndLeavesTheAccountAsItIs()
    {
        (RemoteVaultService other, string secret) = await RegisterAsync("frank");
        var host = new HostEntry { Name = "web", Host = "web.example" };
        await other.SaveHostAsync(host);
        await other.AddKnownHostAsync(new KnownHost { Host = "web.example", Port = 22, KeyType = "ssh-ed25519", FingerprintSha256 = "SHA256:web" });
        await other.SaveDefaultsAsync(new HostOptions { KeepAliveSeconds = 20 });
        await other.SyncAsync();

        (RoutingVaultService vault, VaultMigration migration) = NewClient("laptop");
        AssertOk(await vault.LoginAsync(ServerUrl, "frank", Password, _server.NextCode(secret), false));
        Assert.Equal(VaultError.ValidationFailed, (await migration.SaveOfflineCopyAsync("short", replace: false)).ErrorCode);
        AssertOk(await migration.SaveOfflineCopyAsync(Master, replace: false));
        Assert.Equal(VaultMode.Server, vault.Mode);
        Assert.True(vault.IsLoggedIn);
        Assert.True(vault.Local.Exists);
        Assert.False(vault.Local.IsLoggedIn);

        // Replacing needs the explicit flag.
        Assert.Equal(VaultError.ValidationFailed, (await migration.SaveOfflineCopyAsync("another master", replace: false)).ErrorCode);
        AssertOk(await migration.SaveOfflineCopyAsync("another master", replace: true));

        // A new vault key: none of the local items is the server's blob (and the known host id differs).
        Dictionary<string, byte[]> server = ServerItems();
        Dictionary<string, byte[]> local = LocalItems(vault.Local.FilePath);
        Assert.Equal(3, local.Count);
        Assert.Contains(host.Id.ToString("D"), local.Keys);
        Assert.Equal(2, local.Keys.Count(id => !server.ContainsKey(id))); // known host and settings ids are keyed by the vault key
        Assert.DoesNotContain(local, item => server.TryGetValue(item.Key, out byte[]? data) && data.AsSpan().SequenceEqual(item.Value));

        // A failed unlock changes nothing; switching to the copy signs out of the account (which keeps its data).
        Assert.Equal(VaultError.InvalidCredentials, (await vault.UnlockLocalAsync(Master, false)).ErrorCode);
        Assert.Equal(VaultMode.Server, vault.Mode);
        Assert.True(vault.IsLoggedIn);
        AssertOk(await vault.UnlockLocalAsync("another master", false));
        Assert.Equal("other-frank", Assert.Single(await other.ListSessionsAsync()).DeviceName);
        Assert.Equal(VaultMode.Local, vault.Mode);
        Assert.Equal(host.Id, Assert.Single(vault.Current.Hosts).Id);
        Assert.Equal("SHA256:web", Assert.Single(vault.Current.KnownHosts).FingerprintSha256);
        Assert.Equal(20, vault.Current.Defaults.KeepAliveSeconds);
        await other.SyncAsync();
        Assert.Single(other.Current.Hosts);
    }

    private sealed class MemoryCredentialStore : IDeviceCredentialStore
    {
        private DeviceCredentials? _stored;

        public DeviceCredentials? Load() => _stored;

        public void Save(DeviceCredentials credentials) => _stored = credentials;

        public void Clear() => _stored = null;
    }
}
