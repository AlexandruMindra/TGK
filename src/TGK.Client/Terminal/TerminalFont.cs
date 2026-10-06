using System;
using System.Collections.Generic;
using SkiaSharp;

namespace TGK.Client.Terminal;

/// <summary>A glyph resolved for one character: which font draws it and how far to shift it inside its cells.</summary>
public readonly record struct GlyphRef(int FontIndex, ushort Glyph, float OffsetX);

/// <summary>
/// The terminal's font at one size: cell metrics from the chosen family (see <see cref="TerminalFonts"/>) and a per-character glyph cache
/// with fallback fonts for symbols, CJK and emoji the primary font lacks. UI thread only.
/// </summary>
public sealed class TerminalFont : IDisposable
{
    private const float ItalicSkew = -0.2f;

    // Fonts[FontIndex] draws a GlyphRef. 0-3 are the primary faces: regular, italic, bold, bold italic.
    private readonly List<SKFont> _fonts = [];
    private readonly Dictionary<(nint Face, float Size, bool Italic), int> _fontIndex = new();
    private readonly Dictionary<(int Rune, int Style), GlyphRef> _glyphs = new();

    private readonly SKTypeface _primary;

    /// <param name="family">Null or a family not installed: the bundled font.</param>
    public TerminalFont(float size, string? family = null)
    {
        size = TerminalFonts.DrawSize(family, size);
        Size = size;
        (SKTypeface regularFace, SKTypeface boldFace) = TerminalFonts.Get(family);
        _primary = regularFace;
        Family = family;
        SKTypeface[] faces = [regularFace, boldFace];
        for (int weight = 0; weight < faces.Length; weight++)
        {
            // A family without a bold face (e.g. VT323) draws its bold text emboldened.
            bool embolden = weight == 1 && boldFace == regularFace;
            foreach (bool italic in new[] { false, true })
                _fonts.Add(CreateFont(faces[weight], size, italic, embolden));
        }

        SKFont regular = _fonts[0];
        SKFontMetrics m = regular.Metrics;
        CellWidth = Advance(regular, regular.GetGlyph('M'));
        CellHeight = MathF.Ceiling(regular.Spacing);
        float contentH = m.Descent - m.Ascent;
        Baseline = MathF.Round(-m.Ascent + (CellHeight - contentH) / 2f);
        UnderlineY = Baseline + Math.Max(1f, MathF.Round(m.UnderlinePosition ?? size * 0.1f));
        LineThickness = Math.Max(1f, MathF.Round(m.UnderlineThickness ?? size / 14f));
        StrikeY = MathF.Round(Baseline - (m.XHeight > 0 ? m.XHeight : size * 0.5f) / 2f);
    }

    /// <summary>The size the font is drawn at (the chosen size, adjusted for the family: <see cref="TerminalFonts.DrawSize"/>).</summary>
    public float Size { get; }

    /// <summary>The family asked for (null: the bundled font).</summary>
    public string? Family { get; }
    public float CellWidth { get; }
    public float CellHeight { get; }

    /// <summary>Baseline offset from the top of a cell.</summary>
    public float Baseline { get; }

    public float UnderlineY { get; }
    public float StrikeY { get; }
    public float LineThickness { get; }

    public IReadOnlyList<SKFont> Fonts => _fonts;

    /// <summary>Finds the glyph for <paramref name="rune"/> spanning <paramref name="width"/> cells.</summary>
    public GlyphRef Lookup(int rune, bool bold, bool italic, int width)
    {
        int style = (bold ? 1 : 0) | (italic ? 2 : 0) | (width == 2 ? 4 : 0);
        if (_glyphs.TryGetValue((rune, style), out GlyphRef cached))
            return cached;

        int primary = (bold ? 1 : 0) * 2 + (italic ? 1 : 0);
        ushort glyph = _fonts[primary].GetGlyph(rune);
        GlyphRef result = glyph == 0 ? Fallback(rune, italic, width) ?? new GlyphRef(primary, 0, 0)
            : width == 2 ? new GlyphRef(primary, glyph, Math.Max(0, (2 * CellWidth - Advance(_fonts[primary], glyph)) / 2f))
            : new GlyphRef(primary, glyph, 0);
        _glyphs[(rune, style)] = result;
        return result;
    }

    public void Dispose()
    {
        foreach (SKFont font in _fonts)
            font.Dispose();
        _fonts.Clear();
    }

    private GlyphRef? Fallback(int rune, bool italic, int width)
    {
        SKTypeface? face = FindFallbackFace(rune);
        if (face is null)
            return null;

        // Scale the fallback glyph down when it is wider than its cells (emoji, some CJK fonts) and center it.
        float available = CellWidth * Math.Max(1, width);
        using var probe = new SKFont(face, Size);
        ushort glyph = probe.GetGlyph(rune);
        if (glyph == 0)
            return null;
        float advance = Advance(probe, glyph);
        float size = advance > available * 1.02f ? Size * available / advance : Size;
        int index = AddFont(face, size, italic);
        float offset = Math.Max(0, (available - Math.Min(advance * size / Size, available)) / 2f);
        return new GlyphRef(index, glyph, offset);
    }

    private SKTypeface? FindFallbackFace(int rune)
    {
        SKTypeface face = Blossom.Utils.Fonts.ResolveForCodepoint(_primary, rune);
        if (face != _primary && Blossom.Utils.Fonts.HasGlyph(face, rune))
            return face;
        // A chosen font without the character: the bundled one may have it (box drawing, symbols).
        if (_primary != Theme.Mono && Blossom.Utils.Fonts.HasGlyph(Theme.Mono, rune))
            return Theme.Mono;
        // Blossom's list covers symbols; CJK and other scripts need the fonts below.
        foreach (SKTypeface extra in ExtraFallbacks.Value)
        {
            if (Blossom.Utils.Fonts.HasGlyph(extra, rune))
                return extra;
        }
        return null;
    }

    // SkiaSharp's bundled Linux build has no fontconfig: MatchCharacter finds nothing and FromFamilyName substitutes
    // another font for a missing family. So probe known wide-coverage families and keep the ones actually installed.
    private static readonly Lazy<SKTypeface[]> ExtraFallbacks = new(() =>
    {
        string[] families =
        [
            "Noto Sans Mono CJK SC", "Noto Sans CJK SC", "Noto Sans CJK JP", "Source Han Sans", "Droid Sans Fallback",
            "WenQuanYi Micro Hei", "Microsoft YaHei", "Yu Gothic", "Malgun Gothic", "MS Gothic", "Segoe UI Symbol",
            "Segoe UI Emoji", "Noto Emoji", "Unifont",
        ];
        var faces = new List<SKTypeface>();
        foreach (string family in families)
        {
            SKTypeface? face = SKTypeface.FromFamilyName(family);
            if (face is not null && string.Equals(face.FamilyName, family, StringComparison.OrdinalIgnoreCase) && IsUsable(face))
                faces.Add(face);
        }
        return faces.ToArray();
    });

    // SkiaSharp's Linux build cannot draw bitmap color-emoji fonts (see Blossom's Fonts.HasGlyph).
    private static bool IsUsable(SKTypeface face) =>
        !OperatingSystem.IsLinux() || !(face.FamilyName ?? "").Contains("Color", StringComparison.OrdinalIgnoreCase);

    private int AddFont(SKTypeface face, float size, bool italic)
    {
        var key = (face.Handle, MathF.Round(size, 2), italic);
        if (_fontIndex.TryGetValue(key, out int index))
            return index;
        _fonts.Add(CreateFont(face, size, italic));
        _fontIndex[key] = _fonts.Count - 1;
        return _fonts.Count - 1;
    }

    private static float Advance(SKFont font, ushort glyph)
    {
        Span<float> widths = stackalloc float[1];
        font.GetGlyphWidths([glyph], widths, Span<SKRect>.Empty);
        return widths[0];
    }

    private static SKFont CreateFont(SKTypeface face, float size, bool italic, bool embolden = false) => new(face, size)
    {
        Subpixel = true,
        Edging = SKFontEdging.Antialias,
        Hinting = SKFontHinting.Slight,
        SkewX = italic ? ItalicSkew : 0,
        Embolden = embolden,
    };
}
