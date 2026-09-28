using System.Text;
using Xunit;
using static TGK.Terminal.Tests.TestUtil;

namespace TGK.Terminal.Tests;

public class PrintingTests
{
    [Fact]
    public void Print_WritesTextAndAdvancesCursor()
    {
        var t = Term();
        t.Feed("abc");
        Assert.Equal("abc", t.Row(0));
        Assert.Equal((3, 0), t.Cursor());
        Assert.Equal('a', t.CellAt(0, 0).Rune);
        Assert.Equal(1, t.CellAt(0, 0).Width);
    }

    [Fact]
    public void DefaultCell_IsEmptySingleWidth()
    {
        var cell = default(Cell);
        Assert.Equal(0, cell.Rune);
        Assert.Equal(1, cell.Width);
        Assert.False(cell.IsContinuation);
        Assert.Equal(CellStyle.Default, cell.Style);
    }

    [Fact]
    public void PendingWrap_CursorStaysOnLastColumnUntilNextChar()
    {
        var t = Term(5, 3);
        t.Feed("abcde");
        Assert.Equal((4, 0), t.Cursor());
        Assert.False(t.IsLineWrapped(0));

        t.Feed("f");
        Assert.Equal("abcde", t.Row(0));
        Assert.Equal("f", t.Row(1));
        Assert.Equal((1, 1), t.Cursor());
        Assert.True(t.IsLineWrapped(0));
    }

    [Fact]
    public void PendingWrap_CrLfDoesNotProduceBlankLine()
    {
        var t = Term(5, 3);
        t.Feed("abcde\r\nx");
        Assert.Equal(new[] { "abcde", "x", "" }, t.Screen());
        Assert.False(t.IsLineWrapped(0));
    }

    [Fact]
    public void PendingWrap_IsCancelledByCursorMovement()
    {
        var t = Term(5, 3);
        t.Feed($"abcde{Csi}Dx");
        Assert.Equal("abcxe", t.Row(0));
        Assert.Equal("", t.Row(1));
    }

    [Fact]
    public void PendingWrap_WrapScrollsAtBottom()
    {
        var t = Term(3, 2);
        t.Feed("abcdefg");
        Assert.Equal(new[] { "def", "g" }, t.Screen());
        Assert.Equal("abc", t.Row(-1));
        Assert.True(t.IsLineWrapped(-1));
    }

    [Fact]
    public void AutowrapDisabled_OverwritesLastColumn()
    {
        var t = Term(5, 3);
        t.Feed($"{Csi}?7labcdefg");
        Assert.Equal("abcdg", t.Row(0));
        Assert.Equal((4, 0), t.Cursor());
        Assert.False(t.Modes.AutoWrap);

        t.Feed($"{Csi}?7h");
        Assert.True(t.Modes.AutoWrap);
    }

    [Fact]
    public void CarriageReturn_And_LineFeed()
    {
        var t = Term();
        t.Feed("ab\rc");
        Assert.Equal("cb", t.Row(0));

        t.Feed("\nd");
        Assert.Equal(" d", t.Row(1)); // LF keeps the column

        t.Feed("\u000be\u000cf"); // VT and FF act like LF
        Assert.Equal("  e", t.Row(2));
        Assert.Equal("   f", t.Row(3));
    }

    [Fact]
    public void LineFeedNewLineMode_AlsoReturnsCarriage()
    {
        var t = Term();
        t.Feed($"{Csi}20ha\nb");
        Assert.True(t.Modes.LineFeedNewLine);
        Assert.Equal("b", t.Row(1));
        t.Feed($"{Csi}20l");
        Assert.False(t.Modes.LineFeedNewLine);
    }

    [Fact]
    public void Backspace_MovesLeftAndStopsAtColumnZero()
    {
        var t = Term();
        t.Feed("abc\b\bX");
        Assert.Equal("aXc", t.Row(0));
        t.Feed("\b\b\b\b");
        Assert.Equal((0, 0), t.Cursor());
    }

    [Fact]
    public void Backspace_FromPendingWrapMovesToSecondLastColumn()
    {
        var t = Term(5, 2);
        t.Feed("abcde\bX");
        Assert.Equal("abcXe", t.Row(0));
    }

    [Fact]
    public void Tab_UsesDefaultStopsEvery8AndClampsAtLastColumn()
    {
        var t = Term(20, 2);
        t.Feed("a\tb\tc\td");
        Assert.Equal('b', t.CellAt(0, 8).Rune);
        Assert.Equal('c', t.CellAt(0, 16).Rune);
        Assert.Equal('d', t.CellAt(0, 19).Rune);
    }

    [Fact]
    public void TabStops_SetClearAndMoveBackward()
    {
        var t = Term(20, 2);
        t.Feed($"{Csi}3g{Csi}4G{Esc}H\r\tX"); // clear all, set a stop at column 3
        Assert.Equal('X', t.CellAt(0, 3).Rune);

        t.Feed($"{Csi}0g\r\tY"); // cursor is at column 4; clear stop at column 4 (none), tab to 3 again
        Assert.Equal('Y', t.CellAt(0, 3).Rune);

        t.Feed($"{Csi}4G{Csi}g\r\t"); // clear the stop at column 3: tab goes to the last column
        Assert.Equal(19, t.CursorCol);

        var t2 = Term(30, 2);
        t2.Feed($"{Csi}2I");
        Assert.Equal(16, t2.CursorCol);
        t2.Feed($"{Csi}Z");
        Assert.Equal(8, t2.CursorCol);
        t2.Feed($"{Csi}5Z");
        Assert.Equal(0, t2.CursorCol);
    }

    [Fact]
    public void WideCharacter_OccupiesTwoCells()
    {
        var t = Term();
        t.Feed("中文x");
        Assert.Equal('中', t.CellAt(0, 0).Rune);
        Assert.Equal(2, t.CellAt(0, 0).Width);
        Assert.True(t.CellAt(0, 1).IsContinuation);
        Assert.Equal(0, t.CellAt(0, 1).Width);
        Assert.Equal('文', t.CellAt(0, 2).Rune);
        Assert.Equal('x', t.CellAt(0, 4).Rune);
        Assert.Equal((5, 0), t.Cursor());
    }

    [Fact]
    public void Emoji_IsWide()
    {
        var t = Term();
        t.Feed("😀");
        Assert.Equal(0x1F600, t.CellAt(0, 0).Rune);
        Assert.Equal(2, t.CellAt(0, 0).Width);
        Assert.Equal(2, t.CursorCol);
    }

    [Fact]
    public void WideCharacter_AtLineEnd_WrapsAndLeavesLastCellEmpty()
    {
        var t = Term(5, 3);
        t.Feed("abcd中");
        Assert.Equal("abcd", t.Row(0));
        Assert.Equal(0, t.CellAt(0, 4).Rune);
        Assert.True(t.IsLineWrapped(0));
        Assert.Equal('中', t.CellAt(1, 0).Rune);
        Assert.Equal((2, 1), t.Cursor());
    }

    [Fact]
    public void WideCharacter_FillingLastTwoColumns_SetsPendingWrap()
    {
        var t = Term(5, 3);
        t.Feed("abc中");
        Assert.Equal((4, 0), t.Cursor());
        t.Feed("d");
        Assert.Equal("abc中", t.Row(0));
        Assert.Equal("d", t.Row(1));
    }

    [Fact]
    public void WideCharacter_AtLineEndWithoutAutowrap_IsPlacedInLastTwoColumns()
    {
        var t = Term(5, 3);
        t.Feed($"{Csi}?7labcd中");
        Assert.Equal("abc中", t.Row(0));
        Assert.Equal(0, t.CursorRow);
    }

    [Fact]
    public void OverwritingHalfOfWideCharacter_ClearsTheOtherHalf()
    {
        var t = Term();
        t.Feed("中\ra");
        Assert.Equal('a', t.CellAt(0, 0).Rune);
        Assert.False(t.CellAt(0, 1).IsContinuation);
        Assert.Equal(0, t.CellAt(0, 1).Rune);

        t.Feed($"\r中{Csi}2Gb");
        Assert.Equal(0, t.CellAt(0, 0).Rune);
        Assert.Equal(1, t.CellAt(0, 0).Width);
        Assert.Equal('b', t.CellAt(0, 1).Rune);
    }

    [Fact]
    public void CombiningMark_ComposesWithPreviousCharacter()
    {
        var t = Term();
        t.Feed("éx");
        Assert.Equal('é', t.CellAt(0, 0).Rune);
        Assert.Equal('x', t.CellAt(0, 1).Rune);
        Assert.Equal((2, 0), t.Cursor());
    }

    [Fact]
    public void CombiningMark_WithoutPrecomposedForm_IsDroppedWithoutAdvancing()
    {
        var t = Term();
        t.Feed("q́‍");
        Assert.Equal('q', t.CellAt(0, 0).Rune);
        Assert.Equal((1, 0), t.Cursor());

        t.Feed("\ŕ"); // nothing to combine with
        Assert.Equal((0, 0), t.Cursor());
    }

    [Fact]
    public void CombiningMark_AfterCharacterInLastColumn_Composes()
    {
        var t = Term(3, 2);
        t.Feed("abé");
        Assert.Equal("abé", t.Row(0));
        Assert.Equal((2, 0), t.Cursor());
    }

    [Fact]
    public void Utf8_SplitAcrossFeedCalls()
    {
        var t = Term();
        byte[] bytes = Encoding.UTF8.GetBytes("é中😀");
        foreach (byte b in bytes)
            t.Feed([b]);
        Assert.Equal("é中😀", t.Row(0));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, (byte)'a' }, "�a")]
    [InlineData(new byte[] { 0xC3, (byte)'b' }, "�b")] // truncated sequence
    [InlineData(new byte[] { 0xC0, 0x80 }, "��")] // overlong NUL
    [InlineData(new byte[] { 0xE0, 0x80, 0xAF }, "�")] // overlong '/'
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 }, "�")] // UTF-16 surrogate
    [InlineData(new byte[] { 0xF4, 0x90, 0x80, 0x80 }, "�")] // above U+10FFFF
    [InlineData(new byte[] { 0x80, (byte)'c' }, "�c")] // stray continuation byte
    [InlineData(new byte[] { 0xE4, 0xB8, 0x1B, (byte)'[', (byte)'C', (byte)'d' }, "� d")] // interrupted by ESC
    public void Utf8_InvalidInputProducesReplacementCharacter(byte[] input, string expected)
    {
        var t = Term();
        t.Feed(input);
        Assert.Equal(expected, t.Row(0));
    }

    [Fact]
    public void Utf8_TruncatedSequenceAtEndOfFeedIsCompletedByNextFeed()
    {
        var t = Term();
        t.Feed(new byte[] { 0xE4, 0xB8 });
        Assert.Equal("", t.Row(0));
        t.Feed(new byte[] { 0xAD });
        Assert.Equal("中", t.Row(0));
    }

    [Fact]
    public void DecSpecialGraphics_MapsLineDrawing()
    {
        var t = Term();
        t.Feed($"{Esc}(0lqwqk{Esc}(Bq");
        Assert.Equal("┌─┬─┐q", t.Row(0));
        t.Feed($"\r\n{Esc}(0xa`~{Esc}(B");
        Assert.Equal("│▒◆·", t.Row(1));
    }

    [Fact]
    public void ShiftOutShiftIn_SwitchBetweenG0AndG1()
    {
        var t = Term();
        t.Feed($"{Esc})0q\u000eq\u000fq");
        Assert.Equal("q─q", t.Row(0));
    }

    [Fact]
    public void UkCharset_MapsHash()
    {
        var t = Term();
        t.Feed($"{Esc}(A#{Esc}(B#");
        Assert.Equal("£#", t.Row(0));
    }

    [Fact]
    public void Repeat_RepeatsLastPrintedCharacter()
    {
        var t = Term();
        t.Feed($"ab{Csi}3b");
        Assert.Equal("abbbb", t.Row(0));

        var t2 = Term();
        t2.Feed($"{Csi}3b"); // nothing printed yet
        Assert.Equal("", t2.Row(0));
    }

    [Fact]
    public void Repeat_IsCappedAtTheLineWidth()
    {
        var t = Term(10, 4);
        t.Feed($"x{Csi}65535b");
        Assert.Equal(new[] { "xxxxxxxxxx", "x", "", "" }, t.Screen()); // 1 + 10 characters, not a screenful

        var noWrap = Term(5, 2);
        noWrap.Feed($"{Csi}?7la{Csi}9b");
        Assert.Equal(new[] { "aaaaa", "" }, noWrap.Screen());
        Assert.Equal((4, 0), noWrap.Cursor());

        var wide = Term(10, 4);
        wide.Feed($"中{Csi}65535b");
        Assert.Equal(new[] { "中中中中中", "中", "", "" }, wide.Screen()); // one line's worth of cells
    }

    [Fact]
    public void InsertMode_ShiftsExistingText()
    {
        var t = Term(6, 2);
        t.Feed($"abcde\r{Csi}4hXY");
        Assert.True(t.Modes.Insert);
        Assert.Equal("XYabcd", t.Row(0));
        t.Feed($"{Csi}4lZ");
        Assert.Equal("XYZbcd", t.Row(0));
    }

    [Fact]
    public void ControlCharacterInsideCsi_IsExecuted()
    {
        var t = Term();
        t.Feed($"abc{Csi}1\rCX");
        Assert.Equal("aXc", t.Row(0));
    }

    [Fact]
    public void Bell_RaisesEvent()
    {
        var t = Term();
        int bells = 0;
        t.Bell += () => bells++;
        t.Feed("a\u0007b");
        Assert.Equal(1, bells);
        Assert.Equal("ab", t.Row(0));
    }

    [Fact]
    public void DirtyTracking_And_Version()
    {
        var t = Term();
        t.ClearDirty();
        long v = t.Version;
        t.Feed($"{Csi}3;1Hx");
        Assert.True(t.IsRowDirty(2));
        Assert.False(t.IsRowDirty(0));
        Assert.True(t.Version > v);

        t.ClearDirty();
        Assert.False(t.IsRowDirty(2));
        Assert.False(t.IsRowDirty(99));
    }
}
