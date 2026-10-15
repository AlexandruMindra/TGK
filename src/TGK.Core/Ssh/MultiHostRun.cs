using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Models;

namespace TGK.Core.Ssh;

/// <summary>Where one host is in a <see cref="MultiHostRun"/>.</summary>
public enum HostRunState
{
    /// <summary>Waiting for its turn (at most <see cref="MultiHostRun.MaxParallel"/> hosts run at once).</summary>
    Waiting,
    Connecting,
    Running,

    /// <summary>The command ended with exit code 0.</summary>
    Succeeded,

    /// <summary>The command ended with another exit code or by a signal, or ran out of time.</summary>
    Failed,

    /// <summary>The command could not run: the host could not be reached or signed in to, or the connection dropped.</summary>
    Error,

    /// <summary>Stopped by the user, or not run because a prompt for it (e.g. a password) was cancelled.</summary>
    Cancelled,
}

/// <summary>One host's part of a <see cref="MultiHostRun"/>.</summary>
/// <param name="Result">The command's outcome once it ran (<see cref="HostRunState.Succeeded"/> or <see cref="HostRunState.Failed"/>).</param>
/// <param name="Error">Why there is no result (<see cref="HostRunState.Error"/>, <see cref="HostRunState.Cancelled"/>); user-facing.</param>
/// <param name="StartedAt">When the host's turn came (it started connecting); null while waiting.</param>
/// <param name="Elapsed">From <paramref name="StartedAt"/> to the end, connecting included; zero until it is over.</param>
public sealed record HostRun(HostEntry Host, HostRunState State, CommandResult? Result = null, string? Error = null,
    DateTimeOffset? StartedAt = null, TimeSpan Elapsed = default)
{
    public bool IsOver => State is HostRunState.Succeeded or HostRunState.Failed or HostRunState.Error or HostRunState.Cancelled;

    /// <summary>The outcome in a few words: <c>exit 0</c>, <c>exit 1</c>, <c>ended by signal KILL</c>, <c>timed out</c>, or the error.</summary>
    public string Outcome => Result switch
    {
        { TimedOut: true } => "timed out",
        { ExitCode: { } code } => $"exit {code}",
        { } r => $"ended by signal {r.ExitSignal ?? "(unknown)"}",
        null => Error ?? State switch
        {
            HostRunState.Waiting => "waiting",
            HostRunState.Connecting => "connecting",
            HostRunState.Running => "running",
            _ => State.ToString().ToLowerInvariant(),
        },
    };
}

/// <summary>
/// Runs one command on several hosts, at most <see cref="MaxParallel"/> at a time, and keeps each host's state. How a
/// host is reached and the command run is the caller's business (<see cref="HostCommand"/>); one host failing never
/// stops the others. Single-use: create one per run.
/// </summary>
public sealed class MultiHostRun
{
    /// <summary>Hosts that connect and run at the same time, by default.</summary>
    public const int DefaultMaxParallel = 10;

    private readonly Lock _gate = new();
    private readonly HostRun[] _hosts;
    private readonly List<int> _finished = [];
    private int _started;

    /// <summary>
    /// Runs the command on <paramref name="host"/> and returns its result. Reports
    /// <see cref="HostRunState.Running"/> through <paramref name="progress"/> once connected (connecting is reported
    /// before it is called). Throws <see cref="OperationCanceledException"/> when stopped or when the user declined a
    /// prompt (its message says which), and any other exception with a user-facing message when the command could not run.
    /// </summary>
    public delegate Task<CommandResult> HostCommand(HostEntry host, Action<HostRunState> progress, CancellationToken ct);

    public MultiHostRun(string command, IEnumerable<HostEntry> hosts, int maxParallel = DefaultMaxParallel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParallel, 1);
        Command = command;
        MaxParallel = maxParallel;
        _hosts = hosts.DistinctBy(h => h.Id).Select(h => new HostRun(h, HostRunState.Waiting)).ToArray();
    }

    public string Command { get; }

    public int MaxParallel { get; }

    /// <summary>Raised (on any thread) with the host's index in <see cref="Hosts"/> and its new state.</summary>
    public event Action<int, HostRun>? Changed;

    /// <summary>Each host's current state, in the order they were given.</summary>
    public IReadOnlyList<HostRun> Hosts
    {
        get
        {
            lock (_gate)
                return [.. _hosts];
        }
    }

    /// <summary>The indexes into <see cref="Hosts"/> of the hosts that are over, in the order they finished.</summary>
    public IReadOnlyList<int> Finished
    {
        get
        {
            lock (_gate)
                return [.. _finished];
        }
    }

    /// <summary>Every host is over.</summary>
    public bool IsOver
    {
        get
        {
            lock (_gate)
                return _hosts.All(h => h.IsOver);
        }
    }

    /// <summary>
    /// Runs the command on every host through <paramref name="run"/> and completes when all are over (it never throws
    /// for a host's failure). Cancelling <paramref name="ct"/> stops the hosts still running and skips those waiting.
    /// </summary>
    public async Task RunAsync(HostCommand run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A run runs only once.");
        using var slots = new SemaphoreSlim(MaxParallel);
        await Task.WhenAll(Enumerable.Range(0, _hosts.Length).Select(i => RunHostAsync(i, run, slots, ct))).ConfigureAwait(false);
    }

    private async Task RunHostAsync(int index, HostCommand run, SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            await slots.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Finish(index, HostRunState.Cancelled, null, "Stopped before it started.");
            return;
        }
        try
        {
            // A stopped host may hand its slot over before the stop reaches this one's wait.
            if (ct.IsCancellationRequested)
            {
                Finish(index, HostRunState.Cancelled, null, "Stopped before it started.");
                return;
            }
            HostEntry host;
            lock (_gate)
            {
                host = _hosts[index].Host;
                _hosts[index] = _hosts[index] with { State = HostRunState.Connecting, StartedAt = DateTimeOffset.UtcNow };
            }
            Raise(index);
            CommandResult result = await Task.Run(() => run(host, state => Progress(index, state), ct), ct).ConfigureAwait(false);
            Finish(index, result is { ExitCode: 0, TimedOut: false } ? HostRunState.Succeeded : HostRunState.Failed, result, null);
        }
        catch (OperationCanceledException ex)
        {
            Finish(index, HostRunState.Cancelled, null, ct.IsCancellationRequested ? "Stopped." : ex.Message);
        }
        catch (Exception ex)
        {
            if (ex is not SshSessionException)
                CoreLog.Warn($"Running a command on {Hosts[index].Host.DisplayName} failed: {ex}");
            Finish(index, HostRunState.Error, null, ex.Message);
        }
        finally
        {
            slots.Release();
        }
    }

    // Connecting → Running; never back, and nothing once the host is over.
    private void Progress(int index, HostRunState state)
    {
        lock (_gate)
        {
            HostRun current = _hosts[index];
            if (current.IsOver || state is not (HostRunState.Connecting or HostRunState.Running) || state <= current.State)
                return;
            _hosts[index] = current with { State = state };
        }
        Raise(index);
    }

    private void Finish(int index, HostRunState state, CommandResult? result, string? error)
    {
        lock (_gate)
        {
            HostRun current = _hosts[index];
            TimeSpan elapsed = current.StartedAt is { } started ? DateTimeOffset.UtcNow - started : TimeSpan.Zero;
            _hosts[index] = current with { State = state, Result = result, Error = error, Elapsed = elapsed };
            _finished.Add(index);
        }
        Raise(index);
    }

    private void Raise(int index)
    {
        HostRun run;
        lock (_gate)
            run = _hosts[index];
        Changed?.Invoke(index, run);
    }

    /// <summary>
    /// The outcome of <paramref name="hosts"/> in a few words, e.g. <c>3 succeeded, 1 failed, 1 could not run</c>
    /// (hosts not over yet are counted as <c>n running</c>).
    /// </summary>
    public static string Summarize(IEnumerable<HostRun> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        List<HostRun> all = hosts.ToList();
        int Count(params HostRunState[] states) => all.Count(h => states.Contains(h.State));
        var parts = new List<string>();
        void Add(int n, string what)
        {
            if (n > 0)
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{n} {what}"));
        }
        Add(Count(HostRunState.Waiting, HostRunState.Connecting, HostRunState.Running), "running");
        Add(Count(HostRunState.Succeeded), "succeeded");
        Add(Count(HostRunState.Failed), "failed");
        Add(Count(HostRunState.Error), "could not run");
        Add(Count(HostRunState.Cancelled), "cancelled");
        return parts.Count == 0 ? "no hosts" : string.Join(", ", parts);
    }
}
