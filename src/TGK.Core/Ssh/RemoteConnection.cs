using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace TGK.Core.Ssh;

/// <summary>The outcome of a command run with <see cref="RemoteConnection.RunAsync"/>.</summary>
/// <param name="ExitCode">The command's exit status; null when it ended by a signal or was stopped.</param>
/// <param name="ExitSignal">The signal that ended the command (e.g. <c>KILL</c>), if any.</param>
/// <param name="TimedOut">The command ran longer than allowed and was stopped.</param>
/// <param name="Truncated">Output beyond the limit was dropped (from stdout, stderr or both).</param>
public sealed record CommandResult(int? ExitCode, string? ExitSignal, string Stdout, string Stderr, bool TimedOut, bool Truncated, TimeSpan Duration)
{
    /// <summary>Raw stdout, for callers that need bytes (e.g. base64 decoding); null when not requested.</summary>
    public byte[]? StdoutBytes { get; init; }
}

/// <summary>The server does not offer SFTP (no <c>sftp</c> subsystem); files can still be reached through commands.</summary>
public sealed class SftpUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A non-interactive connection to one host, for agents: commands over exec channels and files over SFTP, through the
/// host's jump hosts like a terminal session. Several calls may run at once (each command is its own channel).
/// </summary>
/// <remarks>
/// <para>
/// SSH.NET cannot share one SSH session between its command and SFTP clients, so SFTP is a second connection along
/// the same route, opened on first use. It must present the host key already trusted for the first one (never a
/// second prompt); its sign-in reuses the credentials, so only one-time codes are asked again.
/// </para>
/// <para>Single-use: connect once; when <see cref="IsConnected"/> turns false, dispose and create a new one.</para>
/// </remarks>
public sealed class RemoteConnection : IDisposable
{
    /// <summary>Most output kept per stream of one command by default.</summary>
    public const int DefaultMaxOutput = 100 * 1024;

    private const string JumpHostPrefix = "Jump host ";

    private readonly IHostKeyVerifier _verifier;
    private readonly InteractivePromptHandler? _promptUser;
    private readonly Lock _gate = new();
    private readonly List<SshHop> _hops = [];
    private readonly SemaphoreSlim _sftpGate = new(1, 1);
    private SftpClient? _sftp;
    private string? _sftpUnavailable; // why the server has no SFTP, once known
    private CancellationTokenSource? _attempt;
    private TimeSpan _stepTimeout;
    private SshConnectRequest? _request;
    private bool _connected;
    private bool _disposed;

    /// <param name="verifier">Decides whether to trust each hop's host key (prompts the user for unknown keys).</param>
    /// <param name="promptUser">Answers sign-in questions the stored credentials can't (one-time codes); null to fail instead.</param>
    public RemoteConnection(IHostKeyVerifier verifier, InteractivePromptHandler? promptUser = null)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _promptUser = promptUser;
    }

    /// <summary>The request this connection was made with (null before <see cref="ConnectAsync"/>).</summary>
    public SshConnectRequest? Request { get { lock (_gate) return _request; } }

    /// <summary>Connected, and every hop is still up.</summary>
    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                if (!_connected || _disposed)
                    return false;
                foreach (SshHop hop in _hops)
                {
                    if (!hop.Client.IsConnected)
                        return false;
                }
                return true;
            }
        }
    }

    /// <summary>The final host's trusted host key fingerprint once connected.</summary>
    public string? HostKeyFingerprint { get { lock (_gate) return _hops.Count > 0 ? _hops[^1].TrustedFingerprint : null; } }

    /// <summary>Why the server has no SFTP (null while unknown or when it has it). Set by <see cref="GetSftpAsync"/>.</summary>
    public string? SftpUnavailableReason { get { lock (_gate) return _sftpUnavailable; } }

    /// <summary>Connects through the jump hosts (if any) and signs in. Prompts (host keys, one-time codes) pause the timeout.</summary>
    /// <exception cref="SshSessionException">Connecting failed; the message is user-facing.</exception>
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
                throw new InvalidOperationException("A remote connection connects only once.");
            _request = request;
            _attempt = attempt;
        }

        SshConnectRequest[] route = [.. request.JumpChain, request];
        SshConnectRequest current = request;
        Rejection rejection = new();
        try
        {
            SshHop? previous = null;
            for (int i = 0; i < route.Length; i++)
            {
                current = route[i];
                SshConnectRequest target = current;
                // Loading an encrypted key runs a slow KDF: off the caller's thread.
                SshHop hop = await Task.Run(() => SshHop.Create(target, i == route.Length - 1, previous?.Forward,
                    _promptUser is null ? null : q => AskUser(q, rejection), SshSession.PromptTimeout), attempt.Token).ConfigureAwait(false);
                hop.Client.HostKeyReceived += (_, e) => OnHostKeyReceived(hop, e, rejection);
                lock (_gate)
                {
                    _hops.Add(hop);
                    _stepTimeout = target.ConnectTimeout;
                }
                attempt.CancelAfter(target.ConnectTimeout);
                try
                {
                    await hop.Client.ConnectAsync(attempt.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (previous is not null && hop.Client.ConnectionInfo.ServerVersion is null
                    && ex is SshConnectionException or SocketException && !attempt.IsCancellationRequested)
                {
                    throw new SshSessionException(SshErrorKind.HostUnreachable,
                        $"{JumpHostPrefix}{previous.Request.Label} could not reach an SSH server at {target.Host}:{target.Port}.", ex) { JumpHostIndex = i - 1 };
                }
                hop.Client.ConnectionInfo.Timeout = target.ConnectTimeout; // prompts are over
                if (i < route.Length - 1)
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
            DisposeHops();
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

    /// <summary>
    /// Runs <paramref name="command"/> (through the user's shell, without a terminal) and collects its output.
    /// Stops it after <paramref name="timeout"/>; keeps at most <paramref name="maxOutput"/> bytes per stream (the rest
    /// is read and dropped, so a chatty command cannot fill memory).
    /// </summary>
    /// <param name="input">Written to the command's stdin, which is then closed; null for no input.</param>
    /// <param name="keepBytes">Also return stdout as bytes (<see cref="CommandResult.StdoutBytes"/>).</param>
    /// <exception cref="SshSessionException">The connection is gone (<see cref="SshErrorKind.ConnectionLost"/>).</exception>
    public async Task<CommandResult> RunAsync(string command, TimeSpan timeout, int maxOutput = DefaultMaxOutput, byte[]? input = null,
        bool keepBytes = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);
        SshClient client = FinalClient();
        var started = DateTime.UtcNow;
        SshCommand cmd;
        try
        {
            cmd = client.CreateCommand(command, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is SshException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            throw Lost(ex);
        }
        using (cmd)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            Task execute;
            try
            {
                execute = cmd.ExecuteAsync(limit.Token);
            }
            catch (Exception ex) when (ex is SshException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                throw Lost(ex);
            }
            Task<Captured> stdout = Task.Run(() => Capture(cmd.OutputStream, maxOutput));
            Task<Captured> stderr = Task.Run(() => Capture(cmd.ExtendedOutputStream, maxOutput));
            if (input is not null)
            {
                try
                {
                    await using Stream stdin = cmd.CreateInputStream();
                    await stdin.WriteAsync(input, limit.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SshException or IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
                {
                    // The command ended early or the time ran out: its result says what happened.
                }
            }
            bool timedOut = false;
            try
            {
                await execute.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true;
            }
            catch (SshOperationTimeoutException)
            {
                timedOut = true;
            }
            catch (Exception ex) when (ex is SshException or SocketException or ObjectDisposedException)
            {
                throw Lost(ex);
            }
            // The streams end with the channel; a server that never closes them must not hang the call.
            Captured output = await stdout.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            Captured errors = await stderr.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            return new CommandResult(timedOut ? null : cmd.ExitStatus, cmd.ExitSignal, Decode(output.Bytes), Decode(errors.Bytes), timedOut,
                output.Truncated || errors.Truncated, DateTime.UtcNow - started)
            {
                StdoutBytes = keepBytes ? output.Bytes : null,
            };
        }
    }

    /// <summary>
    /// The SFTP client for this host, connecting it on first use.
    /// </summary>
    /// <exception cref="SftpUnavailableException">The server has no SFTP (remembered: later calls fail at once).</exception>
    /// <exception cref="SshSessionException">The connection is gone, or the second sign-in failed.</exception>
    public async Task<SftpClient> GetSftpAsync(CancellationToken ct = default)
    {
        await _sftpGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SshHop final;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_sftpUnavailable is { } reason)
                    throw new SftpUnavailableException(reason);
                if (_sftp is { IsConnected: true } open)
                    return open;
                if (!_connected)
                    throw new InvalidOperationException("Not connected.");
                final = _hops[^1];
            }
            if (!final.Client.IsConnected)
                throw Lost(null);

            var sftp = new SftpClient(final.CreateConnectionInfo(SshSession.PromptTimeout))
            {
                OperationTimeout = TimeSpan.FromSeconds(60),
            };
            sftp.HostKeyReceived += (_, e) => e.CanTrust = "SHA256:" + e.FingerPrintSHA256 == final.TrustedFingerprint;
            try
            {
                await sftp.ConnectAsync(ct).ConfigureAwait(false);
            }
            catch (SshException ex) when (ex.Message.Contains("subsystem", StringComparison.OrdinalIgnoreCase))
            {
                sftp.Dispose();
                string reason = $"{final.Request.Host} does not offer SFTP.";
                lock (_gate)
                    _sftpUnavailable = reason;
                throw new SftpUnavailableException(reason, ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sftp.Dispose();
                throw SshErrors.Map(ex, final.Request);
            }
            lock (_gate)
            {
                if (_disposed)
                {
                    sftp.Dispose();
                    throw new ObjectDisposedException(nameof(RemoteConnection));
                }
                _sftp?.Dispose();
                _sftp = sftp;
            }
            return sftp;
        }
        finally
        {
            _sftpGate.Release();
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
        DisposeHops();
    }

    private void DisposeHops()
    {
        SftpClient? sftp;
        SshHop[] hops;
        lock (_gate)
        {
            sftp = _sftp;
            _sftp = null;
            hops = [.. _hops];
            _hops.Clear();
        }
        SshHop.Try(() => sftp?.Dispose());
        for (int i = hops.Length - 1; i >= 0; i--) // innermost first: each hop runs through the ones before it
            hops[i].Dispose();
    }

    private SshClient FinalClient()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_connected || _hops.Count == 0)
                throw new InvalidOperationException("Not connected.");
            return _hops[^1].Client;
        }
    }

    private SshSessionException Lost(Exception? ex)
    {
        lock (_gate)
            _connected = false;
        string host = Request?.Host ?? "the server";
        return new SshSessionException(SshErrorKind.ConnectionLost,
            ex is null ? $"The connection to {host} was lost." : $"The connection to {host} was lost: {SshErrors.DescribeLoss(ex)}.", ex);
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
                hop.TrustedFingerprint = fingerprint;
            else
                rejection.Hop ??= hop.Request;
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

    private static Captured Capture(Stream stream, int max)
    {
        var kept = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        bool truncated = false;
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                int room = max - (int)kept.Length;
                if (room > 0)
                    kept.Write(buffer, 0, Math.Min(room, read));
                if (read > room)
                    truncated = true;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SshException)
        {
            // The channel closed: what arrived is the output.
        }
        return new Captured(kept.ToArray(), truncated);
    }

    private static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private readonly record struct Captured(byte[] Bytes, bool Truncated);

    /// <summary>How prompts of one attempt ended (written from SSH.NET's threads).</summary>
    private sealed class Rejection
    {
        public volatile bool Declined;
        public volatile bool TimedOut;
        public SshConnectRequest? Hop;
    }
}
