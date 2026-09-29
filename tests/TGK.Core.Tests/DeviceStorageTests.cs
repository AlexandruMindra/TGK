using System;
using System.IO;
using TGK.Core.Services;
using Xunit;

namespace TGK.Core.Tests;

public sealed class DeviceStorageTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void CredentialStore_FallsBackToUserOnlyFile_WithoutKeyring()
    {
        var store = new DeviceCredentialStore(_dir.Path, secretTool: Path.Combine(_dir.Path, "no-such-secret-tool"));
        var credentials = new DeviceCredentials("https://tgk.test", "alice", "u1", "s1", "token", [1, 2, 3]);
        Assert.Null(store.Load());

        store.Save(credentials);

        Assert.True(File.Exists(store.FilePath));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.FilePath));
        DeviceCredentials? loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(credentials with { VaultKey = loaded.VaultKey }, loaded);
        Assert.Equal(credentials.VaultKey, loaded.VaultKey);

        store.Clear();
        Assert.False(File.Exists(store.FilePath));
        Assert.Null(store.Load());
    }

    [Fact]
    public void CredentialStore_LocalVaultEntry_IsSeparateFromTheSignIn()
    {
        var signIn = new DeviceCredentialStore(_dir.Path, secretTool: null);
        var local = new DeviceCredentialStore(_dir.Path, secretTool: null, localVault: true);
        signIn.Save(new DeviceCredentials("https://tgk.test", "alice", "u1", "s1", "token", [1]));
        local.Save(new DeviceCredentials("local", "", "vault-id", "", "", [2]));

        Assert.NotEqual(signIn.FilePath, local.FilePath);
        Assert.Equal("alice", signIn.Load()?.Username);
        signIn.Clear();
        Assert.Equal([2], local.Load()?.VaultKey);
        local.Clear();
        Assert.Null(local.Load());
    }

    [Fact]
    public void CredentialStore_IgnoresCorruptFile()
    {
        var store = new DeviceCredentialStore(_dir.Path, secretTool: null);
        File.WriteAllText(store.FilePath, "{ not json");

        Assert.Null(store.Load());
    }

    [Theory]
    [InlineData("tgk.example.com", "https://tgk.example.com/")]
    [InlineData(" https://tgk.example.com:8443/ ", "https://tgk.example.com:8443/")]
    [InlineData("https://example.com/tgk/", "https://example.com/tgk/")]
    [InlineData("http://localhost:5080", "http://localhost:5080/")]
    [InlineData("http://127.0.0.1:5080", "http://127.0.0.1:5080/")]
    [InlineData("http://[::1]:5080", "http://[::1]:5080/")]
    public void ServerAddress_Accepts(string input, string expected)
    {
        Assert.True(ServerAddress.TryParse(input, out Uri? uri, out string? error), error);
        Assert.Equal(expected, uri.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://tgk.example.com")]
    [InlineData("http://192.168.1.10:5080")]
    [InlineData("ftp://tgk.example.com")]
    [InlineData("https://tgk.example.com/?x=1")]
    [InlineData("mock://localhost")]
    public void ServerAddress_Rejects(string input)
    {
        Assert.False(ServerAddress.TryParse(input, out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
