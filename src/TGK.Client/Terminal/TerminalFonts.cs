using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SkiaSharp;
using TGK.Core.Models;

namespace TGK.Client.Terminal;

/// <summary>
/// The terminal font families offered in the settings: the bundled DejaVu Sans Mono and the monospace fonts installed
/// on this device. A family chosen on another device that is not installed here falls back to the bundled font.
/// </summary>
/// <remarks>
/// Only families whose name suggests a terminal font are opened to check that they are monospaced: opening every
/// installed family (thousands on some Linux systems) took seconds and kept hundreds of MB, since Skia keeps each font
/// it opened. The list is built in the background at startup (<see cref="WarmUp"/>). A family left out by its name can
/// still be used when it was chosen elsewhere (<see cref="IsAvailable"/>, <see cref="Get"/>). Typefaces from the font
/// manager are shared objects (the same family always returns the same instance, also to Blossom's fallback fonts),
/// so they are never disposed here.
/// </remarks>
public static class TerminalFonts
{
    private static readonly Dictionary<string, (SKTypeface Regular, SKTypeface Bold)?> Faces = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The bundled font's name (<see cref="EffectiveOptions.DefaultFontFamily"/>).</summary>
    public static string Bundled => EffectiveOptions.DefaultFontFamily;

    /// <summary>The bundled retro font (VT323, after the DEC VT320's), suited to the retro color schemes.</summary>
    public const string Retro = "VT323";

    /// <summary>
    /// The size to draw <paramref name="family"/> at for a font size of <paramref name="size"/>: VT323's letters fill
    /// less of its size (cell 6 px wide at 14 px, where DejaVu Sans Mono's is 8), so it is drawn 1.3 times larger and a
    /// size looks alike in both.
    /// </summary>
    public static float DrawSize(string? family, float size) =>
        string.Equals(family, Retro, StringComparison.OrdinalIgnoreCase) ? MathF.Round(size * 1.3f * 2) / 2 : size;

    /// <summary>Whether <paramref name="family"/> is one of the fonts that come with TGK (always available).</summary>
    public static bool IsBuiltIn(string? family) =>
        string.Equals(family, Bundled, StringComparison.OrdinalIgnoreCase) || string.Equals(family, Retro, StringComparison.OrdinalIgnoreCase);

    /// <summary>Lists the installed fonts on a background thread, so that dialogs offering them open at once.</summary>
    public static void WarmUp() => _ = Task.Run(() => _ = Available);

    /// <summary>The bundled fonts first, then the installed monospace families by name.</summary>
    public static IReadOnlyList<string> Available => AvailableLazy.Value;

    // Words in the names of terminal fonts ("Mono" and "Code" cover most of them; Nerd Fonts keep the base name).
    private static readonly string[] CandidateKeywords =
    [
        "Mono", "Code", "Console", "Terminal", "Typewriter", "Fixed",
        "Courier", "Consolas", "Menlo", "Monaco", "Inconsolata", "Meslo", "Monaspace",
        "Hack", "Iosevka", "Terminus", "Cascadia", "Anonymous", "Hasklig", "Lilex", "Agave",
        "Pragmata", "Proggy", "Monofur", "ProFont", "Andale", "Spleen", "Cozette", "Unifont",
        "Cousine", "Dina", "Envy", "Fantasque", "Gohu", "Hermit",
        "Input", "Monoid", "Overpass", "Oxygen", "Sudo",
    ];

    /// <summary>Whether <paramref name="family"/>'s name suggests a terminal font (only those are checked).</summary>
    public static bool IsCandidate(string family)
    {
        for (int i = 0; i < CandidateKeywords.Length; i++)
        {
            if (family.Contains(CandidateKeywords[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static readonly Lazy<IReadOnlyList<string>> AvailableLazy = new(() =>
    {
        var families = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string family in SKFontManager.Default.GetFontFamilies())
            {
                if (!IsBuiltIn(family)
                    && IsCandidate(family)
                    && IsMonospaceFamily(family))
                {
                    families.Add(family);
                }
            }
        }
        catch (Exception ex)
        {
            Blossom.Log.Warning($"Could not list the installed fonts: {ex.Message}");
        }
        return [Bundled, Retro, .. families];
    });

    /// <summary>Whether <paramref name="family"/> can be used on this device (the bundled font always can).</summary>
    public static bool IsAvailable(string? family) =>
        family is null
        || IsBuiltIn(family)
        || Available.Any(f => string.Equals(f, family, StringComparison.OrdinalIgnoreCase))
        || Resolve(family) is not null;

    /// <summary>Regular and bold faces of <paramref name="family"/>; the bundled font for null, the bundled name or a family not installed.</summary>
    public static (SKTypeface Regular, SKTypeface Bold) Get(string? family) =>
        family is null || string.Equals(family, Bundled, StringComparison.OrdinalIgnoreCase) ? (Theme.Mono, Theme.MonoBold)
        : string.Equals(family, Retro, StringComparison.OrdinalIgnoreCase) ? (Theme.Retro, Theme.Retro)
        : Resolve(family) ?? (Theme.Mono, Theme.MonoBold);

    private static (SKTypeface Regular, SKTypeface Bold)? Resolve(string family)
    {
        if (!Faces.TryGetValue(family, out var faces))
            Faces[family] = faces = Load(family);
        return faces;
    }

    private static bool HasGlyph(SKTypeface face, int codepoint)
    {
        using var font = new SKFont(face);
        return font.ContainsGlyph(codepoint);
    }

    private static bool IsMonospaceFamily(string family)
    {
        SKTypeface? regular = SKTypeface.FromFamilyName(family, SKFontStyle.Normal);
        if (regular is null || !string.Equals(regular.FamilyName, family, StringComparison.OrdinalIgnoreCase) || !HasGlyph(regular, 'M'))
            return false;
        if (regular.IsFixedPitch)
            return true;
        return IsMonospace(regular);
    }

    // SKTypeface.FromFamilyName never fails: a missing family silently resolves to another font, so check the name.
    private static (SKTypeface Regular, SKTypeface Bold)? Load(string family)
    {
        SKTypeface? regular = SKTypeface.FromFamilyName(family, SKFontStyle.Normal);
        if (regular is null || !string.Equals(regular.FamilyName, family, StringComparison.OrdinalIgnoreCase) || !HasGlyph(regular, 'M'))
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
