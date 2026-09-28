using Xunit;
using static TGK.Terminal.Tests.TestUtil;

namespace TGK.Terminal.Tests;

public class CursorTests
{
    [Theory]
    [InlineData("5;5H", 4, 4)]
    [InlineData("H", 0, 0)]
    [InlineData(";3H", 2, 0)]
    [InlineData("3H", 0, 2)]
    [InlineData("99;99H", 9, 4)] // clamped
    [InlineData("2;7f", 6, 1)] // HVP
    [InlineData("3;3H\u001b[A", 2, 1)] // CUU
    [InlineData("3;3H\u001b[2A", 2, 0)]
    [InlineData("3;3H\u001b[9A", 2, 0)]
    [InlineData("3;3H\u001b[B", 2, 3)] // CUD
    [InlineData("3;3H\u001b[9B", 2, 4)]
    [InlineData("3;3H\u001b[C", 3, 2)] // CUF
    [InlineData("3;3H\u001b[99C", 9, 2)]
    [InlineData("3;3H\u001b[0C", 3, 2)] // 0 means 1
    [InlineData("3;3H\u001b[D", 1, 2)] // CUB
    [InlineData("3;3H\u001b[9D", 0, 2)]
    [InlineData("3;3H\u001b[E", 0, 3)] // CNL
    [InlineData("3;3H\u001b[2F", 0, 0)] // CPL
    [InlineData("3;3H\u001b[7G", 6, 2)] // CHA
    [InlineData("3;3H\u001b[7`", 6, 2)] // HPA
    [InlineData("3;3H\u001b[2a", 4, 2)] // HPR
    [InlineData("3;3H\u001b[4d", 2, 3)] // VPA
    [InlineData("3;3H\u001b[e", 2, 3)] // VPR
    public void CursorMovement(string sequence, int col, int row)
    {
        var t = Term(10, 5);
        t.Feed(Csi + sequence);
        Assert.Equal((col, row), t.Cursor());
    }

    [Fact]
    public void CursorUpDown_StopAtScrollMargins()
    {
        var t = Term(10, 10);
        t.Feed($"{Csi}3;6r{Csi}5;1H{Csi}9A");
        Assert.Equal(2, t.CursorRow); // top margin
        t.Feed($"{Csi}9B");
        Assert.Equal(5, t.CursorRow); // bottom margin

        t.Feed($"{Csi}1;1H{Csi}9B"); // outside the region: stops at the region bottom when above it
        Assert.Equal(5, t.CursorRow);
        t.Feed($"{Csi}10;1H{Csi}9A"); // below the region: CUU stops at the region's top
        Assert.Equal(2, t.CursorRow);
        t.Feed($"{Csi}8;1H{Csi}9B"); // below the region: CUD goes to the screen bottom
        Assert.Equal(9, t.CursorRow);
    }

    [Fact]
    public void OriginMode_AddressesRelativeToScrollRegion()
    {
        var t = Term(10, 10);
        var replies = t.CaptureOutput();
        t.Feed($"{Csi}3;6r{Csi}?6h");
        Assert.True(t.Modes.Origin);
        Assert.Equal((0, 2), t.Cursor()); // DECOM homes the cursor to the region

        t.Feed($"{Csi}2;4H");
        Assert.Equal((3, 3), t.Cursor());
        t.Feed($"{Csi}99;1H");
        Assert.Equal(5, t.CursorRow); // clamped to the region
        t.Feed($"{Csi}2d");
        Assert.Equal(3, t.CursorRow);

        t.Feed($"{Csi}6n");
        Assert.Equal($"{Csi}2;1R", replies[^1]);

        t.Feed($"{Csi}?6l");
        Assert.Equal((0, 0), t.Cursor());
    }

    [Fact]
    public void SaveRestoreCursor_Decsc()
    {
        var t = Term();
        t.Feed($"{Csi}2;3H{Csi}1;31m{Esc}(0{Esc}7");
        t.Feed($"{Csi}H{Csi}0m{Esc}(B");
        t.Feed($"{Esc}8q");
        Assert.Equal((3, 1), t.Cursor());
        var cell = t.CellAt(1, 2);
        Assert.Equal('─', cell.Rune); // charset restored
        Assert.True(cell.Style.Has(CellFlags.Bold));
        Assert.Equal(TermColor.Indexed(1), cell.Style.Fg);
    }

    [Fact]
    public void SaveRestoreCursor_AnsiSU()
    {
        var t = Term();
        t.Feed($"{Csi}4;5H{Csi}s{Csi}H{Csi}u");
        Assert.Equal((4, 3), t.Cursor());
    }

    [Fact]
    public void RestoreCursor_WithoutSave_GoesHomeWithDefaultStyle()
    {
        var t = Term();
        t.Feed($"{Csi}3;3H{Csi}1m{Esc}8x");
        Assert.Equal((1, 0), t.Cursor());
        Assert.Equal(CellStyle.Default, t.CellAt(0, 0).Style);
    }

    [Fact]
    public void SaveRestoreCursor_PreservesOriginMode()
    {
        var t = Term(10, 10);
        t.Feed($"{Csi}3;6r{Csi}?6h{Esc}7{Csi}?6l{Esc}8");
        Assert.True(t.Modes.Origin);
    }

    [Fact]
    public void CursorVisibility_AndShape()
    {
        var t = Term();
        Assert.True(t.CursorVisible);
        t.Feed($"{Csi}?25l");
        Assert.False(t.CursorVisible);
        t.Feed($"{Csi}?25h");
        Assert.True(t.CursorVisible);

        t.Feed($"{Csi}6 q");
        Assert.Equal(CursorShape.Bar, t.CursorShape);
        Assert.False(t.CursorBlink);
        t.Feed($"{Csi}3 q");
        Assert.Equal(CursorShape.Underline, t.CursorShape);
        Assert.True(t.CursorBlink);
        t.Feed($"{Csi}2 q");
        Assert.Equal(CursorShape.Block, t.CursorShape);
        Assert.False(t.CursorBlink);
        t.Feed($"{Csi}?12h");
        Assert.True(t.CursorBlink);
        t.Feed($"{Csi}0 q");
        Assert.Equal(CursorShape.Block, t.CursorShape);
    }
}
