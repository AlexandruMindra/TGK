using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TGK.Core.Agents;

/// <summary>Where a running TGK serves agents: written to <see cref="AgentEndpoint.InfoPath"/> while it does.</summary>
public sealed record AgentEndpointInfo(string Socket, string Token, int Pid);

/// <summary>
/// The local endpoint agents reach TGK through: a Unix domain socket (Linux, macOS and Windows 10+) whose clients
/// must first send the token from <see cref="InfoPath"/> (readable only by the user), then speak MCP. Usually the
/// client is <c>tgk-mcp</c> (<see cref="AgentBridge"/>), started by the agent.
/// </summary>
public sealed class AgentEndpoint : IAsyncDisposable
{
    /// <summary>First line a client sends: the protocol name, a space, the token.</summary>
    public const string Hello = "TGK-AGENT/1";

    private const int MaxHelloBytes = 512;
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);

    private readonly AgentToolbox _toolbox;
    private readonly string _infoPath;
    private readonly string _socketPath;
    private readonly Lock _gate = new();
    private readonly List<Connection> _connections = [];
    private readonly CancellationTokenSource _stop = new();
    private Socket? _listener;
    private string _token = "";

    /// <param name="infoPath">Where the socket path and token are published; default <see cref="InfoPath"/>.</param>
    /// <param name="socketPath">The socket; default <see cref="DefaultSocketPath"/>.</param>
    public AgentEndpoint(AgentToolbox toolbox, string? infoPath = null, string? socketPath = null)
    {
        _toolbox = toolbox ?? throw new ArgumentNullException(nameof(toolbox));
        _infoPath = infoPath ?? InfoPath;
        _socketPath = socketPath ?? DefaultSocketPath();
    }

    /// <summary>The published endpoint of the running TGK: <c>agent.json</c> in the config directory.</summary>
    public static string InfoPath => Path.Combine(AppPaths.ConfigDirectory, "agent.json");

    /// <summary>
    /// <c>agent.sock</c> in the config directory, or in a private temporary directory when that path is too long for
    /// a socket (about 100 bytes).
    /// </summary>
    public static string DefaultSocketPath()
    {
        string path = Path.Combine(AppPaths.ConfigDirectory, "agent.sock");
        if (Encoding.UTF8.GetByteCount(path) < 100)
            return path;
        string dir = Path.Combine(Path.GetTempPath(), $"tgk-{Environment.UserName}");
        return Path.Combine(dir, "agent.sock");
    }

    public string SocketPath => _socketPath;

    /// <summary>The agents connected now.</summary>
    public IReadOnlyList<AgentSession> Sessions
    {
        get
        {
            lock (_gate)
                return _connections.Select(c => c.Session).ToList();
        }
    }

    /// <summary>Raised (on any thread) when an agent connected, identified itself or disconnected.</summary>
    public event Action? SessionsChanged;

    /// <summary>Starts listening and publishes the endpoint.</summary>
    /// <exception cref="InvalidOperationException">Another TGK is already serving agents (its socket answers).</exception>
    /// <exception cref="IOException">The socket or the info file could not be created.</exception>
    public void Start()
    {
        string? dir = Path.GetDirectoryName(_socketPath);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if (File.Exists(_socketPath))
        {
            if (IsServing(_socketPath))
                throw new InvalidOperationException("Another TGK window on this computer is already serving agents.");
            File.Delete(_socketPath); // left behind by a TGK that did not exit cleanly
        }

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(8);
        }
        catch (SocketException ex)
        {
            listener.Dispose();
            throw new IOException($"Could not listen on {_socketPath}: {ex.Message}", ex);
        }
        _listener = listener;
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Publish(new AgentEndpointInfo(_socketPath, _token, Environment.ProcessId));
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Ends every agent's session (they can reconnect).</summary>
    public void DisconnectAll()
    {
        Connection[] open;
        lock (_gate)
            open = [.. _connections];
        foreach (Connection c in open)
            c.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener?.Dispose();
        DisconnectAll();
        Task[] running;
        lock (_gate)
            running = _connections.Select(c => c.Task).ToArray();
        await Task.WhenAny(Task.WhenAll(running), Task.Delay(2000)).ConfigureAwait(false);
        TryDelete(_socketPath);
        // Only our own publication: a newer TGK may have replaced it.
        if (ReadInfo(_infoPath) is { } info && info.Token == _token)
            TryDelete(_infoPath);
        _stop.Dispose();
    }

    /// <summary>The endpoint a running TGK published, or null.</summary>
    public static AgentEndpointInfo? ReadInfo(string? path = null)
    {
        try
        {
            string file = path ?? InfoPath;
            return File.Exists(file) ? JsonSerializer.Deserialize<AgentEndpointInfo>(File.ReadAllText(file), TgkJson.Options) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void Publish(AgentEndpointInfo info)
    {
        string temp = _infoPath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(_infoPath)!);
        File.Delete(temp);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options))
            JsonSerializer.Serialize(stream, info, TgkJson.Options);
        File.Move(temp, _infoPath, overwrite: true);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener!.AcceptAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                CoreLog.Warn($"Agent endpoint: accept failed: {ex.Message}");
                await Task.Delay(200).ConfigureAwait(false);
                continue;
            }
            var connection = new Connection(client, CancellationTokenSource.CreateLinkedTokenSource(_stop.Token));
            lock (_gate)
                _connections.Add(connection);
            connection.Task = Task.Run(() => ServeAsync(connection));
        }
    }

    private async Task ServeAsync(Connection connection)
    {
        bool announced = false;
        try
        {
            await using var stream = new NetworkStream(connection.Socket, ownsSocket: true);
            string? hello = await ReadLineAsync(stream, connection.Token).ConfigureAwait(false);
            if (hello != $"{Hello} {_token}")
            {
                await stream.WriteAsync("ERR The TGK agent token is wrong or missing (TGK restarted? run tgk-mcp again).\n"u8.ToArray()).ConfigureAwait(false);
                return;
            }
            await stream.WriteAsync("OK\n"u8.ToArray(), connection.Token).ConfigureAwait(false);
            announced = true;
            SessionsChanged?.Invoke();
            // The agent hanging up must also end its calls in progress (and close the prompts they wait on): the MCP
            // server would otherwise let them finish first.
            var input = new EndNotifyingStream(stream, connection.Cancel);
            await McpAgentServer.RunAsync(input, stream, _toolbox, connection.Session, () => SessionsChanged?.Invoke(), connection.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // The agent went away, or TGK stops serving.
        }
        catch (Exception ex)
        {
            CoreLog.Warn($"Agent session failed: {ex}");
        }
        finally
        {
            lock (_gate)
                _connections.Remove(connection);
            connection.Dispose();
            if (announced)
                SessionsChanged?.Invoke();
        }
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HelloTimeout);
        var bytes = new List<byte>();
        byte[] one = new byte[1];
        try
        {
            while (bytes.Count < MaxHelloBytes)
            {
                if (await stream.ReadAsync(one, timeout.Token).ConfigureAwait(false) == 0)
                    return null;
                if (one[0] == (byte)'\n')
                    return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
                bytes.Add(one[0]);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
        return null;
    }

    private static bool IsServing(string socketPath)
    {
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(socketPath));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A read-only view of a stream that calls <paramref name="ended"/> once reading reaches its end.</summary>
    private sealed class EndNotifyingStream(Stream inner, Action ended) : Stream
    {
        private int _ended;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Check(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Check(await inner.ReadAsync(buffer, ct).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        private int Check(int read)
        {
            if (read == 0 && Interlocked.Exchange(ref _ended, 1) == 0)
                ended();
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Connection(Socket socket, CancellationTokenSource cts) : IDisposable
    {
        public Socket Socket { get; } = socket;
        public AgentSession Session { get; } = new();
        public CancellationToken Token { get; } = cts.Token;
        public Task Task { get; set; } = Task.CompletedTask;

        public void Cancel()
        {
            try
            {
                cts.Cancel();
                Socket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
            {
            }
        }

        public void Dispose() => cts.Dispose();
    }
}
