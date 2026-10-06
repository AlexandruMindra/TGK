using System;
using System.Collections.Generic;
using SkiaSharp;
using TGK.Core.Models;

namespace TGK.Client.Terminal;

/// <summary>
/// A terminal color scheme: the 16 ANSI colors (normal, then bright), default foreground and background, cursor and
/// selection. Hosts refer to schemes by <see cref="Name"/> (<see cref="HostOptions.ColorScheme"/>).
/// </summary>
public sealed class ColorScheme
{
    private ColorScheme(string name, uint[] ansi, uint foreground, uint background, uint cursor, SKColor selection, bool boldIsBright = true)
    {
        Name = name;
        BoldIsBright = boldIsBright;
        Ansi = Array.ConvertAll(ansi, c => new SKColor(0xFF000000 | c));
        Foreground = new SKColor(0xFF000000 | foreground);
        Background = new SKColor(0xFF000000 | background);
        Cursor = new SKColor(0xFF000000 | cursor);
        Selection = selection;
    }

    public string Name { get; }
    public IReadOnlyList<SKColor> Ansi { get; }
    public SKColor Foreground { get; }
    public SKColor Background { get; }
    public SKColor Cursor { get; }
    public SKColor Selection { get; }

    /// <summary>
    /// Bold text in one of the 8 basic colors uses its bright variant (xterm's default). Off for Solarized, whose
    /// "bright" slots hold its grey base tones.
    /// </summary>
    public bool BoldIsBright { get; }

    /// <summary>The app's own dark scheme (the default).</summary>
    public static readonly ColorScheme TgkDark = new(EffectiveOptions.DefaultColorScheme,
    [
        0x2A2D37, 0xE8646A, 0x8FCB7B, 0xE6C07B, 0x3F83DE, 0xC792EA, 0x3FB4C2, 0xCDD3DE,
        0x5E6575, 0xFF7E84, 0xAEE58F, 0xFFD68A, 0x82C4FF, 0xDDAAFF, 0x7CDCE6, 0xF5F7FA,
    ], 0xD8DCE4, 0x0F1015, 0xE8EBF1, Theme.Accent.WithAlpha(96));

    // Standard palettes as published by their authors (Solarized: ethanschoonover.com; Dracula: draculatheme.com spec;
    // Nord: nordtheme.com; Gruvbox: morhetz/gruvbox; One Dark: joshdick/onedark.vim terminal colors).
    private static readonly uint[] SolarizedAnsi =
    [
        0x073642, 0xDC322F, 0x859900, 0xB58900, 0x268BD2, 0xD33682, 0x2AA198, 0xEEE8D5,
        0x002B36, 0xCB4B16, 0x586E75, 0x657B83, 0x839496, 0x6C71C4, 0x93A1A1, 0xFDF6E3,
    ];

    /// <summary>Every built-in scheme, <see cref="TgkDark"/> first.</summary>
    public static IReadOnlyList<ColorScheme> All { get; } =
    [
        TgkDark,
        new("Solarized Dark", SolarizedAnsi, 0x839496, 0x002B36, 0x93A1A1, new SKColor(0x07, 0x36, 0x42), boldIsBright: false),
        new("Solarized Light", SolarizedAnsi, 0x657B83, 0xFDF6E3, 0x586E75, new SKColor(0xEE, 0xE8, 0xD5), boldIsBright: false),
        new("Dracula",
        [
            0x21222C, 0xFF5555, 0x50FA7B, 0xF1FA8C, 0xBD93F9, 0xFF79C6, 0x8BE9FD, 0xF8F8F2,
            0x6272A4, 0xFF6E6E, 0x69FF94, 0xFFFFA5, 0xD6ACFF, 0xFF92DF, 0xA4FFFF, 0xFFFFFF,
        ], 0xF8F8F2, 0x282A36, 0xF8F8F2, new SKColor(0x44, 0x47, 0x5A)),
        new("Nord",
        [
            0x3B4252, 0xBF616A, 0xA3BE8C, 0xEBCB8B, 0x81A1C1, 0xB48EAD, 0x88C0D0, 0xE5E9F0,
            0x4C566A, 0xBF616A, 0xA3BE8C, 0xEBCB8B, 0x81A1C1, 0xB48EAD, 0x8FBCBB, 0xECEFF4,
        ], 0xD8DEE9, 0x2E3440, 0xD8DEE9, new SKColor(0x43, 0x4C, 0x5E)),
        new("Gruvbox Dark",
        [
            0x282828, 0xCC241D, 0x98971A, 0xD79921, 0x458588, 0xB16286, 0x689D6A, 0xA89984,
            0x928374, 0xFB4934, 0xB8BB26, 0xFABD2F, 0x83A598, 0xD3869B, 0x8EC07C, 0xEBDBB2,
        ], 0xEBDBB2, 0x282828, 0xEBDBB2, new SKColor(0x50, 0x49, 0x45)),
        new("One Dark",
        [
            0x282C34, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xABB2BF,
            0x5C6370, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xFFFFFF,
        ], 0xABB2BF, 0x282C34, 0x528BFF, new SKColor(0x3E, 0x44, 0x51)),
        // Retro schemes (the bundled VT323 font suits them; it is chosen separately). Amber CRT: an amber phosphor monitor
        // (IBM 3180, VT220), every color a shade of amber told apart by brightness. Commodore 64: its 16 colors on its blue
        // screen (Pepto's palette). MS-DOS: IBM CGA's colors on black, as at a DOS prompt. Synthwave '84 after Robb Owen's
        // 80s neon theme. Teletype: ink on paper, as a printing terminal.
        new("Amber CRT",
        [
            0x2A1A05, 0xC25A12, 0xFFB000, 0xFFD27A, 0x9C5A0E, 0xD9822B, 0xFFC54D, 0xFFE2A8,
            0x6B4510, 0xE0731E, 0xFFC233, 0xFFE9B8, 0xB8742A, 0xF0A04B, 0xFFD98A, 0xFFF4DE,
        ], 0xFFB000, 0x120B02, 0xFFD27A, new SKColor(0xFF, 0xB0, 0x00, 0x50)),
        new("Commodore 64",
        [
            0x000000, 0x9F4E44, 0x5CAB5E, 0xC9D487, 0x50459B, 0xA057A3, 0x6ABFC6, 0xADADAD,
            0x626262, 0xCB7E75, 0x9AE29B, 0xEDF171, 0x887ECB, 0xC77ACB, 0x9AE6EB, 0xFFFFFF,
        ], 0x887ECB, 0x40318D, 0x887ECB, new SKColor(0x88, 0x7E, 0xCB, 0x60)),
        new("MS-DOS",
        [
            0x000000, 0xAA0000, 0x00AA00, 0xAA5500, 0x0000AA, 0xAA00AA, 0x00AAAA, 0xAAAAAA,
            0x555555, 0xFF5555, 0x55FF55, 0xFFFF55, 0x5555FF, 0xFF55FF, 0x55FFFF, 0xFFFFFF,
        ], 0xAAAAAA, 0x000000, 0xAAAAAA, new SKColor(0xAA, 0xAA, 0xAA, 0x50)),
        new("Synthwave '84",
        [
            0x241B30, 0xFE4450, 0x72F1B8, 0xFEDE5D, 0x6E95FF, 0xFF7EDB, 0x03EDF9, 0xE0D7EE,
            0x495495, 0xFF6E7A, 0x9CFFD6, 0xFFF08A, 0x9AB6FF, 0xFFA6EA, 0x7CF6FF, 0xFFFFFF,
        ], 0xF2E9FF, 0x241B30, 0xFF7EDB, new SKColor(0xFF, 0x7E, 0xDB, 0x50)),
        new("Teletype",
        [
            0x2B2620, 0xA6322B, 0x4F7A28, 0x9A6A12, 0x2F5A8C, 0x7A3E78, 0x2E7A73, 0x8A7F6E,
            0x6B6254, 0xC4453C, 0x5E9130, 0xB98318, 0x3C6FA8, 0x965092, 0x38928A, 0x1A1712,
        ], 0x2B2620, 0xF1E8D2, 0x2B2620, new SKColor(0x2B, 0x26, 0x20, 0x38)),
    ];

    /// <summary>The scheme named <paramref name="name"/> (case-insensitive), or <see cref="TgkDark"/> for an unknown name.</summary>
    public static ColorScheme Find(string? name)
    {
        foreach (ColorScheme scheme in All)
        {
            if (string.Equals(scheme.Name, name, StringComparison.OrdinalIgnoreCase))
                return scheme;
        }
        return TgkDark;
    }
}
