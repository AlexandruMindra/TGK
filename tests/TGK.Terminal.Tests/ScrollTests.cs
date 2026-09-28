using Xunit;
using static TGK.Terminal.Tests.TestUtil;

namespace TGK.Terminal.Tests;

public class ScrollTests
{
    [Fact]
    public void LinesScrolledOffTheTop_GoToScrollback()
    {
        var t = Term(10, 3);
        t.Feed("l1\r\nl2\r\nl3\r\nl4\r\nl5");
        Assert.Equal(new[] { "l3", "l4", "l5" }, t.Screen());
        Assert.Equal(2, t.ScrollbackCount);
        Assert.Equal(2, t.ScrollbackAdded);
        Assert.Equal("l2", t.Row(-1));
        Assert.Equal("l1", t.Row(-2));
        Assert.Equal(10, t.GetLine(-1).Length); // trimmed storage is padded back to Cols
        Assert.Throws<System.ArgumentOutOfRangeException>(() => t.GetLine(-3));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => t.GetLine(3));
    }

    [Fact]
    public void Scrollback_KeepsAttributes()
    {
        var t = Term(10, 1);
        t.Feed($"{Csi}41mab{Csi}K\r\n");
        var line = t.GetLine(-1);
        Assert.Equal(TermColor.Indexed(1), line[0].Style.Bg);
        Assert.Equal(TermColor.Indexed(1), line[9].Style.Bg); // erased with a colored background: kept
    }

    [Fact]
    public void Scrollback_EvictsOldestLinesAtLimit()
    {
        var t = Term(10, 2, scrollback: 3);
        for (int i = 0; i < 10; i++)
            t.Feed($"line{i}\r\n");
        Assert.Equal(3, t.ScrollbackCount);
        Assert.Equal(9, t.ScrollbackAdded);
        Assert.Equal("line8", t.Row(-1));
        Assert.Equal("line6", t.Row(-3));
    }

    [Fact]
    public void Scrollback_Disabled()
    {
        var t = Term(10, 2, scrollback: 0);
        t.Feed("a\r\nb\r\nc");
        Assert.Equal(0, t.ScrollbackCount);
        Assert.Equal(new[] { "b", "c" }, t.Screen());
    }

    [Fact]
    public void ScrollRegion_ScrollsOnlyInsideAndDoesNotSaveLines()
    {
        var t = Term(10, 5);
        t.Feed($"top{Csi}5;1Hbottom{Csi}2;4r");
        Assert.Equal((0, 0), t.Cursor()); // DECSTBM homes the cursor
        t.Feed($"{Csi}2;1Ha\r\nb\r\nc\r\nd\r\ne");
        Assert.Equal(new[] { "top", "c", "d", "e", "bottom" }, t.Screen());
        Assert.Equal(0, t.ScrollbackCount);
    }

    [Fact]
    public void ScrollRegion_StartingAtTop_SavesLinesLikeXterm()
    {
        // apt reserves the last row for its progress bar with DECSTBM 1..rows-1; the output above must be kept.
        var t = Term(10, 4);
        t.Feed($"{Csi}4;1Hstatus{Csi}1;3r");
        t.Feed("a\r\nb\r\nc\r\nd\r\ne");
        Assert.Equal(new[] { "c", "d", "e", "status" }, t.Screen());
        Assert.Equal(2, t.ScrollbackCount);
        Assert.Equal(new[] { "a", "b" }, new[] { t.Row(-2), t.Row(-1) });
    }

    [Fact]
    public void ScrollRegion_InvalidIsIgnored()
    {
        var t = Term(10, 4);
        t.Feed($"{Csi}3;3r{Csi}4;1Ha\r\nb");
        Assert.Equal(new[] { "", "", "a", "b" }, t.Screen()); // full-screen scroll still in effect
    }

    [Fact]
    public void LineFeedBelowScrollRegion_DoesNotScroll()
    {
        var t = Term(10, 4);
        t.Feed($"x{Csi}1;2r{Csi}4;1Ha\nb\nc");
        Assert.Equal(new[] { "x", "", "", "abc" }, t.Screen());
    }

    [Fact]
    public void IndexReverseIndexNextLine()
    {
        var t = Term(10, 3);
        t.Feed($"a{Esc}Db");
        Assert.Equal(new[] { "a", " b", "" }, t.Screen());
        t.Feed($"{Esc}Ec");
        Assert.Equal("c", t.Row(2));

        t.Feed($"{Csi}H{Esc}Mz"); // RI at the top scrolls down
        Assert.Equal(new[] { "z", "a", " b" }, t.Screen());

        t.Feed("\u0084\u0085"); // 8-bit IND and NEL (UTF-8 encoded C1)
        Assert.Equal((0, 2), t.Cursor());
    }

    [Fact]
    public void ScrollUpDown_Sequences()
    {
        var t = Term(10, 4);
        t.Feed("1\r\n2\r\n3\r\n4");
        t.Feed($"{Csi}2S");
        Assert.Equal(new[] { "3", "4", "", "" }, t.Screen());
        Assert.Equal(2, t.ScrollbackCount); // SU with a full-screen region saves lines, like xterm
        t.Feed($"{Csi}T");
        Assert.Equal(new[] { "", "3", "4", "" }, t.Screen());
        t.Feed($"{Csi}1;2;3;4;5T"); // mouse highlight tracking: ignored
        Assert.Equal(new[] { "", "3", "4", "" }, t.Screen());
    }

    [Fact]
    public void ScrollDown_InsideRegion()
    {
        var t = Term(10, 4);
        t.Feed($"1\r\n2\r\n3\r\n4{Csi}2;3r{Csi}S");
        Assert.Equal(new[] { "1", "3", "", "4" }, t.Screen());
        t.Feed($"{Csi}2T");
        Assert.Equal(new[] { "1", "", "", "4" }, t.Screen());
    }

    [Fact]
    public void AlternateScreen_HasNoScrollback()
    {
        var t = Term(10, 2);
        t.Feed("m1\r\nm2\r\nm3");
        Assert.Equal(1, t.ScrollbackCount);
        t.Feed($"{Csi}?1049ha\r\nb\r\nc\r\nd");
        Assert.Equal(0, t.ScrollbackCount);
        t.Feed($"{Csi}?1049l");
        Assert.Equal(1, t.ScrollbackCount);
        Assert.Equal("m1", t.Row(-1));
    }

    [Fact]
    public void ClearScrollback_KeepsScreenAndCounter()
    {
        var t = Term(10, 2);
        t.Feed("l1\r\nl2\r\nl3");
        t.ClearDirty();

        t.ClearScrollback();

        Assert.Equal(0, t.ScrollbackCount);
        Assert.Equal(1, t.ScrollbackAdded);
        Assert.Equal(new[] { "l2", "l3" }, t.Screen());
        Assert.True(t.IsRowDirty(0));
    }
}
