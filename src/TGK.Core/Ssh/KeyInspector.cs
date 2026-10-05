using System;
using System.IO;
using System.Linq;
using System.Numerics;
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

    /// <summary>
    /// The public half of <paramref name="privateKeyText"/> as an <c>authorized_keys</c> line
    /// (<c>ssh-ed25519 AAAA...</c>, plus <paramref name="comment"/> when given), or null when the key cannot be read.
    /// </summary>
    public static string? PublicKey(string privateKeyText, string? passphrase, string? comment = null)
    {
        try
        {
            using PrivateKeyFile keyFile = Load(privateKeyText, passphrase);
            KeyHostAlgorithm algorithm = keyFile.HostKeyAlgorithms.OfType<KeyHostAlgorithm>().First();
            string line = $"{keyFile.Key} {Convert.ToBase64String(algorithm.Data)}";
            return string.IsNullOrWhiteSpace(comment) ? line : $"{line} {comment.Trim()}";
        }
        catch (SshSessionException)
        {
            return null;
        }
    }

    /// <summary>
    /// The key in OpenSSH's own format, unencrypted (<c>-----BEGIN OPENSSH PRIVATE KEY-----</c>, as <c>ssh-keygen</c>
    /// writes it), whatever format it came in (PuTTY, PEM, PKCS#8, encrypted or not): what <c>ssh</c> on another host
    /// can load without a passphrase. Ed25519, ECDSA and RSA keys.
    /// </summary>
    /// <exception cref="SshSessionException">The key can't be read (<see cref="SshErrorKind.KeyError"/>, with the reason).</exception>
    public static string ToOpenSsh(string privateKeyText, string? passphrase)
    {
        using PrivateKeyFile keyFile = Load(privateKeyText, passphrase);
        byte[] publicBlob = keyFile.HostKeyAlgorithms.OfType<KeyHostAlgorithm>().First().Data;
        string type = keyFile.Key.ToString() ?? "";
        byte[] fields;
        switch (keyFile.Key)
        {
            case RsaKey rsa:
                // OpenSSH order: n, e, d, iqmp (q^-1 mod p, computed here: p is prime), p, q.
                BigInteger iqmp = BigInteger.ModPow(rsa.Q, rsa.P - 2, rsa.P);
                fields = KeyGenerator.Wire(w =>
                {
                    w.MPInt(Unsigned(rsa.Modulus));
                    w.MPInt(Unsigned(rsa.Exponent));
                    w.MPInt(Unsigned(rsa.D));
                    w.MPInt(Unsigned(iqmp));
                    w.MPInt(Unsigned(rsa.P));
                    w.MPInt(Unsigned(rsa.Q));
                });
                type = "ssh-rsa";
                break;
            case ED25519Key ed:
                byte[] pub = ed.PublicKey, seed = ed.PrivateKey.AsSpan(0, 32).ToArray();
                // OpenSSH stores the 64-byte "secret key" (seed || public key).
                fields = KeyGenerator.Wire(w => { w.String(pub); w.String([.. seed, .. pub]); });
                CryptographicOperations.ZeroMemory(seed);
                type = "ssh-ed25519";
                break;
            case EcdsaKey ec:
                // The public blob is: type, curve name, point; the private part adds the scalar.
                (string curve, byte[] point) = EcdsaPublic(publicBlob);
                byte[] d = ec.PrivateKey ?? throw KeyError("The ECDSA key has no private part.");
                fields = KeyGenerator.Wire(w => { w.String(curve); w.String(point); w.MPInt(d); });
                break;
            default:
                throw KeyError($"{type} keys can't be converted; use an Ed25519, ECDSA or RSA key.");
        }
        try
        {
            return KeyGenerator.OpenSshPrivateKey(type, publicBlob, fields, "");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fields);
        }
    }

    private static byte[] Unsigned(BigInteger value) => value.ToByteArray(isUnsigned: true, isBigEndian: true);

    private static (string Curve, byte[] Point) EcdsaPublic(byte[] blob)
    {
        int offset = 0;
        byte[] Next()
        {
            int length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(offset));
            byte[] value = blob.AsSpan(offset + 4, length).ToArray();
            offset += 4 + length;
            return value;
        }
        _ = Next(); // the key type
        return (Encoding.ASCII.GetString(Next()), Next());
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
