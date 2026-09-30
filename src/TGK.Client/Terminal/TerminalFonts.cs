using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using TGK.Core.Models;

namespace TGK.Client.Terminal;

/// <summary>
/// The terminal font families offered in the settings: the bundled DejaVu Sans Mono and the monospace fonts installed
/// on this device. A family chosen on another device that is not installed here falls back to the bundled font.
/// </summary>
public static class TerminalFonts
{
    private static readonly Dictionary<string, (SKTypeface Regular, SKTypeface Bold)?> Faces = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The bundled font's name (<see cref="EffectiveOptions.DefaultFontFamily"/>).</summary>
    public static string Bundled => EffectiveOptions.DefaultFontFamily;

    /// <summary>The bundled font first, then the installed monospace families by name.</summary>
    public static IReadOnlyList<string> Available => AvailableLazy.Value;

    private static readonly Lazy<IReadOnlyList<string>> AvailableLazy = new(() =>
    {
        var families = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string family in SKFontManager.Default.GetFontFamilies())
            {
                if (!string.Equals(family, Bundled, StringComparison.OrdinalIgnoreCase) && Load(family) is { } faces && IsMonospace(faces.Regular))
                    families.Add(family);
            }
        }
        catch (Exception ex)
        {
            Blossom.Log.Warning($"Could not list the installed fonts: {ex.Message}");
        }
        return [Bundled, .. families];
    });

    /// <summary>Whether <paramref name="family"/> can be used on this device (the bundled font always can).</summary>
    public static bool IsAvailable(string? family) =>
        family is null || string.Equals(family, Bundled, StringComparison.OrdinalIgnoreCase) || Resolve(family) is not null;

    /// <summary>Regular and bold faces of <paramref name="family"/>; the bundled font for null, the bundled name or a family not installed.</summary>
    public static (SKTypeface Regular, SKTypeface Bold) Get(string? family) =>
        family is null || string.Equals(family, Bundled, StringComparison.OrdinalIgnoreCase)
            ? (Theme.Mono, Theme.MonoBold)
            : Resolve(family) ?? (Theme.Mono, Theme.MonoBold);

    private static (SKTypeface Regular, SKTypeface Bold)? Resolve(string family)
    {
        if (!Faces.TryGetValue(family, out var faces))
            Faces[family] = faces = Load(family);
        return faces;
    }

    // SKTypeface.FromFamilyName never fails: a missing family silently resolves to another font, so check the name.
    private static (SKTypeface Regular, SKTypeface Bold)? Load(string family)
    {
        SKTypeface? regular = SKTypeface.FromFamilyName(family, SKFontStyle.Normal);
        if (regular is null || !string.Equals(regular.FamilyName, family, StringComparison.OrdinalIgnoreCase) || regular.GetGlyph('M') == 0)
            return null;
        SKTypeface? bold = SKTypeface.FromFamilyName(family, SKFontStyle.Bold);
        if (bold is null || !string.Equals(bold.FamilyName, family, StringComparison.OrdinalIgnoreCase))
            bold = regular;
        return (regular, bold);
    }

    // Every ASCII letter, digit and a few punctuation marks as wide as "M".
    private static bool IsMonospace(SKTypeface face)
    {
        using var font = new SKFont(face, 20);
        const string probe = "iMW.l0_m";
        var glyphs = new ushort[probe.Length];
        for (int i = 0; i < probe.Length; i++)
            glyphs[i] = font.GetGlyph(probe[i]);
        if (glyphs.Any(g => g == 0))
            return false;
        var widths = new float[glyphs.Length];
        font.GetGlyphWidths(glyphs, widths, Span<SKRect>.Empty);
        return widths.All(w => w > 0 && Math.Abs(w - widths[1]) < 0.01f);
    }
}
