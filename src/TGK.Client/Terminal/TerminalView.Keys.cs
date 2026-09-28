using System;
using Silk.NET.Input;
using TGK.Client.Input;
using TGK.Terminal;

namespace TGK.Client.Terminal;

// Keyboard: KeyboardHub delivers key strokes (OnKey) and typed text (OnText). Printable keys are sent from OnText,
// except when Ctrl or Alt turns them into control codes / ESC-prefixed "meta" keys.
public sealed partial class TerminalView
{
    // An Alt (or Ctrl+Alt) + printable key waits one loop iteration: if a character event follows it was AltGr
    // (Windows reports AltGr as Ctrl+Alt, Linux as Alt) and the character is sent; otherwise the meta sequence is.
    private (Key Key, int Rune, KeyMods Mods, int Tick)? _pendingAlt;
    private Key? _altGrKey; // last key that produced text with AltGr held: its repeats come as text too

    public bool OnKey(KeyStroke k)
    {
        if (k.Is(Key.C, KeyModifiers.Ctrl | KeyModifiers.Shift))
        {
            Copy();
            return true;
        }
        if (k.Is(Key.V, KeyModifiers.Ctrl | KeyModifiers.Shift) || k.Is(Key.Insert, KeyModifiers.Shift))
        {
            Paste();
            return true;
        }
        if (!Emulator.Modes.AlternateScreen && (k.Is(Key.PageUp, KeyModifiers.Shift) || k.Is(Key.PageDown, KeyModifiers.Shift)))
        {
            ScrollViewport((k.Key == Key.PageUp ? 1 : -1) * Math.Max(1, Rows - 1));
            return true;
        }
        if (!InputEnabled)
            return false; // no session: let the tab handle it (Enter reconnects)

        ResolvePendingAltKey(force: true);
        var mods = ToKeyMods(k.Modifiers);
        if (ToTermKey(k.Key) is { } termKey)
        {
            SendTyped(TerminalInput.EncodeKey(termKey, mods, Emulator.Modes));
            return true;
        }

        int rune = PrintableRune(k.Key, k.Shift);
        if (rune == 0)
            return k.Ctrl || k.Alt; // unknown keys with modifiers are swallowed; the rest may be app shortcuts
        if (k.Alt)
        {
            if (!(k.IsRepeat && k.Key == _altGrKey))
                _pendingAlt = (k.Key, rune, mods, _tick);
        }
        else if (k.Ctrl)
        {
            SendTyped(TerminalInput.EncodeChar(rune, mods));
        }
        return true; // plain printable keys arrive as text
    }

    public void OnText(string text)
    {
        if (!InputEnabled)
            return;
        if (_pendingAlt is { } pending)
        {
            _altGrKey = pending.Key;
            _pendingAlt = null;
        }
        SendTyped(TerminalInput.EncodeText(text));
    }

    private void SendTyped(byte[] bytes)
    {
        if (bytes.Length == 0)
            return;
        ScrollToBottom();
        ResetBlink();
        Send(bytes);
    }

    /// <summary>Sends a pending Alt+key as a meta sequence once no character event claimed it.</summary>
    private void ResolvePendingAltKey(bool force = false)
    {
        if (_pendingAlt is not { } pending || (!force && _tick <= pending.Tick))
            return;
        _pendingAlt = null;
        _altGrKey = null;
        if (InputEnabled)
            SendTyped(TerminalInput.EncodeChar(pending.Rune, pending.Mods));
    }

    private void CancelPendingAltKey() => _pendingAlt = null;

    private static TermKey? ToTermKey(Key key) => key switch
    {
        Key.Up => TermKey.Up,
        Key.Down => TermKey.Down,
        Key.Left => TermKey.Left,
        Key.Right => TermKey.Right,
        Key.Home => TermKey.Home,
        Key.End => TermKey.End,
        Key.PageUp => TermKey.PageUp,
        Key.PageDown => TermKey.PageDown,
        Key.Insert => TermKey.Insert,
        Key.Delete => TermKey.Delete,
        Key.Backspace => TermKey.Backspace,
        Key.Enter or Key.KeypadEnter => TermKey.Enter,
        Key.Tab => TermKey.Tab,
        Key.Escape => TermKey.Escape,
        >= Key.F1 and <= Key.F12 => TermKey.F1 + (key - Key.F1),
        _ => null,
    };

    // The character a printable key produces on a US layout, Shift included; used only when Ctrl/Alt suppress the
    // text event (e.g. Alt+Shift+. must send ESC > for readline's end-of-history).
    private static int PrintableRune(Key key, bool shift) => key switch
    {
        >= Key.A and <= Key.Z => (shift ? 'A' : 'a') + (key - Key.A),
        >= Key.Number0 and <= Key.Number9 => shift ? ")!@#$%^&*("[key - Key.Number0] : '0' + (key - Key.Number0),
        >= Key.Keypad0 and <= Key.Keypad9 => '0' + (key - Key.Keypad0),
        Key.Space => ' ',
        Key.Apostrophe => shift ? '"' : '\'',
        Key.Comma => shift ? '<' : ',',
        Key.Minus => shift ? '_' : '-',
        Key.Period => shift ? '>' : '.',
        Key.Slash => shift ? '?' : '/',
        Key.Semicolon => shift ? ':' : ';',
        Key.Equal => shift ? '+' : '=',
        Key.LeftBracket => shift ? '{' : '[',
        Key.BackSlash => shift ? '|' : '\\',
        Key.RightBracket => shift ? '}' : ']',
        Key.GraveAccent => shift ? '~' : '`',
        Key.KeypadSubtract => '-',
        Key.KeypadDecimal => '.',
        Key.KeypadDivide => '/',
        Key.KeypadEqual => '=',
        Key.KeypadMultiply => '*',
        Key.KeypadAdd => '+',
        _ => 0,
    };
}
