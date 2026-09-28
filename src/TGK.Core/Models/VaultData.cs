using System;
using System.Collections.Generic;
using System.Linq;

namespace TGK.Core.Models;

/// <summary>Everything the server stores for one user; delivered to the client at login.</summary>
public sealed class VaultData
{
    public List<HostGroup> Groups { get; set; } = [];
    public List<HostEntry> Hosts { get; set; } = [];
    public List<Identity> Identities { get; set; } = [];
    public List<KnownHost> KnownHosts { get; set; } = [];

    /// <summary>Incremented on every change; lets the client and server detect stale copies.</summary>
    public long Revision { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public HostEntry? FindHost(Guid id) => Hosts.Find(h => h.Id == id);
    public HostGroup? FindGroup(Guid? id) => id is { } gid ? Groups.Find(g => g.Id == gid) : null;
    public Identity? FindIdentity(Guid? id) => id is { } iid ? Identities.Find(i => i.Id == iid) : null;

    public KnownHost? FindKnownHost(string host, int port) => KnownHosts.Find(k => k.Matches(host, port));

    /// <summary>Deep copy.</summary>
    public VaultData Clone() => new()
    {
        Groups = Groups.Select(g => g.Clone()).ToList(),
        Hosts = Hosts.Select(h => h.Clone()).ToList(),
        Identities = Identities.Select(i => i.Clone()).ToList(),
        KnownHosts = KnownHosts.Select(k => k.Clone()).ToList(),
        Revision = Revision,
        UpdatedAt = UpdatedAt,
    };
}
