using System.Diagnostics;
using System.IO;
using TGK.Core.Ssh;
using Xunit;

namespace TGK.Core.Tests;

public sealed class KeyGeneratorTests : System.IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData(KeyAlgorithm.Ed25519, "ssh-ed25519")]
    [InlineData(KeyAlgorithm.Rsa4096, "ssh-rsa")]
    public void GeneratedKey_LoadsAndMatchesItsPublicKey(KeyAlgorithm algorithm, string expectedType)
    {
        GeneratedKey key = KeyGenerator.Generate(algorithm, "me@laptop");

        Assert.Equal(expectedType, key.KeyType);
        Assert.StartsWith("-----BEGIN OPENSSH PRIVATE KEY-----\n", key.PrivateKey);
        Assert.StartsWith(expectedType + " AAAA", key.PublicKey);
        Assert.EndsWith(" me@laptop", key.PublicKey);
        Assert.False(KeyInspector.NeedsPassphrase(key.PrivateKey));
        Assert.True(KeyInspector.TryInspect(key.PrivateKey, null, out string type, out string fingerprint, out string? error), error);
        Assert.Equal(expectedType, type);
        Assert.Equal(key.Fingerprint, fingerprint);
        Assert.Equal(key.PublicKey, KeyInspector.PublicKey(key.PrivateKey, null, "me@laptop"));
    }

    [Theory]
    [InlineData(KeyAlgorithm.Ed25519)]
    [InlineData(KeyAlgorithm.Rsa4096)]
    public void GeneratedKey_IsAcceptedByOpenSsh(KeyAlgorithm algorithm)
    {
        Assert.SkipUnless(SshKeygen.IsAvailable, "ssh-keygen is not installed");
        GeneratedKey key = KeyGenerator.Generate(algorithm, "tgk");
        string path = Path.Combine(_dir.Path, "key");
        File.WriteAllText(path, key.PrivateKey);
        if (!System.OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // ssh-keygen -y derives the public key from the private one (and checks the container's integrity).
        var info = new ProcessStartInfo("ssh-keygen", ["-y", "-f", path]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(key.PublicKey, output);
    }

    [Fact]
    public void EveryKeyIsDifferent() =>
        Assert.NotEqual(KeyGenerator.Generate(KeyAlgorithm.Ed25519, null).Fingerprint, KeyGenerator.Generate(KeyAlgorithm.Ed25519, null).Fingerprint);
}
