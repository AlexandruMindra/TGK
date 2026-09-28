using System;
using System.Diagnostics;

namespace TGK.Client.Controls;

/// <summary>
/// Monotonic time plus a per-loop tick for UI animation (caret blink, toasts, relative timestamps).
/// Ticks come from the active view's <c>Loop</c> (about once per millisecond, even when idle), so handlers must
/// be cheap and only invalidate when something visibly changes.
/// </summary>
public static class UiClock
{
    private static readonly Stopwatch Watch = Stopwatch.StartNew();

    public static long NowMs => Watch.ElapsedMilliseconds;

    public static event Action? Tick;

    internal static void RaiseTick() => Tick?.Invoke();
}
