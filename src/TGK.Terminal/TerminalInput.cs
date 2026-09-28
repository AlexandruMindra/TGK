using System;
using System.Globalization;
using System.Text;

namespace TGK.Terminal;

/// <summary>Keys that <see cref="TerminalInput.EncodeKey"/> knows how to encode.</summary>
public enum TermKey
{
    /// <summary>A printable character; its code point is passed separately.</summary>
    Char,
    Up,
    Down,
    Left,
    Right,
    Home,
    End,
    PageUp,
    PageDown,
    Insert,
    Delete,
    Backspace,
    Enter,
    Tab,
    Escape,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
}

[Flags]
public enum KeyMods
{
    None = 0,
    Shift = 1,
    Alt = 2,
    Ctrl = 4,
}

public enum MouseButton
{
    /// <summary>No button (motion without a pressed button).</summary>
    None,
    Left,
    Middle,
    Right,
    WheelUp,
    WheelDown,
    WheelLeft,
    WheelRight,
}

public enum MouseEventKind
{
    Press,
    Release,
    Motion,
}

/// <summary>Encodes keyboard, paste, mouse and focus input into the bytes an xterm would send.</summary>
public static class TerminalInput
{
    private const byte Esc = 0x1B;

    /// <summary>
    /// Encodes a key press. For <see cref="TermKey.Char"/>, <paramref name="rune"/> is the character as
    /// typed (Shift already applied, e.g. 'A'); Ctrl turns letters and <c>@[\]^_{|}~</c>/space into C0
    /// controls and Alt prefixes ESC. Returns an empty array for keys that send nothing.
    /// </summary>
    public static byte[] EncodeKey(TermKey key, KeyMods mods, TerminalModes modes, int rune = 0)
    {
        // xterm modifier parameter: 1 + (Shift 1 | Alt 2 | Ctrl 4)
        int m = 1 + ((mods & KeyMods.Shift) != 0 ? 1 : 0) + ((mods & KeyMods.Alt) != 0 ? 2 : 0)
            + ((mods & KeyMods.Ctrl) != 0 ? 4 : 0);
        bool alt = (mods & KeyMods.Alt) != 0;
        bool ctrl = (mods & KeyMods.Ctrl) != 0;

        return key switch
        {
            TermKey.Char => EncodeChar(rune, mods),
            TermKey.Up => Cursor('A', m, modes),
            TermKey.Down => Cursor('B', m, modes),
            TermKey.Right => Cursor('C', m, modes),
            TermKey.Left => Cursor('D', m, modes),
            TermKey.Home => Cursor('H', m, modes),
            TermKey.End => Cursor('F', m, modes),
            TermKey.Insert => Tilde(2, m),
            TermKey.Delete => Tilde(3, m),
            TermKey.PageUp => Tilde(5, m),
            TermKey.PageDown => Tilde(6, m),
            TermKey.Backspace => WithAlt(alt, ctrl ? (byte)0x08 : (byte)0x7F),
            TermKey.Enter => alt ? [Esc, (byte)'\r'] : modes.LineFeedNewLine ? "\r\n"u8.ToArray() : [(byte)'\r'],
            TermKey.Tab => (mods & KeyMods.Shift) != 0 ? "\x1b[Z"u8.ToArray() : WithAlt(alt, (byte)'\t'),
            TermKey.Escape => WithAlt(alt, Esc),
            >= TermKey.F1 and <= TermKey.F4 => FunctionSs3((char)('P' + (key - TermKey.F1)), m),
            >= TermKey.F5 and <= TermKey.F12 => Tilde(FunctionCodes[key - TermKey.F5], m),
            _ => [],
        };
    }

    /// <summary>Encodes a typed character with modifiers (see <see cref="EncodeKey"/>).</summary>
    public static byte[] EncodeChar(int rune, KeyMods mods)
    {
        if ((mods & KeyMods.Ctrl) != 0 && ControlCode(rune) is int control)
            return WithAlt((mods & KeyMods.Alt) != 0, (byte)control);

        var text = EncodeRune(rune);
        if ((mods & KeyMods.Alt) == 0)
            return text;
        var result = new byte[text.Length + 1];
        result[0] = Esc;
        text.CopyTo(result, 1);
        return result;
    }

    /// <summary>Encodes typed text (e.g. from an IME) as UTF-8.</summary>
    public static byte[] EncodeText(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>
    /// Encodes pasted text: newlines become CR, and in bracketed-paste mode the text is wrapped in
    /// ESC[200~ … ESC[201~ with any embedded bracket markers removed so the paste cannot end early.
    /// </summary>
    public static byte[] EncodePaste(string text, TerminalModes modes)
    {
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        if (!modes.BracketedPaste)
            return Encoding.UTF8.GetBytes(text);

        string stripped;
        do
        {
            stripped = text;
            text = text.Replace("\x1b[201~", "").Replace("\x1b[200~", "");
        }
        while (text.Length != stripped.Length);

        return Encoding.UTF8.GetBytes("\x1b[200~" + text + "\x1b[201~");
    }

    /// <summary>
    /// Encodes a mouse event at the 0-based cell (<paramref name="col"/>, <paramref name="row"/>) according
    /// to the tracking mode and encoding in <paramref name="modes"/>. Returns null when the application
    /// did not ask for this event (or, in the legacy encoding, when the position is beyond column/row 223).
    /// For <see cref="MouseEventKind.Motion"/>, <paramref name="button"/> is the button held down, if any.
    /// </summary>
    public static byte[]? EncodeMouse(
        MouseButton button, int col, int row, MouseEventKind kind, KeyMods mods, TerminalModes modes)
    {
        var tracking = modes.MouseTracking;
        bool wheel = button >= MouseButton.WheelUp;
        bool wanted = tracking switch
        {
            MouseTrackingMode.X10 => kind == MouseEventKind.Press,
            MouseTrackingMode.Normal => kind != MouseEventKind.Motion,
            MouseTrackingMode.ButtonEvent => kind != MouseEventKind.Motion || button != MouseButton.None,
            MouseTrackingMode.AnyEvent => true,
            _ => false,
        };
        if (!wanted || (wheel && kind != MouseEventKind.Press) || (button == MouseButton.None && kind != MouseEventKind.Motion))
            return null;

        int code = button switch
        {
            MouseButton.Left => 0,
            MouseButton.Middle => 1,
            MouseButton.Right => 2,
            MouseButton.WheelUp => 64,
            MouseButton.WheelDown => 65,
            MouseButton.WheelLeft => 66,
            MouseButton.WheelRight => 67,
            _ => 3,
        };
        if (kind == MouseEventKind.Motion)
            code += 32;
        if (tracking != MouseTrackingMode.X10)
        {
            if ((mods & KeyMods.Shift) != 0)
                code += 4;
            if ((mods & KeyMods.Alt) != 0)
                code += 8;
            if ((mods & KeyMods.Ctrl) != 0)
                code += 16;
        }

        int x = Math.Max(0, col) + 1;
        int y = Math.Max(0, row) + 1;
        if (modes.MouseSgr)
        {
            char final = kind == MouseEventKind.Release ? 'm' : 'M';
            return Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"\x1b[<{code};{x};{y}{final}"));
        }

        // Legacy encoding: a release does not say which button; values are offset by 32 in single bytes.
        if (kind == MouseEventKind.Release)
            code = 3 | (code & (4 | 8 | 16));
        if (x > 223 || y > 223)
            return null;
        return [Esc, (byte)'[', (byte)'M', (byte)(32 + code), (byte)(32 + x), (byte)(32 + y)];
    }

    /// <summary>Encodes a focus change (ESC[I / ESC[O). Only send it when <see cref="TerminalModes.FocusEvents"/> is set.</summary>
    public static byte[] EncodeFocus(bool focused) => focused ? "\x1b[I"u8.ToArray() : "\x1b[O"u8.ToArray();

    /// <summary>Encodes a focus change, or returns null when the application did not enable focus events.</summary>
    public static byte[]? EncodeFocus(bool focused, TerminalModes modes) => modes.FocusEvents ? EncodeFocus(focused) : null;

    private static ReadOnlySpan<int> FunctionCodes => [15, 17, 18, 19, 20, 21, 23, 24]; // F5..F12

    // Cursor keys, Home and End: CSI 1;m X with modifiers, otherwise SS3 X in application mode or CSI X.
    private static byte[] Cursor(char final, int m, TerminalModes modes)
    {
        if (m > 1)
            return Ascii($"\x1b[1;{m}{final}");
        return [Esc, modes.ApplicationCursorKeys ? (byte)'O' : (byte)'[', (byte)final];
    }

    private static byte[] FunctionSs3(char final, int m) =>
        m > 1 ? Ascii($"\x1b[1;{m}{final}") : [Esc, (byte)'O', (byte)final];

    private static byte[] Tilde(int code, int m) => Ascii(m > 1 ? $"\x1b[{code};{m}~" : $"\x1b[{code}~");

    private static byte[] WithAlt(bool alt, byte b) => alt ? [Esc, b] : [b];

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] EncodeRune(int rune)
    {
        if (rune < 0x80)
            return [(byte)rune];
        return Rune.TryCreate(rune, out var r) ? Encoding.UTF8.GetBytes(r.ToString()) : [];
    }

    // The C0 control xterm sends for Ctrl+rune, or null when Ctrl does not change the character.
    private static int? ControlCode(int rune) => rune switch
    {
        >= 'a' and <= 'z' => rune - 'a' + 1,
        >= 'A' and <= 'Z' => rune - 'A' + 1,
        '@' or ' ' or '2' => 0x00,
        '[' or '{' or '3' => 0x1B,
        '\\' or '|' or '4' => 0x1C,
        ']' or '}' or '5' => 0x1D,
        '^' or '~' or '6' => 0x1E,
        '_' or '/' or '-' or '7' => 0x1F,
        '?' or '8' => 0x7F,
        _ => null,
    };
}
