using System;

namespace TGK.Core;

/// <summary>Diagnostics from TGK.Core. The client routes them to its log; unset, they are dropped.</summary>
public static class CoreLog
{
    /// <summary>Receives warnings (never secrets). May be invoked on any thread.</summary>
    public static Action<string>? Warning { get; set; }

    internal static void Warn(string message) => Warning?.Invoke(message);
}
