using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TGK.Core.Models;
using TGK.Core.Services;
using TGK.Protocol.Dtos;
using Xunit;

namespace TGK.Server.Tests;

/// <summary>
/// The real client (<see cref="RemoteVaultService"/>) against the real server (in-process): two devices of one
/// account register, sync, delete, revoke each other and survive a password change, and the database only ever
/// holds ciphertext.
/// </summary>
public sealed class ClientServerE2ETests : IAsyncLifetime
{
    private const string ServerUrl = "http://localhost";
    private const string User = "alice";
    private const string Password = "correct horse battery";
    private const string NewPassword = "staple tuna 42 violet";
    private const string HostName = "e2e-db-01.internal.example";
    private const string PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nE2E-PRIVATE-KEY-MATERIAL-7f3a9c\n-----END OPENSSH PRIVATE KEY-----";

    private readonly ServerFixture _server = new() { PullPageBytes = 1 }; // one item per page: exercises paging
    private readonly string _clientDir = Path.Combine(Path.GetTempPath(), "tgk-e2e-clients", Guid.NewGuid().ToString("N"));

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        try { Directory.Delete(_clientDir, recursive: true); } catch (IOException) { }
    }

    private RemoteVaultService NewDevice(string name) => new(new RemoteVaultOptions
    {
        BaseDirectory = Path.Combine(_clientDir, name),
        Credentials = new MemoryCredentialStore(),
        HttpHandler = _server.Server.CreateHandler(),
        DeviceName = name,
        Platform = "e2e",
        NewKdf = new KdfParams(KdfParams.Pbkdf2Sha256, FastCrypto.KdfIterations),
        PushDelay = TimeSpan.FromMilliseconds(10),
        PullInterval = TimeSpan.FromHours(1), // the test syncs explicitly
    });

    [Fact]
    public async Task TwoDevices_SyncRevokeAndChangePassword_ServerStoresOnlyCiphertext()
    {
        using RemoteVaultService a = NewDevice("device-a"), b = NewDevice("device-b");

        // Device A creates the account (proving the authenticator with a code) and fills the vault.
        TotpEnrollment enrollment = a.BeginTotpEnrollment(User);
        string secret = enrollment.Secret;
        AssertOk(await a.RegisterAsync(ServerUrl, User, Password, null, secret, _server.NextCode(secret), keepSignedIn: false));
        var group = new HostGroup { Name = "Production" };
        var identity = new Identity { Name = "Deploy key", Username = "deploy", AuthKind = AuthKind.PrivateKey, PrivateKey = PrivateKey, Passphrase = "key-pass" };
        var host = new HostEntry { Name = "db-01", Host = HostName, Port = 2222, GroupId = group.Id, IdentityId = identity.Id, Notes = "primary" };
        await a.SaveGroupAsync(group);
        await a.SaveIdentityAsync(identity);
        await a.SaveHostAsync(host);
        await a.SyncAsync();
        Assert.Null(a.LastError);

        // Device B signs in (with the next code: a used step is refused) and decrypts the same vault.
        AssertOk(await b.LoginAsync(ServerUrl, User, Password, _server.NextCode(secret), keepSignedIn: false));
        AssertSameVault(a.Current, b.Current);
        HostEntry synced = Assert.Single(b.Current.Hosts);
        Assert.Equal((HostName, 2222, group.Id, identity.Id), (synced.Host, synced.Port, synced.GroupId, synced.IdentityId));
        Assert.Equal(PrivateKey, Assert.Single(b.Current.Identities).PrivateKey);

        // B deletes the host; A pulls the tombstone.
        await b.DeleteHostAsync(host.Id);
        await b.SyncAsync();
        await a.SyncAsync();
        Assert.Empty(a.Current.Hosts);
        Assert.Single(a.Current.Groups);

        // B signs out every other device; A notices on its next call.
        var sessions = await b.ListSessionsAsync();
        Assert.Equal(2, sessions.Count);
        Assert.Equal("device-b", Assert.Single(sessions, s => s.Current).DeviceName);
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.SessionEnded += reason => ended.TrySetResult(reason);
        Assert.Equal(1, await b.RevokeOtherSessionsAsync());
        await Record.ExceptionAsync(() => a.SyncAsync());
        Assert.Contains("signed out", await ended.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(a.IsLoggedIn);
        Assert.Empty(a.Current.Groups);

        // A signs in again and changes the password: B's old password stops working, the new one decrypts the same data.
        AssertOk(await a.LoginAsync(ServerUrl, User, Password, _server.NextCode(secret), keepSignedIn: false));
        await a.ChangePasswordAsync(Password, _server.NextCode(secret), NewPassword, revokeOtherSessions: false);
        await b.LogoutAsync();
        LoginResult stale = await b.LoginAsync(ServerUrl, User, Password, _server.NextCode(secret), keepSignedIn: false);
        Assert.Equal(VaultError.InvalidCredentials, stale.ErrorCode);
        AssertOk(await b.LoginAsync(ServerUrl, User, NewPassword, _server.NextCode(secret), keepSignedIn: false));
        AssertSameVault(a.Current, b.Current);
        Assert.Equal(PrivateKey, Assert.Single(b.Current.Identities).PrivateKey);

        // The server holds items, but none of the plaintext (database file and WAL).
        await using (var db = new SqliteConnection($"Data Source={_server.DbPath};Mode=ReadOnly"))
        {
            await db.OpenAsync();
            await using var count = db.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM items WHERE data IS NOT NULL";
            Assert.Equal(2L, (long)(await count.ExecuteScalarAsync())!);
        }
        byte[] stored = [.. ReadShared(_server.DbPath), .. ReadShared(_server.DbPath + "-wal")];
        foreach (string plaintext in new[] { HostName, "E2E-PRIVATE-KEY-MATERIAL", "Deploy key", "Production", "key-pass", Password, NewPassword })
        {
            Assert.False(stored.AsSpan().IndexOf(Encoding.UTF8.GetBytes(plaintext)) >= 0, $"'{plaintext}' is stored in plaintext");
            Assert.False(stored.AsSpan().IndexOf(Encoding.Unicode.GetBytes(plaintext)) >= 0, $"'{plaintext}' is stored as UTF-16");
        }
    }

    private static void AssertOk(LoginResult result) => Assert.True(result.Success, $"{result.ErrorCode}: {result.Error}");

    private static void AssertSameVault(VaultData expected, VaultData actual)
    {
        Assert.Equal(Summary(expected), Summary(actual));

        static string Summary(VaultData v) => string.Join("\n",
            v.Groups.OrderBy(g => g.Id).Select(g => $"g {g.Id} {g.Name}")
                .Concat(v.Identities.OrderBy(i => i.Id).Select(i => $"i {i.Id} {i.Name} {i.Username} {i.AuthKind} {i.PrivateKey} {i.Passphrase}"))
                .Concat(v.Hosts.OrderBy(h => h.Id).Select(h => $"h {h.Id} {h.Name} {h.Host}:{h.Port} {h.GroupId} {h.IdentityId} {h.Notes}")));
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

    private sealed class MemoryCredentialStore : IDeviceCredentialStore
    {
        private DeviceCredentials? _stored;

        public DeviceCredentials? Load() => _stored;

        public void Save(DeviceCredentials credentials) => _stored = credentials;

        public void Clear() => _stored = null;
    }
}
