using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TGK.Core.Agents;

/// <summary>
/// A shell command line as far as the agent policy needs it: the simple commands it chains (split at <c>;</c>,
/// <c>&amp;&amp;</c>, <c>||</c>, <c>|</c>, <c>&amp;</c> and line breaks, respecting quotes), and whether anything in it
/// is too clever to judge (command substitution, process substitution, output redirection to a file).
/// </summary>
/// <remarks>
/// Deliberately conservative: it does not understand the shell, it only recognizes command lines that are plainly
/// harmless. Anything it can't vouch for is "not read-only" and goes to the user (or is refused in read-only mode).
/// </remarks>
public sealed partial class ShellCommand
{
    // Commands that only read, by their first word. Some are read-only only without certain options (see Unsafe).
    private static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.Ordinal)
    {
        "ls", "ll", "cat", "head", "tail", "grep", "egrep", "fgrep", "rg", "find", "stat", "file", "wc", "du", "df",
        "free", "uptime", "ps", "pgrep", "whoami", "id", "groups", "hostname", "uname", "date", "pwd", "echo", "printf",
        "which", "type", "command", "printenv", "lsblk", "lscpu", "lsusb", "lspci", "nproc", "ss", "netstat", "md5sum",
        "sha1sum", "sha256sum", "sha512sum", "cksum", "sort", "uniq", "cut", "tr", "diff", "cmp", "comm", "basename",
        "dirname", "realpath", "readlink", "tree", "journalctl", "dmesg", "last", "w", "who", "getent", "test", "[",
        "true", "false", "column", "nl", "rev", "tac", "od", "hexdump", "strings", "less", "more", "zcat",
        "zgrep", "bzcat", "xzcat", "jq", "env", "locale", "arch", "lsb_release", "hostnamectl", "timedatectl",
        "ip", "systemctl", "git", "docker", "podman", "kubectl", "helm", "service", "crontab", "apt", "dpkg",
        "rpm", "dnf", "yum", "pip", "npm", "sleep",
    };

    // For commands that also change things, the read-only subcommands (first argument).
    private static readonly Dictionary<string, HashSet<string>> ReadOnlySubcommands = new(StringComparer.Ordinal)
    {
        ["systemctl"] = ["status", "show", "list-units", "list-unit-files", "list-timers", "list-sockets", "is-active", "is-enabled", "is-failed", "cat", "--version"],
        ["service"] = ["--status-all"],
        ["git"] = ["status", "log", "diff", "show", "rev-parse", "ls-files", "blame", "describe", "shortlog", "grep", "ls-tree", "cat-file", "--version", "version"],
        ["hostnamectl"] = ["status", "show"],
        ["timedatectl"] = ["status", "show", "list-timezones", "timesync-status", "show-timesync"],
        ["docker"] = ["ps", "images", "logs", "inspect", "version", "info", "stats", "top", "port", "history"],
        ["podman"] = ["ps", "images", "logs", "inspect", "version", "info", "stats", "top", "port", "history"],
        ["kubectl"] = ["get", "describe", "logs", "version", "top", "explain", "api-resources", "cluster-info"],
        ["helm"] = ["list", "ls", "status", "history", "version", "get", "show"],
        ["ip"] = ["a", "addr", "address", "r", "route", "l", "link", "n", "neigh", "-br", "-brief", "-s", "-4", "-6"],
        ["crontab"] = ["-l"],
        ["apt"] = ["list", "show", "search", "policy"],
        ["dpkg"] = ["-l", "-L", "-s", "--list", "--status", "--listfiles"],
        ["rpm"] = ["-q", "-qa", "-qi", "-ql", "-qf"],
        ["dnf"] = ["list", "info", "search", "repolist"],
        ["yum"] = ["list", "info", "search", "repolist"],
        ["pip"] = ["list", "show", "freeze", "--version"],
        ["npm"] = ["ls", "list", "view", "outdated", "--version", "-v"],
        ["env"] = [], // only without arguments (with them it runs a command)
        ["command"] = ["-v", "-V"],
    };

    // Of those, the ones that only read (or print help) without any argument.
    private static readonly HashSet<string> NoArgumentsReadOnly = new(StringComparer.Ordinal)
    {
        "env", "ip", "git", "systemctl", "docker", "podman", "kubectl", "helm", "hostnamectl", "timedatectl",
    };

    // Commands that write their last argument when given more than this many operands (uniq IN OUT, hostname NAME).
    private static readonly Dictionary<string, int> MaxOperands = new(StringComparer.Ordinal)
    {
        ["uniq"] = 1,
        ["hostname"] = 0,
        ["date"] = 0, // date MMDDhhmm sets the clock
    };

    // Options that make a read-only command write or run something.
    private static readonly Dictionary<string, string[]> UnsafeOptions = new(StringComparer.Ordinal)
    {
        ["find"] = ["-delete", "-exec", "-execdir", "-ok", "-okdir", "-fprint", "-fprint0", "-fprintf", "-fls"],
        ["rg"] = ["--pre"],
        ["sort"] = ["-o", "--output"],
        ["dmesg"] = ["-c", "-C", "--clear", "--read-clear"],
        ["journalctl"] = ["--vacuum-size", "--vacuum-time", "--vacuum-files", "--rotate", "--flush", "--relinquish-var", "--setup-keys"],
        ["ip"] = ["add", "del", "delete", "set", "change", "replace", "flush", "append"],
        ["git"] = ["--output", "-o"],
        ["date"] = ["-s", "--set"],
        ["hostname"] = ["-F", "--file"],
        ["tree"] = ["-o"],
        ["file"] = ["-C", "--compile"],
        ["jq"] = ["--rawfile", "--slurpfile"], // reading other files than the ones named is fine, but keep it simple
    };

    private ShellCommand(IReadOnlyList<IReadOnlyList<string>> commands, IReadOnlyList<IReadOnlyList<ShellWord>> parts, bool complex)
    {
        Commands = commands;
        Parts = parts;
        IsComplex = complex;
    }

    /// <summary>The words of each command with what the shell may still do to them (expand globs or variables).</summary>
    public IReadOnlyList<IReadOnlyList<ShellWord>> Parts { get; }

    /// <summary>The simple commands, each as its words (quotes removed).</summary>
    public IReadOnlyList<IReadOnlyList<string>> Commands { get; }

    /// <summary>Contains command or process substitution, output redirection to a file, or a here-document.</summary>
    public bool IsComplex { get; }

    /// <summary>Every word of every command (for spotting paths).</summary>
    public IEnumerable<string> Words => Commands.SelectMany(c => c);

    public static ShellCommand Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var commands = new List<IReadOnlyList<string>>();
        var parts = new List<IReadOnlyList<ShellWord>>();
        var words = new List<ShellWord>();
        var word = new StringBuilder();
        bool inWord = false, complex = false, glob = false, dynamic = false;

        void EndWord()
        {
            if (inWord)
                words.Add(new ShellWord(word.ToString(), glob, dynamic));
            word.Clear();
            inWord = glob = dynamic = false;
        }

        void EndCommand()
        {
            EndWord();
            if (words.Count > 0)
            {
                commands.Add(words.Select(w => w.Text).ToArray());
                parts.Add(words.ToArray());
            }
            words.Clear();
        }

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            switch (c)
            {
                case '\\':
                    if (i + 1 < line.Length)
                        word.Append(line[++i]);
                    inWord = true;
                    break;
                case '\'':
                {
                    int end = line.IndexOf('\'', i + 1);
                    if (end < 0)
                    {
                        complex = true; // unbalanced
                        end = line.Length;
                    }
                    word.Append(line, i + 1, Math.Max(0, end - i - 1));
                    i = end;
                    inWord = true;
                    break;
                }
                case '"':
                {
                    int j = i + 1;
                    for (; j < line.Length && line[j] != '"'; j++)
                    {
                        if (line[j] == '\\' && j + 1 < line.Length)
                        {
                            word.Append(line[++j]);
                            continue;
                        }
                        if (line[j] == '`' || (line[j] == '$' && j + 1 < line.Length && line[j + 1] == '('))
                            complex = true;
                        else if (line[j] == '$')
                            dynamic = true;
                        word.Append(line[j]);
                    }
                    if (j >= line.Length)
                        complex = true; // unbalanced
                    i = j;
                    inWord = true;
                    break;
                }
                case '`':
                    complex = true;
                    word.Append(c);
                    inWord = true;
                    break;
                case '$' when i + 1 < line.Length && line[i + 1] == '(':
                    complex = true;
                    word.Append(c);
                    inWord = true;
                    break;
                case '$':
                    dynamic = true;
                    word.Append(c);
                    inWord = true;
                    break;
                case '*' or '?' or '[':
                    glob = true;
                    word.Append(c);
                    inWord = true;
                    break;
                case '<' when i + 1 < line.Length && line[i + 1] is '(' or '<':
                case '>' when i + 1 < line.Length && line[i + 1] == '(':
                    complex = true; // process substitution, here-document
                    word.Append(c);
                    inWord = true;
                    break;
                case '>':
                {
                    // Output redirection: harmless only to /dev/null or between descriptors (2>&1).
                    string fd = inWord && word.Length == 1 && char.IsDigit(word[0]) ? word.ToString() : "";
                    if (fd.Length > 0)
                    {
                        word.Clear();
                        inWord = false;
                    }
                    else
                    {
                        EndWord();
                    }
                    int j = i + 1;
                    if (j < line.Length && line[j] == '>')
                        j++;
                    if (j < line.Length && line[j] == '&')
                    {
                        j++;
                        while (j < line.Length && char.IsDigit(line[j]))
                            j++;
                        i = j - 1;
                        break;
                    }
                    while (j < line.Length && line[j] == ' ')
                        j++;
                    int start = j;
                    while (j < line.Length && !char.IsWhiteSpace(line[j]) && line[j] is not (';' or '|' or '&'))
                        j++;
                    if (line[start..j] != "/dev/null")
                        complex = true;
                    i = j - 1;
                    break;
                }
                case ';':
                case '|':
                case '&':
                case '\n':
                case '\r':
                    EndCommand();
                    break;
                case ' ':
                case '\t':
                    EndWord();
                    break;
                default:
                    word.Append(c);
                    inWord = true;
                    break;
            }
        }
        EndCommand();
        return new ShellCommand(commands, parts, complex);
    }

    /// <summary>Every simple command is a built-in read-only one (and nothing complex is going on).</summary>
    public bool IsReadOnly => !IsComplex && Commands.Count > 0 && Commands.All(IsReadOnlyCommand);

    /// <summary>
    /// Every simple command is read-only or matches one of <paramref name="allowed"/> (prefixes; <c>*</c> matches anything).
    /// </summary>
    public bool IsAllowed(IEnumerable<string> allowed)
    {
        if (IsComplex || Commands.Count == 0)
            return false;
        Regex[] patterns = allowed.Where(a => !string.IsNullOrWhiteSpace(a)).Select(PrefixRegex).ToArray();
        return Commands.All(c => IsReadOnlyCommand(c) || Matches(c, patterns));
    }

    /// <summary>Every simple command matches one of <paramref name="allowed"/> (ignoring the built-in read-only list).</summary>
    public bool MatchesAllowed(IEnumerable<string> allowed)
    {
        if (IsComplex || Commands.Count == 0)
            return false;
        Regex[] patterns = allowed.Where(a => !string.IsNullOrWhiteSpace(a)).Select(PrefixRegex).ToArray();
        return patterns.Length > 0 && Commands.All(c => Matches(c, patterns));
    }

    private static bool Matches(IReadOnlyList<string> command, Regex[] patterns)
    {
        string text = string.Join(' ', command);
        return patterns.Any(p => p.IsMatch(text));
    }

    private static Regex PrefixRegex(string prefix)
    {
        string[] parts = WhiteSpace().Split(prefix.Trim());
        string body = string.Join(" ", parts.Select(p => string.Join(".*", p.Split('*').Select(Regex.Escape))));
        return new Regex($"^{body}(?: .*)?$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    private static bool IsReadOnlyCommand(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
            return true;
        string name = words[0];
        if (name.Contains('=')) // VAR=value command: the variable could change what runs (LD_PRELOAD…)
            return false;
        if (name.Contains('/'))
            name = name[(name.LastIndexOf('/') + 1)..]; // /usr/bin/ls
        if (!ReadOnlyCommands.Contains(name))
            return false;
        if (ReadOnlySubcommands.TryGetValue(name, out HashSet<string>? subcommands))
        {
            string? first = words.Skip(1).FirstOrDefault(w => name is not ("git" or "docker" or "kubectl") || !w.StartsWith('-'));
            if (first is null)
                return NoArgumentsReadOnly.Contains(name); // e.g. env prints the environment; crontab alone reads a new table

            if (!subcommands.Contains(first))
                return false;
        }
        if (MaxOperands.TryGetValue(name, out int maxOperands) && Operands(words, name) > maxOperands)
            return false;
        if (UnsafeOptions.TryGetValue(name, out string[]? unsafeOptions))
        {
            foreach (string w in words.Skip(1))
            {
                if (unsafeOptions.Any(o => w == o || (o.StartsWith("--", StringComparison.Ordinal) && w.StartsWith(o + "=", StringComparison.Ordinal))))
                    return false;
            }
        }
        return true;
    }

    // Operands of a command: words that are neither options nor the values of options that take one.
    private static int Operands(IReadOnlyList<string> words, string name)
    {
        string[] withValue = name switch
        {
            "date" => ["-d", "--date", "-r", "--reference", "-f", "--file"],
            "uniq" => ["-f", "--skip-fields", "-s", "--skip-chars", "-w", "--check-chars"],
            _ => [],
        };
        int count = 0;
        for (int i = 1; i < words.Count; i++)
        {
            string w = words[i];
            if (Array.IndexOf(withValue, w) >= 0)
                i++;
            else if (!w.StartsWith('-') && !w.StartsWith('+'))
                count++;
        }
        return count;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}

/// <summary>A word of a command line (quotes removed).</summary>
/// <param name="Glob">Has an unquoted <c>*</c>, <c>?</c> or <c>[</c>: the shell expands it to matching paths.</param>
/// <param name="Dynamic">Has a variable the shell expands (<c>$X</c>, outside single quotes).</param>
public readonly record struct ShellWord(string Text, bool Glob, bool Dynamic);
