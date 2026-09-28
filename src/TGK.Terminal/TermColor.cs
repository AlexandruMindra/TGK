using System;

namespace TGK.Terminal;

/// <summary>How a <see cref="TermColor"/> is specified.</summary>
public enum TermColorKind : byte
{
    /// <summary>The renderer's default foreground/background.</summary>
    Default,

    /// <summary>An index into the 256-color palette (0-15 are the ANSI colors).</summary>
    Indexed,

    /// <summary>A 24-bit true color.</summary>
    Rgb,
}

/// <summary>
/// A terminal color: default, palette index or 24-bit RGB. Packed into 32 bits
/// (kind in bits 24-25, payload in bits 0-23). Mapping to actual pixels is the renderer's job
/// (see <see cref="XtermPalette"/>).
/// </summary>
public readonly struct TermColor : IEquatable<TermColor>
{
    private readonly uint _value;

    private TermColor(uint value) => _value = value;

    /// <summary>The renderer's default color (also <c>default(TermColor)</c>).</summary>
    public static TermColor Default => default;

    /// <summary>A palette color (0-255).</summary>
    public static TermColor Indexed(byte index) => new(((uint)TermColorKind.Indexed << 24) | index);

    /// <summary>A 24-bit true color.</summary>
    public static TermColor Rgb(byte r, byte g, byte b) =>
        new(((uint)TermColorKind.Rgb << 24) | ((uint)r << 16) | ((uint)g << 8) | b);

    public TermColorKind Kind => (TermColorKind)(_value >> 24);

    public bool IsDefault => _value == 0;

    /// <summary>Palette index; meaningful only when <see cref="Kind"/> is <see cref="TermColorKind.Indexed"/>.</summary>
    public byte Index => (byte)_value;

    /// <summary>Red component; meaningful only when <see cref="Kind"/> is <see cref="TermColorKind.Rgb"/>.</summary>
    public byte R => (byte)(_value >> 16);

    public byte G => (byte)(_value >> 8);

    public byte B => (byte)_value;

    public bool Equals(TermColor other) => _value == other._value;

    public override bool Equals(object? obj) => obj is TermColor other && Equals(other);

    public override int GetHashCode() => (int)_value;

    public static bool operator ==(TermColor left, TermColor right) => left._value == right._value;

    public static bool operator !=(TermColor left, TermColor right) => left._value != right._value;

    public override string ToString() => Kind switch
    {
        TermColorKind.Indexed => $"Indexed({Index})",
        TermColorKind.Rgb => $"Rgb({R},{G},{B})",
        _ => "Default",
    };
}
