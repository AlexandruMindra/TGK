using System;
using System.Collections.Generic;
using System.Text.Json;
using TGK.Core.Models;

namespace TGK.Core.Services;

/// <summary>
/// What merging a vault into another did, per item (the defaults count as one item). <see cref="Conflicts"/>: groups,
/// hosts and identities the target already had in another version, which it kept (the incoming version was dropped).
/// </summary>
public sealed record MergeCounts(int Added, int Conflicts, int Skipped)
{
    public static MergeCounts None { get; } = new(0, 0, 0);
}

/// <summary>
/// Merges another vault (a backup, or the local vault moving to an account) into the current one without ever
/// replacing what the current one has: groups, hosts and identities are matched by id and only new ones are added
/// (an existing one in another version counts as a conflict and is kept). The target's defaults win (the incoming
/// ones are used only when the target has none), and so do its known host keys (an incoming key is added only for a
/// host and port the target has no key for).
/// </summary>
internal static class VaultMerge
{
    private static readonly string UnsetOptionsJson = Json(new HostOptions());

    /// <summary>True for defaults that set nothing, i.e. a vault without a settings item.</summary>
    public static bool IsUnset(HostOptions options) => Json(options) == UnsetOptionsJson;

    /// <summary>Merges <paramref name="incoming"/> into <paramref name="target"/>, a snapshot copy being edited (entities are added, never modified).</summary>
    public static MergeCounts Merge(VaultData target, VaultData incoming)
    {
        int added = 0, conflicts = 0, skipped = 0;

        void ById<T>(List<T> into, List<T> from, Func<T, Guid> id, Func<T, T> copy) where T : class
        {
            var existing = new Dictionary<Guid, T>();
            foreach (T t in into)
                existing.TryAdd(id(t), t);
            foreach (T item in from)
            {
                if (!existing.TryGetValue(id(item), out T? current))
                {
                    T clone = copy(item);
                    into.Add(clone);
                    existing[id(item)] = clone;
                    added++;
                }
                else if (Content(current) == Content(item))
                    skipped++;
                else
                    conflicts++;
            }
        }

        ById(target.Groups, incoming.Groups, g => g.Id, g => g.Clone());
        ById(target.Identities, incoming.Identities, i => i.Id, i => i.Clone());
        ById(target.Hosts, incoming.Hosts, h => h.Id, h => h.Clone());

        foreach (KnownHost known in incoming.KnownHosts)
        {
            if (target.FindKnownHost(known.Host, known.Port) is not null)
            {
                skipped++;
                continue;
            }
            target.KnownHosts.Add(known.Clone());
            added++;
        }

        if (!IsUnset(incoming.Defaults))
        {
            if (IsUnset(target.Defaults))
            {
                target.Defaults = incoming.Defaults.Clone();
                added++;
            }
            else
                skipped++;
        }
        return new MergeCounts(added, conflicts, skipped);
    }

    /// <summary>What is synced of an entity: when this device last connected to a host is not part of it.</summary>
    private static string Content<T>(T entity) where T : class
    {
        if (entity is HostEntry { LastConnected: not null } host)
        {
            HostEntry copy = host.Clone();
            copy.LastConnected = null;
            return Json(copy);
        }
        return Json(entity);
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, TgkJson.Options);
}
