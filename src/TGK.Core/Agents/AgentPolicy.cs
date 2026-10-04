using System;
using System.Collections.Generic;
using System.Linq;
using TGK.Core.Models;

namespace TGK.Core.Agents;

public enum AgentVerdict
{
    Allow,
    Ask,
    Deny,
}

/// <summary>What the policy says about one operation, and why (the reason is shown to the agent and the user).</summary>
public sealed record AgentDecision(AgentVerdict Verdict, string Reason)
{
    public static AgentDecision Allow(string reason = "") => new(AgentVerdict.Allow, reason);
    public static AgentDecision Ask(string reason) => new(AgentVerdict.Ask, reason);
    public static AgentDecision Deny(string reason) => new(AgentVerdict.Deny, reason);
}

/// <summary>A host's effective agent settings (from <see cref="EffectiveOptions"/>).</summary>
public sealed record AgentRules(AgentAccess Access, IReadOnlyList<string> AllowedCommands, IReadOnlyList<string> ProtectedPaths)
{
    public static AgentRules For(VaultData vault, HostEntry host)
    {
        EffectiveHostOptions o = EffectiveOptions.Resolve(vault, host);
        return new AgentRules(o.AgentAccess.Value, o.AgentCommands.Value, o.AgentProtectedPaths.Value);
    }
}

/// <summary>
/// Decides what an agent may do on a host: allowed, after the user's approval, or not at all. See docs/AGENTS.md.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Off</b>: nothing (the host is not listed).</item>
/// <item><b>Read only</b>: reading files, and commands that are built-in read-only ones or allowed; never protected paths.</item>
/// <item><b>Ask</b>: reading is free; writing, and commands that are not read-only or allowed, need approval.</item>
/// <item><b>Full</b>: everything without asking.</item>
/// </list>
/// In every mode a protected path (built-in or the host's own) needs approval, and is refused in read-only mode;
/// commands count as touching one when a word of theirs is such a path.
/// </remarks>
public static class AgentPolicy
{
    /// <summary>Paths with credentials and other secrets that agents never touch without the user's approval.</summary>
    public static readonly IReadOnlyList<string> BuiltInProtectedPaths =
    [
        "~/.ssh/**", "/root/.ssh/**", "/etc/ssh/*_key", "/etc/shadow", "/etc/gshadow", "/etc/sudoers", "/etc/sudoers.d/**",
        "*.pem", "*.key", "*.p12", "*.pfx", "id_rsa", "id_ecdsa", "id_ed25519", "id_dsa", ".env", "*.kdbx",
        "~/.aws/credentials", "~/.docker/config.json", "~/.kube/config", "~/.netrc", "~/.pgpass", "~/.git-credentials",
    ];

    public static AgentDecision Read(AgentRules rules, string path, string home)
    {
        if (rules.Access == AgentAccess.Off)
            return AgentDecision.Deny("Agents have no access to this host.");
        if (ProtectedPattern(rules, path, home) is { } pattern)
        {
            return rules.Access == AgentAccess.ReadOnly
                ? AgentDecision.Deny($"{RemotePath.Tilde(home, path)} is a protected path ({pattern}); read-only access never reaches it.")
                : AgentDecision.Ask($"{RemotePath.Tilde(home, path)} is a protected path ({pattern}).");
        }
        return AgentDecision.Allow();
    }

    public static AgentDecision Write(AgentRules rules, string path, string home)
    {
        switch (rules.Access)
        {
            case AgentAccess.Off:
                return AgentDecision.Deny("Agents have no access to this host.");
            case AgentAccess.ReadOnly:
                return AgentDecision.Deny("Agents have read-only access to this host; the user can change that in TGK (Agent access).");
        }
        if (ProtectedPattern(rules, path, home) is { } pattern)
            return AgentDecision.Ask($"{RemotePath.Tilde(home, path)} is a protected path ({pattern}).");
        return rules.Access == AgentAccess.Full ? AgentDecision.Allow() : AgentDecision.Ask("Changes need approval on this host.");
    }

    public static AgentDecision Run(AgentRules rules, string command, string home)
    {
        if (rules.Access == AgentAccess.Off)
            return AgentDecision.Deny("Agents have no access to this host.");
        ShellCommand parsed = ShellCommand.Parse(command);
        if (parsed.Commands.Count == 0)
            return AgentDecision.Deny("The command is empty.");

        string? touched = parsed.Words
            .Where(w => w.Contains('/') || w.StartsWith('~') || w.StartsWith('.') || w.Contains('.'))
            .Select(w => (Word: w, Pattern: ProtectedPattern(rules, RemotePath.Resolve(home, w), home)))
            .Where(m => m.Pattern is not null)
            .Select(m => $"{m.Word} ({m.Pattern})")
            .FirstOrDefault();

        bool allowed = parsed.IsAllowed(rules.AllowedCommands);
        switch (rules.Access)
        {
            case AgentAccess.ReadOnly:
                if (touched is not null)
                    return AgentDecision.Deny($"The command touches a protected path: {touched}.");
                return allowed
                    ? AgentDecision.Allow()
                    : AgentDecision.Deny(parsed.IsComplex
                        ? "Agents have read-only access to this host, and this command uses redirection or substitution that could change things."
                        : "Agents have read-only access to this host, and this is not a known read-only command or one of the host's allowed commands.");
            case AgentAccess.Ask:
                if (touched is not null)
                    return AgentDecision.Ask($"The command touches a protected path: {touched}.");
                return allowed ? AgentDecision.Allow() : AgentDecision.Ask("Commands that may change things need approval on this host.");
            default:
                return touched is not null ? AgentDecision.Ask($"The command touches a protected path: {touched}.") : AgentDecision.Allow();
        }
    }

    /// <summary>The protected-path pattern <paramref name="path"/> (absolute) matches, or null.</summary>
    public static string? ProtectedPattern(AgentRules rules, string path, string home)
    {
        foreach (string pattern in BuiltInProtectedPaths.Concat(rules.ProtectedPaths))
        {
            if (RemotePath.Matches(pattern, path, home) || IsInside(pattern, path, home))
                return pattern;
        }
        return null;
    }

    // "~/.ssh/**" also covers the directory itself (listing ~/.ssh).
    private static bool IsInside(string pattern, string path, string home) =>
        pattern.EndsWith("/**", StringComparison.Ordinal) && RemotePath.Matches(pattern[..^3], path, home);
}
