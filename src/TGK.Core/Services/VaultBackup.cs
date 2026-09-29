using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using TGK.Core.Models;
using TGK.Protocol;
using TGK.Protocol.Dtos;

namespace TGK.Core.Services;

/// <summary>
/// The encrypted backup file (<c>*.tgkbackup</c>), JSON:
/// <c>{format: "tgk-backup", version: 1, createdAt, kdf, salt, wrappedVaultKey, manifest, items: [{id, data}]}</c>.
/// A fresh backup key seals the items exactly like a vault key does (same codec and ids); it is wrapped with a key
/// derived from the backup password like an account's vault key. The manifest (sealed with the backup key) holds a
/// hash of the item ids in order, so removing, adding or reordering items is detected too.
/// </summary>
internal static class VaultBackup
{
    public const string FormatName = "tgk-backup";
    public const int Version = 1;

    private const string ManifestAad = "tgk/backup-manifest/v1";

    // Far above any real vault (the server's default quota is 50,000 items and 64 MB); a file past these is refused
    // before it is parsed or its password is tried.
    private const long MaxFileBytes = 128L * 1024 * 1024;
    private const int MaxItems = 50_000;

    /// <summary>Writes <paramref name="data"/> to <paramref name="path"/> (atomically).</summary>
    /// <exception cref="VaultException"><see cref="VaultError.Storage"/>: the file could not be written.</exception>
    public static void Write(string path, VaultData data, string password, KdfParams kdf)
    {
        byte[] salt = VaultCrypto.NewSalt();
        byte[] key = VaultCrypto.NewVaultKey();
        byte[] kek = VaultCrypto.DeriveKeys(password, salt, kdf).Kek;
        try
        {
            var codec = new VaultItems(key);
            var items = new List<BackupItem?>();
            foreach (object entity in LocalVaultService.Entities(data))
            {
                string id = codec.IdOf(entity);
                items.Add(new BackupItem(id, codec.Encrypt(id, entity)));
            }
            byte[] manifest = VaultCrypto.Seal(key, ManifestOf(items), ManifestAad);
            TgkJson.WriteFileAtomic(path, new BackupFile(FormatName, Version, DateTimeOffset.UtcNow, kdf, salt, VaultCrypto.WrapVaultKey(kek, key), manifest, items));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new VaultException(VaultError.Storage, $"Could not write the backup: {ex.Message}", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>Reads and decrypts a backup; items of a kind this version does not know are skipped and counted.</summary>
    /// <exception cref="VaultException">
    /// <see cref="VaultError.InvalidCredentials"/>: wrong password; <see cref="VaultError.Storage"/>: unreadable, not a
    /// backup, too large, from a newer version, or damaged/modified.
    /// </exception>
    public static (VaultData Data, int Unknown) Read(string path, string password)
    {
        BackupFile? file;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                throw new VaultException(VaultError.Storage, "The backup file does not exist.");
            if (info.Length > MaxFileBytes)
                throw new VaultException(VaultError.Storage, "This file is too large to be a TGK backup.");
            file = TgkJson.ReadFile<BackupFile>(path);
        }
        catch (JsonException)
        {
            file = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new VaultException(VaultError.Storage, $"Could not read the backup: {ex.Message}", ex);
        }
        if (file?.Format != FormatName || file.Kdf is null || file.Salt is null || file.WrappedVaultKey is null || file.Manifest is null || file.Items is null)
            throw new VaultException(VaultError.Storage, "This is not a TGK backup file.");
        if (file.Version != Version)
            throw new VaultException(VaultError.Storage, $"This backup has format version {file.Version}; this TGK reads version {Version}.");
        if (file.Salt.Length != VaultCrypto.SaltSize || !file.Kdf.IsAcceptable())
            throw new VaultException(VaultError.Storage, "The backup asks for unsafe password-hashing parameters.");
        if (file.Items.Count > MaxItems)
            throw new VaultException(VaultError.Storage, $"This backup has more than {MaxItems} items.");

        byte[] kek = VaultCrypto.DeriveKeys(password ?? "", file.Salt, file.Kdf).Kek;
        byte[] key;
        try
        {
            key = VaultCrypto.UnwrapVaultKey(kek, file.WrappedVaultKey);
        }
        catch (CryptographicException)
        {
            throw new VaultException(VaultError.InvalidCredentials, "Wrong backup password (or the file is damaged).");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }

        try
        {
            if (!CryptographicOperations.FixedTimeEquals(VaultCrypto.Open(key, file.Manifest, ManifestAad), ManifestOf(file.Items)))
                throw new CryptographicException("The items do not match the manifest.");
            var codec = new VaultItems(key);
            var data = new VaultData();
            int unknown = 0;
            foreach (BackupItem? item in file.Items)
            {
                if (item?.Id is null || item.Data is null || item.Data.Length > ProtocolConstants.MaxItemBytes)
                    throw new CryptographicException("Missing or oversized item.");
                try
                {
                    codec.Apply(data, item.Id, item.Data);
                }
                catch (JsonException ex)
                {
                    CoreLog.Warn($"Skipping backup item {item.Id}: {ex.Message}");
                    unknown++;
                }
            }
            return (data, unknown);
        }
        catch (CryptographicException)
        {
            // Authenticated encryption: any change to an item, moving it to another id, or to the list of items is detected.
            throw new VaultException(VaultError.Storage, "The backup file is damaged or was modified.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>SHA-256 of the item ids in file order (as a JSON array, so no two lists share an encoding).</summary>
    private static byte[] ManifestOf(List<BackupItem?> items) =>
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(items.ConvertAll(i => i?.Id)));

    private sealed record BackupFile(string? Format, int Version, DateTimeOffset CreatedAt, KdfParams? Kdf, byte[]? Salt, byte[]? WrappedVaultKey,
        byte[]? Manifest, List<BackupItem?>? Items);

    private sealed record BackupItem(string? Id, byte[]? Data);
}
