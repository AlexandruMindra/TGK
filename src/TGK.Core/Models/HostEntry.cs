using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TGK.Core.Models;

/// <summary>A saved SSH host.</summary>
public sealed class HostEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;

    /// <summary>Login name; when null the linked identity's username is used.</summary>
    public string? Username { get; set; }

    public Guid? IdentityId { get; set; }
    public Guid? GroupId { get; set; }

    /// <summary>Accent color as <c>#RRGGBB</c>, or null for none.</summary>
    public string? TagColor { get; set; }

    public string? Notes { get; set; }
    public bool Favorite { get; set; }
    public DateTimeOffset? LastConnected { get; set; }

    /// <summary>This host's own settings; unset values are inherited from its group and the vault defaults.</summary>
    public HostOptions Options { get => _options; set => _options = value ?? new(); }

    /// <summary>Port forwardings started with every session to this host (not inherited).</summary>
    public List<PortForward> Tunnels { get => _tunnels; set => _tunnels = value ?? []; }

    private HostOptions _options = new();
    private List<PortForward> _tunnels = [];

    /// <summary>Members this version does not know (e.g. from a newer client), kept so that saving does not drop them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>Name to show in the UI: <see cref="Name"/>, falling back to the address.</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Host : Name;

    public HostEntry Clone()
    {
        var copy = (HostEntry)MemberwiseClone();
        copy._options = _options.Clone();
        copy._tunnels = _tunnels.Select(t => t.Clone()).ToList();
        copy.Unknown = Unknown is null ? null : new(Unknown);
        return copy;
    }
}
