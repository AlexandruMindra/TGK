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

    /// <param name="workingDir">Where the command starts (the <c>cwd</c> argument); default: the home directory.</param>
    public static AgentDecision Run(AgentRules rules, string command, string home, string? workingDir = null)
    {
        if (rules.Access == AgentAccess.Off)
            return AgentDecision.Deny("Agents have no access to this host.");
        ShellCommand parsed = ShellCommand.Parse(command);
        if (parsed.Commands.Count == 0)
            return AgentDecision.Deny("The command is empty.");

        string? touched = TouchedProtectedPath(rules, parsed, home, workingDir ?? home);
        bool allowed = parsed.IsAllowed(rules.AllowedCommands);
        switch (rules.Access)
        {
            case AgentAccess.ReadOnly:
                if (touched is not null)
                    return AgentDecision.Deny($"The command may touch a protected path: {touched}. (The grep tool searches without them.)");
                return allowed
                    ? AgentDecision.Allow()
                    : AgentDecision.Deny(parsed.IsComplex
                        ? "Agents have read-only access to this host, and this command uses redirection or substitution that could change things."
                        : "Agents have read-only access to this host, and this is not a known read-only command or one of the host's allowed commands.");
            case AgentAccess.Ask:
                if (touched is not null)
                    return AgentDecision.Ask($"The command may touch a protected path: {touched}.");
                return allowed ? AgentDecision.Allow() : AgentDecision.Ask("Commands that may change things need approval on this host.");
            default:
                return touched is not null ? AgentDecision.Ask($"The command may touch a protected path: {touched}.") : AgentDecision.Allow();
        }
    }

    /// <summary>
    /// Names (without a slash) of the built-in protected paths, which the grep tool leaves out of its searches. The
    /// directories among them (<c>.ssh</c>, <c>ssh</c>, <c>sudoers.d</c>) are skipped as a whole.
    /// </summary>
    public static readonly IReadOnlyList<string> SearchExcludedNames =
        ["*.pem", "*.key", "*.p12", "*.pfx", "id_rsa", "id_ecdsa", "id_ed25519", "id_dsa", ".env", "*.kdbx", "shadow", "gshadow", "sudoers", "credentials", ".netrc", ".pgpass", ".git-credentials"];

    public static readonly IReadOnlyList<string> SearchExcludedDirs = [".ssh", "ssh", "sudoers.d", ".aws", ".kube", ".docker"];

    /// <summary>
    /// The first word of <paramref name="parsed"/> that is, or may expand to, a protected path, with the pattern; null
    /// when none. Follows <c>cd</c> within the command line, takes globs and variables into account (a variable in a
    /// path could point anywhere), and counts a directory a recursive reader (grep -r, rg, diff -r) goes into when a
    /// protected path lies below it.
    /// </summary>
    public static string? TouchedProtectedPath(AgentRules rules, ShellCommand parsed, string home, string workingDir)
    {
        string? wd = workingDir;
        foreach (IReadOnlyList<ShellWord> command in parsed.Parts)
        {
            string name = command[0].Text;
            name = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;
            if (name is "cd" or "pushd")
            {
                ShellWord? target = command.Skip(1).FirstOrDefault(w => !w.Text.StartsWith('-'));
                if (target is { } t && (t.Glob || Expand(t.Text, home).Contains('$') || t.Text == "-"))
                {
                    wd = null; // somewhere we can't tell
                    continue;
                }
                string dir = target is { } d ? From(home, wd, Expand(d.Text, home)) ?? home : home;
                if (Pattern(rules, dir, home, glob: false) is { } p)
                    return $"{dir} ({p})";
                wd = dir;
                continue;
            }

            bool recursive = IsRecursiveReader(name, command);
            int operands = 0;
            bool patternOption = false; // grep -e / -f and rg -e give the pattern as an option
            foreach (ShellWord word in command.Skip(1))
            {
                string text = word.Text;
                if (text.StartsWith('-'))
                {
                    if (text is "-e" or "-f" or "--regexp" or "--file" || text.StartsWith("--regexp=", StringComparison.Ordinal))
                        patternOption = true;
                    int eq = text.IndexOf('=');
                    if (eq < 0)
                        continue;
                    text = text[(eq + 1)..]; // --file=/etc/shadow
                }
                else
                {
                    operands++;
                }
                if (text.Length == 0)
                    continue;
                string expanded = Expand(text, home);
                if (expanded.Contains('$'))
                {
                    if (expanded.Contains('/'))
                        return $"{word.Text} (a variable in a path could point anywhere)";
                    continue;
                }
                if (wd is null && !expanded.StartsWith('/') && !expanded.StartsWith('~'))
                {
                    if (expanded.Contains('/') || expanded.Contains('.') || word.Glob)
                        return $"{word.Text} (relative to a directory that can't be told)";
                    continue;
                }
                string? path = From(home, wd, expanded);
                if (path is null)
                    continue;
                if (Pattern(rules, path, home, word.Glob) is { } pattern)
                    return $"{word.Text} ({pattern})";
                if (recursive && ProtectedBelow(rules, path, home) is { } below)
                    return $"{word.Text} (it goes into {RemotePath.Tilde(home, below)})";
            }
            // grep -r PATTERN without a path searches the working directory.
            bool searchesHere = recursive && name != "diff" && operands - (patternOption ? 0 : 1) <= 0;
            if (searchesHere && wd is null)
                return "the working directory (which can't be told)";
            if (searchesHere && wd is not null && ProtectedBelow(rules, wd, home) is { } inside)
                return $"the working directory {RemotePath.Tilde(home, wd)} (it goes into {RemotePath.Tilde(home, inside)})";
        }
        return null;
    }

    private static string Expand(string text, string home) => text.Replace("${HOME}", home).Replace("$HOME", home);

    private static string? From(string home, string? wd, string text)
    {
        try
        {
            return text == "~" || text.StartsWith("~/", StringComparison.Ordinal) || text.StartsWith('/')
                ? RemotePath.Resolve(home, text)
                : RemotePath.Resolve(wd ?? home, text);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // grep -r / -R / --recursive / -d recurse, rg (always recursive), diff -r: they read the files below a directory.
    private static bool IsRecursiveReader(string name, IReadOnlyList<ShellWord> command)
    {
        if (name == "rg")
            return true;
        if (name is not ("grep" or "egrep" or "fgrep" or "zgrep" or "diff"))
            return false;
        foreach (ShellWord w in command.Skip(1))
        {
            string t = w.Text;
            if (t is "--recursive" or "--dereference-recursive" || t.StartsWith("--directories=recurse", StringComparison.Ordinal) || t == "recurse")
                return true;
            if (t.Length > 1 && t[0] == '-' && t[1] != '-' && (t.Contains('r') || t.Contains('R')))
                return true;
        }
        return false;
    }

    /// <summary>The pattern a path (or, for a glob, any path it may expand to) matches.</summary>
    private static string? Pattern(AgentRules rules, string path, string home, bool glob)
    {
        if (!glob)
            return ProtectedPattern(rules, path, home);
        // A glob: test the paths it can reach. Its directory part may match a protected directory (then anything in
        // it may be read), the whole glob may match one, and its last part may match a protected file name.
        int slash = path.LastIndexOf('/');
        string dirGlob = slash <= 0 ? "/" : path[..slash], nameGlob = path[(slash + 1)..];
        foreach (string pattern in BuiltInProtectedPaths.Concat(rules.ProtectedPaths))
        {
            string trimmed = pattern.Trim();
            if (!trimmed.Contains('/'))
            {
                string sample = trimmed.Replace("*", "x").Replace("?", "x");
                if (RemotePath.Matches(nameGlob, "/" + sample, home))
                    return pattern;
                continue;
            }
            string fixedDir = FixedDirectory(trimmed, home);
            if (RemotePath.Matches(dirGlob, fixedDir, home) || RemotePath.Matches(path, fixedDir, home)
                || fixedDir.StartsWith(GlobFreePrefix(path) + "/", StringComparison.Ordinal) && path.Contains("**", StringComparison.Ordinal))
                return pattern;
        }
        return null;
    }

    /// <summary>A protected location strictly below <paramref name="dir"/> (of the host's own patterns only, without <paramref name="builtIn"/>), or null.</summary>
    public static string? ProtectedBelow(AgentRules rules, string dir, string home, bool builtIn = true)
    {
        string prefix = dir == "/" ? "/" : dir + "/";
        foreach (string pattern in (builtIn ? BuiltInProtectedPaths : []).Concat(rules.ProtectedPaths))
        {
            if (!pattern.Contains('/'))
                continue;
            string fixedDir = FixedDirectory(pattern.Trim(), home);
            if (fixedDir.StartsWith(prefix, StringComparison.Ordinal))
                return fixedDir;
        }
        return null;
    }

    // "~/.ssh/**" → /home/u/.ssh; "/etc/ssh/*_key" → /etc/ssh; "/etc/shadow" → /etc/shadow.
    private static string FixedDirectory(string pattern, string home)
    {
        string absolute = pattern == "~" || pattern.StartsWith("~/", StringComparison.Ordinal) ? home.TrimEnd('/') + pattern[1..] : pattern;
        return GlobFreePrefix(absolute.StartsWith('/') ? absolute : "/" + absolute);
    }

    // The leading segments without glob characters.
    private static string GlobFreePrefix(string path)
    {
        int glob = path.IndexOfAny(['*', '?', '[']);
        if (glob < 0)
            return path.TrimEnd('/') is { Length: > 0 } p ? p : "/";
        int slash = path.LastIndexOf('/', glob);
        return slash <= 0 ? "/" : path[..slash];
    }

    /// <summary>Commands as root (TGK supplies the sudo password): always the user's call, never in read-only mode.</summary>
    public static AgentDecision Sudo(AgentRules rules) => rules.Access switch
    {
        AgentAccess.Off => AgentDecision.Deny("Agents have no access to this host."),
        AgentAccess.ReadOnly => AgentDecision.Deny("Agents have read-only access to this host; sudo is not available to them."),
        _ => AgentDecision.Ask("Commands as root always need approval."),
    };

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
