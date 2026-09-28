namespace TGK.Core.Ssh;

public enum SessionState
{
    Idle,
    Connecting,
    Authenticating,
    Connected,

    /// <summary>Ended normally: the remote shell exited or the user disconnected / cancelled.</summary>
    Closed,

    /// <summary>Connecting failed or the connection was lost.</summary>
    Failed,
}
