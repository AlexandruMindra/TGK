using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Models;
using TGK.Core.Ssh;

namespace TGK.Core.Agents;

/// <summary>
/// An SSH terminal tab open in the app, as agents may use it (see docs/AGENTS.md, "Open sessions"): its live terminal
/// (read the screen, type), and its SSH connection for the other tools. Members are called on worker threads.
/// </summary>
public interface IAgentTerminal
{
    /// <summary>Short and stable while the tab is open, e.g. <c>t3</c>.</summary>
    string Id { get; }

    /// <summary>The tab's title (the host's name, or the title the remote program set).</summary>
    string Title { get; }

    /// <summary>The saved host, or the unsaved Quick Connect entry, the tab is connected to.</summary>
    HostEntry Host { get; }

    /// <summary>The tab's SSH session is connected.</summary>
    bool IsConnected { get; }

    /// <summary>Grows whenever output arrives (to wait until a program has answered).</summary>
    long OutputVersion { get; }

    /// <summary>What the terminal shows now, with up to <paramref name="scrollbackLines"/> lines of history above it.</summary>
    Task<TerminalSnapshot> ReadAsync(int scrollbackLines, CancellationToken ct);

    /// <summary>Types <paramref name="text"/>, then presses <paramref name="keys"/>, as the user would. False when the tab is not connected or takes no input.</summary>
    Task<bool> SendAsync(string text, IReadOnlyList<TerminalKeyPress> keys, CancellationToken ct);

    /// <summary>A connection over the tab's SSH connection (<see cref="RemoteConnection.Attach"/>); null when not connected.</summary>
    RemoteConnection? AttachConnection();
}

/// <summary>A terminal's screen (rows top to bottom, trailing blanks dropped) and the history above it, oldest first.</summary>
/// <param name="ScrollbackTotal">History lines the terminal has (of which <paramref name="Scrollback"/> are the newest).</param>
/// <param name="AlternateScreen">A full-screen program (vim, top, less…) is showing; it has no history.</param>
public sealed record TerminalSnapshot(IReadOnlyList<string> Screen, IReadOnlyList<string> Scrollback, int ScrollbackTotal,
    int CursorRow, int CursorCol, int Cols, int Rows, bool AlternateScreen);

/// <summary>A key agents can press in a terminal (<see cref="Parse"/>): a named key or a character, with modifiers.</summary>
/// <param name="Key">A name of <see cref="Names"/>, or one character.</param>
public sealed record TerminalKeyPress(string Key, bool Ctrl = false, bool Alt = false, bool Shift = false)
{
    /// <summary>The named keys, as agents write them.</summary>
    public static readonly IReadOnlyList<string> Names =
    [
        "enter", "tab", "escape", "backspace", "delete", "insert", "up", "down", "left", "right", "home", "end",
        "pageup", "pagedown", "f1", "f2", "f3", "f4", "f5", "f6", "f7", "f8", "f9", "f10", "f11", "f12",
    ];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["return"] = "enter", ["esc"] = "escape", ["del"] = "delete", ["ins"] = "insert", ["bs"] = "backspace",
        ["pgup"] = "pageup", ["pgdn"] = "pagedown", ["page_up"] = "pageup", ["page_down"] = "pagedown", ["space"] = " ",
    };

    public bool IsNamed => Key.Length > 1;

    /// <summary>
    /// Reads <c>enter</c>, <c>ctrl+c</c>, <c>alt+b</c>, <c>shift+tab</c>, <c>ctrl+left</c>, <c>space</c>, <c>q</c>…
    /// (case-insensitive; <c>-</c> also separates modifiers, as in <c>C-c</c>).
    /// </summary>
    /// <exception cref="FormatException">Not a key this accepts; the message says what is.</exception>
    public static TerminalKeyPress Parse(string spec)
    {
        string text = (spec ?? "").Trim();
        if (text.Length == 0)
            throw new FormatException("A key is empty.");
        bool ctrl = false, alt = false, shift = false;
        string rest = text;
        while (rest.Length > 1)
        {
            int sep = rest.IndexOfAny(['+', '-'], 1);
            if (sep < 0)
                break;
            string mod = rest[..sep].ToLowerInvariant();
            switch (mod)
            {
                case "ctrl" or "control" or "c":
                    ctrl = true;
                    break;
                case "alt" or "meta" or "m" or "option":
                    alt = true;
                    break;
                case "shift" or "s":
                    shift = true;
                    break;
                default:
                    throw new FormatException($"Unknown modifier \"{rest[..sep]}\" in key \"{text}\" (use ctrl, alt or shift).");
            }
            rest = rest[(sep + 1)..];
        }
        if (rest.Length == 0)
            throw new FormatException($"Key \"{text}\" has no key after its modifiers.");
        if (Aliases.TryGetValue(rest, out string? alias))
            rest = alias;
        if (rest.Length > 1)
        {
            string name = rest.ToLowerInvariant();
            if (!Names.Contains(name))
                throw new FormatException($"Unknown key \"{text}\". Keys: {string.Join(", ", Names)}, a single character, with ctrl+, alt+ or shift+.");
            return new TerminalKeyPress(name, ctrl, alt, shift);
        }
        if (char.IsControl(rest[0]))
            throw new FormatException($"Key \"{text}\" is a control character; write it as ctrl+<letter> or by its name.");
        return new TerminalKeyPress(ctrl ? rest.ToLowerInvariant() : rest, ctrl, alt, shift);
    }

    public override string ToString() =>
        (Ctrl ? "ctrl+" : "") + (Alt ? "alt+" : "") + (Shift ? "shift+" : "") + (Key == " " ? "space" : Key);
}
