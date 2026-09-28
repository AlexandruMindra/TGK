using Xunit;
using static TGK.Terminal.Tests.TestUtil;

namespace TGK.Terminal.Tests;

public class SgrTests
{
    private static CellStyle StyleAfter(string sgr)
    {
        var t = Term();
        t.Feed($"{Csi}{sgr}mx");
        Assert.Equal(t.CurrentStyle, t.CellAt(0, 0).Style);
        return t.CellAt(0, 0).Style;
    }

    [Theory]
    [InlineData("1", CellFlags.Bold)]
    [InlineData("2", CellFlags.Dim)]
    [InlineData("3", CellFlags.Italic)]
    [InlineData("4", CellFlags.Underline)]
    [InlineData("4:1", CellFlags.Underline)]
    [InlineData("4:2", CellFlags.Underline | CellFlags.DoubleUnderline)]
    [InlineData("4:3", CellFlags.Underline | CellFlags.CurlyUnderline)]
    [InlineData("4:4", CellFlags.Underline)]
    [InlineData("4:5", CellFlags.Underline)]
    [InlineData("4:3;4:0", CellFlags.None)]
    [InlineData("21", CellFlags.Underline | CellFlags.DoubleUnderline)]
    [InlineData("5", CellFlags.Blink)]
    [InlineData("7", CellFlags.Inverse)]
    [InlineData("8", CellFlags.Hidden)]
    [InlineData("9", CellFlags.Strikethrough)]
    [InlineData("53", CellFlags.Overline)]
    [InlineData("1;2;3;4;5;7;8;9", CellFlags.Bold | CellFlags.Dim | CellFlags.Italic | CellFlags.Underline | CellFlags.Blink | CellFlags.Inverse | CellFlags.Hidden | CellFlags.Strikethrough)]
    [InlineData("1;2;22", CellFlags.None)]
    [InlineData("3;23", CellFlags.None)]
    [InlineData("4:3;24", CellFlags.None)]
    [InlineData("5;25", CellFlags.None)]
    [InlineData("7;27", CellFlags.None)]
    [InlineData("8;28", CellFlags.None)]
    [InlineData("9;29", CellFlags.None)]
    [InlineData("53;55", CellFlags.None)]
    [InlineData("1;7;0", CellFlags.None)]
    public void Attributes(string sgr, CellFlags expected)
    {
        Assert.Equal(expected, StyleAfter(sgr).Flags);
    }

    [Theory]
    [InlineData("31", 1)]
    [InlineData("37", 7)]
    [InlineData("90", 8)]
    [InlineData("97", 15)]
    [InlineData("38;5;196", 196)]
    [InlineData("38:5:42", 42)]
    [InlineData("38;5;999", 255)] // clamped
    public void ForegroundIndexed(string sgr, int index)
    {
        Assert.Equal(TermColor.Indexed((byte)index), StyleAfter(sgr).Fg);
    }

    [Theory]
    [InlineData("41", 1)]
    [InlineData("47", 7)]
    [InlineData("100", 8)]
    [InlineData("107", 15)]
    [InlineData("48;5;17", 17)]
    [InlineData("48:5:18", 18)]
    public void BackgroundIndexed(string sgr, int index)
    {
        Assert.Equal(TermColor.Indexed((byte)index), StyleAfter(sgr).Bg);
    }

    [Theory]
    [InlineData("38;2;10;20;30")]
    [InlineData("38:2:10:20:30")]
    [InlineData("38:2::10:20:30")]
    [InlineData("38:2:0:10:20:30")]
    public void ForegroundTrueColor(string sgr)
    {
        var fg = StyleAfter(sgr).Fg;
        Assert.Equal(TermColorKind.Rgb, fg.Kind);
        Assert.Equal((10, 20, 30), (fg.R, fg.G, fg.B));
    }

    [Theory]
    [InlineData("48;2;1;2;3")]
    [InlineData("48:2::1:2:3")]
    public void BackgroundTrueColor(string sgr)
    {
        Assert.Equal(TermColor.Rgb(1, 2, 3), StyleAfter(sgr).Bg);
    }

    [Fact]
    public void ColorsCombineWithFollowingAttributes()
    {
        var style = StyleAfter("1;38;2;1;2;3;48;5;4;4");
        Assert.Equal(CellFlags.Bold | CellFlags.Underline, style.Flags);
        Assert.Equal(TermColor.Rgb(1, 2, 3), style.Fg);
        Assert.Equal(TermColor.Indexed(4), style.Bg);

        style = StyleAfter("38:2::9:8:7;1");
        Assert.Equal(TermColor.Rgb(9, 8, 7), style.Fg);
        Assert.Equal(CellFlags.Bold, style.Flags);

        style = StyleAfter("58:2::1:2:3;3"); // underline color is skipped
        Assert.Equal(CellFlags.Italic, style.Flags);
        Assert.Equal(TermColor.Default, style.Fg);
    }

    [Fact]
    public void DefaultColorsAndReset()
    {
        Assert.Equal(TermColor.Default, StyleAfter("31;39").Fg);
        Assert.Equal(TermColor.Default, StyleAfter("41;49").Bg);
        Assert.Equal(CellStyle.Default, StyleAfter("1;31;41;0"));
        Assert.Equal(CellStyle.Default, StyleAfter("1;31;"));  // trailing empty parameter is 0
        Assert.Equal(CellStyle.Default, StyleAfter("1m\u001b["));   // CSI m
    }

    [Fact]
    public void MalformedColorsAreIgnored()
    {
        Assert.Equal(TermColor.Default, StyleAfter("38;2;1;2").Fg);
        Assert.Equal(TermColor.Default, StyleAfter("38;5").Fg);
        Assert.Equal(TermColor.Default, StyleAfter("38;7;1;1").Fg);
        Assert.Equal(TermColor.Default, StyleAfter("38:2:1").Fg);
    }

    [Fact]
    public void PrivateSgrLikeSequences_DoNotChangeStyle()
    {
        var t = Term();
        t.Feed($"{Csi}>4;2m{Csi}?4mx"); // XTMODKEYS and a private query
        Assert.Equal(CellStyle.Default, t.CellAt(0, 0).Style);
    }

    [Fact]
    public void TermColor_Accessors()
    {
        var c = TermColor.Rgb(1, 2, 3);
        Assert.Equal(TermColorKind.Rgb, c.Kind);
        Assert.Equal((1, 2, 3), (c.R, c.G, c.B));
        Assert.Equal(TermColorKind.Indexed, TermColor.Indexed(200).Kind);
        Assert.Equal(200, TermColor.Indexed(200).Index);
        Assert.True(TermColor.Default.IsDefault);
        Assert.Equal(TermColorKind.Default, default(TermColor).Kind);
        Assert.NotEqual(TermColor.Indexed(0), TermColor.Default);
    }

    [Fact]
    public void XtermPalette_HasStandardValues()
    {
        var p = XtermPalette.Default;
        Assert.Equal(256, p.Length);
        Assert.Equal(0xFF000000u, p[0]);
        Assert.Equal(0xFFCD0000u, p[1]);
        Assert.Equal(0xFFFFFFFFu, p[15]);
        Assert.Equal(0xFF000000u, p[16]);
        Assert.Equal(0xFF5F87AFu, p[67]); // cube (1,2,3)
        Assert.Equal(0xFFFFFFFFu, p[231]);
        Assert.Equal(0xFF080808u, p[232]);
        Assert.Equal(0xFFEEEEEEu, p[255]);
    }
}
