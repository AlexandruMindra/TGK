using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace TGK.Core.Ssh;

public enum KeyAlgorithm
{
    Ed25519,
    Rsa4096,
}

/// <summary>A freshly generated key pair: the private key in OpenSSH format plus its public side.</summary>
/// <param name="PrivateKey">Unencrypted <c>-----BEGIN OPENSSH PRIVATE KEY-----</c> text, as <c>ssh-keygen</c> writes it.</param>
/// <param name="PublicKey">The <c>authorized_keys</c> line, e.g. <c>ssh-ed25519 AAAA... comment</c>.</param>
public sealed record GeneratedKey(string PrivateKey, string PublicKey, string KeyType, string Fingerprint);

/// <summary>Generates SSH key pairs (the equivalent of <c>ssh-keygen -t ed25519</c> / <c>-t rsa -b 4096</c>).</summary>
public static class KeyGenerator
{
    public static GeneratedKey Generate(KeyAlgorithm algorithm, string? comment)
    {
        comment = comment?.Trim() ?? "";
        (string type, byte[] publicBlob, byte[] privateFields) = algorithm switch
        {
            KeyAlgorithm.Ed25519 => Ed25519(),
            KeyAlgorithm.Rsa4096 => Rsa(4096),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };
        string privateKey = OpenSshPrivateKey(type, publicBlob, privateFields, comment);
        string publicKey = $"{type} {Convert.ToBase64String(publicBlob)}" + (comment.Length > 0 ? " " + comment : "");
        return new GeneratedKey(privateKey, publicKey, type, KeyInspector.Fingerprint(publicBlob));
    }

    private static (string, byte[], byte[]) Ed25519()
    {
        var key = new Ed25519PrivateKeyParameters(new SecureRandom());
        byte[] seed = key.GetEncoded(), pub = key.GeneratePublicKey().GetEncoded();
        byte[] blob = Wire(w => { w.String("ssh-ed25519"); w.String(pub); });
        // OpenSSH stores the 64-byte "secret key" (seed || public key).
        byte[] fields = Wire(w => { w.String(pub); w.String([.. seed, .. pub]); });
        CryptographicOperations.ZeroMemory(seed);
        return ("ssh-ed25519", blob, fields);
    }

    private static (string, byte[], byte[]) Rsa(int bits)
    {
        using RSA rsa = RSA.Create(bits);
        RSAParameters p = rsa.ExportParameters(includePrivateParameters: true);
        byte[] blob = Wire(w => { w.String("ssh-rsa"); w.MPInt(p.Exponent!); w.MPInt(p.Modulus!); });
        // OpenSSH order: n, e, d, iqmp (= q^-1 mod p, .NET's InverseQ), p, q.
        byte[] fields = Wire(w =>
        {
            w.MPInt(p.Modulus!);
            w.MPInt(p.Exponent!);
            w.MPInt(p.D!);
            w.MPInt(p.InverseQ!);
            w.MPInt(p.P!);
            w.MPInt(p.Q!);
        });
        return ("ssh-rsa", blob, fields);
    }

    /// <summary>The unencrypted <c>openssh-key-v1</c> container (see OpenSSH's PROTOCOL.key).</summary>
    internal static string OpenSshPrivateKey(string type, byte[] publicBlob, byte[] privateFields, string comment)
    {
        uint check = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
        byte[] section = Wire(w =>
        {
            w.UInt32(check);
            w.UInt32(check);
            w.String(type);
            w.Raw(privateFields);
            w.String(comment);
            for (byte pad = 1; w.Length % 8 != 0; pad++)
                w.Raw([pad]);
        });
        byte[] body = Wire(w =>
        {
            w.Raw("openssh-key-v1\0"u8);
            w.String("none"); // cipher
            w.String("none"); // kdf
            w.String([]); // kdf options
            w.UInt32(1); // number of keys
            w.String(publicBlob);
            w.String(section);
        });
        CryptographicOperations.ZeroMemory(section);
        string base64 = Convert.ToBase64String(body);
        var text = new StringBuilder("-----BEGIN OPENSSH PRIVATE KEY-----\n");
        for (int i = 0; i < base64.Length; i += 70)
            text.Append(base64, i, Math.Min(70, base64.Length - i)).Append('\n');
        return text.Append("-----END OPENSSH PRIVATE KEY-----\n").ToString();
    }

    internal static byte[] Wire(Action<SshWriter> write)
    {
        var writer = new SshWriter();
        write(writer);
        return writer.ToArray();
    }

    /// <summary>SSH wire encoding (RFC 4251 §5): big-endian uint32, length-prefixed strings, mpints.</summary>
    internal sealed class SshWriter
    {
        private readonly MemoryStream _stream = new();

        public long Length => _stream.Length;

        public void Raw(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);

        public void UInt32(uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
            Raw(buffer);
        }

        public void String(ReadOnlySpan<byte> bytes)
        {
            UInt32((uint)bytes.Length);
            Raw(bytes);
        }

        public void String(string text) => String(Encoding.UTF8.GetBytes(text));

        /// <summary>A positive big-endian integer: no leading zeros, plus one zero byte if the top bit is set.</summary>
        public void MPInt(ReadOnlySpan<byte> magnitude)
        {
            int start = 0;
            while (start < magnitude.Length && magnitude[start] == 0)
                start++;
            ReadOnlySpan<byte> trimmed = magnitude[start..];
            bool pad = trimmed.Length > 0 && trimmed[0] >= 0x80;
            UInt32((uint)(trimmed.Length + (pad ? 1 : 0)));
            if (pad)
                Raw([0]);
            Raw(trimmed);
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
