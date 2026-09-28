using System;
using System.Collections.Generic;
using System.Text;
using Blossom.Utils;
using SkiaSharp;

namespace TGK.Client.Controls;

public enum TextAlignment
{
    Left,
    Center,
    Right,
}

/// <summary>
/// Immediate-mode drawing helpers used by the custom-drawn controls: text with ellipsis and per-codepoint
/// font fallback, rounded rectangles, shadows. UI thread only (paints and fonts are shared).
/// </summary>
public static class Gfx
{
    private static readonly Dictionary<(float Size, SKTypeface Face), SKPaint> FontCache = new();
    private static readonly SKPaint Fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private static readonly SKPaint Stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private static readonly SKPaint ShadowPaint = new() { IsAntialias = true };

    public const string Ellipsis = "…";

    /// <summary>
    /// A text paint for the UI font at <paramref name="size"/> (SkiaSharp 2.88's SKFont cannot measure strings, so text
    /// is measured and drawn with SKPaint). Shared and cached: only its color is changed, by the draw helpers.
    /// </summary>
    public static SKPaint Font(float size, int weight = Theme.WeightRegular) => Font(size, Theme.Ui(weight));

    public static SKPaint Font(float size, SKTypeface face)
    {
        if (!FontCache.TryGetValue((size, face), out SKPaint? font))
        {
            font = new SKPaint
            {
                Typeface = face,
                TextSize = size,
                IsAntialias = true,
                SubpixelText = true,
                HintingLevel = SKPaintHinting.Slight,
            };
            FontCache[(size, face)] = font;
        }
        return font;
    }

    public static float Measure(string text, float size, int weight = Theme.WeightRegular) => Measure(text, Font(size, weight));

    public static float Measure(string text, SKPaint font)
    {
        if (text.Length == 0)
            return 0;
        if (IsSimple(text))
            return font.MeasureText(text);
        float width = 0;
        foreach ((string run, SKTypeface face) in Runs(text, font.Typeface))
            width += Font(font.TextSize, face).MeasureText(run);
        return width;
    }

    /// <summary>Baseline that vertically centers cap-height text on <paramref name="centerY"/>.</summary>
    public static float Baseline(SKPaint font, float centerY)
    {
        SKFontMetrics m = font.FontMetrics;
        float cap = m.CapHeight > 0 ? m.CapHeight : -m.Ascent * 0.7f;
        return MathF.Round(centerY + cap / 2f);
    }

    /// <summary>Draws one line of text, vertically centered on <paramref name="centerY"/>, ellipsized to <paramref name="maxWidth"/>.</summary>
    public static void Text(SKCanvas c, string text, float x, float centerY, float size, int weight, SKColor color,
        TextAlignment align = TextAlignment.Left, float maxWidth = float.MaxValue) =>
        Text(c, text, x, centerY, Font(size, weight), color, align, maxWidth);

    public static void Text(SKCanvas c, string text, float x, float centerY, SKPaint font, SKColor color,
        TextAlignment align = TextAlignment.Left, float maxWidth = float.MaxValue)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
            return;
        string shown = Ellipsize(text, font, maxWidth);
        float width = Measure(shown, font);
        float left = align switch
        {
            TextAlignment.Center => x - width / 2f,
            TextAlignment.Right => x - width,
            _ => x,
        };
        DrawRuns(c, shown, left, Baseline(font, centerY), font, color);
    }

    /// <summary>Draws text at an explicit baseline without ellipsis (used by text fields).</summary>
    public static void TextAtBaseline(SKCanvas c, string text, float x, float baseline, SKPaint font, SKColor color) =>
        DrawRuns(c, text, x, baseline, font, color);

    public static string Ellipsize(string text, SKPaint font, float maxWidth)
    {
        if (Measure(text, font) <= maxWidth)
            return text;
        float ellipsisWidth = Measure(Ellipsis, font);
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Measure(text[..SafeCut(text, mid)], font) + ellipsisWidth <= maxWidth)
                lo = mid;
            else
                hi = mid - 1;
        }
        return text[..SafeCut(text, lo)].TrimEnd() + Ellipsis;
    }

    /// <summary>Greedy word wrap into at most <paramref name="maxLines"/> lines (the last one ellipsized).</summary>
    public static List<string> Wrap(string text, SKPaint font, float maxWidth, int maxLines = int.MaxValue)
    {
        var lines = new List<string>();
        foreach (string paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = new StringBuilder();
            foreach (string word in paragraph.Split(' '))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Measure(candidate, font) > maxWidth)
                {
                    lines.Add(line.ToString());
                    line.Clear().Append(word);
                }
                else
                {
                    line.Clear().Append(candidate);
                }
                // A single word wider than the line (fingerprints, paths): hard-break it.
                while (Measure(line.ToString(), font) > maxWidth && line.Length > 1)
                {
                    string s = line.ToString();
                    int fit = s.Length - 1;
                    while (fit > 1 && Measure(s[..fit], font) > maxWidth)
                        fit--;
                    lines.Add(s[..fit]);
                    line.Clear().Append(s[fit..]);
                }
            }
            lines.Add(line.ToString());
        }
        if (lines.Count > maxLines)
        {
            lines.RemoveRange(maxLines, lines.Count - maxLines);
            lines[^1] = Ellipsize(lines[^1] + " " + Ellipsis + Ellipsis, font, maxWidth);
        }
        return lines;
    }

    public static void FillRect(SKCanvas c, SKRect r, SKColor color)
    {
        Fill.Color = color;
        c.DrawRect(r, Fill);
    }

    public static void FillRound(SKCanvas c, SKRect r, float radius, SKColor color)
    {
        if (color.Alpha == 0)
            return;
        Fill.Color = color;
        c.DrawRoundRect(r, radius, radius, Fill);
    }

    /// <summary>Rounded rect with only the top corners rounded (tabs).</summary>
    public static void FillTopRound(SKCanvas c, SKRect r, float radius, SKColor color)
    {
        using var rr = new SKRoundRect();
        rr.SetRectRadii(r, [new SKPoint(radius, radius), new SKPoint(radius, radius), SKPoint.Empty, SKPoint.Empty]);
        Fill.Color = color;
        c.DrawRoundRect(rr, Fill);
    }

    /// <summary>1px (or <paramref name="width"/>) border drawn inside <paramref name="r"/>.</summary>
    public static void StrokeRound(SKCanvas c, SKRect r, float radius, SKColor color, float width = 1)
    {
        if (color.Alpha == 0)
            return;
        Stroke.Color = color;
        Stroke.StrokeWidth = width;
        var inset = SKRect.Inflate(r, -width / 2f, -width / 2f);
        c.DrawRoundRect(inset, Math.Max(0, radius - width / 2f), Math.Max(0, radius - width / 2f), Stroke);
    }

    public static void Line(SKCanvas c, float x0, float y0, float x1, float y1, SKColor color, float width = 1)
    {
        Stroke.Color = color;
        Stroke.StrokeWidth = width;
        c.DrawLine(x0, y0, x1, y1, Stroke);
    }

    public static void Circle(SKCanvas c, float cx, float cy, float radius, SKColor color)
    {
        Fill.Color = color;
        c.DrawCircle(cx, cy, radius, Fill);
    }

    /// <summary>Soft drop shadow for a rounded card; <paramref name="r"/> is the card rect.</summary>
    public static void Shadow(SKCanvas c, SKRect r, float radius, float blur, float offsetY, SKColor color)
    {
        ShadowPaint.Color = color;
        ShadowPaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur / 2f);
        r.Offset(0, offsetY);
        c.DrawRoundRect(r, radius, radius, ShadowPaint);
        ShadowPaint.MaskFilter = null;
    }

    private static void DrawRuns(SKCanvas c, string text, float x, float baseline, SKPaint font, SKColor color)
    {
        if (IsSimple(text))
        {
            font.Color = color;
            c.DrawText(text, x, baseline, font);
            return;
        }
        foreach ((string run, SKTypeface face) in Runs(text, font.Typeface))
        {
            SKPaint runFont = Font(font.TextSize, face);
            runFont.Color = color;
            c.DrawText(run, x, baseline, runFont);
            x += runFont.MeasureText(run);
        }
    }

    // Latin text never needs fallback with any of the UI fonts we accept; skip the per-codepoint glyph checks.
    private static bool IsSimple(string text)
    {
        foreach (char ch in text)
        {
            if (ch > 'ɏ' && ch != '…' && ch != '•' && ch != '·' && ch != '–' && ch != '—' && ch != '×')
                return false;
        }
        return true;
    }

    /// <summary>Splits text into runs that each render with one typeface (primary or a symbol/script fallback).</summary>
    private static IEnumerable<(string Run, SKTypeface Face)> Runs(string text, SKTypeface primary)
    {
        var run = new StringBuilder();
        SKTypeface? runFace = null;
        for (int i = 0; i < text.Length; i++)
        {
            int cp = char.IsSurrogatePair(text, i) ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
            int units = cp > 0xFFFF ? 2 : 1;
            SKTypeface face = cp < 0x250 ? primary : Fonts.ResolveForCodepoint(primary, cp);
            if (runFace is not null && face != runFace)
            {
                yield return (run.ToString(), runFace);
                run.Clear();
            }
            runFace = face;
            run.Append(text, i, units);
            i += units - 1;
        }
        if (run.Length > 0 && runFace is not null)
            yield return (run.ToString(), runFace);
    }

    // Never cut between the halves of a surrogate pair.
    private static int SafeCut(string text, int index) =>
        index > 0 && index < text.Length && char.IsLowSurrogate(text[index]) ? index - 1 : index;
}
