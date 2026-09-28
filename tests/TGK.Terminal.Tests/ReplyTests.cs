using System.Collections.Generic;
using System.Text;
using Xunit;
using static TGK.Terminal.Tests.TestUtil;

namespace TGK.Terminal.Tests;

public class ReplyTests
{
    [Theory]
    [InlineData("5n", "\u001b[0n")]
    [InlineData("6n", "\u001b[3;5R")]
    [InlineData("?6n", "\u001b[?3;5;1R")]
    [InlineData("c", "\u001b[?62;22c")]
    [InlineData("0c", "\u001b[?62;22c")]
    [InlineData(">c", "\u001b[>1;10;0c")]
    [InlineData(">q", "\u001bP>|TGK\u001b\\")]
    [InlineData("18t", "\u001b[8;5;10t")]
    public void DeviceQueries(string query, string expected)
    {
        var t = Term(10, 5);
        var replies = t.CaptureOutput();
        t.Feed($"{Csi}3;5H{Csi}{query}");
        Assert.Equal(new[] { expected }, replies);
    }

    [Fact]
    public void CursorPositionReport_DuringPendingWrap_ReportsLastColumn()
    {
        var t = Term(5, 2);
        var replies = t.CaptureOutput();
        t.Feed($"abcde{Csi}6n");
        Assert.Equal($"{Csi}1;5R", replies[0]);
    }

    [Fact]
    public void UnsupportedQueries_SendNothing()
    {
        var t = Term();
        var replies = t.CaptureOutput();
        t.Feed($"{Csi}=c{Csi}14t{Csi}?1$p{Esc}]11;?\u0007");
        Assert.Empty(replies);
    }

    [Fact]
    public void Title_OscTerminatedByBel()
    {
        var t = Term();
        var titles = new List<string>();
        t.TitleChanged += titles.Add;
        t.Feed($"{Esc}]0;user@host: ~\u0007x");
        Assert.Equal("user@host: ~", t.Title);
        Assert.Equal(new[] { "user@host: ~" }, titles);
        Assert.Equal("x", t.Row(0));
    }

    [Fact]
    public void Title_OscTerminatedBySt()
    {
        var t = Term();
        t.Feed($"{Esc}]2;vim — ファイル{Esc}\\x");
        Assert.Equal("vim — ファイル", t.Title);
        Assert.Equal("x", t.Row(0));

        t.Feed("\u009d2;c1 title\u009c"); // 8-bit OSC and ST (UTF-8 encoded C1)
        Assert.Equal("c1 title", t.Title);
    }

    [Fact]
    public void Title_SplitAcrossFeeds()
    {
        var t = Term();
        byte[] bytes = Encoding.UTF8.GetBytes($"{Esc}]0;héllo\u0007");
        foreach (byte b in bytes)
            t.Feed([b]);
        Assert.Equal("héllo", t.Title);
    }

    [Fact]
    public void Title_UnchangedTitleDoesNotRaiseEvent()
    {
        var t = Term();
        int count = 0;
        t.TitleChanged += _ => count++;
        t.Feed($"{Esc}]0;a\u0007{Esc}]0;a\u0007");
        Assert.Equal(1, count);
    }

    [Fact]
    public void OtherOscCommands_AreIgnored()
    {
        var t = Term();
        t.Feed($"{Esc}]0;keep\u0007{Esc}]1;icon\u0007{Esc}]7;file://host/tmp\u0007{Esc}]8;;http://x\u0007link{Esc}]8;;\u0007{Esc}]52;c;aGVsbG8=\u0007{Esc}]garbage\u0007");
        Assert.Equal("keep", t.Title);
        Assert.Equal("link", t.Row(0));
    }

    [Fact]
    public void OverlongOsc_IsTruncatedSafely()
    {
        var t = Term();
        t.Feed($"{Esc}]0;{new string('a', 100_000)}\u0007x");
        Assert.Equal(4094, t.Title.Length);
        Assert.Equal("x", t.Row(0));
    }
}
