using System;
using System.Collections.Immutable;

namespace TGK.Terminal;

/// <summary>The standard xterm 256-color palette as 0xAARRGGBB values.</summary>
public static class XtermPalette
{
    /// <summary>
    /// 0-15: xterm's ANSI colors, 16-231: the 6x6x6 color cube, 232-255: the grayscale ramp.
    /// </summary>
    public static ImmutableArray<uint> Default { get; } = Build();

    private static ImmutableArray<uint> Build()
    {
        var p = ImmutableArray.CreateBuilder<uint>(256);
        p.AddRange(
            0xFF000000, 0xFFCD0000, 0xFF00CD00, 0xFFCDCD00, 0xFF0000EE, 0xFFCD00CD, 0xFF00CDCD, 0xFFE5E5E5,
            0xFF7F7F7F, 0xFFFF0000, 0xFF00FF00, 0xFFFFFF00, 0xFF5C5CFF, 0xFFFF00FF, 0xFF00FFFF, 0xFFFFFFFF);

        ReadOnlySpan<uint> levels = [0, 95, 135, 175, 215, 255];
        for (int r = 0; r < 6; r++)
        {
            for (int g = 0; g < 6; g++)
            {
                for (int b = 0; b < 6; b++)
                    p.Add(Argb(levels[r], levels[g], levels[b]));
            }
        }

        for (int i = 0; i < 24; i++)
        {
            uint v = (uint)(8 + (i * 10));
            p.Add(Argb(v, v, v));
        }

        return p.MoveToImmutable();
    }

    private static uint Argb(uint r, uint g, uint b) => 0xFF000000 | (r << 16) | (g << 8) | b;
}
