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
    public const int MaxScrollbackLines = 1_000_000;
    public const int MaxFontFamilyLength = 128;
    public const int MaxAgentRules = 100;
    public const int MaxAgentRuleLength = 300;

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

    /// <summary>Also offer the weak algorithms listed in <see cref="TGK.Core.Ssh.SshAlgorithms"/>, for old servers and devices.</summary>
    public bool? LegacyAlgorithms { get; set; }

    // ---- Appearance ----

    public float? FontSize { get; set; }

    /// <summary>
    /// Terminal font family: the bundled DejaVu Sans Mono or a monospace font installed on the device. A device
    /// without it uses the bundled font.
    /// </summary>
    public string? FontFamily { get; set; }

    /// <summary>Name of a built-in terminal color scheme.</summary>
    public string? ColorScheme { get; set; }

    // ---- Terminal behaviour (edited in Settings → Terminal, so normally only set in the vault defaults) ----

    /// <summary>Lines kept above the screen; applies to sessions started afterwards.</summary>
    public int? ScrollbackLines { get; set; }

    /// <summary>One of <see cref="TerminalSettings.CursorBlock"/>, <see cref="TerminalSettings.CursorBar"/>, <see cref="TerminalSettings.CursorUnderline"/>.</summary>
    public string? CursorShape { get; set; }

    public bool? CursorBlink { get; set; }

    public bool? CopyOnSelect { get; set; }

    // ---- Agents (MCP) ----

    /// <summary>What local agents (MCP clients connected through TGK) may do on the host; built-in default: <see cref="Models.AgentAccess.Off"/>.</summary>
    public AgentAccess? AgentAccess { get; set; }

    /// <summary>
    /// Command prefixes agents may run without asking (e.g. <c>git status</c>, <c>systemctl status</c>); with
    /// <see cref="Models.AgentAccess.ReadOnly"/> the only commands besides the built-in read-only ones.
    /// </summary>
    public List<string>? AgentCommands { get; set; }

    /// <summary>
    /// Paths (globs: <c>*</c>, <c>**</c>, <c>~/</c>) that agents may only read or change with approval, in addition to
    /// the built-in ones (<c>~/.ssh/**</c>, private keys…); with <see cref="Models.AgentAccess.ReadOnly"/> they are refused.
    /// </summary>
    public List<string>? AgentProtectedPaths { get; set; }

    /// <summary>Members this version does not know (e.g. from a newer client), kept so that saving does not drop them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>True when nothing is set (everything is inherited).</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        JumpHostId is null && KeepAliveSeconds is null && ConnectTimeoutSeconds is null && AutoReconnect is null
        && StartupCommand is null && Environment is null && TerminalType is null
        && FontSize is null && FontFamily is null && ColorScheme is null && LegacyAlgorithms is null
        && ScrollbackLines is null && CursorShape is null && CursorBlink is null && CopyOnSelect is null
        && AgentAccess is null && AgentCommands is null && AgentProtectedPaths is null;

    /// <summary>
    /// <paramref name="defaults"/> with the terminal settings an older version kept on this device only
    /// (<paramref name="local"/>), or null when there is nothing to move: the defaults already set terminal values
    /// (another device moved its own first) or the local ones are the built-in values.
    /// </summary>
    public static HostOptions? WithLocalTerminal(HostOptions defaults, TerminalSettings local)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(local);
        if (defaults.FontSize is not null || defaults.FontFamily is not null || defaults.ScrollbackLines is not null
            || defaults.CursorShape is not null || defaults.CursorBlink is not null || defaults.CopyOnSelect is not null)
            return null;
        var builtIn = new TerminalSettings();
        HostOptions result = defaults.Clone();
        if (Math.Abs(local.FontSize - builtIn.FontSize) > 0.01f && local.FontSize is >= MinFontSize and <= MaxFontSize)
            result.FontSize = local.FontSize;
        if (local.ScrollbackLines != builtIn.ScrollbackLines && local.ScrollbackLines is >= 0 and <= MaxScrollbackLines)
            result.ScrollbackLines = local.ScrollbackLines;
        if (local.CursorShape != builtIn.CursorShape && local.CursorShape is TerminalSettings.CursorBar or TerminalSettings.CursorUnderline)
            result.CursorShape = local.CursorShape;
        if (local.CursorBlink != builtIn.CursorBlink)
            result.CursorBlink = local.CursorBlink;
        if (local.CopyOnSelect != builtIn.CopyOnSelect)
            result.CopyOnSelect = local.CopyOnSelect;
        return JsonSerializer.Serialize(result) == JsonSerializer.Serialize(defaults) ? null : result;
    }

    public HostOptions Clone()
    {
        var copy = (HostOptions)MemberwiseClone();
        copy.Environment = Environment?.Select(e => e.Clone()).ToList();
        copy.AgentCommands = AgentCommands is null ? null : [.. AgentCommands];
        copy.AgentProtectedPaths = AgentProtectedPaths is null ? null : [.. AgentProtectedPaths];
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
        if (FontFamily is not null && (string.IsNullOrWhiteSpace(FontFamily) || FontFamily.Length > MaxFontFamilyLength || FontFamily.Any(char.IsControl)))
            return "Choose a font.";
        if (ScrollbackLines is < 0 or > MaxScrollbackLines)
            return $"The scrollback must be between 0 and {MaxScrollbackLines:N0} lines.";
        if (CursorShape is not null and not (TerminalSettings.CursorBlock or TerminalSettings.CursorBar or TerminalSettings.CursorUnderline))
            return "Choose a cursor style.";
        if (AgentAccess is { } access && !Enum.IsDefined(access))
            return "Choose what agents may do.";
        if (ValidateRules(AgentCommands, "allowed command") is { } commands)
            return commands;
        if (ValidateRules(AgentProtectedPaths, "protected path") is { } paths)
            return paths;
        return null;
    }

    private static string? ValidateRules(List<string>? rules, string what)
    {
        if (rules is null)
            return null;
        if (rules.Count > MaxAgentRules)
            return $"At most {MaxAgentRules} {what}s.";
        foreach (string rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule))
                return $"An {what} is empty.";
            if (rule.Length > MaxAgentRuleLength)
                return $"An {what} is longer than {MaxAgentRuleLength} characters.";
            if (rule.Any(char.IsControl))
                return $"The {what} \"{rule}\" contains a line break or control character.";
        }
        return null;
    }
}

/// <summary>What local agents (MCP clients) may do on a host. See docs/AGENTS.md.</summary>
public enum AgentAccess
{
    /// <summary>Nothing: the host is not shown to agents.</summary>
    Off,

    /// <summary>Read files and run read-only commands (built-in ones and the allowed commands); never change anything.</summary>
    ReadOnly,

    /// <summary>Reads are free; writes and other commands need the user's approval (allowed commands excepted).</summary>
    Ask,

    /// <summary>Everything without asking, except protected paths.</summary>
    Full,
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
