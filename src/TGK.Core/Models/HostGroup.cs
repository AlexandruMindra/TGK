using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TGK.Core.Models;

/// <summary>A folder in the sidebar that groups saved hosts.</summary>
public sealed class HostGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public bool Collapsed { get; set; }

    /// <summary>Settings for the group's hosts; a host's own values win, unset values come from the vault defaults.</summary>
    public HostOptions Options { get => _options; set => _options = value ?? new(); }

    private HostOptions _options = new();

    /// <summary>Members this version does not know (e.g. from a newer client), kept so that saving does not drop them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    public HostGroup Clone()
    {
        var copy = (HostGroup)MemberwiseClone();
        copy._options = _options.Clone();
        copy.Unknown = Unknown is null ? null : new(Unknown);
        return copy;
    }
}
