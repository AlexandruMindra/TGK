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

    /// <summary>
    /// Connection settings for every host (the global level of <see cref="HostOptions"/> inheritance). Synced as the
    /// vault's single "settings" item; a vault without one uses the built-in defaults.
    /// </summary>
    public HostOptions Defaults { get => _defaults; set => _defaults = value ?? new(); }

    private HostOptions _defaults = new();

    /// <summary>
    /// The "reopen my tabs" preference and the saved tabs and split views. Synced as the vault's single "workspace"
    /// item; a vault without one has the preference off.
    /// </summary>
    public Workspace Workspace { get => _workspace; set => _workspace = value ?? new(); }

    private Workspace _workspace = new();

    /// <summary>Incremented on every change; lets the client and server detect stale copies.</summary>
    public long Revision { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public HostEntry? FindHost(Guid id) => Hosts.Find(h => h.Id == id);
    public HostGroup? FindGroup(Guid? id) => id is { } gid ? Groups.Find(g => g.Id == gid) : null;
    public Identity? FindIdentity(Guid? id) => id is { } iid ? Identities.Find(i => i.Id == iid) : null;

    public KnownHost? FindKnownHost(string host, int port) => KnownHosts.Find(k => k.Matches(host, port));

    /// <summary>A copy that shares the entities; edit it by replacing entities, never by changing them.</summary>
    public VaultData ShallowCopy() => new()
    {
        Groups = [.. Groups],
        Hosts = [.. Hosts],
        Identities = [.. Identities],
        KnownHosts = [.. KnownHosts],
        Defaults = Defaults,
        Workspace = Workspace,
        Revision = Revision,
        UpdatedAt = UpdatedAt,
    };

    /// <summary>Deep copy.</summary>
    public VaultData Clone() => new()
    {
        Groups = Groups.Select(g => g.Clone()).ToList(),
        Hosts = Hosts.Select(h => h.Clone()).ToList(),
        Identities = Identities.Select(i => i.Clone()).ToList(),
        KnownHosts = KnownHosts.Select(k => k.Clone()).ToList(),
        Defaults = Defaults.Clone(),
        Workspace = Workspace.Clone(),
        Revision = Revision,
        UpdatedAt = UpdatedAt,
    };
}
