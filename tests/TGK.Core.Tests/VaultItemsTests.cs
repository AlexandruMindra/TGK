using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using TGK.Core.Models;
using TGK.Core.Services;
using TGK.Protocol;
using Xunit;

namespace TGK.Core.Tests;

public sealed class VaultItemsTests
{
    private readonly byte[] _key = VaultCrypto.NewVaultKey();
    private readonly VaultItems _items;

    public VaultItemsTests() => _items = new VaultItems(_key);

    [Fact]
    public void EveryKind_RoundTripsThroughEncryption()
    {
        var group = new HostGroup { Name = "Prod", SortOrder = 2, Collapsed = true };
        var identity = new Identity { Name = "deploy", Username = "ci", AuthKind = AuthKind.PrivateKey, PrivateKey = "-----BEGIN-----", Passphrase = "pp" };
        var host = new HostEntry { Name = "web", Host = "web.example.com", Port = 2222, GroupId = group.Id, IdentityId = identity.Id, TagColor = "#ff0000" };
        var known = new KnownHost { Host = "web.example.com", Port = 2222, KeyType = "ssh-ed25519", FingerprintSha256 = "SHA256:abc", AddedAt = DateTimeOffset.UnixEpoch };

        var vault = new VaultData();
        foreach (object entity in new object[] { group, identity, host, known })
        {
            string id = _items.IdOf(entity);
            _items.Apply(vault, id, _items.Encrypt(id, entity));
        }

        Assert.Equivalent(group, Assert.Single(vault.Groups));
        Assert.Equivalent(identity, Assert.Single(vault.Identities));
        Assert.Equivalent(host, Assert.Single(vault.Hosts));
        Assert.Equivalent(known, Assert.Single(vault.KnownHosts));
        Assert.Equal(AuthKind.PrivateKey, vault.Identities[0].AuthKind);
    }

    [Fact]
    public void Apply_RejectsWrongKeyOrMismatchedId_AndNullDeletes()
    {
        var host = new HostEntry { Host = "a" };
        string id = _items.IdOf(host);
        byte[] data = _items.Encrypt(id, host);
        var vault = new VaultData();

        Assert.ThrowsAny<CryptographicException>(() => new VaultItems(VaultCrypto.NewVaultKey()).Apply(vault, id, data));
        Assert.ThrowsAny<CryptographicException>(() => _items.Apply(vault, Guid.NewGuid().ToString(), data));
        _items.Apply(vault, id, data);
        Assert.Single(vault.Hosts);
        _items.Apply(vault, id, null);
        Assert.Empty(vault.Hosts);
    }

    [Fact]
    public void KnownHostId_IsStableCaseInsensitiveAndKeyedByTheVault()
    {
        Assert.Equal(_items.KnownHostId("Example.COM", 22), _items.KnownHostId("example.com", 22));
        Assert.NotEqual(_items.KnownHostId("example.com", 22), _items.KnownHostId("example.com", 2222));
        Assert.True(Guid.TryParseExact(_items.KnownHostId("example.com", 22), "D", out _));
        // Without the vault key the id cannot be computed from a guessed host name.
        Assert.NotEqual(_items.KnownHostId("example.com", 22), new VaultItems(VaultCrypto.NewVaultKey()).KnownHostId("example.com", 22));
    }

    [Fact]
    public void Hosts_SyncWithoutLastConnected()
    {
        var host = new HostEntry { Host = "a", LastConnected = DateTimeOffset.UnixEpoch };
        string id = _items.IdOf(host);
        var vault = new VaultData();

        Assert.Null(((HostEntry)_items.Apply(vault, id, _items.Encrypt(id, host))!).LastConnected);
        Assert.Equal(DateTimeOffset.UnixEpoch, host.LastConnected); // the caller's entity is untouched
    }

    [Fact]
    public void Diff_FindsReplacedAddedAndRemovedEntities()
    {
        var group = new HostGroup { Name = "g" };
        var inGroup = new HostEntry { Host = "a", GroupId = group.Id };
        var other = new HostEntry { Host = "b" };
        var before = new VaultData { Groups = [group], Hosts = [inGroup, other] };
        VaultData after = before.ShallowCopy();

        VaultEdits.DeleteGroup(group.Id)(after);
        var added = new KnownHost { Host = "k", FingerprintSha256 = "SHA256:x" };
        VaultEdits.AddKnownHost(added)(after);

        var changes = _items.Diff(before, after);
        Assert.Equal(2, changes.Count); // the group's hosts are not rewritten
        Assert.Contains(new ItemChange(_items.IdOf(group), null), changes);
        Assert.Contains(changes, c => c.Id == _items.IdOf(added));
        Assert.Same(inGroup, after.Hosts.Single(h => h.Host == "a"));
        Assert.Same(other, after.Hosts.Single(h => h.Host == "b"));
    }

    [Fact]
    public void Settings_RoundTripAsOneItemWithAVaultKeyedId()
    {
        var defaults = new HostOptions { KeepAliveSeconds = 0, TerminalType = "vt220", Environment = [new EnvVar { Name = "A", Value = "b" }] };
        Assert.Equal(_items.SettingsId, _items.IdOf(defaults));
        Assert.Equal(_items.SettingsId, new VaultItems(_key).SettingsId); // stable for the vault
        Assert.NotEqual(_items.SettingsId, new VaultItems(VaultCrypto.NewVaultKey()).SettingsId);

        byte[] data = _items.Encrypt(_items.SettingsId, defaults);
        Assert.Equal(ItemKinds.Settings, VaultCrypto.DecryptItem(_key, _items.SettingsId, data).Kind);
        var vault = new VaultData();
        object? applied = _items.Apply(vault, _items.SettingsId, data);
        Assert.Same(vault.Defaults, applied);
        Assert.Equivalent(defaults, vault.Defaults);

        // Bound to its id, like every item; a settings payload under another id is rejected.
        Assert.ThrowsAny<CryptographicException>(() => _items.Apply(vault, Guid.NewGuid().ToString(), data));
        string otherId = Guid.NewGuid().ToString();
        Assert.Throws<JsonException>(() => _items.Apply(vault, otherId, _items.Encrypt(otherId, defaults)));

        _items.Apply(vault, _items.SettingsId, null); // deleted on the server: back to the built-in defaults
        Assert.True(vault.Defaults.IsEmpty);
    }

    [Fact]
    public void Diff_FindsChangedDefaults()
    {
        var before = new VaultData();
        VaultData after = before.ShallowCopy();
        Assert.Empty(_items.Diff(before, after));

        VaultEdits.SaveDefaults(new HostOptions { AutoReconnect = true })(after);

        ItemChange change = Assert.Single(_items.Diff(before, after));
        Assert.Equal(_items.SettingsId, change.Id);
        Assert.Same(after.Defaults, change.Entity);
    }

    [Fact]
    public void UnknownKind_IsReportedAsJsonException_WhichCallersSkip()
    {
        // What an older client sees for a kind it predates (as this one would for a future kind).
        string id = Guid.NewGuid().ToString();
        byte[] data = VaultCrypto.EncryptItem(_key, id, "futureKind", new { x = 1 }, TgkJson.Options);

        Assert.Throws<JsonException>(() => _items.Apply(new VaultData(), id, data));
    }
}
