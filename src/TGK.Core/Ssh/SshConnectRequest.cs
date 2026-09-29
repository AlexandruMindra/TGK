using System;
using System.Collections.Generic;
using System.Linq;
using TGK.Core.Models;

namespace TGK.Core.Ssh;

/// <summary>Everything needed to open one interactive SSH session. <see cref="ToString"/> never includes secrets.</summary>
public sealed record SshConnectRequest
{
    /// <summary>Saved host name, for messages about this hop (e.g. "Jump host bastion (10.0.0.1): ..."); optional.</summary>
    public string? Name { get; init; }

    public string Host { get; init; } = "";
    public int Port { get; init; } = 22;
    public string Username { get; init; } = "";
    public string? Password { get; init; }

    /// <summary>Private key text (OpenSSH, PEM/PKCS#8 or PuTTY format). Tried before the password.</summary>
    public string? PrivateKey { get; init; }

    public string? Passphrase { get; init; }
    public int Cols { get; init; } = 80;
    public int Rows { get; init; } = 24;
    public string TermType { get; init; } = EffectiveOptions.DefaultTerminalType;

    /// <summary>Limit for reaching an interactive shell, not counting time spent in the host-key prompt.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(EffectiveOptions.DefaultConnectTimeoutSeconds);

    /// <summary>Interval between SSH keep-alive messages; zero disables them.</summary>
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(EffectiveOptions.DefaultKeepAliveSeconds);

    /// <summary>Also offer the weak algorithms of <see cref="SshAlgorithms"/>.</summary>
    public bool LegacyAlgorithms { get; init; }

    /// <summary>
    /// Jump hosts to connect through (ProxyJump), outermost first. Each hop has its own credentials, timeouts and
    /// algorithms, and its host key is verified against its own host and port; a hop's own
    /// <see cref="JumpChain"/>, <see cref="Tunnels"/>, <see cref="Environment"/> and <see cref="StartupCommand"/> are ignored.
    /// </summary>
    public IReadOnlyList<SshConnectRequest> JumpChain { get; init; } = [];

    /// <summary>Port forwardings started once the shell is open (disabled ones are skipped).</summary>
    public IReadOnlyList<PortForward> Tunnels { get; init; } = [];

    /// <summary>Sent as SSH "env" requests before the shell starts; variables the server refuses are ignored.</summary>
    public IReadOnlyList<EnvVar> Environment { get; init; } = [];

    /// <summary>Typed into the shell once it is open, followed by Enter; null or empty for none.</summary>
    public string? StartupCommand { get; init; }

    /// <summary>"bastion (10.0.0.1)", or just the address when there is no distinct name.</summary>
    internal string Label => string.IsNullOrWhiteSpace(Name) || Name.Trim() == Host.Trim() ? Host : $"{Name.Trim()} ({Host})";

    /// <summary>Builds a request for a saved host; the host's username wins over the identity's. Uses no options (see the vault overload).</summary>
    public static SshConnectRequest ForHost(HostEntry host, Identity? identity, int cols = 80, int rows = 24)
    {
        ArgumentNullException.ThrowIfNull(host);
        bool useKey = identity?.AuthKind == AuthKind.PrivateKey;
        return new SshConnectRequest
        {
            Name = host.DisplayName,
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

    /// <summary>
    /// Builds the request for a saved host with its effective options (<see cref="EffectiveOptions"/>): the jump
    /// chain (each hop with its own identity and options), enabled tunnels, environment, startup command, terminal
    /// type, timeouts and algorithms.
    /// </summary>
    /// <exception cref="SshSessionException">
    /// <see cref="SshErrorKind.InvalidRequest"/>: the jump hosts form a loop, one was deleted or there are too many.
    /// </exception>
    public static SshConnectRequest ForHost(VaultData vault, HostEntry host, int cols = 80, int rows = 24)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(host);
        IReadOnlyList<HostEntry> jumps = EffectiveOptions.ResolveJumpChain(vault, host, out string? error);
        if (error is not null)
            throw new SshSessionException(SshErrorKind.InvalidRequest, error);

        EffectiveHostOptions options = EffectiveOptions.Resolve(vault, host);
        return WithConnectionOptions(ForHost(host, vault.FindIdentity(host.IdentityId), cols, rows), options) with
        {
            JumpChain = jumps.Select(j => WithConnectionOptions(ForHost(j, vault.FindIdentity(j.IdentityId)), EffectiveOptions.Resolve(vault, j))).ToList(),
            Tunnels = host.Tunnels.Where(t => t.Enabled).Select(t => t.Clone()).ToList(),
            Environment = options.Environment.Value,
            StartupCommand = options.StartupCommand.Value,
            TermType = options.TerminalType.Value,
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
        if (JumpChain.Count > EffectiveOptions.MaxJumpHosts)
            return $"A connection can go through at most {EffectiveOptions.MaxJumpHosts} jump hosts.";
        foreach (SshConnectRequest hop in JumpChain)
        {
            if (hop.Validate() is { } problem)
                return $"Jump host {hop.Label}: {problem}";
        }
        foreach (PortForward tunnel in Tunnels)
        {
            if (tunnel.Validate() is { } problem)
                return $"Tunnel {tunnel}: {problem}";
        }
        foreach (EnvVar variable in Environment)
        {
            if (!EnvVar.IsValidName(variable.Name))
                return $"\"{variable.Name}\" is not a valid environment variable name.";
        }
        return null;
    }

    public override string ToString() => $"{Username}@{Host}:{Port}";

    private static SshConnectRequest WithConnectionOptions(SshConnectRequest request, EffectiveHostOptions options) => request with
    {
        ConnectTimeout = TimeSpan.FromSeconds(options.ConnectTimeoutSeconds.Value),
        KeepAlive = TimeSpan.FromSeconds(options.KeepAliveSeconds.Value),
        LegacyAlgorithms = options.LegacyAlgorithms.Value,
    };
}
