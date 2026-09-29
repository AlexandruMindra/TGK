using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TGK.Core.Models;

/// <summary>
/// Connection settings that can be set per host, per group and globally (<see cref="VaultData.Defaults"/>).
/// Every value is optional: null means "inherit", and the nearest non-null value wins (host, then group, then
/// global, then the built-in default; see <see cref="EffectiveOptions"/>). Lists are replaced, never merged.
/// </summary>
public sealed class HostOptions
{
    public const int MaxKeepAliveSeconds = 3600;
    public const int MaxConnectTimeoutSeconds = 600;
    public const float MinFontSize = 6;
    public const float MaxFontSize = 72;

    // ---- Connection ----

    /// <summary>
    /// Saved host to connect through (ProxyJump). Its own jump host is used in turn (see <see cref="EffectiveOptions.ResolveJumpChain"/>).
    /// <see cref="NoJumpHost"/> connects directly, overriding an inherited jump host.
    /// </summary>
    public Guid? JumpHostId { get; set; }

    /// <summary>The <see cref="JumpHostId"/> value for "connect directly" (it overrides an inherited jump host, unlike null).</summary>
    public static Guid NoJumpHost => Guid.Empty;

    /// <summary>Seconds between SSH keep-alive messages; 0 turns them off.</summary>
    public int? KeepAliveSeconds { get; set; }

    public int? ConnectTimeoutSeconds { get; set; }

    /// <summary>Reconnect automatically when an established session is lost (handled by the client).</summary>
    public bool? AutoReconnect { get; set; }

    // ---- Session ----

    /// <summary>Typed into the shell once it opens, followed by Enter. An empty string means "none" (and overrides an inherited command).</summary>
    public string? StartupCommand { get; set; }

    /// <summary>Variables sent as SSH "env" requests. Servers only accept names their configuration allows (OpenSSH: AcceptEnv).</summary>
    public List<EnvVar>? Environment { get; set; }

    /// <summary>The TERM value requested for the PTY, e.g. <c>xterm-256color</c>.</summary>
    public string? TerminalType { get; set; }

    // ---- Appearance & compatibility ----

    public float? FontSize { get; set; }

    /// <summary>Name of a built-in terminal color scheme.</summary>
    public string? ColorScheme { get; set; }

    /// <summary>Also offer the weak algorithms listed in <see cref="TGK.Core.Ssh.SshAlgorithms"/>, for old servers and devices.</summary>
    public bool? LegacyAlgorithms { get; set; }

    /// <summary>Members this version does not know (e.g. from a newer client), kept so that saving does not drop them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>True when nothing is set (everything is inherited).</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        JumpHostId is null && KeepAliveSeconds is null && ConnectTimeoutSeconds is null && AutoReconnect is null
        && StartupCommand is null && Environment is null && TerminalType is null
        && FontSize is null && ColorScheme is null && LegacyAlgorithms is null;

    public HostOptions Clone()
    {
        var copy = (HostOptions)MemberwiseClone();
        copy.Environment = Environment?.Select(e => e.Clone()).ToList();
        copy.Unknown = Unknown is null ? null : new(Unknown);
        return copy;
    }

    /// <summary>Returns a user-facing message for the first invalid value, or null when all are valid.</summary>
    public string? Validate()
    {
        if (KeepAliveSeconds is < 0 or > MaxKeepAliveSeconds)
            return $"The keep-alive interval must be between 0 (off) and {MaxKeepAliveSeconds} seconds.";
        if (ConnectTimeoutSeconds is < 1 or > MaxConnectTimeoutSeconds)
            return $"The connect timeout must be between 1 and {MaxConnectTimeoutSeconds} seconds.";
        if (Environment is not null)
        {
            foreach (EnvVar variable in Environment)
            {
                if (variable is null || !EnvVar.IsValidName(variable.Name))
                    return $"\"{variable?.Name}\" is not a valid environment variable name (letters, digits and _, not starting with a digit).";
            }
        }
        if (TerminalType is not null && (TerminalType.Length == 0 || TerminalType.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))))
            return "The terminal type must be a single word, e.g. xterm-256color.";
        if (FontSize is { } size && (float.IsNaN(size) || size < MinFontSize || size > MaxFontSize))
            return $"The font size must be between {MinFontSize} and {MaxFontSize}.";
        if (ColorScheme is not null && string.IsNullOrWhiteSpace(ColorScheme))
            return "Choose a color scheme.";
        return null;
    }
}

/// <summary>An environment variable for the remote session.</summary>
public sealed partial class EnvVar
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";

    /// <summary>Members this version does not know (e.g. from a newer client), kept so that saving does not drop them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    public EnvVar Clone()
    {
        var copy = (EnvVar)MemberwiseClone();
        copy.Unknown = Unknown is null ? null : new(Unknown);
        return copy;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NamePattern();
}

public enum ForwardKind
{
    /// <summary><c>-L</c>: a local port that leads to a destination reached from the server.</summary>
    Local,

    /// <summary><c>-R</c>: a port on the server that leads to a destination reached from this computer.</summary>
    Remote,

    /// <summary><c>-D</c>: a local SOCKS proxy whose connections leave from the server.</summary>
    Dynamic,
}

/// <summary>A port forwarding (tunnel) of one host, started with each session. Not inherited.</summary>
public sealed class PortForward
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ForwardKind Kind { get; set; }

    /// <summary>Where the listening port is opened: on this computer (Local, Dynamic) or on the server (Remote).</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    public int BindPort { get; set; }

    /// <summary>Target of a Local or Remote forward; unused for Dynamic.</summary>
    public string? DestinationHost { get; set; }

    public int? DestinationPort { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Description { get; set; }

    /// <summary>Members this version does not know (e.g. from a newer client), kept so that saving does not drop them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>
    /// A Local or Dynamic forward listening on an address other than loopback: other computers on the network can use
    /// it (a Dynamic one is then an open proxy into the server's network).
    /// </summary>
    [JsonIgnore]
    public bool IsOpenToNetwork => Kind != ForwardKind.Remote && !IsLoopback(BindAddress.Trim());

    private static bool IsLoopback(string address) =>
        string.Equals(address, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(address.Trim('[', ']'), out IPAddress? ip) && IPAddress.IsLoopback(ip));

    public PortForward Clone()
    {
        var copy = (PortForward)MemberwiseClone();
        copy.Unknown = Unknown is null ? null : new(Unknown);
        return copy;
    }

    /// <summary>Returns a user-facing message for the first problem, or null when the forward is valid.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(BindAddress) || BindAddress.Any(char.IsWhiteSpace))
            return "Enter the address to listen on, e.g. 127.0.0.1.";
        if (BindPort is < 1 or > 65535)
            return "The listening port must be between 1 and 65535.";
        if (Kind == ForwardKind.Dynamic)
            return null;
        if (string.IsNullOrWhiteSpace(DestinationHost) || DestinationHost.Any(char.IsWhiteSpace))
            return "Enter the destination host.";
        if (DestinationPort is not (>= 1 and <= 65535))
            return "The destination port must be between 1 and 65535.";
        return null;
    }

    /// <summary>OpenSSH-style summary, e.g. <c>L 127.0.0.1:8080 → db:5432</c> or <c>D 127.0.0.1:1080</c>.</summary>
    public override string ToString() => Kind switch
    {
        ForwardKind.Local => $"L {BindAddress}:{BindPort} → {DestinationHost}:{DestinationPort}",
        ForwardKind.Remote => $"R {BindAddress}:{BindPort} → {DestinationHost}:{DestinationPort}",
        _ => $"D {BindAddress}:{BindPort}",
    };
}
