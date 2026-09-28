using Xunit;
using static TGK.Terminal.Tests.TestUtil;

namespace TGK.Terminal.Tests;

public class EditTests
{
    // A 6x4 screen filled with distinct rows, cursor at row 2 (1-based), column 3.
    private static TerminalEmulator Filled()
    {
        var t = Term(6, 4);
        t.Feed($"abcdef{Csi}2;1Hghijkl{Csi}3;1Hmnopqr{Csi}4;1Hstuvwx{Csi}2;3H");
        return t;
    }

    [Theory]
    [InlineData("K", "gh")]
    [InlineData("0K", "gh")]
    [InlineData("1K", "   jkl")]
    [InlineData("2K", "")]
    [InlineData("X", "gh jkl")] // ECH
    [InlineData("3X", "gh   l")]
    [InlineData("99X", "gh")]
    [InlineData("P", "ghjkl")] // DCH
    [InlineData("2P", "ghkl")]
    [InlineData("99P", "gh")]
    [InlineData("@", "gh ijk")] // ICH
    [InlineData("2@", "gh  ij")]
    [InlineData("99@", "gh")]
    public void LineEditing(string sequence, string expectedRow)
    {
        var t = Filled();
        t.Feed(Csi + sequence);
        Assert.Equal(expectedRow, t.Row(1));
        Assert.Equal("abcdef", t.Row(0));
        Assert.Equal("mnopqr", t.Row(2));
        Assert.Equal((2, 1), t.Cursor());
    }

    [Theory]
    [InlineData("J", new[] { "abcdef", "gh", "", "" })]
    [InlineData("1J", new[] { "", "   jkl", "mnopqr", "stuvwx" })]
    [InlineData("2J", new[] { "", "", "", "" })]
    public void EraseInDisplay(string sequence, string[] expected)
    {
        var t = Filled();
        t.Feed(Csi + sequence);
        Assert.Equal(expected, t.Screen());
        Assert.Equal((2, 1), t.Cursor());
    }

    [Fact]
    public void EraseInDisplay3_ClearsScrollbackOnly()
    {
        var t = Term(5, 2);
        t.Feed("1\r\n2\r\n3\r\n4");
        Assert.Equal(2, t.ScrollbackCount);
        t.Feed($"{Csi}3J");
        Assert.Equal(0, t.ScrollbackCount);
        Assert.Equal(new[] { "3", "4" }, t.Screen());
    }

    [Fact]
    public void Erase_UsesCurrentBackground()
    {
        var t = Filled();
        t.Feed($"{Csi}44;1m{Csi}K");
        var cell = t.CellAt(1, 4);
        Assert.Equal(0, cell.Rune);
        Assert.Equal(TermColor.Indexed(4), cell.Style.Bg);
        Assert.Equal(TermColor.Default, cell.Style.Fg);
        Assert.Equal(CellFlags.None, cell.Style.Flags); // only the background is applied
        Assert.Equal(TermColor.Default, t.CellAt(1, 1).Style.Bg);

        t.Feed($"{Csi}2J");
        Assert.Equal(TermColor.Indexed(4), t.CellAt(3, 0).Style.Bg);
    }

    [Theory]
    [InlineData("L", new[] { "abcdef", "", "ghijkl", "mnopqr" })]
    [InlineData("2L", new[] { "abcdef", "", "", "ghijkl" })]
    [InlineData("9L", new[] { "abcdef", "", "", "" })]
    [InlineData("M", new[] { "abcdef", "mnopqr", "stuvwx", "" })]
    [InlineData("2M", new[] { "abcdef", "stuvwx", "", "" })]
    [InlineData("9M", new[] { "abcdef", "", "", "" })]
    public void InsertDeleteLines_MoveCursorToColumnZero(string sequence, string[] expected)
    {
        var t = Filled();
        t.Feed(Csi + sequence);
        Assert.Equal(expected, t.Screen());
        Assert.Equal((0, 1), t.Cursor());
    }

    [Fact]
    public void InsertDeleteLines_RespectScrollRegion()
    {
        var t = Filled();
        t.Feed($"{Csi}1;3r{Csi}2;1H{Csi}L");
        Assert.Equal(new[] { "abcdef", "", "ghijkl", "stuvwx" }, t.Screen());
        t.Feed($"{Csi}2M");
        Assert.Equal(new[] { "abcdef", "", "", "stuvwx" }, t.Screen());

        t.Feed($"{Csi}4;1H{Csi}L"); // outside the region: ignored
        Assert.Equal("stuvwx", t.Row(3));
    }

    [Fact]
    public void DeleteChars_SplittingWideCharacter_BlanksBothHalves()
    {
        var t = Term(8, 2);
        t.Feed($"a中b\r{Csi}2C{Csi}P"); // delete the right half of 中
        Assert.Equal("a b", t.Row(0));
        Assert.False(t.CellAt(0, 1).IsContinuation);
        Assert.Equal(0, t.CellAt(0, 1).Rune);
    }

    [Fact]
    public void InsertChars_PushingWideCharacterOffTheEdge_BlanksIt()
    {
        var t = Term(4, 2);
        t.Feed($"ab中\r{Csi}@");
        Assert.Equal(" ab", t.Row(0));
        Assert.Equal(1, t.CellAt(0, 3).Width);
    }

    [Fact]
    public void EraseChars_OverWideCharacterHalf_BlanksWholeCharacter()
    {
        var t = Term(8, 2);
        t.Feed($"a中b{Csi}1;2H{Csi}X");
        Assert.Equal("a  b", t.Row(0));
        Assert.False(t.CellAt(0, 2).IsContinuation);
    }

    [Fact]
    public void ScreenAlignmentTest_FillsWithE()
    {
        var t = Term(3, 2);
        t.Feed($"{Csi}2;2H{Esc}#8");
        Assert.Equal(new[] { "EEE", "EEE" }, t.Screen());
        Assert.Equal((0, 0), t.Cursor());
    }
}
