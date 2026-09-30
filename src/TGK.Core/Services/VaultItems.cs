using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TGK.Core.Models;
using TGK.Protocol;

namespace TGK.Core.Services;

/// <summary>A pending upsert (<see cref="Entity"/> set) or delete of one vault item.</summary>
internal readonly record struct ItemChange(string Id, object? Entity);

/// <summary>
/// Maps the vault's entities to server items for one vault key: each group, host, identity and known host is one
/// item, keyed by its Id (lowercase "D" Guid; known hosts use a Guid keyed from host and port) and sealed with the key.
/// The vault defaults (<see cref="VaultData.Defaults"/>) are the one "settings" item and the saved tabs
/// (<see cref="VaultData.Workspace"/>) the one "workspace" item; their ids are derived from the key.
/// </summary>
/// <remarks>
/// Hosts are synced without <see cref="HostEntry.LastConnected"/>: when a host was last used is kept per device, so
/// connecting never re-uploads a (possibly stale) host over edits made on other devices.
/// </remarks>
internal sealed class VaultItems(byte[] vaultKey)
{
    private static readonly byte[] KnownHostIdInfo = "tgk/knownhost-id/v1"u8.ToArray();
    private static readonly byte[] SettingsIdInfo = "tgk/settings-id/v1"u8.ToArray();
    private static readonly byte[] WorkspaceIdInfo = "tgk/workspace-id/v1"u8.ToArray();

    // Known-host ids are an HMAC under a key only the client has: an unkeyed hash of "host:port" would let the
    // server recover every SSH destination by hashing guesses.
    private readonly byte[] _knownHostIdKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, vaultKey, 32, info: KnownHostIdInfo);

    /// <summary>Id of the settings item: fixed per vault, and like the known-host ids it tells the server nothing.</summary>
    public string SettingsId { get; } = new Guid(HKDF.DeriveKey(HashAlgorithmName.SHA256, vaultKey, 16, info: SettingsIdInfo)).ToString("D");

    /// <summary>Id of the workspace item, derived like <see cref="SettingsId"/>.</summary>
    public string WorkspaceId { get; } = new Guid(HKDF.DeriveKey(HashAlgorithmName.SHA256, vaultKey, 16, info: WorkspaceIdInfo)).ToString("D");

    public static string IdOf(Guid id) => id.ToString("D");

    /// <summary>Stable id for the known-host entry of <paramref name="host"/>:<paramref name="port"/> (host is case-insensitive).</summary>
    public string KnownHostId(string host, int port)
    {
        byte[] mac = HMACSHA256.HashData(_knownHostIdKey, Encoding.UTF8.GetBytes($"{host.ToLowerInvariant()}:{port}"));
        return new Guid(mac.AsSpan(0, 16)).ToString("D");
    }

    public string IdOf(object entity) => entity switch
    {
        HostGroup g => IdOf(g.Id),
        HostEntry h => IdOf(h.Id),
        Identity i => IdOf(i.Id),
        KnownHost k => KnownHostId(k.Host, k.Port),
        HostOptions => SettingsId,
        Workspace => WorkspaceId,
        _ => throw new ArgumentException($"Not a vault entity: {entity.GetType().Name}", nameof(entity)),
    };

    public byte[] Encrypt(string id, object entity) => entity switch
    {
        HostGroup g => VaultCrypto.EncryptItem(vaultKey, id, ItemKinds.Group, g, TgkJson.Options),
        HostEntry h => VaultCrypto.EncryptItem(vaultKey, id, ItemKinds.Host, h.LastConnected is null ? h : WithoutLastConnected(h), TgkJson.Options),
        Identity i => VaultCrypto.EncryptItem(vaultKey, id, ItemKinds.Identity, i, TgkJson.Options),
        KnownHost k => VaultCrypto.EncryptItem(vaultKey, id, ItemKinds.KnownHost, k, TgkJson.Options),
        HostOptions defaults => VaultCrypto.EncryptItem(vaultKey, id, ItemKinds.Settings, new SettingsItem { Defaults = defaults }, TgkJson.Options),
        Workspace workspace => VaultCrypto.EncryptItem(vaultKey, id, ItemKinds.Workspace, workspace, TgkJson.Options),
        _ => throw new ArgumentException($"Not a vault entity: {entity.GetType().Name}", nameof(entity)),
    };

    /// <summary>
    /// Applies a server item to <paramref name="vault"/> (null <paramref name="data"/> = deleted) and returns the new
    /// entity (null for a delete). Entities are replaced, never modified, so older snapshots sharing them stay intact.
    /// </summary>
    /// <exception cref="CryptographicException">Wrong key or tampered item.</exception>
    /// <exception cref="JsonException">
    /// Unknown kind (e.g. from a newer client: callers skip the item, as every client version has) or malformed content.
    /// </exception>
    public object? Apply(VaultData vault, string id, byte[]? data)
    {
        if (data is null)
        {
            Remove(vault, id);
            return null;
        }

        VaultItemPayload payload = VaultCrypto.DecryptItem(vaultKey, id, data);
        switch (payload.Kind)
        {
            case ItemKinds.Group:
                HostGroup group = Read<HostGroup>(payload);
                group.Id = ParseId(id);
                Upsert(vault.Groups, group, g => g.Id == group.Id);
                return group;
            case ItemKinds.Host:
                HostEntry host = Read<HostEntry>(payload);
                host.Id = ParseId(id);
                host.LastConnected = null;
                Upsert(vault.Hosts, host, h => h.Id == host.Id);
                return host;
            case ItemKinds.Identity:
                Identity identity = Read<Identity>(payload);
                identity.Id = ParseId(id);
                Upsert(vault.Identities, identity, i => i.Id == identity.Id);
                return identity;
            case ItemKinds.KnownHost:
                KnownHost known = Read<KnownHost>(payload);
                if (KnownHostId(known.Host, known.Port) != id)
                    throw new JsonException("Known host does not match its item id.");
                Upsert(vault.KnownHosts, known, k => k.Matches(known.Host, known.Port));
                return known;
            case ItemKinds.Settings:
                if (id != SettingsId)
                    throw new JsonException("Settings item does not match its id.");
                vault.Defaults = Read<SettingsItem>(payload).Defaults ?? new HostOptions();
                return vault.Defaults;
            case ItemKinds.Workspace:
                if (id != WorkspaceId)
                    throw new JsonException("Workspace item does not match its id.");
                Workspace workspace = Read<Workspace>(payload);
                if (workspace.Validate() is { } problem)
                    throw new JsonException($"Invalid workspace: {problem}");
                vault.Workspace = workspace;
                return workspace;
            default:
                throw new JsonException($"Unknown item kind '{payload.Kind}'.");
        }
    }

    /// <summary>The items that differ between two snapshots, compared by reference (edits replace entities).</summary>
    public List<ItemChange> Diff(VaultData before, VaultData after)
    {
        var changes = new List<ItemChange>();
        DiffList(before.Groups, after.Groups, changes);
        DiffList(before.Hosts, after.Hosts, changes);
        DiffList(before.Identities, after.Identities, changes);
        DiffList(before.KnownHosts, after.KnownHosts, changes);
        if (!ReferenceEquals(before.Defaults, after.Defaults))
            changes.Add(new ItemChange(SettingsId, after.Defaults));
        if (!ReferenceEquals(before.Workspace, after.Workspace))
            changes.Add(new ItemChange(WorkspaceId, after.Workspace));
        return changes;
    }

    public static void Upsert<T>(List<T> list, T item, Predicate<T> match)
    {
        int index = list.FindIndex(match);
        if (index >= 0)
            list[index] = item;
        else
            list.Add(item);
    }

    private void DiffList<T>(List<T> before, List<T> after, List<ItemChange> changes) where T : class
    {
        var old = new Dictionary<string, T>(before.Count);
        foreach (T item in before)
            old[IdOf(item)] = item;
        foreach (T item in after)
        {
            string id = IdOf(item);
            if (!old.Remove(id, out T? previous) || !ReferenceEquals(previous, item))
                changes.Add(new ItemChange(id, item));
        }
        foreach (string id in old.Keys)
            changes.Add(new ItemChange(id, null));
    }

    private void Remove(VaultData vault, string id)
    {
        vault.Groups.RemoveAll(g => IdOf(g.Id) == id);
        vault.Hosts.RemoveAll(h => IdOf(h.Id) == id);
        vault.Identities.RemoveAll(i => IdOf(i.Id) == id);
        vault.KnownHosts.RemoveAll(k => KnownHostId(k.Host, k.Port) == id);
        if (id == SettingsId)
            vault.Defaults = new HostOptions();
        if (id == WorkspaceId)
            vault.Workspace = new Workspace();
    }

    private static HostEntry WithoutLastConnected(HostEntry host)
    {
        HostEntry copy = host.Clone();
        copy.LastConnected = null;
        return copy;
    }

    private static T Read<T>(VaultItemPayload payload) =>
        payload.Data.Deserialize<T>(TgkJson.Options) ?? throw new JsonException("Empty item.");

    /// <summary>Payload of the settings item; room for more synced settings besides the defaults.</summary>
    private sealed class SettingsItem
    {
        public HostOptions? Defaults { get; set; }
    }

    private static Guid ParseId(string id) =>
        Guid.TryParseExact(id, "D", out Guid guid) ? guid : throw new JsonException($"Item id '{id}' is not a Guid.");
}
