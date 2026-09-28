using System;
using TGK.Core.Models;

namespace TGK.Core.Ssh;

/// <summary>Everything needed to open one interactive SSH session. <see cref="ToString"/> never includes secrets.</summary>
public sealed record SshConnectRequest
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 22;
    public string Username { get; init; } = "";
    public string? Password { get; init; }

    /// <summary>Private key text (OpenSSH, PEM/PKCS#8 or PuTTY format). Tried before the password.</summary>
    public string? PrivateKey { get; init; }

    public string? Passphrase { get; init; }
    public int Cols { get; init; } = 80;
    public int Rows { get; init; } = 24;
    public string TermType { get; init; } = "xterm-256color";

    /// <summary>Limit for reaching an interactive shell, not counting time spent in the host-key prompt.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Interval between SSH keep-alive messages; zero disables them.</summary>
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Builds a request for a saved host; the host's username wins over the identity's.</summary>
    public static SshConnectRequest ForHost(HostEntry host, Identity? identity, int cols = 80, int rows = 24)
    {
        ArgumentNullException.ThrowIfNull(host);
        bool useKey = identity?.AuthKind == AuthKind.PrivateKey;
        return new SshConnectRequest
        {
            Host = host.Host.Trim(),
            Port = host.Port,
            Username = (string.IsNullOrWhiteSpace(host.Username) ? identity?.Username : host.Username)?.Trim() ?? "",
            Password = useKey ? null : identity?.Password,
            PrivateKey = useKey ? identity!.PrivateKey : null,
            Passphrase = useKey ? identity!.Passphrase : null,
            Cols = cols,
            Rows = rows,
        };
    }

    /// <summary>Returns a user-facing message for the first problem found, or null when the request is valid.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
            return "Enter a host name or address.";
        if (Host.AsSpan().IndexOfAny(" \t\r\n") >= 0)
            return "The host name must not contain spaces.";
        if (Port is < 1 or > 65535)
            return "Port must be between 1 and 65535.";
        if (string.IsNullOrWhiteSpace(Username))
            return "Enter a username.";
        if (string.IsNullOrEmpty(Password) && string.IsNullOrWhiteSpace(PrivateKey))
            return "Enter a password or choose a private key.";
        if (Cols < 1 || Rows < 1)
            return "The terminal size must be at least 1x1.";
        if (string.IsNullOrWhiteSpace(TermType))
            return "The terminal type must not be empty.";
        if (ConnectTimeout <= TimeSpan.Zero)
            return "The connect timeout must be positive.";
        if (KeepAlive < TimeSpan.Zero)
            return "The keep-alive interval must not be negative.";
        return null;
    }

    public override string ToString() => $"{Username}@{Host}:{Port}";
}
