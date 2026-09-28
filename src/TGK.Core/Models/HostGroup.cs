using System;

namespace TGK.Core.Models;

/// <summary>A folder in the sidebar that groups saved hosts.</summary>
public sealed class HostGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public bool Collapsed { get; set; }

    public HostGroup Clone() => (HostGroup)MemberwiseClone();
}
