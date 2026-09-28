using System;

namespace TGK.Terminal;

// SGR: Select Graphic Rendition.
public sealed partial class TerminalEmulator
{
    private void SelectGraphicRendition(VtParams p)
    {
        if (p.Count == 0)
        {
            _style = default;
            return;
        }

        var style = _style;
        for (int i = 0; i < p.Count;)
        {
            int code = p[i];
            int next = i + 1;
            while (next < p.Count && p.IsSubParam(next))
                next++; // colon sub-parameters belong to this code
            bool hasSub = next > i + 1;

            switch (code)
            {
                case 0:
                    style = default;
                    break;
                case 1:
                    style.Flags |= CellFlags.Bold;
                    break;
                case 2:
                    style.Flags |= CellFlags.Dim;
                    break;
                case 3:
                    style.Flags |= CellFlags.Italic;
                    break;
                case 4:
                    style.Flags = (style.Flags & ~CellFlags.AnyUnderline) | UnderlineStyle(hasSub ? p[i + 1] : 1);
                    break;
                case 5:
                case 6:
                    style.Flags |= CellFlags.Blink;
                    break;
                case 7:
                    style.Flags |= CellFlags.Inverse;
                    break;
                case 8:
                    style.Flags |= CellFlags.Hidden;
                    break;
                case 9:
                    style.Flags |= CellFlags.Strikethrough;
                    break;
                case 21:
                    style.Flags = (style.Flags & ~CellFlags.AnyUnderline) | UnderlineStyle(2);
                    break;
                case 22:
                    style.Flags &= ~(CellFlags.Bold | CellFlags.Dim);
                    break;
                case 23:
                    style.Flags &= ~CellFlags.Italic;
                    break;
                case 24:
                    style.Flags &= ~CellFlags.AnyUnderline;
                    break;
                case 25:
                    style.Flags &= ~CellFlags.Blink;
                    break;
                case 27:
                    style.Flags &= ~CellFlags.Inverse;
                    break;
                case 28:
                    style.Flags &= ~CellFlags.Hidden;
                    break;
                case 29:
                    style.Flags &= ~CellFlags.Strikethrough;
                    break;
                case >= 30 and <= 37:
                    style.Fg = TermColor.Indexed((byte)(code - 30));
                    break;
                case 39:
                    style.Fg = TermColor.Default;
                    break;
                case >= 40 and <= 47:
                    style.Bg = TermColor.Indexed((byte)(code - 40));
                    break;
                case 49:
                    style.Bg = TermColor.Default;
                    break;
                case 53:
                    style.Flags |= CellFlags.Overline;
                    break;
                case 55:
                    style.Flags &= ~CellFlags.Overline;
                    break;
                case >= 90 and <= 97:
                    style.Fg = TermColor.Indexed((byte)(code - 90 + 8));
                    break;
                case >= 100 and <= 107:
                    style.Bg = TermColor.Indexed((byte)(code - 100 + 8));
                    break;
                case 38:
                case 48:
                case 58: // underline color: parsed so its parameters are skipped, but not stored
                    TermColor? color = hasSub
                        ? ParseColonColor(p, i + 1, next)
                        : ParseSemicolonColor(p, i + 1, out next);
                    if (color is { } c)
                    {
                        if (code == 38)
                            style.Fg = c;
                        else if (code == 48)
                            style.Bg = c;
                    }

                    break;
            }

            i = next;
        }

        _style = style;
    }

    private static CellFlags UnderlineStyle(int kind) => kind switch
    {
        0 => CellFlags.None,
        2 => CellFlags.Underline | CellFlags.DoubleUnderline,
        3 => CellFlags.Underline | CellFlags.CurlyUnderline,
        _ => CellFlags.Underline, // 1 single, 4 dotted and 5 dashed
    };

    // 38:5:n, 38:2:r:g:b or 38:2:colorspace:r:g:b in p[from..to).
    private static TermColor? ParseColonColor(VtParams p, int from, int to)
    {
        int count = to - from;
        return p[from] switch
        {
            5 when count >= 2 => TermColor.Indexed(ClampByte(p[from + 1])),
            2 when count >= 5 => TermColor.Rgb(ClampByte(p[from + 2]), ClampByte(p[from + 3]), ClampByte(p[from + 4])),
            2 when count == 4 => TermColor.Rgb(ClampByte(p[from + 1]), ClampByte(p[from + 2]), ClampByte(p[from + 3])),
            _ => null,
        };
    }

    // 38;5;n or 38;2;r;g;b starting at p[from]; next receives the index after the consumed parameters.
    private static TermColor? ParseSemicolonColor(VtParams p, int from, out int next)
    {
        next = from + 1;
        if (from >= p.Count)
            return null;

        switch (p[from])
        {
            case 5 when from + 1 < p.Count:
                next = from + 2;
                return TermColor.Indexed(ClampByte(p[from + 1]));
            case 2 when from + 3 < p.Count:
                next = from + 4;
                return TermColor.Rgb(ClampByte(p[from + 1]), ClampByte(p[from + 2]), ClampByte(p[from + 3]));
            default:
                next = p.Count; // malformed: ignore the rest, like xterm
                return null;
        }
    }

    private static byte ClampByte(int value) => (byte)Math.Min(value, 255);
}
