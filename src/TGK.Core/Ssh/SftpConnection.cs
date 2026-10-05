using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace TGK.Core.Ssh;

/// <summary>
/// An SFTP connection to one host for the file browser, through the host's jump hosts like a terminal session: the
/// jump hosts are SSH connections forwarding to the next hop, the final host is signed in to once, by the SFTP client
/// itself (so a one-time code is asked once, unlike <see cref="RemoteConnection"/>, whose SFTP is a second sign-in).
/// </summary>
/// <remarks>
/// <para>Single-use: connect once; when <see cref="IsConnected"/> turns false (see <see cref="Lost"/>), dispose and create a new one.</para>
/// <para>
/// Prompts (host keys, sign-in questions) run on SSH.NET's threads while the connection waits; the connect timeout is
/// paused meanwhile and each prompt gets <see cref="SshSession.PromptTimeout"/>.
/// </para>
/// </remarks>
public sealed class SftpConnection : IDisposable
{
    private const string JumpHostPrefix = "Jump host ";

    private readonly IHostKeyVerifier _verifier;
    private readonly InteractivePromptHandler? _promptUser;
    private readonly Lock _gate = new();
    private readonly List<SshHop> _jumps = [];
    private SshHop? _final; // holds the final host's credentials; its own SSH client is never connected
    private SftpClient? _client;
    private CancellationTokenSource? _attempt;
    private TimeSpan _stepTimeout;
    private SshConnectRequest? _request;
    private bool _connected, _lost, _disposed;
    private byte[]? _hostKey; // the final host's public key blob, once trusted

    /// <param name="verifier">Decides whether to trust each hop's host key (prompts the user for unknown keys).</param>
    /// <param name="promptUser">Answers sign-in questions the stored credentials can't (one-time codes); null to fail instead.</param>
    public SftpConnection(IHostKeyVerifier verifier, InteractivePromptHandler? promptUser = null)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _promptUser = promptUser;
    }

    /// <summary>
    /// Raised once, on a background thread, when an established connection dropped (the server or a jump host went
    /// away), with a user-facing reason. Not raised by <see cref="Dispose"/>.
    /// </summary>
    public event Action<string>? Lost;

    /// <summary>The request this connection was made with (null before <see cref="ConnectAsync"/>).</summary>
    public SshConnectRequest? Request { get { lock (_gate) return _request; } }

    /// <summary>Connected, and every hop is still up.</summary>
    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                if (!_connected || _lost || _disposed || _client is not { IsConnected: true })
                    return false;
                foreach (SshHop hop in _jumps)
                {
                    if (!hop.Client.IsConnected)
                        return false;
                }
                return true;
            }
        }
    }

    /// <summary>The final host's trusted host key fingerprint once connected.</summary>
    public string? HostKeyFingerprint { get { lock (_gate) return _final?.TrustedFingerprint; } }

    /// <summary>
    /// The final host's trusted public key (SSH wire format, its first field the key type) once connected, e.g. for a
    /// known_hosts line another machine checks the host against.
    /// </summary>
    public byte[]? HostKey { get { lock (_gate) return _hostKey; } }

    /// <summary>The SFTP client of an established connection.</summary>
    /// <exception cref="SshSessionException">Not connected, or the connection is gone (<see cref="SshErrorKind.ConnectionLost"/>).</exception>
    public SftpClient Client
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_connected && !_lost && _client is { IsConnected: true } client)
                    return client;
            }
            throw new SshSessionException(SshErrorKind.ConnectionLost, $"The connection to {Request?.Host ?? "the server"} was lost.");
        }
    }

    /// <summary>Connects through the jump hosts (if any), signs in and opens SFTP. Prompts pause the timeout.</summary>
    /// <exception cref="SshSessionException">Connecting failed; the message is user-facing.</exception>
    /// <exception cref="SftpUnavailableException">The server does not offer SFTP.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled or a prompt was declined.</exception>
    public async Task ConnectAsync(SshConnectRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Validate() is { } problem)
            throw new SshSessionException(SshErrorKind.InvalidRequest, problem);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_request is not null)
                throw new InvalidOperationException("An SFTP connection connects only once.");
            _request = request;
            _attempt = attempt;
        }

        SshConnectRequest[] route = [.. request.JumpChain, request];
        SshConnectRequest current = request;
        var rejection = new Rejection();
        try
        {
            SshHop? previous = null;
            for (int i = 0; i < route.Length; i++)
            {
                current = route[i];
                SshConnectRequest target = current;
                bool isFinal = i == route.Length - 1;
                // Loading an encrypted key runs a slow KDF: off the caller's thread.
                SshHop hop = await Task.Run(() => SshHop.Create(target, isFinal, previous?.Forward,
                    _promptUser is null ? null : q => AskUser(q, rejection), SshSession.PromptTimeout), attempt.Token).ConfigureAwait(false);
                BaseClient client;
                lock (_gate)
                {
                    if (isFinal)
                    {
                        _final = hop;
                        _client = new SftpClient(hop.CreateConnectionInfo(SshSession.PromptTimeout))
                        {
                            KeepAliveInterval = target.KeepAlive > TimeSpan.Zero ? target.KeepAlive : Timeout.InfiniteTimeSpan,
                            OperationTimeout = TimeSpan.FromSeconds(60),
                        };
                        client = _client;
                    }
                    else
                    {
                        _jumps.Add(hop);
                        client = hop.Client;
                    }
                    _stepTimeout = target.ConnectTimeout;
                }
                client.HostKeyReceived += (_, e) => OnHostKeyReceived(hop, e, rejection);
                client.ErrorOccurred += (_, e) => OnConnectionError(hop, e.Exception);
                attempt.CancelAfter(target.ConnectTimeout);
                try
                {
                    await client.ConnectAsync(attempt.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (previous is not null && client.ConnectionInfo.ServerVersion is null
                    && ex is SshConnectionException or SocketException && !attempt.IsCancellationRequested)
                {
                    throw new SshSessionException(SshErrorKind.HostUnreachable,
                        $"{JumpHostPrefix}{previous.Request.Label} could not reach an SSH server at {target.Host}:{target.Port}.", ex) { JumpHostIndex = i - 1 };
                }
                catch (SshException ex) when (isFinal && ex.Message.Contains("subsystem", StringComparison.OrdinalIgnoreCase))
                {
                    throw new SftpUnavailableException($"{target.Host} does not offer SFTP.", ex);
                }
                client.ConnectionInfo.Timeout = target.ConnectTimeout; // prompts are over
                if (!isFinal)
                    hop.ForwardTo(route[i + 1]);
                previous = hop;
            }
            attempt.CancelAfter(Timeout.InfiniteTimeSpan);
            lock (_gate)
            {
                _attempt = null;
                ObjectDisposedException.ThrowIf(_disposed, this);
                _connected = true;
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
                _attempt = null;
            DisposeClients();
            if (ex is SftpUnavailableException)
                throw;
            if (ct.IsCancellationRequested || rejection.Declined)
                throw new OperationCanceledException("The connection attempt was cancelled.", ex, ct);
            SshSessionException error =
                rejection.TimedOut ? new(SshErrorKind.Timeout, $"The prompt was not answered within {SshSession.PromptTimeout.TotalSeconds:0} seconds.", ex)
                : rejection.Hop is { } rejected ? new(SshErrorKind.HostKeyRejected, $"The host key of {rejected.Host} was not trusted.", ex)
                : ex is OperationCanceledException ? new(SshErrorKind.Timeout, $"Timed out connecting to {current.Host}:{current.Port}.", ex)
                : SshErrors.Map(ex, current);
            int jumpIndex = Array.IndexOf(route, current);
            if (jumpIndex >= 0 && jumpIndex < route.Length - 1 && error.JumpHostIndex is null)
                error = new SshSessionException(error.Kind, $"{JumpHostPrefix}{current.Label}: {error.Message}", ex) { JumpHostIndex = jumpIndex };
            throw error;
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? attempt;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _connected = false;
            attempt = _attempt;
        }
        try
        {
            attempt?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        // Disconnecting waits for SSH.NET's listener threads: keep it off the caller's (UI) thread.
        _ = Task.Run(DisposeClients);
    }

    /// <summary>Marks the connection lost (e.g. an operation found it gone) and raises <see cref="Lost"/> once.</summary>
    public void MarkLost(string reason)
    {
        lock (_gate)
        {
            if (!_connected || _lost || _disposed)
                return;
            _lost = true;
        }
        Lost?.Invoke(reason);
    }

    private void DisposeClients()
    {
        SftpClient? client;
        SshHop? final;
        SshHop[] jumps;
        lock (_gate)
        {
            client = _client;
            final = _final;
            jumps = [.. _jumps];
            _client = null;
            _final = null;
            _jumps.Clear();
        }
        if (client is not null)
        {
            IList<AuthenticationMethod> methods = client.ConnectionInfo.AuthenticationMethods; // unreadable once disposed
            SshHop.Try(client.Dispose);
            foreach (AuthenticationMethod method in methods)
                SshHop.Try(() => (method as IDisposable)?.Dispose());
        }
        final?.Dispose();
        for (int i = jumps.Length - 1; i >= 0; i--) // innermost first: each hop runs through the ones before it
            jumps[i].Dispose();
    }

    // SSH.NET's listener thread: the server, a jump host or the network went away.
    private void OnConnectionError(SshHop hop, Exception error)
    {
        string reason = hop.IsFinal
            ? $"Connection lost: {SshErrors.DescribeLoss(error)}."
            : $"Connection lost: {JumpHostPrefix}{hop.Request.Label}: {SshErrors.DescribeLoss(error)}.";
        MarkLost(reason);
    }

    // SSH.NET's listener thread, during key exchange: the connection waits for the answer.
    private void OnHostKeyReceived(SshHop hop, HostKeyEventArgs e, Rejection rejection)
    {
        string fingerprint = "SHA256:" + e.FingerPrintSHA256;
        lock (_gate)
        {
            if (hop.TrustedFingerprint is not null)
            {
                e.CanTrust = fingerprint == hop.TrustedFingerprint; // re-key: the key must not change mid-connection
                return;
            }
        }
        var info = new HostKeyInfo(hop.Request.Host, hop.Request.Port, e.HostKeyName, fingerprint);
        bool trusted = WaitForUser(token => _verifier.VerifyAsync(info, token), false, rejection);
        lock (_gate)
        {
            if (trusted)
            {
                hop.TrustedFingerprint = fingerprint;
                if (hop.IsFinal)
                    _hostKey = e.HostKey;
            }
            else
            {
                rejection.Hop ??= hop.Request;
            }
        }
        e.CanTrust = trusted;
    }

    private string? AskUser(InteractivePrompt question, Rejection rejection)
    {
        string? answer = WaitForUser(token => _promptUser!(question, token), null, rejection);
        if (answer is null && !rejection.TimedOut)
            rejection.Declined = true;
        return answer;
    }

    /// <summary>Runs a prompt with the connect timeout paused; <paramref name="fallback"/> when it failed or timed out.</summary>
    private T WaitForUser<T>(Func<CancellationToken, Task<T>> ask, T fallback, Rejection rejection)
    {
        CancellationToken attemptToken;
        lock (_gate)
        {
            if (_attempt is null)
                return fallback;
            attemptToken = _attempt.Token;
            _attempt.CancelAfter(Timeout.InfiniteTimeSpan);
        }
        using var window = CancellationTokenSource.CreateLinkedTokenSource(attemptToken);
        window.CancelAfter(SshSession.PromptTimeout);
        T result;
        try
        {
            result = ask(window.Token).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            result = fallback;
        }
        lock (_gate)
        {
            if (window.IsCancellationRequested && !attemptToken.IsCancellationRequested)
            {
                rejection.TimedOut = true;
                result = fallback;
            }
            _attempt?.CancelAfter(_stepTimeout);
        }
        return result;
    }

    /// <summary>How prompts of one attempt ended (written from SSH.NET's threads).</summary>
    private sealed class Rejection
    {
        public volatile bool Declined;
        public volatile bool TimedOut;
        public SshConnectRequest? Hop;
    }
}
