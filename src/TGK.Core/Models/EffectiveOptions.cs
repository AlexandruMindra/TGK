using System;
using System.Collections.Generic;
using System.Linq;

namespace TGK.Core.Models;

/// <summary>Where an effective option value came from.</summary>
public enum OptionSource
{
    /// <summary>Nothing was set: the built-in default.</summary>
    Default,

    /// <summary>The vault defaults (<see cref="VaultData.Defaults"/>).</summary>
    Global,

    Group,
    Host,
}

/// <summary>An effective option value and the level that set it.</summary>
public readonly record struct Resolved<T>(T Value, OptionSource Source);

/// <summary>
/// The settings a connection to a host actually uses. Every value is set (<see cref="JumpHostId"/> is null for a
/// direct connection); each one says where it came from, e.g. to show "inherited from group Production".
/// </summary>
public sealed record EffectiveHostOptions(
    Resolved<Guid?> JumpHostId,
    Resolved<int> KeepAliveSeconds,
    Resolved<int> ConnectTimeoutSeconds,
    Resolved<bool> AutoReconnect,
    Resolved<string> StartupCommand,
    Resolved<IReadOnlyList<EnvVar>> Environment,
    Resolved<string> TerminalType,
    Resolved<float> FontSize,
    Resolved<string> ColorScheme,
    Resolved<bool> LegacyAlgorithms,
    Resolved<string> FontFamily,
    Resolved<int> ScrollbackLines,
    Resolved<string> CursorShape,
    Resolved<bool> CursorBlink,
    Resolved<bool> CopyOnSelect,
    Resolved<AgentAccess> AgentAccess,
    Resolved<IReadOnlyList<string>> AgentCommands,
    Resolved<IReadOnlyList<string>> AgentProtectedPaths)
{
    /// <summary>Name of the host's group (for <see cref="OptionSource.Group"/>), or null when it has none.</summary>
    public string? GroupName { get; init; }
}

/// <summary>Resolves <see cref="HostOptions"/> inheritance: host, then group, then vault defaults, then built-in defaults.</summary>
public static class EffectiveOptions
{
    public const int DefaultKeepAliveSeconds = 30;
    public const int DefaultConnectTimeoutSeconds = 15;
    public const bool DefaultAutoReconnect = false;
    public const string DefaultTerminalType = "xterm-256color";
    public const string DefaultColorScheme = "TGK Dark";
    public const bool DefaultLegacyAlgorithms = false;
    public const AgentAccess DefaultAgentAccess = AgentAccess.Off;

    /// <summary>The bundled terminal font.</summary>
    public const string DefaultFontFamily = "DejaVu Sans Mono";

    /// <summary>Most jump hosts one connection may go through.</summary>
    public const int MaxJumpHosts = 4;

    /// <param name="localFontSize">A built-in font size other than <see cref="TerminalSettings.FontSize"/>'s default.</param>
    public static EffectiveHostOptions Resolve(VaultData vault, HostEntry host, float? localFontSize = null)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(host);
        HostGroup? group = vault.FindGroup(host.GroupId);
        return Resolve(host.Options, group?.Options, vault.Defaults, localFontSize, host.Id) with { GroupName = group?.Name };
    }

    /// <summary>
    /// Resolves explicit levels; pass null for a level that does not apply, e.g. <c>Resolve(null, null, vault.Defaults)</c>
    /// for what a group inherits. A jump host equal to <paramref name="hostId"/> is skipped: a bastion inside a group
    /// whose hosts go through it is reached the way the next level says (or directly).
    /// </summary>
    public static EffectiveHostOptions Resolve(HostOptions? host, HostOptions? group, HostOptions? global, float? localFontSize = null, Guid? hostId = null)
    {
        (HostOptions? Options, OptionSource Source)[] levels = [(host, OptionSource.Host), (group, OptionSource.Group), (global, OptionSource.Global)];

        Resolved<Guid?> jump = new(null, OptionSource.Default);
        foreach ((HostOptions? options, OptionSource source) in levels)
        {
            if (options?.JumpHostId is { } id && id != hostId)
            {
                jump = new(id == HostOptions.NoJumpHost ? null : id, source);
                break;
            }
        }

        return new EffectiveHostOptions(
            jump,
            Value(levels, o => o.KeepAliveSeconds, DefaultKeepAliveSeconds),
            Value(levels, o => o.ConnectTimeoutSeconds, DefaultConnectTimeoutSeconds),
            Value(levels, o => o.AutoReconnect, DefaultAutoReconnect),
            Reference(levels, o => o.StartupCommand, ""),
            Reference<IReadOnlyList<EnvVar>>(levels, o => o.Environment?.Select(e => e.Clone()).ToList(), []),
            Reference(levels, o => o.TerminalType, DefaultTerminalType),
            Value(levels, o => o.FontSize, localFontSize ?? new TerminalSettings().FontSize),
            Reference(levels, o => o.ColorScheme, DefaultColorScheme),
            Value(levels, o => o.LegacyAlgorithms, DefaultLegacyAlgorithms),
            Reference(levels, o => o.FontFamily, DefaultFontFamily),
            Value(levels, o => o.ScrollbackLines, BuiltIn.ScrollbackLines),
            Reference(levels, o => o.CursorShape, BuiltIn.CursorShape),
            Value(levels, o => o.CursorBlink, BuiltIn.CursorBlink),
            Value(levels, o => o.CopyOnSelect, BuiltIn.CopyOnSelect),
            Value(levels, o => o.AgentAccess, DefaultAgentAccess),
            Reference<IReadOnlyList<string>>(levels, o => o.AgentCommands?.ToList(), []),
            Reference<IReadOnlyList<string>>(levels, o => o.AgentProtectedPaths?.ToList(), []));
    }

    private static readonly TerminalSettings BuiltIn = new();

    /// <summary>The terminal settings a session with <paramref name="options"/> uses.</summary>
    public static TerminalSettings Terminal(EffectiveHostOptions options) => new()
    {
        FontSize = options.FontSize.Value,
        FontFamily = options.FontFamily.Value,
        ScrollbackLines = options.ScrollbackLines.Value,
        CursorShape = options.CursorShape.Value,
        CursorBlink = options.CursorBlink.Value,
        CopyOnSelect = options.CopyOnSelect.Value,
    };

    /// <summary>
    /// The saved hosts a connection to <paramref name="host"/> goes through, outermost (connected first) first; empty
    /// for a direct connection. Each jump host's own effective jump host is followed in turn. On a loop, a missing
    /// jump host or more than <see cref="MaxJumpHosts"/> hops the result is empty and <paramref name="error"/> says why.
    /// </summary>
    public static IReadOnlyList<HostEntry> ResolveJumpChain(VaultData vault, HostEntry host, out string? error) =>
        ResolveJumpChain(vault, host, out error, out _);

    /// <summary>
    /// The first jump-host loop or too-long chain that <paramref name="after"/> (a vault with an edit applied) has and
    /// <paramref name="before"/> did not, or null. Every host is checked: an edit of one level (a group, the defaults,
    /// a host others go through) can break the route of many. A route broken before the edit does not count, so
    /// an older problem elsewhere never blocks an unrelated edit, and neither does a deleted jump host.
    /// </summary>
    public static string? NewJumpChainProblem(VaultData before, VaultData after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        foreach (HostEntry host in after.Hosts)
        {
            ResolveJumpChain(after, host, out string? error, out bool structural);
            if (!structural)
                continue;
            if (before.FindHost(host.Id) is { } old)
            {
                ResolveJumpChain(before, old, out _, out bool wasStructural);
                if (wasStructural)
                    continue;
            }
            return error;
        }
        return null;
    }

    /// <param name="structural">The error is a loop or too many jump hosts (not a deleted jump host).</param>
    private static IReadOnlyList<HostEntry> ResolveJumpChain(VaultData vault, HostEntry host, out string? error, out bool structural)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(host);
        var path = new List<HostEntry> { host }; // host first, then its jump host, then that one's...
        HostEntry current = host;
        structural = true;
        while (Resolve(vault, current).JumpHostId.Value is { } jumpId)
        {
            HostEntry? jump = vault.FindHost(jumpId);
            if (jump is null)
            {
                error = $"The jump host of {current.DisplayName} no longer exists. Choose another one in its connection settings.";
                structural = false;
                return [];
            }
            if (path.Exists(h => h.Id == jump.Id))
            {
                error = $"The jump hosts form a loop: {string.Join(" → ", path.Select(h => h.DisplayName))} → {jump.DisplayName}.";
                return [];
            }
            if (path.Count > MaxJumpHosts)
            {
                error = $"{host.DisplayName} goes through more than {MaxJumpHosts} jump hosts.";
                return [];
            }
            path.Add(jump);
            current = jump;
        }
        error = null;
        structural = false;
        return path.Skip(1).Reverse().ToList();
    }

    private static Resolved<T> Value<T>((HostOptions? Options, OptionSource Source)[] levels, Func<HostOptions, T?> get, T fallback) where T : struct
    {
        foreach ((HostOptions? options, OptionSource source) in levels)
        {
            if (options is not null && get(options) is { } value)
                return new(value, source);
        }
        return new(fallback, OptionSource.Default);
    }

    private static Resolved<T> Reference<T>((HostOptions? Options, OptionSource Source)[] levels, Func<HostOptions, T?> get, T fallback) where T : class
    {
        foreach ((HostOptions? options, OptionSource source) in levels)
        {
            if (options is not null && get(options) is { } value)
                return new(value, source);
        }
        return new(fallback, OptionSource.Default);
    }
}
