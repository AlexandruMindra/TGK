using System;
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

    /// <summary>Name to show in the UI: <see cref="Name"/>, falling back to the address.</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Host : Name;

    public HostEntry Clone() => (HostEntry)MemberwiseClone();
}
