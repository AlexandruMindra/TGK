using System;
using System.Linq;
using TGK.Client.Terminal;
using Xunit;

namespace TGK.Client.Tests;

public sealed class ColorSchemeTests
{
    [Fact]
    public void Schemes_HaveDistinctNames_AndTheDefaultFirst()
    {
        Assert.Equal(ColorScheme.TgkDark, ColorScheme.All[0]);
        Assert.Equal(ColorScheme.All.Count, ColorScheme.All.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ColorScheme.All, s => Assert.Equal(16, s.Ansi.Count));
        Assert.Equal(ColorScheme.TgkDark, ColorScheme.Find("no such scheme"));
    }

    [Fact]
    public void RetroSchemes_ComeWithTheBundledRetroFont()
    {
        foreach (string name in new[] { "Green CRT", "Amber CRT", "Commodore 64", "MS-DOS", "Synthwave '84" })
        {
            ColorScheme retro = ColorScheme.Find(name.ToLowerInvariant());
            Assert.Equal(name, retro.Name);
            Assert.Equal(TerminalFonts.Retro, retro.Font);
        }
        Assert.Equal("Green CRT", ColorScheme.Find("Retro CRT").Name); // its earlier name
        Assert.Null(ColorScheme.TgkDark.Font);

        Assert.True(TerminalFonts.IsBuiltIn(TerminalFonts.Retro));
        Assert.True(TerminalFonts.IsAvailable(TerminalFonts.Retro));
        Assert.Equal(TerminalFonts.Retro, TerminalFonts.Available[1]);
        (SkiaSharp.SKTypeface regular, SkiaSharp.SKTypeface bold) = TerminalFonts.Get(TerminalFonts.Retro);
        Assert.Equal("VT323", regular.FamilyName);
        Assert.Same(regular, bold); // no bold face: the terminal emboldens it

        using var font = new TerminalFont(16, TerminalFonts.Retro);
        Assert.False(font.Fonts[0].Embolden);
        Assert.True(font.Fonts[2].Embolden);
        using var bundled = new TerminalFont(16);
        Assert.False(bundled.Fonts[2].Embolden); // DejaVu Sans Mono has a real bold
    }
}
