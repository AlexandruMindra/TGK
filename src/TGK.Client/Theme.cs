using System;
using System.Collections.Generic;
using System.IO;
using Blossom;
using Blossom.Utils;
using SkiaSharp;

namespace TGK.Client;

/// <summary>Central design tokens: colors, metrics, type scale and fonts. Every control reads from here.</summary>
public static class Theme
{
    // ---- Surfaces (darkest to lightest) ----
    /// <summary>Tab strip and window chrome; the darkest surface.</summary>
    public static readonly SKColor Chrome = new(13, 14, 18);
    /// <summary>View background behind everything.</summary>
    public static readonly SKColor AppBg = new(17, 18, 23);
    public static readonly SKColor Sidebar = new(19, 20, 25);
    /// <summary>Content area and the active tab (they merge visually).</summary>
    public static readonly SKColor Surface = new(23, 25, 31);
    /// <summary>Cards and raised panels on top of <see cref="Surface"/>.</summary>
    public static readonly SKColor SurfaceRaised = new(30, 32, 40);
    public static readonly SKColor SurfaceHover = new(38, 41, 51);
    public static readonly SKColor SurfacePressed = new(46, 49, 61);
    /// <summary>Dialogs, menus and popups.</summary>
    public static readonly SKColor Overlay = new(29, 31, 39);
    /// <summary>Text inputs and wells.</summary>
    public static readonly SKColor Input = new(14, 15, 20);
    public static readonly SKColor Scrim = new(5, 6, 9, 170);
    /// <summary>Suggested terminal background (Agent D may override per profile).</summary>
    public static readonly SKColor TerminalBg = new(15, 16, 21);

    // ---- Lines ----
    public static readonly SKColor Border = new(255, 255, 255, 20);
    public static readonly SKColor BorderStrong = new(255, 255, 255, 38);
    public static readonly SKColor BorderInput = new(255, 255, 255, 30);

    // ---- Text ----
    public static readonly SKColor TextPrimary = new(232, 235, 241);
    public static readonly SKColor TextSecondary = new(172, 178, 192);
    public static readonly SKColor TextMuted = new(120, 127, 142);
    public static readonly SKColor TextDisabled = new(82, 87, 100);
    public static readonly SKColor TextOnAccent = new(255, 255, 255);

    // ---- Accent & status ----
    public static readonly SKColor Accent = new(82, 139, 255);
    public static readonly SKColor AccentHover = new(106, 156, 255);
    public static readonly SKColor AccentPressed = new(64, 118, 232);
    public static readonly SKColor AccentSoft = Accent.WithAlpha(40);
    public static readonly SKColor Selection = Accent.WithAlpha(90);
    public static readonly SKColor Success = new(63, 185, 80);
    public static readonly SKColor Warning = new(222, 164, 48);
    public static readonly SKColor Danger = new(235, 87, 79);
    public static readonly SKColor DangerHover = new(245, 108, 100);
    public static readonly SKColor DangerSoft = Danger.WithAlpha(34);
    public static readonly SKColor Idle = new(110, 118, 132);

    /// <summary>Tag colors offered in the host editor (<c>#RRGGBB</c>, matching the sample vault).</summary>
    public static readonly string[] TagColors = ["#E5534B", "#D29922", "#3FB950", "#39C5CF", "#58A6FF", "#A371F7", "#DB61A2", "#8B949E"];

    // ---- Radii ----
    public const float RadiusSm = 4;
    public const float Radius = 6;
    public const float RadiusLg = 10;

    // ---- Spacing scale ----
    public const float Space1 = 4;
    public const float Space2 = 8;
    public const float Space3 = 12;
    public const float Space4 = 16;
    public const float Space5 = 24;
    public const float Space6 = 32;

    // ---- Type scale ----
    public const float FontXs = 11;
    public const float FontSm = 12;
    public const float FontBase = 13;
    public const float FontMd = 14;
    public const float FontLg = 16;
    public const float FontXl = 20;
    public const float FontXxl = 28;
    public const int WeightRegular = 400;
    public const int WeightSemibold = 600;

    // ---- Layout metrics ----
    public const float TabStripHeight = 40;
    public const float StatusBarHeight = 24;
    public const float SidebarWidth = 260;
    public const float ControlHeight = 32;
    public const float FieldHeight = 34;

    /// <summary>Family name of the resolved UI font (for diagnostics).</summary>
    public static string UiFamily { get; }

    /// <summary>Bundled DejaVu Sans Mono, the terminal font.</summary>
    public static SKTypeface Mono { get; }
    public static SKTypeface MonoBold { get; }

    private static readonly SKTypeface UiRegular;
    private static readonly SKTypeface UiBold;
    private static readonly List<SKData> FontData = []; // SKTypeface.FromData needs the data kept alive

    static Theme()
    {
        (UiRegular, UiBold, UiFamily) = ResolveUiFont();
        Mono = LoadBundledFont("DejaVuSansMono.ttf") ?? UiRegular;
        MonoBold = LoadBundledFont("DejaVuSansMono-Bold.ttf") ?? Mono;
    }

    /// <summary>UI typeface for a CSS-style weight (≥ 600 is bold).</summary>
    public static SKTypeface Ui(int weight = WeightRegular) => weight >= WeightSemibold ? UiBold : UiRegular;

    /// <summary>Parses <c>#RRGGBB</c>; returns <paramref name="fallback"/> for null or malformed input.</summary>
    public static SKColor ParseHex(string? hex, SKColor fallback) =>
        hex is { Length: 7 } && hex[0] == '#' && SKColor.TryParse(hex, out SKColor c) ? c : fallback;

    public static SKColor Mix(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t),
        (byte)(a.Green + (b.Green - a.Green) * t),
        (byte)(a.Blue + (b.Blue - a.Blue) * t),
        (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));

    // SKTypeface.FromFamilyName never fails on Linux: a missing family silently resolves to some other font
    // (e.g. Symbola). Only accept a family whose resolved name matches, then fall back to Blossom's Roboto.
    private static (SKTypeface Regular, SKTypeface Bold, string Family) ResolveUiFont()
    {
        // Variable fonts (e.g. Cantarell) are skipped on purpose: Skia 2.88 cannot select their bold instance.
        string[] candidates = ["Segoe UI", "Inter", "Noto Sans", "Ubuntu", "Open Sans", "Roboto", "Liberation Sans", "Nimbus Sans", "Arial", "DejaVu Sans"];
        foreach (string family in candidates)
        {
            SKTypeface? regular = SKTypeface.FromFamilyName(family, SKFontStyle.Normal);
            if (regular is null || !regular.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase))
                continue;
            SKTypeface? bold = SKTypeface.FromFamilyName(family, new SKFontStyle(Theme.WeightSemibold, 5, SKFontStyleSlant.Upright));
            if (bold is null || !bold.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase))
                bold = regular;
            return (regular, bold, family);
        }

        Log.Warning("No known UI font found; using Blossom's embedded Roboto.");
        SKTypeface roboto = LoadEmbeddedRoboto() ?? SKTypeface.Default;
        return (roboto, roboto, roboto.FamilyName);
    }

    private static SKTypeface? LoadEmbeddedRoboto()
    {
        using Stream? stream = typeof(Fonts).Assembly.GetManifestResourceStream("Blossom.assets.fonts.roboto.medium.ttf");
        if (stream is null)
            return null;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return FromData(copy.ToArray());
    }

    private static SKTypeface? LoadBundledFont(string fileName)
    {
        foreach (string dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            string path = Path.Combine(dir, "assets", "fonts", fileName);
            if (File.Exists(path))
                return FromData(File.ReadAllBytes(path));
        }
        Log.Warning($"Bundled font {fileName} not found.");
        return null;
    }

    private static SKTypeface? FromData(byte[] bytes)
    {
        SKData data = SKData.CreateCopy(bytes);
        SKTypeface? tf = SKTypeface.FromData(data);
        if (tf is null)
        {
            data.Dispose();
            return null;
        }
        FontData.Add(data);
        return tf;
    }
}
