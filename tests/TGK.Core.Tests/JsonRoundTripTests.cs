using System;
using System.IO;
using System.Text.Json;
using TGK.Core.Models;
using TGK.Core.Services;
using Xunit;

namespace TGK.Core.Tests;

public class JsonRoundTripTests
{
    [Fact]
    public void VaultData_RoundTripsAllFields()
    {
        var group = new HostGroup { Name = "Prod", SortOrder = 3, Collapsed = true };
        var identity = new Identity
        {
            Name = "Key", Username = "ops", AuthKind = AuthKind.PrivateKey, PrivateKey = "-----BEGIN-----",
            Passphrase = "pp", KeyType = "ssh-ed25519", Fingerprint = "SHA256:abc",
        };
        var host = new HostEntry
        {
            Name = "web", Host = "web.example.com", Port = 2200, Username = "root", IdentityId = identity.Id,
            GroupId = group.Id, TagColor = "#FF0000", Notes = "n", Favorite = true,
            LastConnected = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        };
        var known = new KnownHost { Host = "web.example.com", Port = 2200, KeyType = "ssh-ed25519", FingerprintSha256 = "SHA256:xyz", AddedAt = DateTimeOffset.UnixEpoch };
        var vault = new VaultData
        {
            Groups = [group], Hosts = [host], Identities = [identity], KnownHosts = [known],
            Revision = 42, UpdatedAt = new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.FromHours(2)),
        };

        string json = JsonSerializer.Serialize(vault, TgkJson.Options);
        VaultData copy = JsonSerializer.Deserialize<VaultData>(json, TgkJson.Options)!;

        Assert.Contains("\"knownHosts\"", json);
        Assert.DoesNotContain("displayName", json);
        Assert.Contains("\"authKind\": \"privateKey\"", json);
        Assert.Equal(json, JsonSerializer.Serialize(copy, TgkJson.Options));
        Assert.Equal(host.LastConnected, copy.Hosts[0].LastConnected);
        Assert.Equal(AuthKind.PrivateKey, copy.Identities[0].AuthKind);
        Assert.Equal(42, copy.Revision);
    }

    [Fact]
    public void VaultData_CloneIsDeep()
    {
        var vault = new VaultData { Hosts = [new HostEntry { Name = "a", Host = "h" }] };
        VaultData clone = vault.Clone();
        clone.Hosts[0].Name = "b";
        Assert.Equal("a", vault.Hosts[0].Name);
    }

    [Fact]
    public void PrefsStore_SavesAndLoads()
    {
        using var dir = new TempDirectory();
        var store = new PrefsStore(dir.Path);
        var prefs = new ClientPrefs
        {
            LastServer = "https://tgk.example.com", LastUsername = "axel", KeepSignedIn = false,
            SidebarCollapsed = true, SidebarWidth = 300,
            Terminal = new TerminalSettings { FontSize = 16, ScrollbackLines = 500, CursorShape = TerminalSettings.CursorBar, CursorBlink = false, CopyOnSelect = true },
        };

        store.Save(prefs);
        ClientPrefs loaded = new PrefsStore(dir.Path).Load();

        Assert.Equal(Path.Combine(dir.Path, "prefs.json"), store.FilePath);
        Assert.Equal("https://tgk.example.com", loaded.LastServer);
        Assert.Equal(300, loaded.SidebarWidth);
        Assert.False(loaded.KeepSignedIn);
        Assert.Equal(TerminalSettings.CursorBar, loaded.Terminal.CursorShape);
        Assert.True(loaded.Terminal.CopyOnSelect);
        Assert.DoesNotContain("password", File.ReadAllText(store.FilePath), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Fact]
    public void PrefsStore_ReturnsDefaultsForMissingOrCorruptFile()
    {
        using var dir = new TempDirectory();
        var store = new PrefsStore(dir.Path);
        Assert.Equal(14, store.Load().Terminal.FontSize);

        File.WriteAllText(store.FilePath, "{ not json");
        ClientPrefs prefs = store.Load();
        Assert.Equal(10000, prefs.Terminal.ScrollbackLines);
        Assert.Equal(TerminalSettings.CursorBlock, prefs.Terminal.CursorShape);
    }
}
