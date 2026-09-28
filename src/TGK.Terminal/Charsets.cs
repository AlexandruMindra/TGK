namespace TGK.Terminal;

/// <summary>94-character sets that can be designated into G0/G1.</summary>
internal enum Charset : byte
{
    Ascii,

    /// <summary>ESC ( 0: DEC Special Graphics (line drawing).</summary>
    DecSpecialGraphics,

    /// <summary>ESC ( A: like ASCII but '#' is '£'.</summary>
    Uk,
}

internal static class Charsets
{
    // DEC Special Graphics for 0x5F..0x7E.
    private static readonly int[] DecGraphics =
    [
        0x00A0, // _ blank
        0x25C6, // ` diamond
        0x2592, // a checkerboard
        0x2409, // b HT
        0x240C, // c FF
        0x240D, // d CR
        0x240A, // e LF
        0x00B0, // f degree
        0x00B1, // g plus/minus
        0x2424, // h NL
        0x240B, // i VT
        0x2518, // j ┘
        0x2510, // k ┐
        0x250C, // l ┌
        0x2514, // m └
        0x253C, // n ┼
        0x23BA, // o scan line 1
        0x23BB, // p scan line 3
        0x2500, // q ─ (scan line 5)
        0x23BC, // r scan line 7
        0x23BD, // s scan line 9
        0x251C, // t ├
        0x2524, // u ┤
        0x2534, // v ┴
        0x252C, // w ┬
        0x2502, // x │
        0x2264, // y ≤
        0x2265, // z ≥
        0x03C0, // { pi
        0x2260, // | not equal
        0x00A3, // } pound
        0x00B7, // ~ centered dot
    ];

    public static Charset FromDesignator(char final) => final switch
    {
        '0' => Charset.DecSpecialGraphics,
        'A' => Charset.Uk,
        _ => Charset.Ascii,
    };

    /// <summary>Maps an ASCII character through <paramref name="charset"/>.</summary>
    public static int Map(Charset charset, int c) => charset switch
    {
        Charset.DecSpecialGraphics when c is >= 0x5F and <= 0x7E => DecGraphics[c - 0x5F],
        Charset.Uk when c == '#' => 0x00A3,
        _ => c,
    };
}
