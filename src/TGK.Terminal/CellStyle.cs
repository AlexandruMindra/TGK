using System;

namespace TGK.Terminal;

/// <summary>Text attributes set by SGR.</summary>
[Flags]
public enum CellFlags : ushort
{
    None = 0,
    Bold = 1 << 0,
    Dim = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Blink = 1 << 4,
    Inverse = 1 << 5,
    Hidden = 1 << 6,
    Strikethrough = 1 << 7,

    /// <summary>SGR 21 / 4:2. Set together with <see cref="Underline"/> so simple renderers still underline.</summary>
    DoubleUnderline = 1 << 8,

    /// <summary>SGR 4:3. Set together with <see cref="Underline"/> so simple renderers still underline.</summary>
    CurlyUnderline = 1 << 9,

    /// <summary>SGR 53.</summary>
    Overline = 1 << 10,

    /// <summary>All underline variants, for clearing.</summary>
    AnyUnderline = Underline | DoubleUnderline | CurlyUnderline,
}

/// <summary>Colors and attributes of a cell. <c>default</c> is the plain default style.</summary>
public struct CellStyle : IEquatable<CellStyle>
{
    public CellStyle(TermColor fg, TermColor bg, CellFlags flags)
    {
        Fg = fg;
        Bg = bg;
        Flags = flags;
    }

    public TermColor Fg { get; set; }

    public TermColor Bg { get; set; }

    public CellFlags Flags { get; set; }

    public static CellStyle Default => default;

    public readonly bool Has(CellFlags flag) => (Flags & flag) != 0;

    public readonly bool Equals(CellStyle other) => Fg == other.Fg && Bg == other.Bg && Flags == other.Flags;

    public override readonly bool Equals(object? obj) => obj is CellStyle other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Fg, Bg, Flags);

    public static bool operator ==(CellStyle left, CellStyle right) => left.Equals(right);

    public static bool operator !=(CellStyle left, CellStyle right) => !left.Equals(right);

    public override readonly string ToString() => $"Fg={Fg} Bg={Bg} Flags={Flags}";
}
