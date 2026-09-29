using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Models;
using TGK.Core.Ssh;
using TGK.Terminal;
using Xunit;

namespace TGK.Core.Tests;

/// <summary>
/// Real SSH sessions against a test server (see docs/DEV-TESTING.md). Skipped unless <c>TGK_E2E_SSH</c> is set to
/// <c>user:password@host:port</c>; the key test also needs <c>TGK_E2E_SSH_KEY</c> (path to an authorized private key)
/// and the legacy test <c>TGK_E2E_SSH_LEGACY</c> (a server that only offers legacy key exchange). The jump host and
/// tunnel tests need a server that allows port forwarding; jump tests reach the server through itself.
/// </summary>
[Trait("Category", "E2E")]
public class SshEndToEndTests
{
    private const int Cols = 100, Rows = 30;
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Password_RunsCommandAndResizes()
    {
        Target target = Target.FromEnvironment();
        await using var term = await Session.OpenAsync(target.Request() with { Password = target.Password });

        term.Send("echo tgk-$((6*7))\r");
        await term.WaitForLineAsync(line => line.Trim() == "tgk-42");

        term.Resize(120, 40);
        term.Send("stty size\r");
        await term.WaitForLineAsync(line => line.Trim() == "40 120");
        Assert.StartsWith("SHA256:", term.HostKey?.FingerprintSha256);
    }

    [Fact]
    public async Task PrivateKey_RunsCommand()
    {
        Target target = Target.FromEnvironment();
        string? keyPath = Environment.GetEnvironmentVariable("TGK_E2E_SSH_KEY");
        Assert.SkipWhen(string.IsNullOrEmpty(keyPath), "TGK_E2E_SSH_KEY is not set");

        await using var term = await Session.OpenAsync(target.Request() with { PrivateKey = File.ReadAllText(keyPath!) });

        term.Send("echo tgk-$((6*7))\r");
        await term.WaitForLineAsync(line => line.Trim() == "tgk-42");
    }

    [Fact]
    public async Task WrongPassword_FailsWithAuthenticationError()
    {
        Target target = Target.FromEnvironment();
        using var session = new SshSession(new TrustAll());

        var ex = await Assert.ThrowsAsync<SshSessionException>(() =>
            session.ConnectAsync(target.Request() with { Password = target.Password + "-wrong" }, TestContext.Current.CancellationToken));

        Assert.Equal(SshErrorKind.AuthenticationFailed, ex.Kind);
        Assert.Equal(SessionState.Failed, session.State);
    }

    [Fact]
    public async Task JumpHost_ConnectsThrough_AndVerifiesEachHopByItsOwnName()
    {
        Target target = Target.FromEnvironment();
        SshConnectRequest jump = target.Request() with { Name = "bastion", Password = target.Password };
        // "localhost" is resolved by the jump host; the final host key must be checked as localhost, not 127.0.0.1:<local port>.
        SshConnectRequest request = target.Request() with { Host = "localhost", Password = target.Password, JumpChain = [jump] };

        await using var term = await Session.OpenAsync(request);
        term.Send("echo tgk-$((6*7))\r");
        await term.WaitForLineAsync(line => line.Trim() == "tgk-42");

        Assert.Equal([(target.Host, target.Port), ("localhost", target.Port)], term.Verified.Select(i => (i.Host, i.Port)));
        Assert.Equal(term.Verified[^1].FingerprintSha256, term.Ssh.HostKeyFingerprint);
    }

    [Fact]
    public async Task TwoJumpHosts_AreChained()
    {
        Target target = Target.FromEnvironment();
        SshConnectRequest hop = target.Request() with { Password = target.Password };
        SshConnectRequest request = hop with { JumpChain = [hop, hop with { Host = "localhost" }] };

        await using var term = await Session.OpenAsync(request);
        term.Send("echo tgk-$((6*7))\r");
        await term.WaitForLineAsync(line => line.Trim() == "tgk-42");

        Assert.Equal([target.Host, "localhost", target.Host], term.Verified.Select(i => i.Host));
        Assert.All(term.Verified, i => Assert.Equal(target.Port, i.Port));
    }

    [Fact]
    public async Task JumpHost_ThatCannotReachTheTarget_IsNamedInTheError()
    {
        Target target = Target.FromEnvironment();
        int closed = UnusedPort();
        SshConnectRequest request = target.Request() with
        {
            Port = closed,
            Password = target.Password,
            JumpChain = [target.Request() with { Name = "bastion", Password = target.Password }],
        };
        using var session = new SshSession(new TrustAll());

        var ex = await Assert.ThrowsAsync<SshSessionException>(() => session.ConnectAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(SshErrorKind.HostUnreachable, ex.Kind);
        Assert.StartsWith($"Jump host bastion ({target.Host}) could not reach an SSH server at {target.Host}:{closed} (", ex.Message);
        Assert.Equal(0, ex.JumpHostIndex);
    }

    [Fact]
    public async Task JumpHost_WrongPassword_IsAttributedToTheJumpHost()
    {
        Target target = Target.FromEnvironment();
        SshConnectRequest request = target.Request() with
        {
            Password = target.Password,
            JumpChain = [target.Request() with { Name = "bastion", Password = target.Password + "-wrong" }],
        };
        using var session = new SshSession(new TrustAll());

        var ex = await Assert.ThrowsAsync<SshSessionException>(() => session.ConnectAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(SshErrorKind.AuthenticationFailed, ex.Kind);
        Assert.Equal(0, ex.JumpHostIndex);
        Assert.StartsWith($"Jump host bastion ({target.Host}): Authentication failed for {target.User}@{target.Host}", ex.Message);
    }

    [Fact]
    public async Task LocalAndRemoteTunnels_CarryData()
    {
        Target target = Target.FromEnvironment();
        await using var echo = new EchoServer();
        int local = UnusedPort(), remote = UnusedPort();
        SshConnectRequest request = target.Request() with
        {
            Password = target.Password,
            Tunnels =
            [
                new PortForward { Kind = ForwardKind.Local, BindPort = local, DestinationHost = "127.0.0.1", DestinationPort = echo.Port },
                // The test server runs on this machine, so its listening port is reachable here too.
                new PortForward { Kind = ForwardKind.Remote, BindPort = remote, DestinationHost = "127.0.0.1", DestinationPort = echo.Port },
                new PortForward { Kind = ForwardKind.Dynamic, BindPort = UnusedPort(), Enabled = false },
            ],
        };

        await using var term = await Session.OpenAsync(request);
        IReadOnlyList<TunnelStatus> tunnels = await term.WaitForTunnelsAsync();

        Assert.Equal(2, tunnels.Count); // the disabled one is not started
        Assert.All(tunnels, t => Assert.Equal(TunnelState.Active, t.State));
        foreach (int port in new[] { local, remote })
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
            Assert.Equal($"ping {port}", await RoundTripAsync(client.GetStream(), $"ping {port}"));
        }
    }

    [Fact]
    public async Task DynamicTunnel_IsASocksProxy()
    {
        Target target = Target.FromEnvironment();
        await using var echo = new EchoServer();
        int socks = UnusedPort();
        SshConnectRequest request = target.Request() with
        {
            Password = target.Password,
            Tunnels = [new PortForward { Kind = ForwardKind.Dynamic, BindPort = socks }],
        };

        await using var term = await Session.OpenAsync(request);
        Assert.Equal(TunnelState.Active, Assert.Single(await term.WaitForTunnelsAsync()).State);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, socks, TestContext.Current.CancellationToken);
        NetworkStream stream = client.GetStream();
        // SOCKS5: no authentication, then CONNECT 127.0.0.1:<echo port>.
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 5, 0 }, await ReadAsync(stream, 2));
        await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, (byte)(echo.Port >> 8), (byte)echo.Port }, TestContext.Current.CancellationToken);
        byte[] reply = await ReadAsync(stream, 10);
        Assert.Equal(0, reply[1]); // succeeded
        Assert.Equal("through socks", await RoundTripAsync(stream, "through socks"));
    }

    [Fact]
    public async Task TunnelOnABusyPort_Fails_WhileTheShellKeepsWorking()
    {
        Target target = Target.FromEnvironment();
        var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        try
        {
            int port = ((IPEndPoint)busy.LocalEndpoint).Port;
            SshConnectRequest request = target.Request() with
            {
                Password = target.Password,
                Tunnels = [new PortForward { Kind = ForwardKind.Local, BindPort = port, DestinationHost = "127.0.0.1", DestinationPort = 22 }],
            };

            await using var term = await Session.OpenAsync(request);
            TunnelStatus tunnel = Assert.Single(await term.WaitForTunnelsAsync());

            Assert.Equal(TunnelState.Failed, tunnel.State);
            Assert.Equal($"Port {port} on 127.0.0.1 is already in use.", tunnel.Error);
            term.Send("echo tgk-$((6*7))\r");
            await term.WaitForLineAsync(line => line.Trim() == "tgk-42");
            Assert.Equal(SessionState.Connected, term.Ssh.State);

            await term.DisposeAsync();
            Assert.Equal(TunnelState.Failed, Assert.Single(term.Ssh.Tunnels).State); // the reason stays after the session ends
        }
        finally
        {
            busy.Stop();
        }
    }

    [Fact]
    public async Task Environment_AndStartupCommand_ReachTheShell()
    {
        Target target = Target.FromEnvironment();
        SshConnectRequest request = target.Request() with
        {
            Password = target.Password,
            Environment = [new EnvVar { Name = "TGK_E2E_VAR", Value = "hello-env" }],
            StartupCommand = "echo startup-$((2*21)) env-$TGK_E2E_VAR",
        };

        await using var term = await Session.OpenAsync(request);

        await term.WaitForLineAsync(line => line.Trim().StartsWith("startup-42 env-", StringComparison.Ordinal));
        Assert.SkipUnless(term.ScreenText().Contains("startup-42 env-hello-env"), "The test server did not accept the env request.");
    }

    [Fact]
    public async Task LegacyOnlyServer_NeedsLegacyAlgorithms()
    {
        string? spec = Environment.GetEnvironmentVariable("TGK_E2E_SSH_LEGACY");
        Assert.SkipWhen(string.IsNullOrEmpty(spec), "TGK_E2E_SSH_LEGACY is not set (user:password@host:port of a legacy-only server)");
        Target target = Target.Parse(spec!);
        using var session = new SshSession(new TrustAll());

        var ex = await Assert.ThrowsAsync<SshSessionException>(() =>
            session.ConnectAsync(target.Request() with { Password = target.Password }, TestContext.Current.CancellationToken));
        Assert.Equal(SshErrorKind.AlgorithmMismatch, ex.Kind);
        Assert.Contains("\"Legacy algorithms\"", ex.Message);

        await using var term = await Session.OpenAsync(target.Request() with { Password = target.Password, LegacyAlgorithms = true });
        term.Send("echo tgk-$((6*7))\r");
        await term.WaitForLineAsync(line => line.Trim() == "tgk-42");
    }

    [Fact]
    public async Task DroppedConnection_EndsTheSessionAsLost()
    {
        Target target = Target.FromEnvironment();
        await using var relay = new Relay(target.Host, target.Port);
        await using var term = await Session.OpenAsync(target.Request() with { Host = "127.0.0.1", Port = relay.Port, Password = target.Password });
        term.Send("echo tgk-$((6*7))\r");
        await term.WaitForLineAsync(line => line.Trim() == "tgk-42");

        relay.DropAll(); // what the client sees when the server dies

        await WaitUntilAsync(() => term.Ssh.State == SessionState.Failed);
        Assert.StartsWith("Connection lost: ", term.Ssh.LastError);
    }

    [Fact]
    public async Task DroppedJumpHostConnection_EndsTheSessionAsLost()
    {
        Target target = Target.FromEnvironment();
        await using var relay = new Relay(target.Host, target.Port);
        SshConnectRequest jump = target.Request() with { Name = "bastion", Host = "127.0.0.1", Port = relay.Port, Password = target.Password };
        await using var term = await Session.OpenAsync(target.Request() with { Password = target.Password, JumpChain = [jump] });
        term.Send("echo tgk-$((6*7))\r");
        await term.WaitForLineAsync(line => line.Trim() == "tgk-42");

        relay.DropAll(); // only the jump host's connection: the final one runs through its local forwarding

        await WaitUntilAsync(() => term.Ssh.State == SessionState.Failed);
        Assert.StartsWith("Connection lost: Jump host bastion (127.0.0.1): ", term.Ssh.LastError);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + WaitTimeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Condition not met within {WaitTimeout.TotalSeconds:0}s.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private static int UnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<byte[]> ReadAsync(Stream stream, int count)
    {
        byte[] buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, TestContext.Current.CancellationToken).AsTask().WaitAsync(WaitTimeout, TestContext.Current.CancellationToken);
        return buffer;
    }

    private static async Task<string> RoundTripAsync(Stream stream, string text)
    {
        byte[] data = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(data, TestContext.Current.CancellationToken);
        return Encoding.ASCII.GetString(await ReadAsync(stream, data.Length));
    }

    /// <summary>A loopback TCP server that sends back what it receives.</summary>
    private sealed class EchoServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accepting;

        public EchoServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accepting = AcceptAsync();
        }

        public int Port { get; }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            NetworkStream stream = client.GetStream();
                            byte[] buffer = new byte[4096];
                            int read;
                            try
                            {
                                while ((read = await stream.ReadAsync(buffer, _stop.Token)) > 0)
                                    await stream.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
                            }
                            catch (Exception ex) when (ex is IOException or OperationCanceledException)
                            {
                            }
                        }
                    });
                }
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _accepting;
            _stop.Dispose();
        }
    }

    /// <summary>A loopback TCP relay to the test server whose connections can be cut at once, like a server that died.</summary>
    private sealed class Relay : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly ConcurrentBag<TcpClient> _sockets = [];
        private readonly Task _accepting;

        public Relay(string host, int port)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accepting = AcceptAsync(host, port);
        }

        public int Port { get; }

        private async Task AcceptAsync(string host, int port)
        {
            try
            {
                while (true)
                {
                    TcpClient inbound = await _listener.AcceptTcpClientAsync();
                    var outbound = new TcpClient();
                    _sockets.Add(inbound);
                    _sockets.Add(outbound);
                    await outbound.ConnectAsync(host, port);
                    _ = PumpAsync(inbound, outbound);
                    _ = PumpAsync(outbound, inbound);
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
            }
        }

        private static async Task PumpAsync(TcpClient from, TcpClient to)
        {
            try
            {
                await from.GetStream().CopyToAsync(to.GetStream());
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }

        public void DropAll()
        {
            foreach (TcpClient socket in _sockets)
                socket.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            DropAll();
            await _accepting;
        }
    }

    private sealed record Target(string User, string Password, string Host, int Port)
    {
        public static Target FromEnvironment()
        {
            string? spec = Environment.GetEnvironmentVariable("TGK_E2E_SSH");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "TGK_E2E_SSH is not set (user:password@host:port)");
            return Parse(spec!);
        }

        public static Target Parse(string spec)
        {
            int at = spec.LastIndexOf('@'), colon = spec.IndexOf(':'), portColon = spec.LastIndexOf(':');
            if (at < 0 || colon < 0 || colon > at || portColon < at)
                throw new InvalidOperationException("TGK_E2E_SSH must look like user:password@host:port");
            return new Target(spec[..colon], spec[(colon + 1)..at], spec[(at + 1)..portColon],
                int.Parse(spec[(portColon + 1)..], CultureInfo.InvariantCulture));
        }

        public SshConnectRequest Request() => new()
        {
            Host = Host,
            Port = Port,
            Username = User,
            Cols = Cols,
            Rows = Rows,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
    }

    private sealed class TrustAll : IHostKeyVerifier
    {
        private readonly ConcurrentQueue<HostKeyInfo> _seen = new();

        /// <summary>Every host key asked about, in order.</summary>
        public List<HostKeyInfo> Seen => [.. _seen];

        public Task<bool> VerifyAsync(HostKeyInfo info, CancellationToken ct)
        {
            _seen.Enqueue(info);
            return Task.FromResult(true);
        }
    }

    /// <summary>An SSH session wired to an emulator the way a terminal tab does it.</summary>
    private sealed class Session : IAsyncDisposable
    {
        private readonly SshSession _ssh;
        private readonly TerminalEmulator _emulator = new(Cols, Rows);
        private readonly TrustAll _verifier = new();

        private Session()
        {
            _ssh = new SshSession(_verifier);
            _ssh.DataReceived += (buffer, count) =>
            {
                lock (_emulator.SyncRoot)
                    _emulator.Feed(buffer.AsSpan(0, count));
            };
            _emulator.Output += reply => _ssh.Send(reply);
        }

        public HostKeyInfo? HostKey => _verifier.Seen.LastOrDefault();
        public List<HostKeyInfo> Verified => _verifier.Seen;
        public SshSession Ssh => _ssh;

        public static async Task<Session> OpenAsync(SshConnectRequest request)
        {
            var session = new Session();
            await session._ssh.ConnectAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(SessionState.Connected, session._ssh.State);
            return session;
        }

        public void Send(string text) => _ssh.SendText(text);

        public void Resize(int cols, int rows)
        {
            lock (_emulator.SyncRoot)
                _emulator.Resize(cols, rows);
            _ssh.Resize(cols, rows, 0, 0);
        }

        public string ScreenText()
        {
            lock (_emulator.SyncRoot)
                return _emulator.GetText(0, 0, _emulator.Rows - 1, _emulator.Cols);
        }

        /// <summary>Waits until no tunnel is starting any more and returns their states.</summary>
        public async Task<IReadOnlyList<TunnelStatus>> WaitForTunnelsAsync()
        {
            DateTime deadline = DateTime.UtcNow + WaitTimeout;
            while (_ssh.Tunnels.Any(t => t.State == TunnelState.Starting) && DateTime.UtcNow < deadline)
                await Task.Delay(20, TestContext.Current.CancellationToken);
            return _ssh.Tunnels;
        }

        /// <summary>Polls the rendered screen until a line matches (the shell echoes asynchronously).</summary>
        public async Task WaitForLineAsync(Func<string, bool> match)
        {
            DateTime deadline = DateTime.UtcNow + WaitTimeout;
            string screen = "";
            while (DateTime.UtcNow < deadline)
            {
                screen = ScreenText();
                foreach (string line in screen.Split('\n'))
                {
                    if (match(line))
                        return;
                }
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
            Assert.Fail($"No matching line within {WaitTimeout.TotalSeconds:0}s. Screen:\n{screen}");
        }

        public ValueTask DisposeAsync()
        {
            _ssh.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
