using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;
using TGK.Core.Models;

namespace TGK.Core.Ssh;

/// <summary>One interactive SSH shell — the connection behind a terminal tab.</summary>
/// <remarks>
/// <para>Single-use: <see cref="ConnectAsync"/> may be called once; to reconnect, dispose and create a new session.</para>
/// <para>
/// Jump hosts (<see cref="SshConnectRequest.JumpChain"/>) are connected in turn, each next hop through a local
/// forwarding on the previous one that listens on an ephemeral 127.0.0.1 port for the session's lifetime (SSH.NET
/// cannot run SSH over a channel stream); like any <c>ssh -L</c>, other local processes could connect to that port,
/// which only reaches the next hop's SSH port.
/// </para>
/// <para>
/// Threading: <see cref="StateChanged"/>, <see cref="DataReceived"/> and <see cref="TunnelsChanged"/> are raised on background threads, so UI
/// handlers must marshal (e.g. <c>Browser.Post</c>). <see cref="Send"/>, <see cref="Resize"/>, <see cref="Disconnect"/>
/// and <see cref="Dispose"/> are thread-safe and do not wait on the network, so they are fine on the UI thread.
/// </para>
/// </remarks>
public sealed class SshSession : IDisposable
{
    /// <summary>
    /// How long a prompt shown while connecting (host key, keyboard-interactive question) may stay open. Servers drop
    /// sign-ins that take too long (OpenSSH's LoginGraceTime and asyncssh's login timeout are 120 s), so the prompt
    /// closes with a clear message before that instead of leaving a dead connection behind it.
    /// </summary>
    public static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(100);

    private const int ReadBufferSize = 32 * 1024;
    private static readonly TimeSpan ConnectionErrorGrace = TimeSpan.FromMilliseconds(250);
    private const string JumpHostPrefix = "Jump host ";

    // Output SSH.NET may buffer ahead of the reader before its listener thread is held (see OnShellData), and input
    // that may wait for a server that stopped reading before further input is refused.
    private const long MaxBufferedOutput = 256 * 1024;
    private const long MaxPendingInput = 4 * 1024 * 1024;

    // Hidden prompts that ask for the account password (PAM's "Password: " and common translations). Prompts that also
    // mention a code or token (e.g. "One-time password (OATH)") are second factors and never get the password.
    private static readonly string[] PasswordWords =
        ["password", "passwort", "passwd", "mot de passe", "contraseña", "senha", "wachtwoord", "hasło", "пароль", "lösenord", "salasana", "heslo", "密码", "密碼", "パスワード", "비밀번호"];
    private static readonly string[] SecondFactorWords = ["one-time", "one time", "otp", "oath", "code", "token", "verification", "factor"];

    private readonly IHostKeyVerifier _verifier;
    private readonly InteractivePromptHandler? _promptUser;
    private readonly Lock _gate = new();
    private readonly object _outputFlow = new(); // Monitor for the listener waiting on the reader (see OnShellData)
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private long _pendingInput; // bytes in _outbox (bounded by MaxPendingInput)

    // Guarded by _gate.
    private SessionState _state = SessionState.Idle;
    private CancellationTokenSource? _attempt; // live only while ConnectAsync runs
    private Connection? _connection;           // set once the shell is open; until then the connect task owns it
    private SshConnectRequest? _currentHop;    // the hop being connected (the request itself for the final host)
    private TimeSpan _stepTimeout;             // the current hop's connect timeout
    private string? _trustedFingerprint;       // the final host's
    private SshConnectRequest? _rejectedHop;    // whose host key the user did not trust
    private bool _promptDeclined;
    private bool _promptTimedOut;
    private TerminalSize _size;
    private TerminalSize _sentSize;
    private string? _lastError;
    private TunnelStatus[] _tunnels = [];
    private bool _disposed;

    /// <param name="verifier">Decides whether to trust the server's host key.</param>
    /// <param name="promptUser">
    /// Answers keyboard-interactive questions the stored credentials can't (one-time codes and the like). Without it,
    /// such a server fails with <see cref="SshErrorKind.UnsupportedAuthMethod"/>.
    /// </param>
    public SshSession(IHostKeyVerifier verifier, InteractivePromptHandler? promptUser = null)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _promptUser = promptUser;
    }

    /// <summary>
    /// Raised on a background thread after every state change, with a user-facing message
    /// (for <see cref="SessionState.Failed"/> the error, e.g. "Connection refused by host:22.").
    /// </summary>
    public event Action<SessionState, string?>? StateChanged;

    /// <summary>
    /// Terminal output as (buffer, count), raised on the session's reader thread. The buffer is reused for the next
    /// read as soon as the handler returns: consume or copy the bytes synchronously and do not keep the array. The
    /// handler may block while its consumer is behind: the session then stops reading from the server, and SSH flow
    /// control slows the remote program down.
    /// </summary>
    public event Action<byte[], int>? DataReceived;

    /// <summary>Raised on a background thread whenever <see cref="Tunnels"/> changed.</summary>
    public event Action? TunnelsChanged;

    public SessionState State { get { lock (_gate) return _state; } }

    /// <summary>
    /// The enabled tunnels of the request and how they are doing: Starting until the shell is open, then Active or
    /// Failed (with the reason); Stopped once the session ended. A failed tunnel never ends the session.
    /// </summary>
    public IReadOnlyList<TunnelStatus> Tunnels { get { lock (_gate) return _tunnels; } }

    /// <summary>The message of the last <see cref="SessionState.Failed"/> transition.</summary>
    public string? LastError { get { lock (_gate) return _lastError; } }

    /// <summary>The (final) server's host key fingerprint once it was trusted.</summary>
    public string? HostKeyFingerprint { get { lock (_gate) return _trustedFingerprint; } }

    /// <summary>
    /// Connects (through the jump hosts, if any), authenticates (private key, then password, then keyboard-interactive)
    /// and opens a PTY shell. Completes once <see cref="State"/> is <see cref="SessionState.Connected"/>; the tunnels
    /// start and the startup command is sent right after.
    /// </summary>
    /// <exception cref="SshSessionException">Connecting failed; the message is user-facing. State becomes Failed.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> was cancelled, <see cref="Disconnect"/> was called or the user declined a prompt. State becomes Closed.
    /// </exception>
    public async Task ConnectAsync(SshConnectRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != SessionState.Idle)
                throw new InvalidOperationException("A session connects only once; create a new SshSession to reconnect.");
            _state = SessionState.Connecting;
            _size = new TerminalSize(request.Cols, request.Rows, 0, 0);
            _tunnels = request.Tunnels.Where(t => t.Enabled).Select(t => new TunnelStatus(t.Clone(), TunnelState.Starting)).ToArray();
        }
        StateChanged?.Invoke(SessionState.Connecting, request.JumpChain.Count == 0
            ? $"Connecting to {request.Host}:{request.Port}..."
            : $"Connecting to jump host {request.JumpChain[0].Label}...");

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<Connection>? openTask = null;
        Connection connection;
        try
        {
            if (request.Validate() is { } problem)
                throw new SshSessionException(SshErrorKind.InvalidRequest, problem);
            lock (_gate)
                _attempt = attempt;

            // Loading an encrypted key runs a slow KDF and SSH.NET connects synchronously: keep both off the caller's thread.
            CancellationToken token = attempt.Token;
            openTask = Task.Run(() => OpenAsync(request, token));
            connection = await openTask.WaitAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            bool cancelled, promptTimedOut;
            SshConnectRequest? rejected, hop;
            lock (_gate)
            {
                _attempt = null;
                _connection = null; // disposed below once the open task is done with it
                cancelled = ct.IsCancellationRequested || _state == SessionState.Closed || _promptDeclined;
                rejected = _rejectedHop;
                promptTimedOut = _promptTimedOut;
                hop = _currentHop ?? request;
            }
            if (openTask is not null)
                DisposeWhenDone(openTask);

            if (cancelled)
            {
                TryTransition(SessionState.Closed, "Connection cancelled.");
                throw new OperationCanceledException("The connection attempt was cancelled.", ex, ct);
            }

            SshSessionException error =
                promptTimedOut ? new(SshErrorKind.Timeout,
                    $"The prompt was not answered within {PromptTimeout.TotalSeconds:0} seconds, and servers do not wait much longer for a sign-in. Retry to connect again.", ex)
                : rejected is not null ? new(SshErrorKind.HostKeyRejected, $"The host key of {rejected.Host} was not trusted.", ex)
                : ex is OperationCanceledException ? new(SshErrorKind.Timeout, $"Timed out connecting to {hop.Host}:{hop.Port}.", ex)
                : SshErrors.Map(ex, hop);
            int jumpIndex = IndexOf(request.JumpChain, hop);
            if (jumpIndex >= 0 && error.JumpHostIndex is null)
                error = new SshSessionException(error.Kind, $"{JumpHostPrefix}{hop.Label}: {error.Message}", ex) { JumpHostIndex = jumpIndex };
            TryTransition(SessionState.Failed, error.Message);
            throw error;
        }

        lock (_gate)
            _attempt = null;
        if (!TryTransition(SessionState.Connected, $"Connected to {request.Host}."))
            throw new OperationCanceledException("The session was closed while connecting.", ct); // Close() disposed the connection

        ShellStream shell = connection.Shell!;
        new Thread(() => ReadLoop(shell)) { IsBackground = true, Name = $"SSH reader {request.Host}" }.Start();
        _ = Task.Run(() => WriteLoopAsync(shell));
        SyncWindowSize();
        if (!string.IsNullOrEmpty(request.StartupCommand))
            SendText(request.StartupCommand.ReplaceLineEndings("\r") + "\r");
        if (Tunnels.Count > 0)
            _ = Task.Run(() => StartTunnels(connection.Final));
    }

    /// <summary>
    /// Queues input for the remote shell. Returns false when it was not queued: the session is not connected, or the
    /// server has stopped reading and too much input is already waiting for it.
    /// </summary>
    public bool Send(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return true;
        lock (_gate)
        {
            if (_state != SessionState.Connected)
                return false;
        }
        if (Interlocked.Read(ref _pendingInput) > MaxPendingInput)
            return false;
        Interlocked.Add(ref _pendingInput, data.Length);
        return _outbox.Writer.TryWrite(data.ToArray());
    }

    /// <summary>Sends <paramref name="text"/> UTF-8 encoded (see <see cref="Send"/>).</summary>
    public bool SendText(string text) => Send(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Reports the terminal size to the server (SSH window-change). Repeated calls with an unchanged size are
    /// ignored, so this can be called on every layout pass; a size set while connecting is applied once connected.
    /// </summary>
    public void Resize(int cols, int rows, int pixelWidth, int pixelHeight)
    {
        if (cols < 1 || rows < 1)
            return;
        lock (_gate)
            _size = new TerminalSize(cols, rows, Math.Max(0, pixelWidth), Math.Max(0, pixelHeight));
        SyncWindowSize();
    }

    /// <summary>Closes the session (or abandons a connection attempt). State becomes Closed.</summary>
    public void Disconnect() => Close(SessionState.Closed, "Disconnected.");

    public void Dispose()
    {
        Disconnect();
        lock (_gate)
            _disposed = true;
    }

    /// <summary>
    /// Connects every hop and opens the shell on the last one; runs on a worker thread and disposes its connection if
    /// it fails. Each jump host forwards a local port to the next hop, which is connected through it.
    /// </summary>
    private async Task<Connection> OpenAsync(SshConnectRequest request, CancellationToken token)
    {
        var connection = new Connection();
        try
        {
            SshConnectRequest[] hops = [.. request.JumpChain, request];
            Hop? previous = null;
            for (int i = 0; i < hops.Length; i++)
            {
                SshConnectRequest target = hops[i];
                lock (_gate)
                    _currentHop = target;
                if (previous is not null)
                    TryTransition(SessionState.Connecting, $"Connecting to {target.Label} through {previous.Request.Label}...");

                Hop hop = CreateHop(target, isFinal: i == hops.Length - 1, via: previous?.Forward);
                connection.Hops.Add(hop);
                lock (_gate)
                {
                    _stepTimeout = target.ConnectTimeout;
                    _attempt?.CancelAfter(target.ConnectTimeout); // the limit starts once the key is loaded
                }
                token.ThrowIfCancellationRequested();

                try
                {
                    await hop.Client.ConnectAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex) when (previous is not null && hop.Client.ConnectionInfo.ServerVersion is null
                    && ex is SshConnectionException or SocketException && !token.IsCancellationRequested)
                {
                    // The local end of the forwarding always accepts; no SSH banner came back through it, so the jump host
                    // could not open its connection onward (SSH.NET does not report the jump host's reason).
                    throw new SshSessionException(SshErrorKind.HostUnreachable,
                        $"{JumpHostPrefix}{previous.Request.Label} could not reach an SSH server at {target.Host}:{target.Port} " +
                        "(connection refused, unreachable from the jump host, or port forwarding is disabled there).", ex) { JumpHostIndex = i - 1 };
                }
                hop.Client.ConnectionInfo.Timeout = target.ConnectTimeout; // prompts are over
                token.ThrowIfCancellationRequested();

                if (i < hops.Length - 1)
                    hop.ForwardTo(hops[i + 1]);
                previous = hop;
            }

            SshClient client = connection.Final;
            TerminalSize size;
            lock (_gate)
                size = _size;
            ShellStream shell = request.Environment.Count > 0 && EnvironmentShell.IsSupported
                ? EnvironmentShell.Open(client, request.TermType, (uint)size.Cols, (uint)size.Rows, (uint)size.PixelWidth, (uint)size.PixelHeight,
                    ReadBufferSize, request.Environment)
                : client.CreateShellStream(request.TermType, (uint)size.Cols, (uint)size.Rows, (uint)size.PixelWidth, (uint)size.PixelHeight, ReadBufferSize);
            shell.DataReceived += OnShellData;
            // From here on SSH.NET's timeout only limits how long a write may wait for the server to accept more input
            // (its channel window). A busy program may not read its input for a long time; that is not an error. The
            // jump hosts carry that input too.
            foreach (Hop hop in connection.Hops)
                hop.Client.ConnectionInfo.Timeout = Timeout.InfiniteTimeSpan;

            lock (_gate)
            {
                connection.Shell = shell; // disposed together with the connection from here on
                if (token.IsCancellationRequested || _state is SessionState.Closed or SessionState.Failed)
                    throw new OperationCanceledException(token);
                _connection = connection;
                _sentSize = size;
            }
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Loads the key and creates the client for one hop, connecting directly or, for a hop behind a jump host, to
    /// <paramref name="via"/>'s local port. Its host key is always verified against the hop's own host and port.
    /// </summary>
    private Hop CreateHop(SshConnectRequest request, bool isFinal, ForwardedPortLocal? via)
    {
        string username = request.Username.Trim();
        PrivateKeyFile? keyFile = string.IsNullOrWhiteSpace(request.PrivateKey)
            ? null
            : KeyInspector.Load(request.PrivateKey, request.Passphrase);

        // SSH.NET tries the methods the server allows in the order listed here.
        ConnectionInfo? info = null;
        var methods = new List<AuthenticationMethod>();
        if (keyFile is not null)
            methods.Add(new PrivateKeyAuthenticationMethod(username, new SingleSignatureKeySource(keyFile, () => info?.ServerVersion)));
        if (!string.IsNullOrEmpty(request.Password))
            methods.Add(new PasswordAuthenticationMethod(username, request.Password));
        if (!string.IsNullOrEmpty(request.Password) || _promptUser is not null)
        {
            var interactive = new KeyboardInteractiveAuthenticationMethod(username);
            bool passwordSent = false; // the prompts of one sign-in arrive one request at a time
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (AuthenticationPrompt prompt in e.Prompts)
                    prompt.Response = AnswerPrompt(request, e.Instruction, prompt, ref passwordSent);
            };
            methods.Add(interactive);
        }

        info = via is null
            ? new ConnectionInfo(request.Host.Trim(), request.Port, username, methods.ToArray())
            : new ConnectionInfo(via.BoundHost, (int)via.BoundPort, username, methods.ToArray());
        // SSH.NET waits for key exchange and authentication, which include our prompts, with this timeout.
        info.Timeout = request.ConnectTimeout + PromptTimeout;
        if (!request.LegacyAlgorithms)
            SshAlgorithms.RemoveLegacy(info);

        var client = new SshClient(info)
        {
            KeepAliveInterval = request.KeepAlive > TimeSpan.Zero ? request.KeepAlive : Timeout.InfiniteTimeSpan,
        };
        var hop = new Hop(request, isFinal, client, keyFile);
        client.HostKeyReceived += (_, e) => OnHostKeyReceived(hop, e);
        client.ErrorOccurred += (_, e) => OnConnectionError(hop, e.Exception);
        return hop;
    }

    // Runs on an SSH.NET worker thread; throwing fails the keyboard-interactive method (and so the sign-in).
    private string AnswerPrompt(SshConnectRequest request, string instruction, AuthenticationPrompt prompt, ref bool passwordSent)
    {
        if (!prompt.IsEchoed && !string.IsNullOrEmpty(request.Password) && IsPasswordPrompt(prompt.Request))
        {
            // The password is offered once: a server that asks again has rejected it.
            if (passwordSent)
                throw new SshAuthenticationException("Permission denied (keyboard-interactive).");
            passwordSent = true;
            return request.Password;
        }

        if (_promptUser is null)
        {
            throw new SshSessionException(SshErrorKind.UnsupportedAuthMethod,
                $"{request.Host} asks \"{prompt.Request.Trim()}\" to sign in, which can't be answered here.");
        }
        var question = new InteractivePrompt(request.Host, request.Port, request.Username, instruction ?? "", prompt.Request, prompt.IsEchoed);
        string? answer = WaitForUser(token => _promptUser(question, token), null);
        if (answer is not null)
            return answer;
        lock (_gate)
            _promptDeclined |= !_promptTimedOut;
        throw new SshAuthenticationException("The sign-in prompt was not answered.");
    }

    private static bool IsPasswordPrompt(string text) =>
        Array.Exists(PasswordWords, w => text.Contains(w, StringComparison.OrdinalIgnoreCase))
        && !Array.Exists(SecondFactorWords, w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    // Raised on SSH.NET's message-listener thread during key exchange; the connection waits for our answer.
    private void OnHostKeyReceived(Hop hop, HostKeyEventArgs e)
    {
        string fingerprint = "SHA256:" + e.FingerPrintSHA256;
        lock (_gate)
        {
            if (hop.TrustedFingerprint is not null)
            {
                // Re-key on an established connection: the host key must not change mid-session.
                e.CanTrust = fingerprint == hop.TrustedFingerprint;
                return;
            }
        }

        SshConnectRequest request = hop.Request;
        var info = new HostKeyInfo(request.Host, request.Port, e.HostKeyName, fingerprint);
        bool trusted = WaitForUser(token => _verifier.VerifyAsync(info, token), false);
        lock (_gate)
        {
            if (trusted)
            {
                hop.TrustedFingerprint = fingerprint;
                if (hop.IsFinal)
                    _trustedFingerprint = fingerprint;
            }
            else
            {
                _rejectedHop = request;
            }
        }
        if (trusted && hop.IsFinal)
            TryTransition(SessionState.Authenticating, $"Authenticating as {request.Username}...");
        e.CanTrust = trusted;
    }

    /// <summary>
    /// Runs a user prompt while the connection waits for it. The connect timeout is paused meanwhile; the prompt gets
    /// <see cref="PromptTimeout"/>, after which it is cancelled. Returns <paramref name="fallback"/> when the attempt
    /// was abandoned, the prompt failed or it timed out (recorded, so the failure can say so).
    /// </summary>
    private T WaitForUser<T>(Func<CancellationToken, Task<T>> ask, T fallback)
    {
        CancellationToken attemptToken;
        lock (_gate)
        {
            if (_attempt is null)
                return fallback; // the attempt was abandoned
            attemptToken = _attempt.Token;
            _attempt.CancelAfter(Timeout.InfiniteTimeSpan);
        }

        using var window = CancellationTokenSource.CreateLinkedTokenSource(attemptToken);
        window.CancelAfter(PromptTimeout);
        T result;
        try
        {
            result = ask(window.Token).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            result = fallback; // cancelled, or the prompt failed: never a positive answer by default
        }

        lock (_gate)
        {
            if (window.IsCancellationRequested && !attemptToken.IsCancellationRequested)
            {
                _promptTimedOut = true;
                result = fallback;
            }
            _attempt?.CancelAfter(_stepTimeout); // a fresh budget for the rest of the sign-in
        }
        return result;
    }

    // SSH.NET's listener thread. A dropped connection (the server died, the network went away) is reported only here:
    // the shell stream stays open and the reader would wait forever, and a jump host's connection carries the hops
    // behind it. So the session ends as lost: at once for a jump host (the root cause, reported before the hops behind
    // it notice), after a moment for the final host, in which a shell that exited just before (and whose server then
    // disconnected) ends it as closed instead.
    private void OnConnectionError(Hop hop, Exception error)
    {
        if (State != SessionState.Connected)
            return;
        if (!hop.IsFinal)
        {
            Close(SessionState.Failed, $"Connection lost: {JumpHostPrefix}{hop.Request.Label}: {SshErrors.DescribeLoss(error)}.");
            return;
        }
        _ = Task.Delay(ConnectionErrorGrace).ContinueWith(
            _ => Close(SessionState.Failed, $"Connection lost: {SshErrors.DescribeLoss(error)}."), TaskScheduler.Default);
    }

    // SSH.NET's listener thread, after it buffered the data for the reader. SSH.NET re-opens the channel window as
    // data arrives, so a server flooding faster than the reader (and the UI behind it) consumes would fill memory
    // without limit. While the reader is behind, hold the listener here: SSH.NET stops reading the socket and TCP
    // flow control stops the server.
    private void OnShellData(object? sender, ShellDataEventArgs e)
    {
        if (sender is not ShellStream shell)
            return;
        lock (_outputFlow)
        {
            while (shell.Length > MaxBufferedOutput && State == SessionState.Connected)
                Monitor.Wait(_outputFlow, 250);
        }
    }

    private void ReadLoop(ShellStream shell)
    {
        byte[] buffer = new byte[ReadBufferSize];
        Exception? failure = null;
        try
        {
            // Read blocks until data arrives and returns 0 once the channel or connection has closed.
            int read;
            while ((read = shell.Read(buffer, 0, buffer.Length)) > 0)
            {
                lock (_outputFlow)
                    Monitor.PulseAll(_outputFlow); // the buffer shrank: the listener may go on
                DataReceived?.Invoke(buffer, read);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (failure is null)
            Close(SessionState.Closed, "Connection closed.");
        else
            Close(SessionState.Failed, $"Connection lost: {SshErrors.DescribeLoss(failure)}.");
    }

    private async Task WriteLoopAsync(ShellStream shell)
    {
        ChannelReader<byte[]> reader = _outbox.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out byte[]? chunk))
                {
                    shell.Write(chunk); // waits while the server's window for our input is full
                    Interlocked.Add(ref _pendingInput, -chunk.Length);
                }
                shell.Flush(); // ShellStream buffers writes until flushed
            }
        }
        catch (ObjectDisposedException)
        {
            // Closing: the read loop or Close reports how the session ended.
        }
        catch (Exception ex) when (ex is SshException or SocketException or InvalidOperationException)
        {
            // Input can no longer be delivered, so the session is unusable even if output still arrives.
            Close(SessionState.Failed, $"Connection lost: {SshErrors.DescribeLoss(ex)}.");
        }
    }

    private void SyncWindowSize()
    {
        ShellStream shell;
        TerminalSize size;
        lock (_gate)
        {
            if (_state != SessionState.Connected || _connection?.Shell is not { } open || _size == _sentSize)
                return;
            shell = open;
            size = _sentSize = _size;
        }
        try
        {
            shell.ChangeWindowSize((uint)size.Cols, (uint)size.Rows, (uint)size.PixelWidth, (uint)size.PixelHeight);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or SocketException or InvalidOperationException)
        {
            // Closing; nothing to resize.
        }
    }

    /// <summary>Starts the tunnels one by one on a worker thread; each ends up Active or Failed, never failing the session.</summary>
    private void StartTunnels(SshClient client)
    {
        TunnelStatus[] tunnels;
        lock (_gate)
            tunnels = _tunnels;
        for (int i = 0; i < tunnels.Length; i++)
        {
            PortForward forward = tunnels[i].Forward;
            TunnelStatus status;
            try
            {
                ForwardedPort port = forward.Kind switch
                {
                    ForwardKind.Local => new ForwardedPortLocal(forward.BindAddress, (uint)forward.BindPort, forward.DestinationHost!, (uint)forward.DestinationPort!.Value),
                    ForwardKind.Remote => new ForwardedPortRemote(forward.BindAddress, (uint)forward.BindPort, forward.DestinationHost!, (uint)forward.DestinationPort!.Value),
                    _ => new ForwardedPortDynamic(forward.BindAddress, (uint)forward.BindPort),
                };
                // One forwarded connection failing (e.g. its destination refused it) leaves the tunnel running.
                port.Exception += (_, e) => CoreLog.Warn($"Tunnel {forward}: {e.Exception.Message}");
                client.AddForwardedPort(port);
                port.Start();
                status = tunnels[i] with { State = TunnelState.Active };
            }
            catch (Exception ex)
            {
                status = tunnels[i] with { State = TunnelState.Failed, Error = DescribeTunnelFailure(forward, ex) };
            }

            lock (_gate)
            {
                if (_state != SessionState.Connected)
                    return; // closed meanwhile: Close reported the tunnels stopped
                _tunnels = [.. _tunnels];
                _tunnels[i] = status;
            }
            TunnelsChanged?.Invoke();
        }
    }

    private static string DescribeTunnelFailure(PortForward forward, Exception ex) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } =>
            $"Port {forward.BindPort} on {forward.BindAddress} is already in use.",
        SocketException { SocketErrorCode: SocketError.AccessDenied } =>
            $"Not allowed to listen on port {forward.BindPort} (ports below 1024 need administrator rights).",
        SocketException { SocketErrorCode: SocketError.AddressNotAvailable } =>
            $"{forward.BindAddress} is not an address of this computer.",
        SshException when forward.Kind == ForwardKind.Remote =>
            $"The server refused to listen on {forward.BindAddress}:{forward.BindPort} (port forwarding may be disabled there, or the port is in use).",
        _ => ex.Message,
    };

    /// <summary>Moves to <paramref name="next"/> unless the session has already ended. Returns false if it had.</summary>
    private bool TryTransition(SessionState next, string? message)
    {
        bool tunnelsStopped;
        lock (_gate)
        {
            if (_state is SessionState.Closed or SessionState.Failed)
                return false;
            _state = next;
            if (next == SessionState.Failed)
                _lastError = message;
            tunnelsStopped = (next is SessionState.Closed or SessionState.Failed) && StopTunnels();
        }
        StateChanged?.Invoke(next, message);
        if (tunnelsStopped)
            TunnelsChanged?.Invoke();
        return true;
    }

    /// <summary>Marks the tunnels that are not Failed as Stopped; returns whether any changed. Caller holds <see cref="_gate"/>.</summary>
    private bool StopTunnels()
    {
        if (!Array.Exists(_tunnels, t => t.State is TunnelState.Starting or TunnelState.Active))
            return false;
        _tunnels = _tunnels.Select(t => t.State == TunnelState.Failed ? t : t with { State = TunnelState.Stopped }).ToArray();
        return true;
    }

    private void Close(SessionState state, string message)
    {
        Connection? connection;
        CancellationTokenSource? attempt;
        bool tunnelsStopped;
        lock (_gate)
        {
            if (_state is SessionState.Idle or SessionState.Closed or SessionState.Failed)
                return;
            _state = state;
            if (state == SessionState.Failed)
                _lastError = message;
            connection = _connection;
            _connection = null;
            attempt = _attempt;
            tunnelsStopped = StopTunnels();
        }

        try
        {
            attempt?.Cancel(); // makes a pending ConnectAsync give up
        }
        catch (ObjectDisposedException)
        {
            // ConnectAsync finished concurrently.
        }
        _outbox.Writer.TryComplete();
        lock (_outputFlow)
            Monitor.PulseAll(_outputFlow); // releases a listener held by OnShellData
        StateChanged?.Invoke(state, message);
        if (tunnelsStopped)
            TunnelsChanged?.Invoke();

        // Disconnecting waits for SSH.NET's listener thread, so keep it off the caller's (UI) thread.
        if (connection is not null)
            _ = Task.Run(connection.Dispose);
    }

    private static int IndexOf(IReadOnlyList<SshConnectRequest> chain, SshConnectRequest hop)
    {
        for (int i = 0; i < chain.Count; i++)
        {
            if (ReferenceEquals(chain[i], hop))
                return i;
        }
        return -1;
    }

    /// <summary>Disposes the connection of an abandoned attempt once its open task is done (a failed task disposed its own).</summary>
    private static void DisposeWhenDone(Task<Connection> openTask) =>
        openTask.ContinueWith(task =>
        {
            if (task.Status == TaskStatus.RanToCompletion)
                task.Result.Dispose();
            else
                _ = task.Exception; // observed: the failure was already reported (or the attempt abandoned)
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    private readonly record struct TerminalSize(int Cols, int Rows, int PixelWidth, int PixelHeight);

    /// <summary>The SSH.NET objects behind one connection attempt, disposed together (once).</summary>
    private sealed class Connection : IDisposable
    {
        private int _disposed;

        /// <summary>Jump hosts in connection order, then the final host.</summary>
        public List<Hop> Hops { get; } = [];

        public SshClient Final => Hops[^1].Client;
        public ShellStream? Shell { get; set; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Try(() => Shell?.Dispose());
            for (int i = Hops.Count - 1; i >= 0; i--) // innermost first: each hop runs through the ones before it
                Hops[i].Dispose();
        }
    }

    /// <summary>One SSH connection of a (possibly jumped) session.</summary>
    private sealed class Hop(SshConnectRequest request, bool isFinal, SshClient client, PrivateKeyFile? keyFile) : IDisposable
    {
        public SshConnectRequest Request { get; } = request;
        public bool IsFinal { get; } = isFinal;
        public SshClient Client { get; } = client;

        /// <summary>Guarded by the session's gate.</summary>
        public string? TrustedFingerprint { get; set; }

        /// <summary>The local port leading to the next hop (jump hosts only).</summary>
        public ForwardedPortLocal? Forward { get; private set; }

        /// <summary>Listens on an ephemeral loopback port whose connections this hop forwards to <paramref name="next"/>.</summary>
        public void ForwardTo(SshConnectRequest next)
        {
            var forward = new ForwardedPortLocal(IPAddress.Loopback.ToString(), 0, next.Host.Trim(), (uint)next.Port);
            Client.AddForwardedPort(forward);
            forward.Start();
            Forward = forward;
        }

        public void Dispose()
        {
            IList<AuthenticationMethod> methods = Client.ConnectionInfo.AuthenticationMethods; // unreadable once disposed
            // Best effort: each step runs even if an earlier one fails on a half-closed connection.
            Try(Client.Dispose);
            foreach (AuthenticationMethod method in methods)
                Try(() => (method as IDisposable)?.Dispose());
            Try(() => keyFile?.Dispose());
        }
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // Teardown errors are irrelevant: the connection is gone either way.
        }
    }
}
