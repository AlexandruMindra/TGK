using Xunit;
using static TGK.Terminal.Tests.TestUtil;

namespace TGK.Terminal.Tests;

public class TextTests
{
    [Fact]
    public void GetText_SingleLine_EndIsExclusive()
    {
        var t = Term(20, 3);
        t.Feed("hello world");
        Assert.Equal("hello", t.GetText(0, 0, 0, 5));
        Assert.Equal("world", t.GetText(0, 6, 0, 11));
        Assert.Equal("hello world", t.GetText(0, 0, 0, 20));
    }

    [Fact]
    public void GetText_TrimsTrailingBlanksAndJoinsHardLinesWithNewline()
    {
        var t = Term(10, 3);
        t.Feed("ab   \r\ncd\r\n  ef");
        Assert.Equal("ab\ncd\n  ef", t.GetText(0, 0, 2, 10));
        Assert.Equal("b\ncd\n  e", t.GetText(0, 1, 2, 3));
    }

    [Fact]
    public void GetText_JoinsSoftWrappedLines()
    {
        var t = Term(5, 4);
        t.Feed("abcdefgh ij\r\nxy");
        Assert.True(t.IsLineWrapped(0));
        Assert.True(t.IsLineWrapped(1));
        Assert.False(t.IsLineWrapped(2));
        Assert.Equal("abcdefgh ij", t.GetText(0, 0, 2, 5));
    }

    [Fact]
    public void GetText_SoftWrapKeepsSpacesAtWrapPoint()
    {
        var t = Term(5, 2);
        t.Feed("abcd efgh");
        Assert.Equal("abcd efgh", t.GetText(0, 0, 1, 5));
    }

    [Fact]
    public void GetText_WideCharacters()
    {
        var t = Term(10, 2);
        t.Feed("a中文b");
        Assert.Equal("a中文b", t.GetText(0, 0, 0, 10));
        Assert.Equal("中文", t.GetText(0, 2, 0, 5)); // starts on the right half of 中
        Assert.Equal("文", t.GetText(0, 3, 0, 4));
    }

    [Fact]
    public void GetText_WideCharacterWrapPadding_IsDropped()
    {
        var t = Term(5, 2);
        t.Feed("abcd中");
        Assert.Equal("abcd中", t.GetText(0, 0, 1, 5));
    }

    [Fact]
    public void GetText_ReversedRangeAndScrollback()
    {
        var t = Term(10, 2);
        t.Feed("one\r\ntwo\r\nthree");
        Assert.Equal("one\ntwo\nthree", t.GetText(1, 10, -1, 0));
        Assert.Equal("one\ntwo\nthree", t.GetText(-99, 5, 99, 0)); // clamped
    }

    [Fact]
    public void GetText_EmptyRange()
    {
        var t = Term(10, 2);
        t.Feed("abc");
        Assert.Equal("", t.GetText(0, 1, 0, 1));
        Assert.Equal("", t.GetText(1, 0, 1, 10));
    }

    [Theory]
    [InlineData(0, 0, 3)]
    [InlineData(2, 0, 3)]
    [InlineData(3, 3, 4)] // single space between words
    [InlineData(5, 4, 22)] // path-like word
    [InlineData(23, 23, 24)] // separator
    [InlineData(25, 24, 27)] // word inside parentheses
    [InlineData(29, 28, 30)] // run of blanks up to the line end
    public void GetWordBounds(int col, int start, int end)
    {
        var t = Term(30, 2);
        t.Feed("foo ~/src/a-b.c:12:x=y (baz)");
        Assert.Equal((start, end), t.GetWordBounds(0, col));
    }

    [Fact]
    public void GetWordBounds_WideCharacters()
    {
        var t = Term(20, 2);
        t.Feed("ab 日本語 cd");
        Assert.Equal((3, 9), t.GetWordBounds(0, 4)); // clicking the right half of 日
        Assert.Equal((3, 9), t.GetWordBounds(0, 8));
    }

    [Fact]
    public void GetWordBounds_OnTrimmedScrollbackLine()
    {
        var t = Term(10, 1);
        t.Feed("ab cd\r\n");
        Assert.Equal((3, 5), t.GetWordBounds(-1, 4));
        Assert.Equal((5, 10), t.GetWordBounds(-1, 8));
    }
}
