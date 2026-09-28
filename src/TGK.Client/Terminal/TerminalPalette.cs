using SkiaSharp;
using TGK.Terminal;

namespace TGK.Client.Terminal;

/// <summary>
/// Maps <see cref="TermColor"/>s to pixels: a soft 16-color scheme tuned for the dark UI, followed by the standard
/// xterm 6×6×6 cube and grayscale ramp, plus the default foreground/background and cursor colors.
/// </summary>
public sealed class TerminalPalette
{
    private readonly SKColor[] _colors = new SKColor[256];

    public TerminalPalette()
    {
        uint[] ansi =
        [
            0xFF2A2D37, 0xFFE8646A, 0xFF8FCB7B, 0xFFE6C07B, 0xFF3F83DE, 0xFFC792EA, 0xFF3FB4C2, 0xFFCDD3DE, // normal
            0xFF5E6575, 0xFFFF7E84, 0xFFAEE58F, 0xFFFFD68A, 0xFF82C4FF, 0xFFDDAAFF, 0xFF7CDCE6, 0xFFF5F7FA, // bright
        ];
        for (int i = 0; i < 16; i++)
            _colors[i] = new SKColor(ansi[i]);
        for (int i = 16; i < 256; i++)
            _colors[i] = new SKColor(XtermPalette.Default[i]);
    }

    public SKColor Foreground { get; } = new(0xD8, 0xDC, 0xE4);
    public SKColor Background { get; } = Theme.TerminalBg;
    public SKColor Cursor { get; } = new(0xE8, 0xEB, 0xF1);
    public SKColor Selection { get; } = Theme.Accent.WithAlpha(96);

    /// <summary>Resolves the foreground and background of a cell style, applying bold-bright, dim, inverse and hidden.</summary>
    public (SKColor Fg, SKColor Bg) Resolve(CellStyle style)
    {
        CellFlags flags = style.Flags;
        TermColor fgColor = style.Fg;
        // Like xterm, bold text in one of the 8 basic colors uses its bright variant (bold black stays readable).
        if ((flags & CellFlags.Bold) != 0 && fgColor.Kind == TermColorKind.Indexed && fgColor.Index < 8)
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
