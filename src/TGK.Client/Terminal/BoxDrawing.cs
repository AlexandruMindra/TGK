using System;
using SkiaSharp;

namespace TGK.Client.Terminal;

/// <summary>
/// Draws box-drawing (U+2500–U+257F), block-element (U+2580–U+259F) and Powerline separator characters procedurally,
/// so lines join seamlessly across cells instead of leaving the gaps a font glyph shows when the cell is taller than
/// the glyph. UI thread only (the paints are shared).
/// </summary>
public static class BoxDrawing
{
    // Arm weights per character from U+2500, four digits each in the order up, right, down, left:
    // 0 none, 1 light, 2 heavy, 3 double. '.' marks characters left to the font (dashed lines) or drawn specially.
    private const string Arms =
        "0101020210102020" + "................................" +                       // 2500-250B
        "0110021001200220001100120021002211001200210022001001100220012002" +       // 250C-251B
        "1110121021101120212022101220222010111012201110212021201210222022" +       // 251C-252B
        "0111011202110212012101220221022211011102120112022101210222012202" +       // 252C-253B
        "1111111212111212211111212121211222111122122122121222212222212222" +       // 253C-254B
        "................" +                                                       // 254C-254F
        "0303303003100130033000130031003313003100330010033001300313103130" +       // 2550-255F
        "3330101330313033031301310333130331013303131331313333" +                   // 2560-256C
        "............................" +                                           // 256D-2573
        "000110000100001000022000020000200201102001022010";                        // 2574-257F

    private static readonly SKPaint Fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private static readonly SKPaint Smooth = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private static readonly SKPaint Stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Butt };

    /// <summary>True for the characters <see cref="Draw"/> handles.</summary>
    public static bool Handles(int rune) => rune switch
    {
        >= 0x2500 and <= 0x257F => Arms[(rune - 0x2500) * 4] != '.' || rune is >= 0x256D and <= 0x2573,
        >= 0x2580 and <= 0x259F => true,
        >= 0xE0B0 and <= 0xE0B3 => true,
        _ => false,
    };

    /// <summary>Draws <paramref name="rune"/> into the cell (<paramref name="x"/>, <paramref name="y"/>, <paramref name="w"/>, <paramref name="h"/>).</summary>
    public static void Draw(SKCanvas c, int rune, float x, float y, float w, float h, SKColor color, float light)
    {
        Fill.Color = Smooth.Color = Stroke.Color = color;
        if (rune >= 0x2580 && rune <= 0x259F)
            DrawBlock(c, rune, x, y, w, h, color);
        else if (rune >= 0xE0B0)
            DrawPowerline(c, rune, x, y, w, h, light);
        else if (rune is >= 0x256D and <= 0x2573)
            DrawCurve(c, rune, x, y, w, h, light);
        else
            DrawArms(c, rune, x, y, w, h, light);
    }

    private static void DrawArms(SKCanvas c, int rune, float x, float y, float w, float h, float t1)
    {
        int i = (rune - 0x2500) * 4;
        int up = Arms[i] - '0', right = Arms[i + 1] - '0', down = Arms[i + 2] - '0', left = Arms[i + 3] - '0';
        float t2 = Math.Max(2, t1 * 2);
        float g = Math.Min(Math.Max(t1 + 1, MathF.Round(w / 5f)), MathF.Floor(w / 2f - t1)); // double-line offset
        float cx = MathF.Floor(x + w / 2f), cy = MathF.Floor(y + h / 2f);
        float f = MathF.Floor(t1 / 2f);
        float Thick(int k) => k == 1 ? t1 : k == 2 ? t2 : 0;
        float tv = Math.Max(Thick(up), Thick(down)), th = Math.Max(Thick(left), Thick(right));
        bool dblV = up == 3 || down == 3, dblH = left == 3 || right == 3;

        // Horizontal arms.
        if (right is 1 or 2)
        {
            float t = Thick(right);
            float x0 = dblV ? (up == 3 && down == 3 && left == 0 ? cx + g - f : cx - g - f) : cx - MathF.Floor((tv > 0 ? tv : t) / 2f);
            Rect(c, x0, cy - MathF.Floor(t / 2f), x + w, t);
        }
        if (left is 1 or 2)
        {
            float t = Thick(left);
            float x1 = dblV ? (up == 3 && down == 3 && right == 0 ? cx - g - f + t1 : cx + g - f + t1)
                : cx - MathF.Floor((tv > 0 ? tv : t) / 2f) + (tv > 0 ? tv : t);
            Rect(c, x, cy - MathF.Floor(t / 2f), x1, t);
        }
        if (right == 3)
        {
            Rect(c, up == 3 ? cx + g - f : down == 3 ? cx - g - f : cx - f, cy - g - f, x + w, t1);
            Rect(c, down == 3 ? cx + g - f : up == 3 ? cx - g - f : cx - f, cy + g - f, x + w, t1);
        }
        if (left == 3)
        {
            Rect(c, x, cy - g - f, (up == 3 ? cx - g : down == 3 ? cx + g : cx) - f + t1, t1);
            Rect(c, x, cy + g - f, (down == 3 ? cx - g : up == 3 ? cx + g : cx) - f + t1, t1);
        }

        // Vertical arms.
        if (down is 1 or 2)
        {
            float t = Thick(down);
            float y0 = dblH ? (left == 3 && right == 3 && up == 0 ? cy + g - f : cy - g - f) : cy - MathF.Floor((th > 0 ? th : t) / 2f);
            VRect(c, cx - MathF.Floor(t / 2f), y0, y + h, t);
        }
        if (up is 1 or 2)
        {
            float t = Thick(up);
            float y1 = dblH ? (left == 3 && right == 3 && down == 0 ? cy - g - f + t1 : cy + g - f + t1)
                : cy - MathF.Floor((th > 0 ? th : t) / 2f) + (th > 0 ? th : t);
            VRect(c, cx - MathF.Floor(t / 2f), y, y1, t);
        }
        if (down == 3)
        {
            VRect(c, cx - g - f, left == 3 ? cy + g - f : right == 3 ? cy - g - f : cy - f, y + h, t1);
            VRect(c, cx + g - f, right == 3 ? cy + g - f : left == 3 ? cy - g - f : cy - f, y + h, t1);
        }
        if (up == 3)
        {
            VRect(c, cx - g - f, y, (left == 3 ? cy - g : right == 3 ? cy + g : cy) - f + t1, t1);
            VRect(c, cx + g - f, y, (right == 3 ? cy - g : left == 3 ? cy + g : cy) - f + t1, t1);
        }
    }

    // Horizontal bar from x0 to x1 with top edge at top.
    private static void Rect(SKCanvas c, float x0, float top, float x1, float t)
    {
        if (x1 > x0)
            c.DrawRect(new SKRect(x0, top, x1, top + t), Fill);
    }

    // Vertical bar from y0 to y1 with left edge at left.
    private static void VRect(SKCanvas c, float left, float y0, float y1, float t)
    {
        if (y1 > y0)
            c.DrawRect(new SKRect(left, y0, left + t, y1), Fill);
    }

    private static void DrawCurve(SKCanvas c, int rune, float x, float y, float w, float h, float t1)
    {
        Stroke.StrokeWidth = t1;
        float lx = MathF.Floor(x + w / 2f) - MathF.Floor(t1 / 2f) + t1 / 2f; // centers of the lines they continue
        float ly = MathF.Floor(y + h / 2f) - MathF.Floor(t1 / 2f) + t1 / 2f;
        float r = Math.Min(w, h) / 2f;
        using var path = new SKPathBuilder();
        switch (rune)
        {
            case 0x256D: // ╭ down and right
                path.MoveTo(lx, y + h);
                path.ArcTo(new SKRect(lx, ly, lx + 2 * r, ly + 2 * r), 180, 90, false);
                path.LineTo(x + w, ly);
                break;
            case 0x256E: // ╮ down and left
                path.MoveTo(x, ly);
                path.ArcTo(new SKRect(lx - 2 * r, ly, lx, ly + 2 * r), 270, 90, false);
                path.LineTo(lx, y + h);
                break;
            case 0x256F: // ╯ up and left
                path.MoveTo(lx, y);
                path.ArcTo(new SKRect(lx - 2 * r, ly - 2 * r, lx, ly), 0, 90, false);
                path.LineTo(x, ly);
                break;
            case 0x2570: // ╰ up and right
                path.MoveTo(lx, y);
                path.ArcTo(new SKRect(lx, ly - 2 * r, lx + 2 * r, ly), 180, -90, false);
                path.LineTo(x + w, ly);
                break;
            default: // ╱ ╲ ╳
                if (rune != 0x2572)
                {
                    path.MoveTo(x + w, y);
                    path.LineTo(x, y + h);
                }
                if (rune != 0x2571)
                {
                    path.MoveTo(x, y);
                    path.LineTo(x + w, y + h);
                }
                break;
        }
        using SKPath curve = path.Detach();
        c.DrawPath(curve, Stroke);
    }

    private static void DrawBlock(SKCanvas c, int rune, float x, float y, float w, float h, SKColor color)
    {
        // Snap to whole pixels so neighbouring blocks tile without seams.
        float x0 = MathF.Round(x), x1 = MathF.Round(x + w), y0 = MathF.Round(y), y1 = MathF.Round(y + h);
        float wr = x1 - x0, hr = y1 - y0;
        float Eighths(float size, int n) => MathF.Round(size * n / 8f);
        switch (rune)
        {
            case 0x2580: Box(x0, y0, x1, y0 + Eighths(hr, 4)); break;
            case >= 0x2581 and <= 0x2588: Box(x0, y1 - Eighths(hr, rune - 0x2580), x1, y1); break;
            case >= 0x2589 and <= 0x258F: Box(x0, y0, x0 + Eighths(wr, 0x2590 - rune), y1); break;
            case 0x2590: Box(x0 + Eighths(wr, 4), y0, x1, y1); break;
            case >= 0x2591 and <= 0x2593:
                Fill.Color = color.WithAlpha((byte)(color.Alpha * (rune - 0x2590) / 4));
                Box(x0, y0, x1, y1);
                break;
            case 0x2594: Box(x0, y0, x1, y0 + Eighths(hr, 1)); break;
            case 0x2595: Box(x1 - Eighths(wr, 1), y0, x1, y1); break;
            default:
                // Quadrants U+2596–U+259F; bits: 1 upper left, 2 upper right, 4 lower left, 8 lower right.
                ReadOnlySpan<byte> masks = [4, 8, 1, 13, 9, 7, 11, 2, 6, 14];
                int mask = masks[rune - 0x2596];
                float mx = x0 + Eighths(wr, 4), my = y0 + Eighths(hr, 4);
                if ((mask & 1) != 0) Box(x0, y0, mx, my);
                if ((mask & 2) != 0) Box(mx, y0, x1, my);
                if ((mask & 4) != 0) Box(x0, my, mx, y1);
                if ((mask & 8) != 0) Box(mx, my, x1, y1);
                break;
        }

        void Box(float l, float t, float r, float b) => c.DrawRect(new SKRect(l, t, r, b), Fill);
    }

    private static void DrawPowerline(SKCanvas c, int rune, float x, float y, float w, float h, float t1)
    {
        using var builder = new SKPathBuilder();
        bool pointsRight = rune is 0xE0B0 or 0xE0B1;
        float tip = pointsRight ? x + w : x, back = pointsRight ? x : x + w;
        builder.MoveTo(back, y);
        builder.LineTo(tip, y + h / 2f);
        builder.LineTo(back, y + h);
        bool solid = rune is 0xE0B0 or 0xE0B2;
        if (solid)
            builder.Close();
        using SKPath path = builder.Detach();
        if (solid)
        {
            c.DrawPath(path, Smooth);
        }
        else
        {
            Stroke.StrokeWidth = t1;
            c.DrawPath(path, Stroke);
        }
    }
}
