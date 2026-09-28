using Xunit;
using static TGK.Terminal.Tests.TestUtil;

namespace TGK.Terminal.Tests;

public class ResizeTests
{
    [Fact]
    public void ShrinkRows_WithCursorAtBottom_PushesTopLinesToScrollback()
    {
        var t = Term(10, 5);
        t.Feed("1\r\n2\r\n3\r\n4\r\n5");
        t.Resize(10, 3);
        Assert.Equal(new[] { "3", "4", "5" }, t.Screen());
        Assert.Equal(2, t.ScrollbackCount);
        Assert.Equal("2", t.Row(-1));
        Assert.Equal((1, 2), t.Cursor());
    }

    [Fact]
    public void ShrinkRows_WithCursorAtTop_DropsBottomLines()
    {
        var t = Term(10, 5);
        t.Feed($"1\r\n2{Csi}H");
        t.Resize(10, 3);
        Assert.Equal(new[] { "1", "2", "" }, t.Screen());
        Assert.Equal(0, t.ScrollbackCount);
        Assert.Equal((0, 0), t.Cursor());
    }

    [Fact]
    public void GrowRows_PullsLinesBackFromScrollback()
    {
        var t = Term(10, 3);
        t.Feed("1\r\n2\r\n3\r\n4\r\n5");
        Assert.Equal(2, t.ScrollbackCount);
        t.Resize(10, 6);
        Assert.Equal(new[] { "1", "2", "3", "4", "5", "" }, t.Screen());
        Assert.Equal(0, t.ScrollbackCount);
        Assert.Equal((1, 4), t.Cursor());
        Assert.Equal(10, t.GetLine(0).Length);
    }

    [Fact]
    public void GrowRows_WithoutScrollback_AddsBlankLines()
    {
        var t = Term(10, 2);
        t.Feed("a\r\nb");
        t.Resize(10, 4);
        Assert.Equal(new[] { "a", "b", "", "" }, t.Screen());
        Assert.Equal((1, 1), t.Cursor());
    }

    [Fact]
    public void Columns_TruncateAndPad()
    {
        var t = Term(10, 2);
        t.Feed("abcdefghij");
        t.Resize(4, 2);
        Assert.Equal("abcd", t.Row(0));
        Assert.Equal(3, t.CursorCol);
        t.Resize(8, 2);
        Assert.Equal("abcd", t.Row(0));
        Assert.Equal(8, t.GetLine(0).Length);
    }

    [Fact]
    public void Columns_ShrinkingThroughWideCharacter_BlanksIt()
    {
        var t = Term(10, 2);
        t.Feed("abc中");
        t.Resize(4, 2);
        Assert.Equal("abc", t.Row(0));
        Assert.Equal(1, t.CellAt(0, 3).Width);
    }

    [Fact]
    public void Columns_ExtendDefaultTabStops()
    {
        var t = Term(10, 2);
        t.Resize(30, 2);
        t.Feed("\t\t\tx");
        Assert.Equal('x', t.CellAt(0, 24).Rune);
    }

    [Fact]
    public void Resize_ResetsScrollRegion()
    {
        var t = Term(10, 5);
        t.Feed($"{Csi}2;3r");
        t.Resize(10, 4);
        t.Feed($"{Csi}4;1Ha\r\nb");
        Assert.Equal(new[] { "", "", "a", "b" }, t.Screen());
    }

    [Fact]
    public void ScrollbackLinesFromWiderScreen_AreTruncatedByGetLine()
    {
        var t = Term(10, 1);
        t.Feed("0123456789\r\nx");
        t.Resize(5, 1);
        Assert.Equal(5, t.GetLine(-1).Length);
        Assert.Equal("01234", t.Row(-1));
        t.Resize(12, 1);
        Assert.Equal("0123456789", t.Row(-1));
    }

    [Fact]
    public void LinesRecycledFromFullScrollback_TakeTheCurrentWidth()
    {
        var t = Term(5, 2, scrollback: 2);
        t.Feed("a\r\nb\r\nc\r\n"); // the scrollback is now full of 5-column lines
        t.Resize(10, 2);
        t.Feed("1\r\n"); // evicts a 5-column line and reuses it as the new bottom row
        Assert.Equal(new[] { "b", "c" }, new[] { t.Row(-2), t.Row(-1) });

        t.Feed("0123456789");
        Assert.Equal("0123456789", t.Row(1));
    }

    [Fact]
    public void AlternateScreen_ResizeDoesNotTouchScrollback_AndMainStaysAnchored()
    {
        var t = Term(10, 5);
        t.Feed("1\r\n2\r\n3\r\n4\r\n5");
        t.Feed($"{Csi}?1049h{Csi}5;1Halt");
        t.Resize(10, 3);
        Assert.Equal(0, t.ScrollbackCount);
        Assert.Equal("alt", t.Row(2));
        Assert.Equal(2, t.CursorRow);

        t.Feed($"{Csi}?1049l");
        Assert.Equal(new[] { "3", "4", "5" }, t.Screen());
        Assert.Equal((1, 2), t.Cursor()); // saved cursor moved with its line
        Assert.Equal(2, t.ScrollbackCount);
    }

    [Fact]
    public void Resize_ClampsCursorAndMarksEverythingDirty()
    {
        var t = Term(10, 5);
        t.Feed($"{Csi}5;10H");
        t.ClearDirty();
        long version = t.Version;
        t.Resize(3, 5);
        Assert.Equal((2, 4), t.Cursor());
        Assert.True(t.IsRowDirty(0) && t.IsRowDirty(4));
        Assert.True(t.Version > version);

        t.Resize(0, -5); // clamped to 1x1
        Assert.Equal((1, 1), (t.Cols, t.Rows));
        Assert.Equal((0, 0), t.Cursor());
    }
}
