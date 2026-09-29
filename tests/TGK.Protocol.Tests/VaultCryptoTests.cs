using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TGK.Protocol;
using TGK.Protocol.Dtos;
using Xunit;

namespace TGK.Protocol.Tests;

public class VaultCryptoTests
{
    private static readonly KdfParams FastKdf = new(KdfParams.Pbkdf2Sha256, 1000);

    [Fact]
    public void SealOpen_RoundTripsWithExpectedLayout()
    {
        byte[] key = VaultCrypto.NewVaultKey();
        byte[] plain = Encoding.UTF8.GetBytes("secret payload");

        byte[] sealedData = VaultCrypto.Seal(key, plain, "aad");

        Assert.Equal(0x01, sealedData[0]);
        Assert.Equal(1 + 12 + plain.Length + 16, sealedData.Length);
        Assert.Equal(plain, VaultCrypto.Open(key, sealedData, "aad"));
        Assert.NotEqual(sealedData, VaultCrypto.Seal(key, plain, "aad")); // fresh nonce each time
        Assert.Empty(VaultCrypto.Open(key, VaultCrypto.Seal(key, [], "aad"), "aad"));
    }

    [Theory]
    [InlineData(0)]   // version byte
    [InlineData(5)]   // nonce
    [InlineData(14)]  // ciphertext
    [InlineData(-1)]  // tag
    public void Open_RejectsTampering(int index)
    {
        byte[] key = VaultCrypto.NewVaultKey();
        byte[] sealedData = VaultCrypto.Seal(key, Encoding.UTF8.GetBytes("secret payload"), "aad");
        sealedData[index < 0 ? sealedData.Length + index : index] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Open(key, sealedData, "aad"));
    }

    [Fact]
    public void Open_RejectsWrongKeyAadAndTruncation()
    {
        byte[] key = VaultCrypto.NewVaultKey();
        byte[] sealedData = VaultCrypto.Seal(key, Encoding.UTF8.GetBytes("secret payload"), "aad");

        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Open(VaultCrypto.NewVaultKey(), sealedData, "aad"));
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Open(key, sealedData, "other"));
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Open(key, sealedData.AsSpan(0, 28), "aad"));
        Assert.Throws<ArgumentException>(() => VaultCrypto.Open(new byte[16], sealedData, "aad"));
    }

    [Fact]
    public void WrapUnwrap_RoundTripsAndNeedsTheRightKek()
    {
        byte[] kek = VaultCrypto.NewVaultKey();
        byte[] vaultKey = VaultCrypto.NewVaultKey();
        byte[] wrapped = VaultCrypto.WrapVaultKey(kek, vaultKey);

        Assert.Equal(vaultKey, VaultCrypto.UnwrapVaultKey(kek, wrapped));
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.UnwrapVaultKey(VaultCrypto.NewVaultKey(), wrapped));
        // Bound to its purpose: an item sealed with the same key cannot pass as a wrapped vault key.
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.UnwrapVaultKey(kek, VaultCrypto.Seal(kek, vaultKey, "tgk/item/v1:x")));
    }

    [Fact]
    public void DeriveKeys_IsDeterministicAndSeparatesKeys()
    {
        byte[] salt = VaultCrypto.NewSalt();
        var (authKey, kek) = VaultCrypto.DeriveKeys("correct horse", salt, FastKdf);
        var (authKey2, kek2) = VaultCrypto.DeriveKeys("correct horse", salt, FastKdf);

        Assert.Equal(32, authKey.Length);
        Assert.Equal(32, kek.Length);
        Assert.Equal(authKey, authKey2);
        Assert.Equal(kek, kek2);
        Assert.NotEqual(authKey, kek);
        Assert.NotEqual(authKey, VaultCrypto.DeriveKeys("correct horse", VaultCrypto.NewSalt(), FastKdf).AuthKey);
        Assert.NotEqual(authKey, VaultCrypto.DeriveKeys("correct horsf", salt, FastKdf).AuthKey);
        Assert.NotEqual(authKey, VaultCrypto.DeriveKeys("correct horse", salt, FastKdf with { Iterations = 1001 }).AuthKey);
    }

    [Fact]
    public void DeriveKeys_MatchesSpecConstruction()
    {
        byte[] salt = new byte[16];
        byte[] master = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes("pw"), salt, 1000, HashAlgorithmName.SHA256, 32);
        var (authKey, kek) = VaultCrypto.DeriveKeys("pw", salt, FastKdf);

        Assert.Equal(HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.UTF8.GetBytes("tgk/auth/v1")), authKey);
        Assert.Equal(HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.UTF8.GetBytes("tgk/kek/v1")), kek);
    }

    [Fact]
    public void DeriveKeys_NormalizesPassword()
    {
        byte[] salt = VaultCrypto.NewSalt();
        // Precomposed vs decomposed "Å" normalize to the same NFKC string.
        Assert.Equal(VaultCrypto.DeriveKeys("Ångström", salt, FastKdf).AuthKey, VaultCrypto.DeriveKeys("Ångström", salt, FastKdf).AuthKey);
    }

    [Fact]
    public void DeriveKeys_RejectsBadParameters()
    {
        Assert.Throws<NotSupportedException>(() => VaultCrypto.DeriveKeys("pw", VaultCrypto.NewSalt(), new KdfParams("argon2id", 3)));
        Assert.Throws<ArgumentException>(() => VaultCrypto.DeriveKeys("pw", new byte[8], FastKdf));
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => VaultCrypto.DeriveKeys("pw", VaultCrypto.NewSalt(), FastKdf with { Iterations = 0 }));
    }

    [Fact]
    public void EncryptDecryptItem_RoundTripsAndIsBoundToId()
    {
        byte[] vaultKey = VaultCrypto.NewVaultKey();
        byte[] sealedData = VaultCrypto.EncryptItem(vaultKey, "id-1", ItemKinds.Host, new { Name = "web", Port = 22 });

        VaultItemPayload payload = VaultCrypto.DecryptItem(vaultKey, "id-1", sealedData);

        Assert.Equal(ItemKinds.Host, payload.Kind);
        Assert.Equal("web", payload.Data.GetProperty("name").GetString());
        Assert.Equal(22, payload.Data.GetProperty("port").GetInt32());
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.DecryptItem(vaultKey, "id-2", sealedData));
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.DecryptItem(VaultCrypto.NewVaultKey(), "id-1", sealedData));
    }

    [Fact]
    public void EncryptItem_UsesCallerSerializerOptions()
    {
        byte[] vaultKey = VaultCrypto.NewVaultKey();
        var pascal = new JsonSerializerOptions { PropertyNamingPolicy = null };
        byte[] sealedData = VaultCrypto.EncryptItem(vaultKey, "g", ItemKinds.Group, new { Name = "Prod" }, pascal);

        Assert.Equal("Prod", VaultCrypto.DecryptItem(vaultKey, "g", sealedData).Data.GetProperty("Name").GetString());
    }

    [Fact]
    public void KdfParams_Acceptability()
    {
        Assert.True(KdfParams.Default.IsAcceptable());
        Assert.False(FastKdf.IsAcceptable());
        Assert.False(new KdfParams("argon2id", KdfParams.DefaultIterations).IsAcceptable());
        Assert.False((KdfParams.Default with { Iterations = KdfParams.MaxIterations + 1 }).IsAcceptable());
    }
}
