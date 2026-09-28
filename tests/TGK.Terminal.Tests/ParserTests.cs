using System;
using System.Diagnostics;
using System.Text;
using Xunit;
using static TGK.Terminal.Tests.TestUtil;

namespace TGK.Terminal.Tests;

public class ParserTests
{
    [Theory]
    [InlineData("\u001bP1$r0m\u001b\\")] // DCS (DECRQSS reply)
    [InlineData("\u001bPq#0;2;0;0;0#0~~@@vv\u001b\\")] // sixel
    [InlineData("\u001b_Gf=24;AAAA\u001b\\")] // APC (kitty graphics)
    [InlineData("\u001b^privacy message\u001b\\")] // PM
    [InlineData("\u001bXstart of string\u001b\\")] // SOS
    [InlineData("\u0090ignored\u009c")] // 8-bit DCS ... ST
    public void StringSequences_AreConsumedAndIgnored(string sequence)
    {
        var t = Term(20, 2);
        t.Feed($"a{sequence}b");
        Assert.Equal("ab", t.Row(0));
    }

    [Fact]
    public void CancelAndSubstitute_AbortSequences()
    {
        var t = Term();
        t.Feed($"{Csi}5\u0018A{Csi}3\u001aB");
        Assert.Equal("AB", t.Row(0));
    }

    [Fact]
    public void EscapeInsideCsi_StartsNewSequence()
    {
        var t = Term();
        t.Feed($"{Csi}5{Csi}2Cx");
        Assert.Equal("  x", t.Row(0));
    }

    [Fact]
    public void HugeParameters_AreClampedWithoutOverflow()
    {
        var t = Term(10, 5);
        t.Feed($"{Csi}99999999999999999999999;99999999999999999999999H");
        Assert.Equal((9, 4), t.Cursor());
        t.Feed($"{Csi}99999999999999999999A{Csi}99999999999D");
        Assert.Equal((0, 0), t.Cursor());
        t.Feed($"{Csi}99999999999@{Csi}99999999999P{Csi}99999999999L{Csi}99999999999M{Csi}99999999999X");
        Assert.Equal((0, 0), t.Cursor());
    }

    [Fact]
    public void TooManyParameters_AreDropped()
    {
        var t = Term();
        var sb = new StringBuilder(Csi);
        for (int i = 0; i < 200; i++)
            sb.Append("1;");
        sb.Append("31mx");
        t.Feed(sb.ToString());
        Assert.Equal('x', t.CellAt(0, 0).Rune);
        Assert.Equal(CellFlags.Bold, t.CellAt(0, 0).Style.Flags);
        Assert.Equal(TermColor.Default, t.CellAt(0, 0).Style.Fg); // 31 was beyond the parameter limit
    }

    [Fact]
    public void InvalidSequences_AreIgnored()
    {
        var t = Term();
        t.Feed($"{Csi}1?2Ha{Csi}1 2Hb{Csi}  !!!Hc{Esc}%Gd{Esc}(%5e{Csi}?1;2$pf");
        Assert.Equal("abcdef", t.Row(0));
    }

    [Fact]
    public void NonAsciiInsideEscapeSequence_AbortsSequenceAndPrints()
    {
        var t = Term();
        t.Feed($"{Csi}1é");
        Assert.Equal("é", t.Row(0));
    }

    [Fact]
    public void DeleteCharacter_IsIgnored()
    {
        var t = Term();
        t.Feed("a\u007fb");
        Assert.Equal("ab", t.Row(0));
    }

    [Fact]
    public void RandomInput_NeverThrowsAndKeepsCursorInBounds()
    {
        var random = new Random(1234);
        var t = Term(40, 12);
        var chunk = new byte[4096];
        for (int i = 0; i < 256; i++)
        {
            random.NextBytes(chunk);
            // Bias towards ESC and CSI-relevant bytes so that many sequences are exercised.
            for (int j = 0; j < chunk.Length; j += 7)
                chunk[j] = (byte)"\u001b[;?0123456789mHJKr\n\r"[random.Next(21)];
            t.Feed(chunk);
            Assert.InRange(t.CursorCol, 0, t.Cols - 1);
            Assert.InRange(t.CursorRow, 0, t.Rows - 1);
            if (i % 64 == 0)
                t.Resize(random.Next(1, 80), random.Next(1, 30));
        }

        for (int line = -t.ScrollbackCount; line < t.Rows; line++)
            Assert.Equal(t.Cols, t.GetLine(line).Length);
    }

    [Fact]
    public void Throughput_TenMegabytesOfMixedOutput()
    {
        // ~10 MB of typical colored output (ls/git log style): SGR changes, UTF-8, tabs and line feeds.
        var sb = new StringBuilder();
        string[] words = ["alpha", "beta", "gamma", "délta", "日本語", "src/main.cs", "0x1F600", "~/work"];
        int n = 0;
        while (sb.Length < 1_000_000)
        {
            sb.Append($"{Csi}1;3{n % 8}m{words[n % words.Length]}{Csi}0m {Csi}38;5;{n % 256}m{n}{Csi}m\t");
            sb.Append($"{Csi}38;2;{n % 256};100;200mtruecolor{Csi}39m ");
            if (++n % 5 == 0)
                sb.Append("\r\n");
        }

        byte[] block = Encoding.UTF8.GetBytes(sb.ToString());
        var t = new TerminalEmulator(120, 40);
        t.Feed(block); // warm up
        var sw = Stopwatch.StartNew();
        long total = 0;
        while (total < 10_000_000)
        {
            t.Feed(block);
            total += block.Length;
        }

        sw.Stop();
        // Well under a second in Release; the bound is lenient for Debug builds and slow CI machines.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"{total / 1_000_000.0:F1} MB took {sw.ElapsedMilliseconds} ms");
    }
}
