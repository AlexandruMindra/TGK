using Xunit;
using static TGK.Terminal.Tests.TestUtil;

namespace TGK.Terminal.Tests;

public class ModeTests
{
    [Fact]
    public void AlternateScreen1049_SavesCursorClearsAndRestores()
    {
        var t = Term(10, 3);
        t.Feed($"main1\r\nmain2{Csi}31m");
        t.Feed($"{Csi}?1049h");
        Assert.True(t.Modes.AlternateScreen);
        Assert.Equal(new[] { "", "", "" }, t.Screen());
        Assert.Equal((5, 1), t.Cursor()); // position kept on entry

        t.Feed($"{Csi}Halt{Csi}0m");
        Assert.Equal("alt", t.Row(0));

        t.Feed($"{Csi}?1049l");
        Assert.False(t.Modes.AlternateScreen);
        Assert.Equal(new[] { "main1", "main2", "" }, t.Screen());
        Assert.Equal((5, 1), t.Cursor());
        Assert.Equal(TermColor.Indexed(1), t.CurrentStyle.Fg); // restored with the cursor

        t.Feed($"{Csi}?1049h");
        Assert.Equal("", t.Row(0)); // the alternate screen is cleared on each entry
    }

    [Fact]
    public void AlternateScreen1047_ClearsOnExit()
    {
        var t = Term(10, 3);
        t.Feed($"main{Csi}?1047hx");
        Assert.Equal("    x", t.Row(0));
        t.Feed($"{Csi}?1047l");
        Assert.Equal("main", t.Row(0));
        t.Feed($"{Csi}?47h");
        Assert.Equal("", t.Row(0));
    }

    [Fact]
    public void AlternateScreen47_KeepsContent()
    {
        var t = Term(10, 3);
        t.Feed($"main{Csi}?47h{Csi}Halt{Csi}?47l");
        Assert.Equal("main", t.Row(0));
        t.Feed($"{Csi}?47h");
        Assert.Equal("alt", t.Row(0));
    }

    [Fact]
    public void Mode1048_SavesAndRestoresCursor()
    {
        var t = Term();
        t.Feed($"{Csi}3;4H{Csi}?1048h{Csi}H{Csi}?1048l");
        Assert.Equal((3, 2), t.Cursor());
    }

    [Fact]
    public void ScreenSwitch_MarksAllRowsDirty()
    {
        var t = Term(10, 3);
        t.ClearDirty();
        t.Feed($"{Csi}?1049h");
        Assert.True(t.IsRowDirty(0) && t.IsRowDirty(1) && t.IsRowDirty(2));
    }

    [Theory]
    [InlineData(1000, MouseTrackingMode.Normal)]
    [InlineData(1002, MouseTrackingMode.ButtonEvent)]
    [InlineData(1003, MouseTrackingMode.AnyEvent)]
    [InlineData(9, MouseTrackingMode.X10)]
    public void MouseTrackingModes(int mode, MouseTrackingMode expected)
    {
        var t = Term();
        t.Feed($"{Csi}?{mode}h");
        Assert.Equal(expected, t.Modes.MouseTracking);
        t.Feed($"{Csi}?{mode}l");
        Assert.Equal(MouseTrackingMode.None, t.Modes.MouseTracking);
    }

    [Fact]
    public void BooleanPrivateModes()
    {
        var t = Term();
        var m = t.Modes;
        Assert.False(m.ApplicationCursorKeys || m.BracketedPaste || m.MouseSgr || m.FocusEvents || m.ApplicationKeypad);

        t.Feed($"{Csi}?1;2004;1006;1004h{Esc}=");
        m = t.Modes;
        Assert.True(m.ApplicationCursorKeys && m.BracketedPaste && m.MouseSgr && m.FocusEvents && m.ApplicationKeypad);

        t.Feed($"{Csi}?1;2004;1006;1004l{Esc}>");
        m = t.Modes;
        Assert.False(m.ApplicationCursorKeys || m.BracketedPaste || m.MouseSgr || m.FocusEvents || m.ApplicationKeypad);
    }

    [Fact]
    public void UnknownModes_AreIgnored()
    {
        var t = Term();
        t.Feed($"{Csi}?5;1005;1015;9999h{Csi}12;999hx");
        Assert.Equal("x", t.Row(0));
    }

    [Fact]
    public void SoftReset_ResetsModesButKeepsScreen()
    {
        var t = Term(10, 5);
        t.Feed($"text{Csi}2;4r{Csi}?6;1h{Csi}4h{Csi}?25l{Csi}1;31m{Esc}(0{Csi}?7l");
        t.Feed($"{Csi}!p");
        var m = t.Modes;
        Assert.False(m.Origin || m.ApplicationCursorKeys || m.Insert);
        Assert.True(m.AutoWrap);
        Assert.True(t.CursorVisible);
        Assert.Equal(CellStyle.Default, t.CurrentStyle);
        Assert.Equal("text", t.Row(0));

        t.Feed($"{Csi}5;1Hq\r\n"); // charset back to ASCII, full-screen scroll region again
        Assert.Equal("q", t.Row(3));
    }

    [Fact]
    public void FullReset_ClearsEverything()
    {
        var t = Term(10, 2);
        t.Feed($"a\r\nb\r\nc{Csi}?1049h{Csi}?1;2004h{Csi}5 q{Esc}]0;title\u0007");
        t.Feed($"{Esc}c");
        Assert.Equal(new[] { "", "" }, t.Screen());
        Assert.Equal(0, t.ScrollbackCount);
        Assert.False(t.Modes.AlternateScreen);
        Assert.False(t.Modes.ApplicationCursorKeys);
        Assert.False(t.Modes.BracketedPaste);
        Assert.True(t.Modes.AutoWrap);
        Assert.Equal(CursorShape.Block, t.CursorShape);
        Assert.Equal((0, 0), t.Cursor());
        Assert.Equal("title", t.Title); // RIS keeps the title, like xterm
    }

    [Fact]
    public void ResetModes_LeavesAlternateScreenAndModesButKeepsHistory()
    {
        var t = Term(10, 3);
        t.Feed($"a\r\nb\r\nc\r\nprompt");
        t.Feed($"{Csi}?1049h{Csi}?1;1000;1006;1004;2004h{Esc}={Csi}2;3r{Csi}5 q{Csi}?25l{Csi}1;31m{Esc}]0;vim\u0007{Csi}H");
        t.Feed($"{Csi}1;2"); // the session died in the middle of a sequence

        t.ResetModes();

        TerminalModes m = t.Modes;
        Assert.False(m.AlternateScreen);
        Assert.Equal(MouseTrackingMode.None, m.MouseTracking);
        Assert.False(m.MouseSgr || m.ApplicationCursorKeys || m.ApplicationKeypad || m.BracketedPaste || m.FocusEvents);
        Assert.True(m.AutoWrap);
        Assert.True(t.CursorVisible);
        Assert.Equal(CursorShape.Block, t.CursorShape);
        Assert.Equal(CellStyle.Default, t.CurrentStyle);
        Assert.Equal("", t.Title);
        // The main screen and history stay; the restored cursor was mid-line, so it moved to a fresh line.
        Assert.Equal(new[] { "c", "prompt", "" }, t.Screen());
        Assert.Equal(2, t.ScrollbackCount);
        Assert.Equal((0, 2), t.Cursor());

        t.Feed("H\r\nnext"); // the parser state was reset too: "H" is text, not the end of the cut-off CSI
        Assert.Equal(new[] { "prompt", "H", "next" }, t.Screen());
    }

    [Fact]
    public void Reset_AlsoClearsTitleAndParserState()
    {
        var t = Term();
        t.Feed($"x{Esc}]0;title\u0007{Csi}1;2"); // ends in the middle of a CSI sequence
        t.Reset();
        Assert.Equal("", t.Title);
        t.Feed("H");
        Assert.Equal("H", t.Row(0));
    }
}
