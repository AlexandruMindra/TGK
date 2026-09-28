using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Security;

namespace TGK.Core.Ssh;

/// <summary>Parses private keys for display (type, fingerprint) and loads them for authentication.</summary>
public static class KeyInspector
{
    /// <summary>
    /// Parses <paramref name="privateKeyText"/> and reports its algorithm (e.g. <c>ssh-ed25519</c>, <c>ssh-rsa</c>)
    /// and the SHA256 fingerprint of its public key as OpenSSH prints it (<c>SHA256:...</c>).
    /// On failure <paramref name="error"/> holds a user-facing reason (use <see cref="NeedsPassphrase"/> to
    /// tell "passphrase required" apart from other problems).
    /// </summary>
    public static bool TryInspect(string privateKeyText, string? passphrase,
        out string keyType, out string fingerprintSha256, out string? error)
    {
        keyType = "";
        fingerprintSha256 = "";
        try
        {
            using PrivateKeyFile keyFile = Load(privateKeyText, passphrase);
            KeyHostAlgorithm algorithm = keyFile.HostKeyAlgorithms.OfType<KeyHostAlgorithm>().First();
            keyType = keyFile.Key.ToString() ?? ""; // OpenSSH algorithm name, e.g. ssh-ed25519
            fingerprintSha256 = Fingerprint(algorithm.Data);
            error = null;
            return true;
        }
        catch (SshSessionException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>True when the key is encrypted and cannot be loaded without a passphrase.</summary>
    public static bool NeedsPassphrase(string privateKeyText)
    {
        if (string.IsNullOrWhiteSpace(privateKeyText))
            return false;
        string text = Normalize(privateKeyText);
        if (text.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal))
            return true;
        try
        {
            using PrivateKeyFile _ = Open(text, passphrase: null);
            return false;
        }
        catch (SshPassPhraseNullOrEmptyException)
        {
            return true;
        }
        catch (Exception)
        {
            return false; // not a readable key at all
        }
    }

    /// <summary>OpenSSH-style fingerprint of an SSH public key blob: <c>SHA256:</c> + unpadded base64.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> publicKeyBlob) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(publicKeyBlob)).TrimEnd('=');

    /// <summary>Loads a key for authentication, translating failures into <see cref="SshErrorKind.KeyError"/>.</summary>
    internal static PrivateKeyFile Load(string privateKeyText, string? passphrase)
    {
        if (string.IsNullOrWhiteSpace(privateKeyText))
            throw KeyError("No private key was provided.");
        string text = Normalize(privateKeyText);
        string? pass = string.IsNullOrEmpty(passphrase) ? null : passphrase;
        try
        {
            return Open(text, pass);
        }
        catch (SshPassPhraseNullOrEmptyException ex)
        {
            throw KeyError("The private key is encrypted: enter its passphrase.", ex);
        }
        catch (Exception ex)
        {
            if (pass is not null && NeedsPassphrase(text))
                throw KeyError("Incorrect passphrase for the private key.", ex);
            if (pass is null && text.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal))
                throw KeyError("The private key is encrypted: enter its passphrase.", ex);
            throw KeyError("The private key is invalid or in an unsupported format.", ex);
        }
    }

    private static PrivateKeyFile Open(string normalizedText, string? passphrase)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(normalizedText));
        return new PrivateKeyFile(stream, passphrase);
    }

    // Pasted keys often come with CRLF line endings or surrounding whitespace.
    private static string Normalize(string text) => text.Replace("\r\n", "\n").Trim() + "\n";

    private static SshSessionException KeyError(string message, Exception? inner = null) =>
        new(SshErrorKind.KeyError, message, inner);
}
