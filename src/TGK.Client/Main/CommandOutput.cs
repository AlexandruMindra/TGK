using System;
using System.Globalization;
using System.Linq;
using System.Text;
using TGK.Core.Ssh;

namespace TGK.Client.Main;

/// <summary>
/// The text a run tab writes into its (read-only) terminal: each host's outcome and output. Output from the servers
/// is shown as text: only color changes (SGR) survive, every other escape sequence and control character is dropped,
/// so it can't move the cursor, clear the screen or retitle anything.
/// </summary>
public static class CommandOutput
{
    private const char Esc = '\u001b';
    private const string Reset = "\u001b[0m", Dim = "\u001b[2m", Bold = "\u001b[1m";
    private const string Red = "\u001b[31m", Green = "\u001b[32m", Yellow = "\u001b[33m";

    /// <summary>The line that starts a run's output: when, the command, how many hosts.</summary>
    public static string RunHeader(string command, int hosts, bool sudo, DateTimeOffset at) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Dim}{at.ToLocalTime():HH:mm:ss}{Reset} {Yellow}$ {Clean(command, keepColors: false).Replace("\r\n", " ")}{Reset}" +
            $"{Dim} · {hosts} host{(hosts == 1 ? "" : "s")}{(sudo ? " · as root (sudo)" : "")}{Reset}\r\n\r\n");

    /// <summary>
    /// One host's block: a title line with its name, address, outcome and time, then its output (errors in red), at most
    /// <paramref name="maxLines"/> lines of each stream.
    /// </summary>
    public static string HostBlock(HostRun run, string address, int maxLines = int.MaxValue)
    {
        var sb = new StringBuilder();
        (string color, string mark) = run.State switch
        {
            HostRunState.Succeeded => (Green, "✓"),
            HostRunState.Failed or HostRunState.Error => (Red, "✗"),
            HostRunState.Cancelled => (Yellow, "■"),
            _ => (Dim, "…"),
        };
        string outcome = run.State == HostRunState.Error ? "could not run" : run.State == HostRunState.Cancelled ? "cancelled" : run.Outcome;
        sb.Append(CultureInfo.InvariantCulture, $"{Bold}── {Clean(run.Host.DisplayName, false)}{Reset} {Dim}{Clean(address, false)}{Reset}  {color}{mark} {outcome}{Reset}");
        if (run.IsOver && run.StartedAt is not null)
            sb.Append(CultureInfo.InvariantCulture, $" {Dim}{Duration(Took(run))}{Reset}");
        sb.Append("\r\n");
        if (run.Result is { } result)
        {
            int dropped = 0;
            if (result.Stdout.Length > 0)
            {
                string stdout = Head(result.Stdout, maxLines, ref dropped);
                sb.Append(Clean(stdout, keepColors: true)).Append(Reset).Append(EndLine(stdout));
            }
            if (result.Stderr.Length > 0)
            {
                string stderr = Head(result.Stderr, maxLines, ref dropped);
                sb.Append(Red).Append(Clean(stderr, keepColors: false)).Append(Reset).Append(EndLine(stderr));
            }
            if (dropped > 0)
                sb.Append(Dim).Append(CultureInfo.InvariantCulture, $"… {dropped} more line{(dropped == 1 ? "" : "s")}: click the host in the list to see all of its output.").Append(Reset).Append("\r\n");
            if (result.Stdout.Length == 0 && result.Stderr.Length == 0)
                sb.Append(Dim).Append("(no output)").Append(Reset).Append("\r\n");
            if (result.TimedOut)
                sb.Append(Yellow).Append(CultureInfo.InvariantCulture, $"Stopped after {Duration(result.Duration)}: it ran out of time.").Append(Reset).Append("\r\n");
            if (result.Truncated)
                sb.Append(Dim).Append(CultureInfo.InvariantCulture, $"[output cut at {RemoteConnection.DefaultMaxOutput / 1024} KB per stream]").Append(Reset).Append("\r\n");
        }
        else if (run.Error is { } error)
        {
            sb.Append(run.State == HostRunState.Error ? Red : Dim).Append(Clean(error, false)).Append(Reset).Append("\r\n");
        }
        else if (!run.IsOver)
        {
            sb.Append(Dim).Append(run.State == HostRunState.Waiting ? "Waiting for its turn…" : run.State == HostRunState.Connecting ? "Connecting…" : "Running…")
                .Append(Reset).Append("\r\n");
        }
        sb.Append("\r\n");
        return sb.ToString();
    }

    /// <summary>How long the host took: the command's own time once it ran (not the prompts and connecting before it).</summary>
    public static TimeSpan Took(HostRun run) => run.Result?.Duration ?? run.Elapsed;

    /// <summary><c>0.4 s</c>, <c>12 s</c>, <c>3 min 5 s</c>.</summary>
    public static string Duration(TimeSpan d) =>
        d.TotalSeconds < 10 ? string.Create(CultureInfo.InvariantCulture, $"{d.TotalSeconds:0.0} s")
        : d.TotalMinutes < 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)d.TotalSeconds} s")
        : string.Create(CultureInfo.InvariantCulture, $"{(int)d.TotalMinutes} min {d.Seconds} s");

    /// <summary>
    /// <paramref name="text"/> for the terminal: line breaks as CR LF, tabs kept, SGR (colors) kept when
    /// <paramref name="keepColors"/>, every other escape sequence and control character dropped.
    /// </summary>
    public static string Clean(string text, bool keepColors)
    {
        var sb = new StringBuilder(text.Length + 16);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n')
            {
                sb.Append("\r\n");
            }
            else if (c == '\t')
            {
                sb.Append(c);
            }
            else if (c == Esc)
            {
                int end = SequenceEnd(text, i, out bool sgr);
                if (sgr && keepColors)
                    sb.Append(text, i, end - i);
                i = end - 1;
            }
            else if (!char.IsControl(c))
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    // The index after the escape sequence starting at text[start] (ESC): CSI up to its final byte, OSC/DCS/APC/PM/SOS
    // up to their terminator, other sequences after one more character. SGR: a CSI of digits, ';' and ':' ending in 'm'.
    private static int SequenceEnd(string text, int start, out bool sgr)
    {
        sgr = false;
        int i = start + 1;
        if (i >= text.Length)
            return i;
        char kind = text[i++];
        if (kind == '[')
        {
            bool plain = true;
            while (i < text.Length && text[i] is >= ' ' and <= '?')
            {
                plain &= text[i] is (>= '0' and <= '9') or ';' or ':';
                i++;
            }
            if (i >= text.Length)
                return i;
            sgr = plain && text[i] == 'm';
            return text[i] is >= '@' and <= '~' ? i + 1 : i;
        }
        if (kind is ']' or 'P' or '_' or '^' or 'X')
        {
            while (i < text.Length)
            {
                if (text[i] == '\u0007')
                    return i + 1;
                if (text[i] == Esc && i + 1 < text.Length && text[i + 1] == '\\')
                    return i + 2;
                i++;
            }
            return i;
        }
        return kind is >= ' ' and <= '/' && i < text.Length ? i + 1 : i; // ESC ( B and the like
    }

    // The first maxLines lines of text (counting the rest into dropped).
    private static string Head(string text, int maxLines, ref int dropped)
    {
        int end = -1;
        for (int line = 0; line < maxLines; line++)
        {
            end = text.IndexOf('\n', end + 1);
            if (end < 0 || end == text.Length - 1)
                return text;
        }
        string rest = text[(end + 1)..];
        dropped += rest.Count(c => c == '\n') + (rest.EndsWith('\n') ? 0 : 1);
        return text[..(end + 1)];
    }

    private static string EndLine(string output) => output.EndsWith('\n') ? "" : "\r\n";
}
