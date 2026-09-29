using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TGK.Protocol.Dtos;

namespace TGK.Protocol;

/// <summary>
/// Client-side end-to-end encryption. The password yields an auth key (sent to the server, which stores only a
/// verifier of it) and a key-encryption key that wraps the random vault key; items are sealed with the vault key.
/// Sealed format: <c>0x01 || nonce(12) || ciphertext || tag(16)</c>, AES-256-GCM.
/// </summary>
public static class VaultCrypto
{
    public const int KeySize = 32;
    public const int SaltSize = 16;

    private const byte FormatVersion = 0x01;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Overhead = 1 + NonceSize + TagSize;

    private const string VaultKeyAad = "tgk/vk/v1";
    private const string ItemAadPrefix = "tgk/item/v1:";
    private static readonly byte[] AuthKeyInfo = "tgk/auth/v1"u8.ToArray();
    private static readonly byte[] KekInfo = "tgk/kek/v1"u8.ToArray();

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltSize);

    public static byte[] NewVaultKey() => RandomNumberGenerator.GetBytes(KeySize);

    /// <summary>
    /// PBKDF2-HMAC-SHA256 over the NFKC-normalized password, then HKDF-SHA256 (empty salt) into the auth key and
    /// the key-encryption key. Deliberately slow at production iteration counts.
    /// </summary>
    public static (byte[] AuthKey, byte[] Kek) DeriveKeys(string password, ReadOnlySpan<byte> salt, KdfParams kdf)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(kdf);
        if (kdf.Algorithm != KdfParams.Pbkdf2Sha256)
            throw new NotSupportedException($"Unsupported KDF '{kdf.Algorithm}'.");
        ArgumentOutOfRangeException.ThrowIfLessThan(kdf.Iterations, 1);
        if (salt.Length != SaltSize)
            throw new ArgumentException($"Salt must be {SaltSize} bytes.", nameof(salt));

        byte[] passwordBytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC));
        byte[] master = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, kdf.Iterations, HashAlgorithmName.SHA256, KeySize);
        try
        {
            return (HKDF.DeriveKey(HashAlgorithmName.SHA256, master, KeySize, info: AuthKeyInfo),
                    HKDF.DeriveKey(HashAlgorithmName.SHA256, master, KeySize, info: KekInfo));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(master);
        }
    }

    public static byte[] WrapVaultKey(ReadOnlySpan<byte> kek, ReadOnlySpan<byte> vaultKey)
    {
        CheckKey(vaultKey);
        return Seal(kek, vaultKey, VaultKeyAad);
    }

    /// <exception cref="CryptographicException">Wrong key-encryption key (i.e. wrong password) or tampered data.</exception>
    public static byte[] UnwrapVaultKey(ReadOnlySpan<byte> kek, ReadOnlySpan<byte> wrappedVaultKey)
    {
        byte[] vaultKey = Open(kek, wrappedVaultKey, VaultKeyAad);
        return vaultKey.Length == KeySize ? vaultKey : throw new CryptographicException("Wrapped vault key has the wrong size.");
    }

    /// <summary>
    /// Seals <c>{kind, data}</c> bound to <paramref name="itemId"/>. <paramref name="dataOptions"/> serializes the
    /// model (defaults to <see cref="ProtocolJson.Options"/>).
    /// </summary>
    public static byte[] EncryptItem<T>(ReadOnlySpan<byte> vaultKey, string itemId, string kind, T data, JsonSerializerOptions? dataOptions = null)
    {
        var payload = new VaultItemPayload(kind, JsonSerializer.SerializeToElement(data, dataOptions ?? ProtocolJson.Options));
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, ProtocolJson.Options);
        try
        {
            return Seal(vaultKey, plaintext, ItemAadPrefix + itemId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <exception cref="CryptographicException">Wrong key, wrong item id, or tampered data.</exception>
    public static VaultItemPayload DecryptItem(ReadOnlySpan<byte> vaultKey, string itemId, ReadOnlySpan<byte> sealedData)
    {
        byte[] plaintext = Open(vaultKey, sealedData, ItemAadPrefix + itemId);
        try
        {
            return JsonSerializer.Deserialize<VaultItemPayload>(plaintext, ProtocolJson.Options)
                ?? throw new JsonException("Empty vault item payload.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static byte[] Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, string aad)
    {
        CheckKey(key);
        var output = new byte[Overhead + plaintext.Length];
        output[0] = FormatVersion;
        Span<byte> nonce = output.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(1 + NonceSize, plaintext.Length), output.AsSpan(output.Length - TagSize), Encoding.UTF8.GetBytes(aad));
        return output;
    }

    /// <exception cref="CryptographicException">Unknown format, truncation, tampering, wrong key or wrong aad.</exception>
    public static byte[] Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> sealedData, string aad)
    {
        CheckKey(key);
        if (sealedData.Length < Overhead || sealedData[0] != FormatVersion)
            throw new CryptographicException("Unsupported or truncated ciphertext.");
        var plaintext = new byte[sealedData.Length - Overhead];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(sealedData.Slice(1, NonceSize), sealedData.Slice(1 + NonceSize, plaintext.Length), sealedData[^TagSize..], plaintext, Encoding.UTF8.GetBytes(aad));
        return plaintext;
    }

    private static void CheckKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
            throw new ArgumentException($"Key must be {KeySize} bytes.", nameof(key));
    }
}
