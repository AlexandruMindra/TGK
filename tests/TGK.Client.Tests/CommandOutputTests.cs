using System;
using System.Linq;
using TGK.Client.Main;
using TGK.Core.Models;
using TGK.Core.Ssh;
using Xunit;

namespace TGK.Client.Tests;

public class CommandOutputTests
{
    private static readonly HostEntry Web = new() { Name = "web-01", Host = "10.0.0.5" };

    private static HostRun Ran(string stdout, string stderr = "", int? exit = 0, bool timedOut = false) =>
        new(Web, exit == 0 && !timedOut ? HostRunState.Succeeded : HostRunState.Failed,
            new CommandResult(timedOut ? null : exit, null, stdout, stderr, timedOut, false, TimeSpan.FromMilliseconds(420)),
            StartedAt: DateTimeOffset.UnixEpoch, Elapsed: TimeSpan.FromSeconds(9));

    [Fact]
    public void Server_output_keeps_colors_but_no_other_escape_sequence_or_control_character()
    {
        string raw = "\u001b[1;31mred\u001b[0m \u001b[2J\u001b[Hcleared \u001b]0;new title\u0007titled \u001b]8;;http://x\u001b\\link\u001b]8;;\u001b\\ "
            + "\u001b(Bcharset \u001b[?1049hscreen \u001b[38;2;1:2:3mrgb\u001b[m\a\b\u0000\u009bend\r\n\ttab";
        Assert.Equal("\u001b[1;31mred\u001b[0m cleared titled link charset screen \u001b[38;2;1:2:3mrgb\u001b[mend\r\n\ttab",
            CommandOutput.Clean(raw, keepColors: true));
        Assert.Equal("red cleared titled link charset screen rgbend\r\n\ttab", CommandOutput.Clean(raw, keepColors: false));
    }

    [Fact]
    public void A_cut_off_escape_sequence_is_dropped()
    {
        Assert.Equal("ok", CommandOutput.Clean("ok\u001b[31", true));
        Assert.Equal("ok", CommandOutput.Clean("ok\u001b]0;tit", true));
        Assert.Equal("ok", CommandOutput.Clean("ok\u001b", true));
    }

    [Fact]
    public void A_host_block_has_the_outcome_the_command_time_and_both_streams()
    {
        string block = CommandOutput.HostBlock(Ran("up 3 days\n", "warning\n", exit: 2), "deploy@10.0.0.5");
        string[] lines = Plain(block);
        Assert.Equal("── web-01 deploy@10.0.0.5  ✗ exit 2 0.4 s", lines[0]); // the command's time, not the connecting
        Assert.Equal("up 3 days", lines[1]);
        Assert.Equal("warning", lines[2]);
        Assert.Contains("\u001b[31mwarning", block); // errors in red
        Assert.Equal("", lines[3]);
    }

    [Fact]
    public void Long_output_is_cut_in_the_all_hosts_view()
    {
        string stdout = string.Concat(Enumerable.Range(1, 10).Select(i => $"{i}\n"));
        string[] cut = Plain(CommandOutput.HostBlock(Ran(stdout, "e1\ne2\ne3"), "x", maxLines: 4));
        Assert.Equal(["1", "2", "3", "4", "e1", "e2", "e3"], cut[1..8]);
        Assert.Equal("… 6 more lines: click the host in the list to see all of its output.", cut[8]);
        Assert.Equal(12, Plain(CommandOutput.HostBlock(Ran(stdout), "x")).Length - 1); // title, 10 lines, blank
    }

    [Fact]
    public void Hosts_that_did_not_run_say_why()
    {
        string error = CommandOutput.HostBlock(new HostRun(Web, HostRunState.Error, Error: "Connection refused by 10.0.0.5:22.",
            StartedAt: DateTimeOffset.UnixEpoch, Elapsed: TimeSpan.FromSeconds(0.1)), "x");
        Assert.Equal(["── web-01 x  ✗ could not run 0.1 s", "Connection refused by 10.0.0.5:22."], Plain(error)[..2]);
        string cancelled = CommandOutput.HostBlock(new HostRun(Web, HostRunState.Cancelled, Error: "No password was entered."), "x");
        Assert.Equal(["── web-01 x  ■ cancelled", "No password was entered."], Plain(cancelled)[..2]);
        Assert.Equal("(no output)", Plain(CommandOutput.HostBlock(Ran(""), "x"))[1]);
        Assert.Equal("Stopped after 0.4 s: it ran out of time.", Plain(CommandOutput.HostBlock(Ran("", timedOut: true), "x"))[2]);
    }

    [Theory]
    [InlineData(0.04, "0.0 s")]
    [InlineData(2.5, "2.5 s")]
    [InlineData(42, "42 s")]
    [InlineData(185, "3 min 5 s")]
    public void Durations_are_short(double seconds, string expected) => Assert.Equal(expected, CommandOutput.Duration(TimeSpan.FromSeconds(seconds)));

    // The lines as shown, without colors.
    private static string[] Plain(string text) => CommandOutput.Clean(text, keepColors: false).Split("\r\n");
}
