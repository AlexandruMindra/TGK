using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Models;
using TGK.Core.Ssh;
using Xunit;

namespace TGK.Core.Tests;

public sealed class MultiHostRunTests
{
    private static HostEntry Host(string name) => new() { Name = name, Host = name + ".example.com", Username = "deploy" };

    private static CommandResult Exit(int code, string stdout = "") => new(code, null, stdout, "", false, false, TimeSpan.FromMilliseconds(5));

    [Fact]
    public async Task EveryHost_GetsItsOutcome_AndOneFailingHostStopsNoOther()
    {
        HostEntry ok = Host("ok"), failing = Host("failing"), unreachable = Host("unreachable"), declined = Host("declined"), slow = Host("slow");
        var run = new MultiHostRun("uptime", [ok, failing, unreachable, declined, slow]);
        await run.RunAsync(async (host, progress, ct) =>
        {
            progress(HostRunState.Running);
            await Task.Yield();
            return host.Name switch
            {
                "ok" => Exit(0, "up 3 days"),
                "failing" => Exit(2),
                "unreachable" => throw new SshSessionException(SshErrorKind.HostUnreachable, "unreachable.example.com is not reachable."),
                "declined" => throw new OperationCanceledException("No password was entered."),
                "slow" => new CommandResult(null, null, "", "", TimedOut: true, false, TimeSpan.FromSeconds(1)),
                _ => throw new InvalidOperationException(),
            };
        });

        Assert.True(run.IsOver);
        IReadOnlyList<HostRun> hosts = run.Hosts;
        Assert.Equal(["ok", "failing", "unreachable", "declined", "slow"], hosts.Select(h => h.Host.Name));
        Assert.Equal(HostRunState.Succeeded, hosts[0].State);
        Assert.Equal("up 3 days", hosts[0].Result!.Stdout);
        Assert.Equal("exit 0", hosts[0].Outcome);
        Assert.Equal(HostRunState.Failed, hosts[1].State);
        Assert.Equal("exit 2", hosts[1].Outcome);
        Assert.Equal(HostRunState.Error, hosts[2].State);
        Assert.Equal("unreachable.example.com is not reachable.", hosts[2].Error);
        Assert.Equal(HostRunState.Cancelled, hosts[3].State);
        Assert.Equal("No password was entered.", hosts[3].Error);
        Assert.Equal(HostRunState.Failed, hosts[4].State);
        Assert.Equal("timed out", hosts[4].Outcome);
        Assert.All(hosts, h => Assert.NotNull(h.StartedAt));
        Assert.Equal(5, run.Finished.Count);
        Assert.Equal("1 succeeded, 2 failed, 1 could not run, 1 cancelled", MultiHostRun.Summarize(hosts));
    }

    [Fact]
    public async Task NoMoreThanMaxParallel_RunAtOnce()
    {
        HostEntry[] hosts = Enumerable.Range(0, 12).Select(i => Host($"h{i}")).ToArray();
        var run = new MultiHostRun("true", hosts, maxParallel: 3);
        int now = 0, peak = 0;
        await run.RunAsync(async (_, _, _) =>
        {
            int current = Interlocked.Increment(ref now);
            InterlockedMax(ref peak, current);
            await Task.Delay(20);
            Interlocked.Decrement(ref now);
            return Exit(0);
        });
        Assert.Equal(3, peak);
        Assert.All(run.Hosts, h => Assert.Equal(HostRunState.Succeeded, h.State));

        static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
            {
            }
        }
    }

    [Fact]
    public async Task Stopping_CancelsTheRunningHosts_AndSkipsTheWaitingOnes()
    {
        HostEntry[] hosts = [Host("a"), Host("b"), Host("c")];
        var run = new MultiHostRun("sleep 600", hosts, maxParallel: 2);
        using var stop = new CancellationTokenSource();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        Task task = run.RunAsync(async (_, progress, ct) =>
        {
            progress(HostRunState.Running);
            if (Interlocked.Increment(ref started) == 2)
                running.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Exit(0);
        }, stop.Token);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([HostRunState.Running, HostRunState.Running, HostRunState.Waiting], run.Hosts.Select(h => h.State));

        await stop.CancelAsync();
        await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(run.Hosts, h => Assert.Equal(HostRunState.Cancelled, h.State));
        Assert.Equal(["Stopped.", "Stopped.", "Stopped before it started."], run.Hosts.Select(h => h.Error));
        Assert.Equal(2, started);
    }

    [Fact]
    public async Task Changes_AreRaised_InOrder_AndProgressNeverGoesBack()
    {
        HostEntry host = Host("web");
        var run = new MultiHostRun("ls", [host]);
        var seen = new ConcurrentQueue<HostRunState>();
        run.Changed += (index, state) =>
        {
            Assert.Equal(0, index);
            seen.Enqueue(state.State);
        };
        await run.RunAsync((_, progress, _) =>
        {
            progress(HostRunState.Running);
            progress(HostRunState.Connecting); // ignored: it is already running
            progress(HostRunState.Succeeded); // ignored: only the run decides how it ends
            return Task.FromResult(Exit(1));
        });
        Assert.Equal([HostRunState.Connecting, HostRunState.Running, HostRunState.Failed], seen);
        // Progress reported after the end changes nothing.
        Assert.Equal(HostRunState.Failed, run.Hosts[0].State);
    }

    [Fact]
    public async Task TheSameHost_RunsOnce_AndARunRunsOnce()
    {
        HostEntry host = Host("web");
        var run = new MultiHostRun("id", [host, host]);
        int calls = 0;
        await run.RunAsync((_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Exit(0));
        });
        Assert.Equal(1, calls);
        Assert.Single(run.Hosts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.RunAsync((_, _, _) => Task.FromResult(Exit(0))));
    }

    [Fact]
    public void Summarize_CountsHostsStillGoing_AsRunning()
    {
        HostEntry host = Host("web");
        Assert.Equal("no hosts", MultiHostRun.Summarize([]));
        Assert.Equal("2 running, 1 succeeded", MultiHostRun.Summarize(
        [
            new HostRun(host, HostRunState.Waiting),
            new HostRun(host, HostRunState.Running),
            new HostRun(host, HostRunState.Succeeded, Exit(0)),
        ]));
        Assert.Equal("ended by signal KILL", new HostRun(host, HostRunState.Failed, new CommandResult(null, "KILL", "", "", false, false, TimeSpan.Zero)).Outcome);
    }

    [Fact]
    public void ACommand_IsRequired()
    {
        Assert.Throws<ArgumentException>(() => new MultiHostRun("  ", [Host("web")]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MultiHostRun("ls", [Host("web")], maxParallel: 0));
    }
}
