using System.IO;
using TGK.Core.Ssh;
using Xunit;

namespace TGK.Core.Tests;

public sealed class KeyInspectorTests : System.IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("ed25519", null, "ssh-ed25519")]
    [InlineData("rsa", 2048, "ssh-rsa")]
    [InlineData("ecdsa", 256, "ecdsa-sha2-nistp256")]
    public void UnencryptedKey_ReportsTypeAndOpenSshFingerprint(string type, int? bits, string expectedType)
    {
        Assert.SkipUnless(SshKeygen.IsAvailable, "ssh-keygen is not installed");
        string path = SshKeygen.Generate(_dir.Path, type, "", bits);
        string text = File.ReadAllText(path);

        Assert.False(KeyInspector.NeedsPassphrase(text));
        Assert.True(KeyInspector.TryInspect(text, null, out string keyType, out string fingerprint, out string? error), error);
        Assert.Null(error);
        Assert.Equal(expectedType, keyType);
        Assert.Equal(SshKeygen.Fingerprint(path + ".pub"), fingerprint);
    }

    [Theory]
    [InlineData("ed25519", null, "ssh-ed25519")]
    [InlineData("rsa", 2048, "ssh-rsa")]
    public void EncryptedKey_NeedsPassphrase_AndOpensWithTheRightOne(string type, int? bits, string expectedType)
    {
        Assert.SkipUnless(SshKeygen.IsAvailable, "ssh-keygen is not installed");
        string path = SshKeygen.Generate(_dir.Path, type, "correct horse", bits);
        string text = File.ReadAllText(path);

        Assert.True(KeyInspector.NeedsPassphrase(text));

        Assert.False(KeyInspector.TryInspect(text, null, out _, out _, out string? missing));
        Assert.Contains("passphrase", missing);

        Assert.False(KeyInspector.TryInspect(text, "wrong", out _, out _, out string? wrong));
        Assert.Contains("Incorrect passphrase", wrong);

        Assert.True(KeyInspector.TryInspect(text, "correct horse", out string keyType, out string fingerprint, out string? error), error);
        Assert.Equal(expectedType, keyType);
        Assert.Equal(SshKeygen.Fingerprint(path + ".pub"), fingerprint);
    }

    [Fact]
    public void LegacyPemRsaKey_IsSupported()
    {
        Assert.SkipUnless(SshKeygen.IsAvailable, "ssh-keygen is not installed");
        // -m PEM writes the traditional "BEGIN RSA PRIVATE KEY" format.
        string path = SshKeygen.Generate(_dir.Path, "rsa", "", 2048, format: "PEM");
        string text = File.ReadAllText(path);
        Assert.StartsWith("-----BEGIN RSA PRIVATE KEY-----", text);

        Assert.True(KeyInspector.TryInspect(text, null, out string keyType, out string fingerprint, out string? error), error);
        Assert.Equal("ssh-rsa", keyType);
        Assert.Equal(SshKeygen.Fingerprint(path + ".pub"), fingerprint);
    }

    [Fact]
    public void PastedKeyWithCrLfAndPadding_IsAccepted()
    {
        Assert.SkipUnless(SshKeygen.IsAvailable, "ssh-keygen is not installed");
        string path = SshKeygen.Generate(_dir.Path, "ed25519", "");
        string pasted = "\r\n  " + File.ReadAllText(path).Replace("\n", "\r\n") + "\r\n\r\n";

        Assert.True(KeyInspector.TryInspect(pasted, null, out _, out string fingerprint, out string? error), error);
        Assert.Equal(SshKeygen.Fingerprint(path + ".pub"), fingerprint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a key")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----\n")]
    public void Garbage_IsRejectedWithMessage(string text)
    {
        Assert.False(KeyInspector.TryInspect(text, null, out string keyType, out string fingerprint, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal("", keyType);
        Assert.Equal("", fingerprint);
        Assert.False(KeyInspector.NeedsPassphrase(text));
    }

    [Fact]
    public void Fingerprint_MatchesOpenSshFormat()
    {
        // SHA-256 of an empty blob, base64 without padding.
        Assert.Equal("SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU", KeyInspector.Fingerprint([]));
    }
}
