using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TGK.Core.Models;
using TGK.Core.Services;
using Xunit;

namespace TGK.Core.Tests;

public sealed class VaultBackupTests : IDisposable
{
    private const string Master = "correct horse 42";
    private const string BackupPassword = "backup battery staple";

    private readonly TempDirectory _dir = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string BackupPath => Path.Combine(_dir.Path, "vault.tgkbackup");

    /// <summary>A client with a new, unlocked local vault in its own directory.</summary>
    private async Task<(RoutingVaultService Vault, VaultMigration Migration)> NewLocalClientAsync(string name)
    {
        string directory = Path.Combine(_dir.Path, name);
        var local = new LocalVaultService(new LocalVaultOptions { BaseDirectory = directory, Credentials = new MemoryCredentialStore(), NewKdf = FakeTgkServer.TestKdf });
        var vault = new RoutingVaultService(
            new RemoteVaultService(new RemoteVaultOptions { BaseDirectory = directory, Credentials = new MemoryCredentialStore() }),
            new MockVaultService(directory, TimeSpan.Zero), local);
        LoginResult created = await vault.CreateLocalAsync(Master, false, Ct);
        Assert.True(created.Success, created.Error);
        return (vault, new VaultMigration(vault));
    }

    [Fact]
    public async Task ExportImport_RoundTrips_AndMergesByTheRules()
    {
        (RoutingVaultService source, VaultMigration sourceMigration) = await NewLocalClientAsync("source");
        var group = new HostGroup { Name = "Prod" };
        var identity = new Identity { Name = "deploy", PrivateKey = "PRIVATE-KEY-MATERIAL-5d2" };
        var host = new HostEntry { Name = "db", Host = "db.example", GroupId = group.Id, IdentityId = identity.Id };
        await source.SaveGroupAsync(group);
        await source.SaveIdentityAsync(identity);
        await source.SaveHostAsync(host);
        await source.AddKnownHostAsync(new KnownHost { Host = "db.example", Port = 22, KeyType = "ssh-ed25519", FingerprintSha256 = "SHA256:backup" });
        await source.AddKnownHostAsync(new KnownHost { Host = "new.example", Port = 22, KeyType = "ssh-ed25519", FingerprintSha256 = "SHA256:new" });
        await source.SaveDefaultsAsync(new HostOptions { KeepAliveSeconds = 15 });

        var tooShort = await Assert.ThrowsAsync<VaultException>(() => sourceMigration.ExportBackupAsync(BackupPath, "short", Ct));
        Assert.Equal(VaultError.ValidationFailed, tooShort.Code);
        await sourceMigration.ExportBackupAsync(BackupPath, BackupPassword, Ct);
        string json = await File.ReadAllTextAsync(BackupPath, Ct);
        Assert.Equal("tgk-backup", JsonNode.Parse(json)!["format"]!.GetValue<string>());
        Assert.DoesNotContain("db.example", json);
        Assert.DoesNotContain("PRIVATE-KEY-MATERIAL", json);

        // Into an empty vault: everything is added, including the defaults (it has none).
        (RoutingVaultService empty, VaultMigration emptyMigration) = await NewLocalClientAsync("empty");
        Assert.Equal(new MergeCounts(6, 0, 0), await emptyMigration.ImportBackupAsync(BackupPath, BackupPassword, Ct));
        Assert.Equal(host.Id, Assert.Single(empty.Current.Hosts).Id);
        Assert.Equal("PRIVATE-KEY-MATERIAL-5d2", Assert.Single(empty.Current.Identities).PrivateKey);
        Assert.Equal(15, empty.Current.Defaults.KeepAliveSeconds);
        Assert.Equal(2, empty.Current.KnownHosts.Count);

        // Again: identical items are skipped; one edited since is a conflict and keeps the newer edit (never rolled back).
        Assert.Equal(new MergeCounts(0, 0, 6), await emptyMigration.ImportBackupAsync(BackupPath, BackupPassword, Ct));
        HostEntry edited = empty.Current.Hosts[0].Clone();
        edited.Name = "edited";
        await empty.SaveHostAsync(edited);
        Assert.Equal(new MergeCounts(0, 1, 5), await emptyMigration.ImportBackupAsync(BackupPath, BackupPassword, Ct));
        Assert.Equal("edited", Assert.Single(empty.Current.Hosts).Name);
        edited.Name = "db";
        await empty.SaveHostAsync(edited);
        await empty.TouchHostAsync(edited.Id); // when this device last connected is not part of the host
        Assert.Equal(new MergeCounts(0, 0, 6), await emptyMigration.ImportBackupAsync(BackupPath, BackupPassword, Ct));

        // Into a vault with its own defaults and a key for db.example: those win.
        (RoutingVaultService other, VaultMigration otherMigration) = await NewLocalClientAsync("other");
        await other.SaveDefaultsAsync(new HostOptions { KeepAliveSeconds = 99 });
        await other.AddKnownHostAsync(new KnownHost { Host = "DB.example", Port = 22, KeyType = "ssh-rsa", FingerprintSha256 = "SHA256:mine" });
        Assert.Equal(new MergeCounts(4, 0, 2), await otherMigration.ImportBackupAsync(BackupPath, BackupPassword, Ct));
        Assert.Equal(99, other.Current.Defaults.KeepAliveSeconds);
        Assert.Equal("SHA256:mine", other.Current.FindKnownHost("db.example", 22)!.FingerprintSha256);
        Assert.Equal("SHA256:new", other.Current.FindKnownHost("new.example", 22)!.FingerprintSha256);

        // What was imported is stored: it is there after unlocking again.
        await other.LogoutAsync();
        Assert.True((await other.UnlockLocalAsync(Master, false, Ct)).Success);
        Assert.Equal(host.Id, Assert.Single(other.Current.Hosts).Id);
    }

    [Fact]
    public async Task Import_RejectsWrongPasswordTamperingAndOtherFiles_WithoutChangingTheVault()
    {
        (RoutingVaultService source, VaultMigration migration) = await NewLocalClientAsync("source");
        await source.SaveHostAsync(new HostEntry { Name = "a", Host = "a.example" });
        await source.SaveHostAsync(new HostEntry { Name = "b", Host = "b.example" });
        await migration.ExportBackupAsync(BackupPath, BackupPassword, Ct);
        (RoutingVaultService target, VaultMigration targetMigration) = await NewLocalClientAsync("target");

        async Task<VaultError> ImportFails(string path, string password) =>
            (await Assert.ThrowsAsync<VaultException>(() => targetMigration.ImportBackupAsync(path, password, Ct))).Code;

        Assert.Equal(VaultError.InvalidCredentials, await ImportFails(BackupPath, "not the backup password"));

        // A modified item, or one moved to another item's id: nothing is imported.
        JsonNode file = JsonNode.Parse(await File.ReadAllTextAsync(BackupPath, Ct))!;
        JsonArray items = file["items"]!.AsArray();
        byte[] data = Convert.FromBase64String(items[1]!["data"]!.GetValue<string>());
        data[^1] ^= 1;
        items[1]!["data"] = Convert.ToBase64String(data);
        string tampered = Path.Combine(_dir.Path, "tampered.tgkbackup");
        await File.WriteAllTextAsync(tampered, file.ToJsonString(), Ct);
        Assert.Equal(VaultError.Storage, await ImportFails(tampered, BackupPassword));

        file = JsonNode.Parse(await File.ReadAllTextAsync(BackupPath, Ct))!;
        items = file["items"]!.AsArray();
        items[0]!["id"] = items[1]!["id"]!.GetValue<string>();
        await File.WriteAllTextAsync(tampered, file.ToJsonString(), Ct);
        Assert.Equal(VaultError.Storage, await ImportFails(tampered, BackupPassword));

        // Items removed (e.g. the pinned host keys): the manifest no longer matches.
        file = JsonNode.Parse(await File.ReadAllTextAsync(BackupPath, Ct))!;
        file["items"]!.AsArray().RemoveAt(0);
        await File.WriteAllTextAsync(tampered, file.ToJsonString(), Ct);
        Assert.Equal(VaultError.Storage, await ImportFails(tampered, BackupPassword));
        Assert.Empty(target.Current.Hosts);

        // Past the size limit: refused before parsing.
        using (FileStream huge = File.Create(tampered))
            huge.SetLength(200L * 1024 * 1024);
        Assert.Equal(VaultError.Storage, await ImportFails(tampered, BackupPassword));

        string notBackup = Path.Combine(_dir.Path, "notes.txt");
        await File.WriteAllTextAsync(notBackup, "hello", Ct);
        Assert.Equal(VaultError.Storage, await ImportFails(notBackup, BackupPassword));
        Assert.Equal(VaultError.Storage, await ImportFails(Path.Combine(_dir.Path, "missing.tgkbackup"), BackupPassword));

        Assert.Equal(new MergeCounts(2, 0, 0), await targetMigration.ImportBackupAsync(BackupPath, BackupPassword, Ct));
        Assert.Equal(new[] { "a", "b" }, target.Current.Hosts.Select(h => h.Name).Order());
    }
}
