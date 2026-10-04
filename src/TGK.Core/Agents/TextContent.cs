using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TGK.Core.Agents;

/// <summary>
/// A text file's content as agents see it: decoded (UTF-8, else Latin-1), with its byte order mark and line endings
/// remembered so that an edit writes them back unchanged.
/// </summary>
public sealed class TextContent
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private TextContent(string text, bool bom, bool latin1, string newline)
    {
        Text = text;
        HasBom = bom;
        IsLatin1 = latin1;
        Newline = newline;
    }

    /// <summary>The content with its original line endings.</summary>
    public string Text { get; }

    public bool HasBom { get; }

    /// <summary>The bytes were not valid UTF-8 and were read as Latin-1 (and are written back that way).</summary>
    public bool IsLatin1 { get; }

    /// <summary><c>\r\n</c> when every line ends that way, else <c>\n</c>.</summary>
    public string Newline { get; }

    /// <summary>
    /// Decodes <paramref name="bytes"/>; null for binary content (a NUL byte in the first 8 KB), which agents should
    /// not edit as text.
    /// </summary>
    public static TextContent? Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8192)) >= 0)
            return null;
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        ReadOnlySpan<byte> body = bom ? bytes.AsSpan(3) : bytes;
        string text;
        bool latin1 = false;
        try
        {
            text = StrictUtf8.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(body);
            latin1 = true;
        }
        return new TextContent(text, bom, latin1, DetectNewline(text));
    }

    /// <summary>New content written like this one (same BOM, encoding and line endings).</summary>
    public TextContent With(string text) => new(text, HasBom, IsLatin1, Newline);

    /// <summary>A new file's content: UTF-8 without BOM, line endings as given.</summary>
    public static TextContent New(string text) => new(text, false, false, DetectNewline(text));

    public byte[] Encode()
    {
        byte[] body = IsLatin1 ? Encoding.Latin1.GetBytes(Text) : Encoding.UTF8.GetBytes(Text);
        if (!HasBom)
            return body;
        byte[] withBom = new byte[body.Length + 3];
        withBom[0] = 0xEF;
        withBom[1] = 0xBB;
        withBom[2] = 0xBF;
        body.CopyTo(withBom, 3);
        return withBom;
    }

    /// <summary>The text with <c>\n</c> line endings (what agents read and write).</summary>
    public string Normalized => Newline == "\r\n" ? Text.Replace("\r\n", "\n") : Text;

    /// <summary>Text in agent form (<c>\n</c>) converted to this file's line endings.</summary>
    public string ToFileNewlines(string text) => Newline == "\r\n" ? text.Replace("\r\n", "\n").Replace("\n", "\r\n") : text;

    /// <summary>
    /// <c>cat -n</c> style: lines from <paramref name="offset"/> (1-based), at most <paramref name="limit"/>, each cut at
    /// <paramref name="maxLineLength"/> characters.
    /// </summary>
    public (string Text, int TotalLines, int First, int Last) Numbered(int offset, int limit, int maxLineLength)
    {
        string[] lines = Normalized.Split('\n');
        int total = lines.Length;
        if (total > 0 && lines[^1].Length == 0)
            total--; // a final newline does not start another line
        int first = Math.Max(1, offset);
        int last = Math.Min(total, first + Math.Max(1, limit) - 1);
        var sb = new StringBuilder();
        for (int n = first; n <= last; n++)
        {
            string line = lines[n - 1];
            if (line.Length > maxLineLength)
                line = line[..maxLineLength] + $"… [{line.Length - maxLineLength} more characters]";
            sb.Append(n.ToString(CultureInfo.InvariantCulture).PadLeft(6)).Append('\t').Append(line).Append('\n');
        }
        return (sb.ToString(), total, first, last);
    }

    /// <summary>
    /// Replaces <paramref name="oldText"/> with <paramref name="newText"/> (both in agent form, <c>\n</c>). Without
    /// <paramref name="all"/> the old text must occur exactly once.
    /// </summary>
    /// <exception cref="RemoteFileException">Not found, or found more than once without <paramref name="all"/>.</exception>
    public (TextContent Result, int Count) Replace(string oldText, string newText, bool all)
    {
        if (string.IsNullOrEmpty(oldText))
            throw new RemoteFileException("old_string is empty; use write_file to create or rewrite a file.");
        if (oldText == newText)
            throw new RemoteFileException("old_string and new_string are the same; nothing to change.");
        string needle = ToFileNewlines(oldText), replacement = ToFileNewlines(newText);
        int count = Count(Text, needle);
        if (count == 0)
            throw new RemoteFileException("old_string was not found in the file. Read the file again and copy the text exactly (indentation included).");
        if (count > 1 && !all)
            throw new RemoteFileException($"old_string occurs {count} times; add surrounding lines to make it unique, or pass replace_all.");
        string text = all ? Text.Replace(needle, replacement, StringComparison.Ordinal) : ReplaceFirst(Text, needle, replacement);
        return (With(text), count);
    }

    /// <summary>The line number (1-based) where <paramref name="text"/> (agent form) first occurs, or 1.</summary>
    public int LineOf(string text)
    {
        int index = Normalized.IndexOf(text, StringComparison.Ordinal);
        if (index < 0)
            return 1;
        int line = 1;
        for (int i = 0; i < index; i++)
        {
            if (Normalized[i] == '\n')
                line++;
        }
        return line;
    }

    /// <summary>SHA-256 of the bytes, hex.</summary>
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string DetectNewline(string text)
    {
        int lf = 0, crlf = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
                continue;
            lf++;
            if (i > 0 && text[i - 1] == '\r')
                crlf++;
        }
        return lf > 0 && crlf == lf ? "\r\n" : "\n";
    }

    private static int Count(string text, string needle)
    {
        int count = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string ReplaceFirst(string text, string needle, string replacement)
    {
        int i = text.IndexOf(needle, StringComparison.Ordinal);
        return text[..i] + replacement + text[(i + needle.Length)..];
    }
}

/// <summary>A compact unified diff of two texts, for approval dialogs.</summary>
public static class TextDiff
{
    private const int Context = 3;
    private const int MaxLcsCells = 4_000_000;

    /// <summary>Lines prefixed with <c>+</c>, <c>-</c> or a space; hunks start with <c>@@ -a,b +c,d @@</c>.</summary>
    public static string Unified(string before, string after, int maxLines = 400)
    {
        string[] a = SplitLines(before), b = SplitLines(after);
        int prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
            prefix++;
        int suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)])
            suffix++;

        // The changed middle, as a list of (op, line); LCS when small enough, else all removed then all added.
        var ops = new List<(char Op, string Line, int A, int B)>();
        int am = a.Length - prefix - suffix, bm = b.Length - prefix - suffix;
        if ((long)am * bm <= MaxLcsCells)
            Lcs(a, b, prefix, am, bm, ops);
        else
        {
            for (int i = 0; i < am; i++)
                ops.Add(('-', a[prefix + i], prefix + i, -1));
            for (int j = 0; j < bm; j++)
                ops.Add(('+', b[prefix + j], -1, prefix + j));
        }

        // Context around the changes.
        var all = new List<(char Op, string Line, int A, int B)>();
        for (int i = Math.Max(0, prefix - Context); i < prefix; i++)
            all.Add((' ', a[i], i, i));
        all.AddRange(ops);
        int afterA = a.Length - suffix, afterB = b.Length - suffix;
        for (int k = 0; k < Math.Min(Context, suffix); k++)
            all.Add((' ', a[afterA + k], afterA + k, afterB + k));

        if (ops.Count == 0)
            return "(no changes)";
        var sb = new StringBuilder();
        int first = prefix - Math.Min(Context, prefix);
        int countA = all.FindAll(o => o.Op != '+').Count, countB = all.FindAll(o => o.Op != '-').Count;
        sb.Append(CultureInfo.InvariantCulture, $"@@ -{first + 1},{countA} +{first + 1},{countB} @@\n");
        int shown = 0;
        foreach ((char op, string line, _, _) in all)
        {
            if (++shown > maxLines)
            {
                sb.Append(CultureInfo.InvariantCulture, $"… {all.Count - maxLines} more lines\n");
                break;
            }
            sb.Append(op).Append(line).Append('\n');
        }
        return sb.ToString();
    }

    private static void Lcs(string[] a, string[] b, int offset, int n, int m, List<(char, string, int, int)> ops)
    {
        var len = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
                len[i, j] = a[offset + i] == b[offset + j] ? len[i + 1, j + 1] + 1 : Math.Max(len[i + 1, j], len[i, j + 1]);
        }
        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[offset + x] == b[offset + y])
            {
                ops.Add((' ', a[offset + x], offset + x, offset + y));
                x++;
                y++;
            }
            else if (x < n && (y == m || len[x + 1, y] >= len[x, y + 1]))
            {
                ops.Add(('-', a[offset + x], offset + x, -1)); // removals before additions, as diff shows them
                x++;
            }
            else
            {
                ops.Add(('+', b[offset + y], -1, offset + y));
                y++;
            }
        }
    }

    private static string[] SplitLines(string text)
    {
        if (text.Length == 0)
            return [];
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }
}
