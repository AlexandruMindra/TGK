using System;
using System.Text;
using Silk.NET.Input;

namespace TGK.Client.Input;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Super = 8,
}

/// <summary>A key press (or the OS's auto-repeat of one) with the modifiers held at that moment.</summary>
public readonly record struct KeyStroke(Key Key, KeyModifiers Modifiers, bool IsRepeat = false)
{
    public bool Ctrl => (Modifiers & KeyModifiers.Ctrl) != 0;
    public bool Alt => (Modifiers & KeyModifiers.Alt) != 0;
    public bool Shift => (Modifiers & KeyModifiers.Shift) != 0;
    public bool Super => (Modifiers & KeyModifiers.Super) != 0;

    /// <summary>True for <paramref name="key"/> with exactly <paramref name="modifiers"/> held.</summary>
    public bool Is(Key key, KeyModifiers modifiers = KeyModifiers.None) => Key == key && Modifiers == modifiers;

    /// <summary>Enter or keypad Enter.</summary>
    public bool IsEnter => Key is Key.Enter or Key.KeypadEnter;

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Ctrl) sb.Append("Ctrl+");
        if (Alt) sb.Append("Alt+");
        if (Shift) sb.Append("Shift+");
        if (Super) sb.Append("Super+");
        sb.Append(Key);
        if (IsRepeat) sb.Append(" (repeat)");
        return sb.ToString();
    }
}
