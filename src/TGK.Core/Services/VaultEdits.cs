using System;
using TGK.Core.Models;

namespace TGK.Core.Services;

/// <summary>
/// The vault mutations shared by the vault services: validate the input, then return an edit that REPLACES
/// entities in the lists of a snapshot copy (never changes them), so the remote service can find the changed
/// items by reference and older snapshots stay intact.
/// </summary>
internal static class VaultEdits
{
    public static Action<VaultData> SaveHost(HostEntry host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (string.IsNullOrWhiteSpace(host.Host))
            throw new ArgumentException("A host needs an address.", nameof(host));
        if (host.Port is < 1 or > 65535)
            throw new ArgumentException("Port must be between 1 and 65535.", nameof(host));
        if (host.Id == Guid.Empty)
            host.Id = Guid.NewGuid();
        CheckOptions(host.Options, nameof(host));
        if (host.Options.JumpHostId == host.Id)
            throw new ArgumentException("A host cannot be its own jump host.", nameof(host));
        foreach (PortForward tunnel in host.Tunnels)
        {
            if (tunnel.Validate() is { } problem)
                throw new ArgumentException($"Tunnel {tunnel}: {problem}", nameof(host));
        }

        HostEntry copy = host.Clone();
        return vault => VaultItems.Upsert(vault.Hosts, copy, h => h.Id == copy.Id);
    }

    public static Action<VaultData> DeleteHost(Guid hostId) => vault => vault.Hosts.RemoveAll(h => h.Id == hostId);

    public static Action<VaultData> SaveGroup(HostGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (string.IsNullOrWhiteSpace(group.Name))
            throw new ArgumentException("A group needs a name.", nameof(group));
        if (group.Id == Guid.Empty)
            group.Id = Guid.NewGuid();
        CheckOptions(group.Options, nameof(group));

        HostGroup copy = group.Clone();
        return vault => VaultItems.Upsert(vault.Groups, copy, g => g.Id == copy.Id);
    }

    /// <summary>
    /// Its hosts become ungrouped: a GroupId without a group reads as none. The hosts are left as they are, so no
    /// (possibly stale) copy of them is synced over edits made on other devices.
    /// </summary>
    public static Action<VaultData> DeleteGroup(Guid groupId) => vault => vault.Groups.RemoveAll(g => g.Id == groupId);

    public static Action<VaultData> SaveIdentity(Identity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.Name))
            throw new ArgumentException("An identity needs a name.", nameof(identity));
        if (identity.Id == Guid.Empty)
            identity.Id = Guid.NewGuid();

        Identity copy = identity.Clone();
        return vault => VaultItems.Upsert(vault.Identities, copy, i => i.Id == copy.Id);
    }

    /// <summary>Hosts that used it lose the link: like a deleted group, an IdentityId without an identity reads as none.</summary>
    public static Action<VaultData> DeleteIdentity(Guid identityId) => vault => vault.Identities.RemoveAll(i => i.Id == identityId);

    /// <summary>Replaces any previous key for the same host and port.</summary>
    public static Action<VaultData> AddKnownHost(KnownHost knownHost)
    {
        ArgumentNullException.ThrowIfNull(knownHost);
        if (string.IsNullOrWhiteSpace(knownHost.Host) || string.IsNullOrWhiteSpace(knownHost.FingerprintSha256))
            throw new ArgumentException("A known host needs a host name and fingerprint.", nameof(knownHost));

        KnownHost copy = knownHost.Clone();
        if (copy.AddedAt == default)
            copy.AddedAt = DateTimeOffset.UtcNow;
        return vault => VaultItems.Upsert(vault.KnownHosts, copy, k => k.Matches(copy.Host, copy.Port));
    }

    /// <summary>Replaces the vault defaults (the global level of <see cref="HostOptions"/> inheritance).</summary>
    public static Action<VaultData> SaveDefaults(HostOptions defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        CheckOptions(defaults, nameof(defaults));
        HostOptions copy = defaults.Clone();
        return vault => vault.Defaults = copy;
    }

    /// <summary>Replaces the workspace ("reopen my tabs" and the saved tabs); an invalid one is rejected.</summary>
    public static Action<VaultData> SaveWorkspace(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.Validate() is { } problem)
            throw new ArgumentException(problem, nameof(workspace));
        Workspace copy = workspace.Clone();
        return vault => vault.Workspace = copy;
    }

    public static Action<VaultData> TouchHost(Guid hostId, DateTimeOffset at) => vault => SetLastConnected(vault, hostId, at);

    private static void CheckOptions(HostOptions options, string paramName)
    {
        if (options.Validate() is { } problem)
            throw new ArgumentException(problem, paramName);
    }

    public static void SetLastConnected(VaultData vault, Guid hostId, DateTimeOffset? at)
    {
        int index = vault.Hosts.FindIndex(h => h.Id == hostId);
        if (index < 0 || vault.Hosts[index].LastConnected == at)
            return;
        HostEntry copy = vault.Hosts[index].Clone();
        copy.LastConnected = at;
        vault.Hosts[index] = copy;
    }
}
