using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace TGK.Core.Ssh;

/// <summary>One interactive SSH shell — the connection behind a terminal tab.</summary>
/// <remarks>
/// <para>Single-use: <see cref="ConnectAsync"/> may be called once; to reconnect, dispose and create a new session.</para>
/// <para>
/// Threading: <see cref="StateChanged"/> and <see cref="DataReceived"/> are raised on background threads, so UI
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
    private SshConnectRequest? _request;
    private CancellationTokenSource? _attempt; // live only while ConnectAsync runs
    private Connection? _connection;           // set once the shell is open; until then the connect task owns it
    private string? _trustedFingerprint;
    private bool _hostKeyRejected;
    private bool _promptDeclined;
    private bool _promptTimedOut;
    private Exception? _connectionError;
    private TerminalSize _size;
    private TerminalSize _sentSize;
    private string? _lastError;
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

    public SessionState State { get { lock (_gate) return _state; } }

    /// <summary>The message of the last <see cref="SessionState.Failed"/> transition.</summary>
    public string? LastError { get { lock (_gate) return _lastError; } }

    /// <summary>The server's host key fingerprint once it was trusted.</summary>
    public string? HostKeyFingerprint { get { lock (_gate) return _trustedFingerprint; } }

    /// <summary>
    /// Connects, authenticates (private key, then password, then keyboard-interactive) and opens a PTY shell.
    /// Completes once <see cref="State"/> is <see cref="SessionState.Connected"/>.
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
            _request = request;
            _size = new TerminalSize(request.Cols, request.Rows, 0, 0);
        }
        StateChanged?.Invoke(SessionState.Connecting, $"Connecting to {request.Host}:{request.Port}...");

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
            bool cancelled, hostKeyRejected, promptTimedOut;
            lock (_gate)
            {
                _attempt = null;
                _connection = null; // disposed below once the open task is done with it
                cancelled = ct.IsCancellationRequested || _state == SessionState.Closed || _promptDeclined;
                hostKeyRejected = _hostKeyRejected;
                promptTimedOut = _promptTimedOut;
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
                : hostKeyRejected ? new(SshErrorKind.HostKeyRejected, $"The host key of {request.Host} was not trusted.", ex)
                : ex is OperationCanceledException ? new(SshErrorKind.Timeout, $"Timed out connecting to {request.Host}:{request.Port}.", ex)
                : SshErrors.Map(ex, request);
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

    /// <summary>Creates the connection and opens the shell; runs on a worker thread and disposes its connection if it fails.</summary>
    private async Task<Connection> OpenAsync(SshConnectRequest request, CancellationToken token)
    {
        Connection connection = CreateConnection(request);
        try
        {
            lock (_gate)
                _attempt?.CancelAfter(request.ConnectTimeout); // the limit starts once the key is loaded
            token.ThrowIfCancellationRequested();

            SshClient client = connection.Client;
            await client.ConnectAsync(token).ConfigureAwait(false);
            client.ConnectionInfo.Timeout = request.ConnectTimeout; // prompts are over
            token.ThrowIfCancellationRequested();

            TerminalSize size;
            lock (_gate)
                size = _size;
            ShellStream shell = client.CreateShellStream(request.TermType,
                (uint)size.Cols, (uint)size.Rows, (uint)size.PixelWidth, (uint)size.PixelHeight, ReadBufferSize);
            shell.DataReceived += OnShellData;
            // From here on SSH.NET's timeout only limits how long a write may wait for the server to accept more input
            // (its channel window). A busy program may not read its input for a long time; that is not an error.
            client.ConnectionInfo.Timeout = Timeout.InfiniteTimeSpan;

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

    private Connection CreateConnection(SshConnectRequest request)
    {
        string username = request.Username.Trim();
        PrivateKeyFile? keyFile = string.IsNullOrWhiteSpace(request.PrivateKey)
            ? null
            : KeyInspector.Load(request.PrivateKey, request.Passphrase);

        // SSH.NET tries the methods the server allows in the order listed here.
        var methods = new List<AuthenticationMethod>();
        if (keyFile is not null)
            methods.Add(new PrivateKeyAuthenticationMethod(username, keyFile));
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

        var info = new ConnectionInfo(request.Host.Trim(), request.Port, username, methods.ToArray())
        {
            // SSH.NET waits for key exchange and authentication, which include our prompts, with this timeout.
            Timeout = request.ConnectTimeout + PromptTimeout,
        };
        var client = new SshClient(info)
        {
            KeepAliveInterval = request.KeepAlive > TimeSpan.Zero ? request.KeepAlive : Timeout.InfiniteTimeSpan,
        };
        client.HostKeyReceived += OnHostKeyReceived;
        client.ErrorOccurred += OnConnectionError;
        return new Connection(client, keyFile);
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
    private void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        string fingerprint = "SHA256:" + e.FingerPrintSHA256;
        SshConnectRequest? request;
        lock (_gate)
        {
            if (_trustedFingerprint is not null)
            {
                // Re-key on an established connection: the host key must not change mid-session.
                e.CanTrust = fingerprint == _trustedFingerprint;
                return;
            }
            request = _request;
        }
        if (request is null)
        {
            e.CanTrust = false;
            return;
        }

        var info = new HostKeyInfo(request.Host, request.Port, e.HostKeyName, fingerprint);
        bool trusted = WaitForUser(token => _verifier.VerifyAsync(info, token), false);
        lock (_gate)
        {
            if (trusted)
                _trustedFingerprint = fingerprint;
            else
                _hostKeyRejected = true;
        }
        if (trusted)
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
        TimeSpan connectTimeout;
        lock (_gate)
        {
            if (_attempt is null || _request is null)
                return fallback; // the attempt was abandoned
            attemptToken = _attempt.Token;
            connectTimeout = _request.ConnectTimeout;
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
            _attempt?.CancelAfter(connectTimeout); // a fresh budget for the rest of the sign-in
        }
        return result;
    }

    private void OnConnectionError(object? sender, ExceptionEventArgs e)
    {
        lock (_gate)
        {
            if (_state == SessionState.Connected)
                _connectionError ??= e.Exception;
        }
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

        Exception? error;
        lock (_gate)
            error = failure ?? _connectionError;
        if (error is null)
            Close(SessionState.Closed, "Connection closed.");
        else
            Close(SessionState.Failed, $"Connection lost: {SshErrors.DescribeLoss(error)}.");
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

    /// <summary>Moves to <paramref name="next"/> unless the session has already ended. Returns false if it had.</summary>
    private bool TryTransition(SessionState next, string? message)
    {
        lock (_gate)
        {
            if (_state is SessionState.Closed or SessionState.Failed)
                return false;
            _state = next;
            if (next == SessionState.Failed)
                _lastError = message;
        }
        StateChanged?.Invoke(next, message);
        return true;
    }

    private void Close(SessionState state, string message)
    {
        Connection? connection;
        CancellationTokenSource? attempt;
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

        // Disconnecting waits for SSH.NET's listener thread, so keep it off the caller's (UI) thread.
        if (connection is not null)
            _ = Task.Run(connection.Dispose);
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
    private sealed class Connection(SshClient client, PrivateKeyFile? keyFile) : IDisposable
    {
        private int _disposed;

        public SshClient Client { get; } = client;
        public ShellStream? Shell { get; set; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            IList<AuthenticationMethod> methods = Client.ConnectionInfo.AuthenticationMethods; // unreadable once disposed
            // Best effort: each step runs even if an earlier one fails on a half-closed connection.
            Try(() => Shell?.Dispose());
            Try(Client.Dispose);
            foreach (AuthenticationMethod method in methods)
                Try(() => (method as IDisposable)?.Dispose());
            Try(() => keyFile?.Dispose());
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
}
