using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TGK.Core.Models;
using TGK.Core.Services;
using Xunit;

namespace TGK.Core.Tests;

public sealed class MockVaultServiceTests : IDisposable
{
    private const string Server = "https://tgk.example.com";
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private MockVaultService NewService() => new(_dir.Path, TimeSpan.Zero);

    private async Task<MockVaultService> LoggedInAsync(string user = "alice")
    {
        MockVaultService vault = NewService();
        LoginResult result = await vault.LoginAsync(Server, user, "secret", TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error);
        return vault;
    }

    [Fact]
    public async Task Login_Succeeds_AndSeedsSampleDataOnFirstLogin()
    {
        MockVaultService vault = NewService();
        int changes = 0;
        vault.Changed += () => changes++;
        Assert.Equal(SyncState.Offline, vault.Status);

        LoginResult result = await vault.LoginAsync(Server, "Alice", "secret", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.True(vault.IsLoggedIn);
        Assert.Equal("Alice", vault.CurrentUser);
        Assert.Equal(SyncState.Idle, vault.Status);
        Assert.NotNull(vault.LastSync);
        Assert.True(changes > 0);
        Assert.True(File.Exists(Path.Combine(_dir.Path, "dev-vault-alice.json")));

        VaultData data = vault.Current;
        Assert.Equal(["Production", "Staging", "Personal"], data.Groups.Select(g => g.Name));
        Assert.InRange(data.Hosts.Count, 6, 8);
        Assert.Contains(data.Hosts, h => h.TagColor is not null);
        Identity identity = Assert.Single(data.Identities);
        Assert.Equal(AuthKind.Password, identity.AuthKind);
        Assert.True(string.IsNullOrEmpty(identity.Password));
        Assert.All(data.Identities, i => Assert.Null(i.PrivateKey));
        Assert.All(data.Hosts.Where(h => h.GroupId is not null), h => Assert.NotNull(data.FindGroup(h.GroupId)));
    }

    [Theory]
    [InlineData(Server, "alice", "wrong")]
    [InlineData(Server, "", "secret")]
    [InlineData(Server, "alice", "")]
    [InlineData("", "alice", "secret")]
    [InlineData("https://offline.example.com", "alice", "secret")]
    public async Task Login_Fails_WithErrorMessage(string server, string user, string password)
    {
        MockVaultService vault = NewService();

        LoginResult result = await vault.LoginAsync(server, user, password, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.False(vault.IsLoggedIn);
        Assert.Empty(vault.Current.Hosts);
        Assert.Empty(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public async Task Mutations_RequireLogin()
    {
        MockVaultService vault = NewService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.SaveHostAsync(new HostEntry { Host = "h" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.SyncAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveAndDeleteHost_PersistAcrossInstances()
    {
        MockVaultService vault = await LoggedInAsync();
        var host = new HostEntry { Id = Guid.Empty, Name = "new", Host = "new.example.com", Port = 2022, TagColor = "#123456" };

        await vault.SaveHostAsync(host);
        Assert.NotEqual(Guid.Empty, host.Id);
        host.Name = "mutated after save"; // the vault must have stored a copy
        Assert.Equal("new", vault.Current.FindHost(host.Id)!.Name);
        Assert.Equal(SyncState.Idle, vault.Status);

        MockVaultService reopened = await LoggedInAsync();
        HostEntry stored = reopened.Current.FindHost(host.Id)!;
        Assert.Equal("new.example.com", stored.Host);
        Assert.Equal(2022, stored.Port);

        HostEntry edit = stored.Clone();
        edit.Name = "renamed";
        await reopened.SaveHostAsync(edit);
        await reopened.DeleteHostAsync(reopened.Current.Hosts[0].Id);
        int expectedCount = reopened.Current.Hosts.Count;

        MockVaultService third = await LoggedInAsync();
        Assert.Equal("renamed", third.Current.FindHost(host.Id)!.Name);
        Assert.Equal(expectedCount, third.Current.Hosts.Count);
    }

    [Fact]
    public async Task SaveHost_RejectsInvalidInput()
    {
        MockVaultService vault = await LoggedInAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => vault.SaveHostAsync(new HostEntry { Host = " " }));
        await Assert.ThrowsAsync<ArgumentException>(() => vault.SaveHostAsync(new HostEntry { Host = "h", Port = 70000 }));
    }

    [Fact]
    public async Task DeleteGroup_UngroupsItsHosts_AndPersists()
    {
        MockVaultService vault = await LoggedInAsync();
        HostGroup production = vault.Current.Groups.Single(g => g.Name == "Production");
        var hostIds = vault.Current.Hosts.Where(h => h.GroupId == production.Id).Select(h => h.Id).ToList();
        Assert.NotEmpty(hostIds);

        await vault.SaveGroupAsync(new HostGroup { Name = "Lab", SortOrder = 9 });
        await vault.DeleteGroupAsync(production.Id);

        MockVaultService reopened = await LoggedInAsync();
        Assert.DoesNotContain(reopened.Current.Groups, g => g.Id == production.Id);
        Assert.Contains(reopened.Current.Groups, g => g.Name == "Lab");
        Assert.All(hostIds, id => Assert.Null(reopened.Current.FindHost(id)!.GroupId));
    }

    [Fact]
    public async Task SaveAndDeleteIdentity_PersistAndUnlinkHosts()
    {
        MockVaultService vault = await LoggedInAsync();
        var identity = new Identity { Name = "ops key", Username = "ops", AuthKind = AuthKind.PrivateKey, PrivateKey = "KEY", Passphrase = "pp" };
        await vault.SaveIdentityAsync(identity);
        HostEntry host = vault.Current.Hosts[0].Clone();
        host.IdentityId = identity.Id;
        await vault.SaveHostAsync(host);

        MockVaultService reopened = await LoggedInAsync();
        Identity stored = reopened.Current.FindIdentity(identity.Id)!;
        Assert.Equal(AuthKind.PrivateKey, stored.AuthKind);
        Assert.Equal("KEY", stored.PrivateKey);
        Assert.Equal("pp", stored.Passphrase);

        await reopened.DeleteIdentityAsync(identity.Id);
        MockVaultService third = await LoggedInAsync();
        Assert.Null(third.Current.FindIdentity(identity.Id));
        Assert.Null(third.Current.FindHost(host.Id)!.IdentityId);
    }

    [Fact]
    public async Task AddKnownHost_ReplacesKeyForSameHostAndPort()
    {
        MockVaultService vault = await LoggedInAsync();
        await vault.AddKnownHostAsync(new KnownHost { Host = "srv", Port = 22, KeyType = "ssh-ed25519", FingerprintSha256 = "SHA256:old" });
        await vault.AddKnownHostAsync(new KnownHost { Host = "SRV", Port = 22, KeyType = "ssh-ed25519", FingerprintSha256 = "SHA256:new" });
        await vault.AddKnownHostAsync(new KnownHost { Host = "srv", Port = 2222, KeyType = "ssh-rsa", FingerprintSha256 = "SHA256:other" });

        MockVaultService reopened = await LoggedInAsync();
        Assert.Equal(2, reopened.Current.KnownHosts.Count);
        KnownHost known = reopened.Current.FindKnownHost("srv", 22)!;
        Assert.Equal("SHA256:new", known.FingerprintSha256);
        Assert.NotEqual(default, known.AddedAt);
    }

    [Fact]
    public async Task TouchHost_SetsLastConnected()
    {
        MockVaultService vault = await LoggedInAsync();
        HostEntry host = vault.Current.Hosts.First(h => h.LastConnected is null);
        DateTimeOffset before = DateTimeOffset.UtcNow;

        await vault.TouchHostAsync(host.Id);

        MockVaultService reopened = await LoggedInAsync();
        Assert.True(reopened.Current.FindHost(host.Id)!.LastConnected >= before);
    }

    [Fact]
    public async Task Mutation_PublishesNewSnapshot_AndBumpsRevision()
    {
        MockVaultService vault = await LoggedInAsync();
        VaultData before = vault.Current;
        int hostCount = before.Hosts.Count;

        await vault.DeleteHostAsync(before.Hosts[0].Id);

        Assert.Equal(hostCount, before.Hosts.Count); // old snapshot untouched
        Assert.Equal(hostCount - 1, vault.Current.Hosts.Count);
        Assert.Equal(before.Revision + 1, vault.Current.Revision);
    }

    [Fact]
    public async Task Logout_FlushesPendingChanges_AndClearsState()
    {
        var vault = new MockVaultService(_dir.Path, TimeSpan.FromMilliseconds(200));
        Assert.True((await vault.LoginAsync(Server, "alice", "secret", TestContext.Current.CancellationToken)).Success);
        var host = new HostEntry { Name = "late", Host = "late.example.com" };

        Task save = vault.SaveHostAsync(host); // still "in flight" when logging out
        Assert.Equal(SyncState.Syncing, vault.Status);
        await vault.LogoutAsync();
        await save;

        Assert.False(vault.IsLoggedIn);
        Assert.Null(vault.CurrentUser);
        Assert.Empty(vault.Current.Hosts);
        Assert.Equal(SyncState.Offline, vault.Status);

        MockVaultService reopened = await LoggedInAsync();
        Assert.NotNull(reopened.Current.FindHost(host.Id));
    }

    [Fact]
    public async Task Sync_PullsChangesMadeByAnotherClient()
    {
        MockVaultService deviceA = await LoggedInAsync();
        MockVaultService deviceB = await LoggedInAsync();
        var host = new HostEntry { Name = "from A", Host = "a.example.com" };

        await deviceA.SaveHostAsync(host);
        Assert.Null(deviceB.Current.FindHost(host.Id));
        await deviceB.SyncAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(deviceB.Current.FindHost(host.Id));
        Assert.Equal(SyncState.Idle, deviceB.Status);
    }

    [Fact]
    public async Task UsersHaveSeparateVaultFiles()
    {
        MockVaultService alice = await LoggedInAsync("alice");
        await alice.SaveHostAsync(new HostEntry { Name = "only alice", Host = "a" });
        MockVaultService bob = await LoggedInAsync("bob");

        Assert.DoesNotContain(bob.Current.Hosts, h => h.Name == "only alice");
        Assert.True(File.Exists(alice.GetVaultFilePath("alice")));
        Assert.True(File.Exists(alice.GetVaultFilePath("bob")));
    }
}
