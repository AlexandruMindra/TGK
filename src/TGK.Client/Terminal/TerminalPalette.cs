using SkiaSharp;
using TGK.Terminal;

namespace TGK.Client.Terminal;

/// <summary>
/// Maps <see cref="TermColor"/>s to pixels: the 16 colors of a <see cref="ColorScheme"/>, followed by the standard
/// xterm 6×6×6 cube and grayscale ramp, plus the scheme's default foreground/background, cursor and selection colors.
/// </summary>
public sealed class TerminalPalette
{
    private readonly SKColor[] _colors = new SKColor[256];

    public TerminalPalette(ColorScheme scheme)
    {
        Scheme = scheme;
        for (int i = 0; i < 16; i++)
            _colors[i] = scheme.Ansi[i];
        for (int i = 16; i < 256; i++)
            _colors[i] = new SKColor(XtermPalette.Default[i]);
    }

    public ColorScheme Scheme { get; }
    public SKColor Foreground => Scheme.Foreground;
    public SKColor Background => Scheme.Background;
    public SKColor Cursor => Scheme.Cursor;
    public SKColor Selection => Scheme.Selection;

    /// <summary>Resolves the foreground and background of a cell style, applying bold-bright, dim, inverse and hidden.</summary>
    public (SKColor Fg, SKColor Bg) Resolve(CellStyle style)
    {
        CellFlags flags = style.Flags;
        TermColor fgColor = style.Fg;
        // Like xterm, bold text in one of the 8 basic colors uses its bright variant (bold black stays readable).
        if ((flags & CellFlags.Bold) != 0 && Scheme.BoldIsBright && fgColor.Kind == TermColorKind.Indexed && fgColor.Index < 8)
            fgColor = TermColor.Indexed((byte)(fgColor.Index + 8));

        SKColor fg = Map(fgColor, Foreground);
        SKColor bg = Map(style.Bg, Background);
        if ((flags & CellFlags.Inverse) != 0)
            (fg, bg) = (bg, fg);
        if ((flags & CellFlags.Dim) != 0)
            fg = Theme.Mix(fg, bg, 0.45f);
        if ((flags & CellFlags.Hidden) != 0)
            fg = bg;
        return (fg, bg);
    }

    private SKColor Map(TermColor color, SKColor fallback) => color.Kind switch
    {
        TermColorKind.Indexed => _colors[color.Index],
        TermColorKind.Rgb => new SKColor(color.R, color.G, color.B),
        _ => fallback,
    };
}
