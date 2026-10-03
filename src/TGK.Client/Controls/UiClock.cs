using System;
using System.Diagnostics;
using System.Threading;
using Blossom;

namespace TGK.Client.Controls;

/// <summary>
/// Monotonic time plus a per-loop tick for UI animation (caret blink, toasts, relative timestamps).
/// Ticks come from the active view's <c>Loop</c>. Blossom's main loop sleeps until there is input, a posted action or
/// a due timer, so <see cref="Start"/> keeps a heartbeat of about 60 ticks per second and <see cref="RequestTick"/>
/// wakes the loop for work that cannot wait for it. Handlers must be cheap and only invalidate when something visibly
/// changes.
/// </summary>
public static class UiClock
{
    private const int HeartbeatMs = 16;

    private static readonly Stopwatch Watch = Stopwatch.StartNew();
    private static IDisposable? _heartbeat;
    private static int _tickRequested;

    public static long NowMs => Watch.ElapsedMilliseconds;

    public static event Action? Tick;

    /// <summary>Starts the heartbeat. Call once, on the UI thread, once the window exists.</summary>
    internal static void Start() => _heartbeat ??= Shell.PostPeriodic(TimeSpan.FromMilliseconds(HeartbeatMs), static () => { });

    /// <summary>Runs a loop iteration (and so a tick) as soon as possible. Thread-safe; requests coalesce.</summary>
    public static void RequestTick()
    {
        if (Interlocked.Exchange(ref _tickRequested, 1) == 0)
            Shell.Post(static () => Volatile.Write(ref _tickRequested, 0));
    }

    internal static void RaiseTick() => Tick?.Invoke();
}
