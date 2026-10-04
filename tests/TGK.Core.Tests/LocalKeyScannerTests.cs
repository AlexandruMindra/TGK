using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TGK.Core.Ssh;
using Xunit;

namespace TGK.Core.Tests;

public sealed class LocalKeyScannerTests : IDisposable
{
    private readonly TempDirectory _home = new();
    private readonly string _ssh;

    public LocalKeyScannerTests()
    {
        _ssh = Dir(".ssh");
    }

    public void Dispose() => _home.Dispose();

    private string Dir(string relative) => Directory.CreateDirectory(Path.Combine(_home.Path, relative)).FullName;

    private static GeneratedKey WriteKey(string path, KeyAlgorithm algorithm = KeyAlgorithm.Ed25519, bool withPub = true)
    {
        GeneratedKey key = KeyGenerator.Generate(algorithm, Path.GetFileName(path));
        File.WriteAllText(path, key.PrivateKey);
        if (withPub)
            File.WriteAllText(path + ".pub", key.PublicKey + "\n");
        return key;
    }

    [Fact]
    public void FindsKeysInDotSsh_SkippingEverythingElse()
    {
        GeneratedKey ed = WriteKey(Path.Combine(_ssh, "id_ed25519"));
        GeneratedKey work = WriteKey(Path.Combine(_ssh, "work"), withPub: false);
        File.WriteAllText(Path.Combine(_ssh, "known_hosts"), "example.com ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl\n");
        File.WriteAllText(Path.Combine(_ssh, "config"), "Host *\n  ServerAliveInterval 30\n");
        File.WriteAllText(Path.Combine(_ssh, "notes.txt"), new string('x', 500));

        List<LocalKey> keys = LocalKeyScanner.Scan(_home.Path);

        Assert.Equal(2, keys.Count);
        Assert.Equal(Path.Combine(_ssh, "id_ed25519"), keys[0].Path); // default names come first
        Assert.Equal(ed.Fingerprint, keys[0].Fingerprint);
        Assert.Equal("id_ed25519", keys[0].Comment); // from the .pub
        Assert.Equal(work.Fingerprint, keys[1].Fingerprint);
        Assert.All(keys, k => Assert.False(k.Encrypted));
    }

    [Fact]
    public void EncryptedOpenSshKey_IsNamedWithoutItsPassphrase()
    {
        Assert.SkipUnless(SshKeygen.IsAvailable, "ssh-keygen is not installed");
        string path = SshKeygen.Generate(_ssh, "ed25519", "secret");
        File.Delete(path + ".pub"); // the fingerprint must come from the key file itself

        LocalKey key = Assert.Single(LocalKeyScanner.Scan(_home.Path));
        Assert.True(key.Encrypted);
        Assert.Equal("ssh-ed25519", key.KeyType);
        Assert.NotNull(key.Fingerprint);
    }

    [Fact]
    public void EncryptedPemKey_UsesItsPubFile()
    {
        Assert.SkipUnless(SshKeygen.IsAvailable, "ssh-keygen is not installed");
        string path = SshKeygen.Generate(_ssh, "rsa", "secret", 2048, format: "PEM");

        LocalKey key = Assert.Single(LocalKeyScanner.Scan(_home.Path));
        Assert.True(key.Encrypted);
        Assert.Equal("ssh-rsa", key.KeyType);
        Assert.Equal(SshKeygen.Fingerprint(path + ".pub"), key.Fingerprint);
    }

    [Fact]
    public void PuttyKey_IsRead()
    {
        GeneratedKey source = KeyGenerator.Generate(KeyAlgorithm.Ed25519, null);
        string blob = source.PublicKey.Split(' ')[1];
        File.WriteAllText(Path.Combine(_ssh, "server.ppk"),
            "PuTTY-User-Key-File-3: ssh-ed25519\nEncryption: aes256-cbc\nComment: my putty key\n" +
            $"Public-Lines: 2\n{blob[..40]}\n{blob[40..]}\nPrivate-Lines: 1\nAAAA\nPrivate-MAC: 00\n");

        LocalKey key = Assert.Single(LocalKeyScanner.Scan(_home.Path));
        Assert.True(key.Encrypted);
        Assert.Equal("ssh-ed25519", key.KeyType);
        Assert.Equal(source.Fingerprint, key.Fingerprint);
        Assert.Equal("my putty key", key.Comment);
    }

    [Fact]
    public void FollowsIdentityFileFromSshConfig_AndSubfolders()
    {
        GeneratedKey elsewhere = WriteKey(Path.Combine(Dir("projects/infra"), "deploy"));
        GeneratedKey nested = WriteKey(Path.Combine(Dir(".ssh/clients"), "acme"));
        File.WriteAllText(Path.Combine(_ssh, "config"),
            "Host prod\n  IdentityFile ~/projects/infra/deploy\n  IdentityFile=\"%d/missing\"\n  IdentityFile ~/.ssh/%h\n");

        List<LocalKey> keys = LocalKeyScanner.Scan(_home.Path);

        Assert.Equal([nested.Fingerprint, elsewhere.Fingerprint], keys.Select(k => k.Fingerprint));
    }

    [Fact]
    public void SameKeyInTwoPlaces_IsListedOnce()
    {
        string original = Path.Combine(_ssh, "id_rsa");
        WriteKey(original, KeyAlgorithm.Rsa4096);
        File.Copy(original, Path.Combine(_ssh, "id_rsa.backup"));

        Assert.Single(LocalKeyScanner.Scan(_home.Path));
    }

    [Fact]
    public void UserFolders_AreOnlyScannedOnRequest_AndOnlyTheirTopLevel()
    {
        string downloads = Dir("Downloads");
        WriteKey(Path.Combine(downloads, "aws.pem"), withPub: false);
        WriteKey(Path.Combine(Dir("Downloads/deep"), "hidden.pem"), withPub: false);
        // Isolate from the machine's own XDG config.
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Dir(".config"));
        try
        {
            Assert.Empty(LocalKeyScanner.Scan(_home.Path));
            LocalKey key = Assert.Single(LocalKeyScanner.Scan(_home.Path, includeUserFolders: true));
            Assert.Equal(Path.Combine(downloads, "aws.pem"), key.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);
        }
    }

    [Fact]
    public void LocalizedXdgDownloadFolder_IsHonored()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "XDG folders are Linux only");
        string downloads = Dir("Descărcări");
        WriteKey(Path.Combine(downloads, "key.pem"), withPub: false);
        File.WriteAllText(Path.Combine(Dir(".config"), "user-dirs.dirs"),
            "# comment\nXDG_DOWNLOAD_DIR=\"$HOME/Descărcări\"\nXDG_DESKTOP_DIR=\"$HOME/\"\n");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_home.Path, ".config"));
        try
        {
            Assert.Contains(downloads, LocalKeyScanner.UserFolders(_home.Path));
            Assert.DoesNotContain(_home.Path, LocalKeyScanner.UserFolders(_home.Path)); // "$HOME/" means unset
            Assert.Single(LocalKeyScanner.Scan(_home.Path, includeUserFolders: true));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);
        }
    }
}
