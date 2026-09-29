using TGK.Core.Models;

namespace TGK.Core.Ssh;

public enum TunnelState
{
    /// <summary>Waiting for the shell to open, or being set up.</summary>
    Starting,

    /// <summary>Listening (on this computer, or on the server for a Remote forward).</summary>
    Active,

    /// <summary>Could not start; <see cref="TunnelStatus.Error"/> says why. The session is not affected.</summary>
    Failed,

    /// <summary>The session ended.</summary>
    Stopped,
}

/// <summary>A tunnel of a session and how it is doing; <see cref="Error"/> is a user-facing message for <see cref="TunnelState.Failed"/>.</summary>
public sealed record TunnelStatus(PortForward Forward, TunnelState State, string? Error = null);
