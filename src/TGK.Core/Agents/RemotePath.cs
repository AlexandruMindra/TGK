using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace TGK.Core.Agents;

/// <summary>POSIX paths on a remote host: resolving what an agent passes, quoting for the shell, glob patterns.</summary>
public static class RemotePath
{
    /// <summary>
    /// The absolute, normalized form of <paramref name="path"/>: <c>~</c> and <c>~/…</c> and relative paths are taken
    /// from <paramref name="home"/>; <c>.</c>, <c>..</c> and repeated slashes are folded (above <c>/</c> stays <c>/</c>).
    /// </summary>
    /// <exception cref="ArgumentException">Empty, or contains a NUL or a line break.</exception>
    public static string Resolve(string home, string path)
    {
        ArgumentNullException.ThrowIfNull(home);
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("The path is empty.");
        if (path.AsSpan().IndexOfAny('\0', '\n', '\r') >= 0)
            throw new ArgumentException("The path must not contain NUL characters or line breaks.");
        string full = path == "~" ? home
            : path.StartsWith("~/", StringComparison.Ordinal) ? home.TrimEnd('/') + path[1..]
            : path.StartsWith('/') ? path
            : home.TrimEnd('/') + "/" + path;
        return Normalize(full);
    }

    /// <summary>Folds <c>.</c>, <c>..</c> and repeated slashes of an absolute path; drops a trailing slash.</summary>
    public static string Normalize(string absolute)
    {
        var parts = new List<string>();
        foreach (string part in absolute.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
                continue;
            if (part == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return "/" + string.Join('/', parts);
    }

    public static string Directory(string absolute)
    {
        int slash = absolute.LastIndexOf('/');
        return slash <= 0 ? "/" : absolute[..slash];
    }

    public static string FileName(string absolute)
    {
        int slash = absolute.LastIndexOf('/');
        return slash < 0 ? absolute : absolute[(slash + 1)..];
    }

    /// <summary>
    /// <paramref name="absolute"/> written with <c>~</c> for <paramref name="home"/> where it applies, for messages and
    /// for matching patterns written that way.
    /// </summary>
    public static string Tilde(string home, string absolute)
    {
        string h = home.TrimEnd('/');
        if (h.Length == 0)
            return absolute;
        if (absolute == h)
            return "~";
        return absolute.StartsWith(h + "/", StringComparison.Ordinal) ? "~" + absolute[h.Length..] : absolute;
    }

    /// <summary>Single-quotes <paramref name="value"/> for a POSIX shell.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Whether <paramref name="path"/> (absolute) matches <paramref name="pattern"/>: <c>*</c> matches within one
    /// segment, <c>**</c> across segments, <c>?</c> one character; a pattern starting with <c>~</c> is taken from
    /// <paramref name="home"/>, and a pattern without a slash matches the file name anywhere (like <c>*.pem</c>).
    /// </summary>
    public static bool Matches(string pattern, string path, string home)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;
        pattern = pattern.Trim();
        if (!pattern.Contains('/'))
            return GlobRegex(pattern).IsMatch(FileName(path));
        if (pattern == "~" || pattern.StartsWith("~/", StringComparison.Ordinal))
            pattern = home.TrimEnd('/') + pattern[1..];
        else if (!pattern.StartsWith('/'))
            pattern = "**/" + pattern;
        return GlobRegex(pattern).IsMatch(path);
    }

    private static Regex GlobRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                bool slashAfter = i + 2 < pattern.Length && pattern[i + 2] == '/';
                sb.Append(slashAfter ? "(?:.*/)?" : ".*");
                i += slashAfter ? 2 : 1;
            }
            else if (c == '*')
            {
                sb.Append("[^/]*");
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant);
    }
}
