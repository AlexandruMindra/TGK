using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using TGK.Core.Ssh;
using Xunit;

namespace TGK.Core.Tests;

/// <summary>
/// <see cref="KeyInspector.ToOpenSsh"/> (keys handed to <c>ssh</c> on another host for direct copies): whatever the
/// format in, OpenSSH's <c>ssh-keygen</c> must load the result, derive the same public key and sign with it.
/// </summary>
public sealed class KeyConversionTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("ed25519", null, "", null)]
    [InlineData("ed25519", null, "pass phrase", null)]
    [InlineData("rsa", 2048, "", null)]
    [InlineData("rsa", 2048, "", "PEM")]
    [InlineData("rsa", 2048, "pass phrase", "PKCS8")]
    [InlineData("ecdsa", 256, "", null)]
    [InlineData("ecdsa", 384, "", "PEM")]
    [InlineData("ecdsa", 521, "pass phrase", null)]
    public void AnyKey_BecomesAnUnencryptedOpenSshKey_ThatSshKeygenUses(string type, int? bits, string passphrase, string? format)
    {
        Assert.SkipUnless(SshKeygen.IsAvailable, "ssh-keygen is not installed");
        string path = SshKeygen.Generate(_dir.Path, type, passphrase, bits, format);

        string converted = KeyInspector.ToOpenSsh(File.ReadAllText(path), passphrase.Length > 0 ? passphrase : null);

        Assert.StartsWith("-----BEGIN OPENSSH PRIVATE KEY-----\n", converted);
        Assert.False(KeyInspector.NeedsPassphrase(converted));
        AssertSameKey(converted, File.ReadAllText(path + ".pub"));
    }

    [Fact]
    public void PuttyKey_BecomesAnOpenSshKey()
    {
        Assert.SkipUnless(SshKeygen.IsAvailable, "ssh-keygen is not installed");
        using RSA rsa = RSA.Create(2048);
        RSAParameters p = rsa.ExportParameters(includePrivateParameters: true);
        byte[] publicBlob = Wire(w => { w.String("ssh-rsa"); w.MPInt(p.Exponent!); w.MPInt(p.Modulus!); });
        byte[] privateBlob = Wire(w => { w.MPInt(p.D!); w.MPInt(p.P!); w.MPInt(p.Q!); w.MPInt(p.InverseQ!); });
        string ppk = PuttyV2(publicBlob, privateBlob, "imported from PuTTY");

        string converted = KeyInspector.ToOpenSsh(ppk.Replace("\n", "\r\n"), null);

        AssertSameKey(converted, "ssh-rsa " + Convert.ToBase64String(publicBlob));
    }

    [Fact]
    public void UnreadableKey_SaysWhy()
    {
        var ex = Assert.Throws<SshSessionException>(() => KeyInspector.ToOpenSsh("not a key", null));
        Assert.Equal(SshErrorKind.KeyError, ex.Kind);
    }

    // ssh-keygen derives the public key from the converted private key and signs with it; the signature is checked
    // against the original public key (so the private part is right, not only the public one stored beside it).
    private void AssertSameKey(string privateKey, string publicKeyLine)
    {
        string keyPath = Path.Combine(_dir.Path, "converted_" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(keyPath, privateKey);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        string[] expected = publicKeyLine.Split(' ');
        string[] derived = Run(null, "-y", "-f", keyPath).Split(' ');
        Assert.Equal(expected[0], derived[0]);
        Assert.Equal(expected[1], derived[1]);

        string data = Path.Combine(_dir.Path, "data.txt");
        File.WriteAllText(data, "signed by the converted key");
        Run(null, "-Y", "sign", "-f", keyPath, "-n", "tgk-test", data);
        string publicPath = keyPath + ".original.pub";
        File.WriteAllText(publicPath, $"{expected[0]} {expected[1]}\n");
        Run(File.ReadAllBytes(data), "-Y", "check-novalidate", "-n", "tgk-test", "-f", publicPath, "-s", data + ".sig");
    }

    private static string Run(byte[]? input, params string[] arguments)
    {
        var info = new ProcessStartInfo("ssh-keygen") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        using Process process = Process.Start(info)!;
        if (input is not null)
            process.StandardInput.BaseStream.Write(input);
        process.StandardInput.Close();
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"ssh-keygen {string.Join(' ', arguments)} failed: {error}");
        return output.Trim();
    }

    // PuTTY's .ppk version 2, unencrypted: the MAC is HMAC-SHA1 keyed with SHA-1 of a fixed string.
    private static string PuttyV2(byte[] publicBlob, byte[] privateBlob, string comment)
    {
        byte[] macData = Wire(w =>
        {
            w.String("ssh-rsa");
            w.String("none");
            w.String(comment);
            w.String(publicBlob);
            w.String(privateBlob);
        });
        byte[] mac = HMACSHA1.HashData(SHA1.HashData("putty-private-key-file-mac-key"u8), macData);
        var sb = new StringBuilder();
        sb.Append("PuTTY-User-Key-File-2: ssh-rsa\nEncryption: none\nComment: ").Append(comment).Append('\n');
        AppendLines(sb, "Public-Lines", publicBlob);
        AppendLines(sb, "Private-Lines", privateBlob);
        sb.Append("Private-MAC: ").Append(Convert.ToHexString(mac).ToLowerInvariant()).Append('\n');
        return sb.ToString();
    }

    private static void AppendLines(StringBuilder sb, string header, byte[] blob)
    {
        string base64 = Convert.ToBase64String(blob);
        int lines = (base64.Length + 63) / 64;
        sb.Append(header).Append(": ").Append(lines).Append('\n');
        for (int i = 0; i < base64.Length; i += 64)
            sb.Append(base64, i, Math.Min(64, base64.Length - i)).Append('\n');
    }

    private static byte[] Wire(Action<KeyGenerator.SshWriter> write) => KeyGenerator.Wire(write);
}
