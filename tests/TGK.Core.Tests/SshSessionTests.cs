using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Models;
using TGK.Core.Ssh;
using Xunit;

namespace TGK.Core.Tests;

/// <summary>Failure paths that need no SSH server. End-to-end sessions are covered separately.</summary>
public class SshSessionTests
{
    private sealed class RejectAll : IHostKeyVerifier
    {
        public Task<bool> VerifyAsync(HostKeyInfo info, CancellationToken ct) => Task.FromResult(false);
    }

    private static (SshSession Session, ConcurrentQueue<(SessionState, string?)> States) NewSession()
    {
        var session = new SshSession(new RejectAll());
        var states = new ConcurrentQueue<(SessionState, string?)>();
        session.StateChanged += (state, message) => states.Enqueue((state, message));
        return (session, states);
    }

    private static SshConnectRequest Request(string host, int port, TimeSpan? timeout = null) => new()
    {
        Host = host,
        Port = port,
        Username = "tester",
        Password = "pw",
        ConnectTimeout = timeout ?? TimeSpan.FromSeconds(10),
    };

    private static int UnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task UnreachableJumpHost_IsNamedInTheError()
    {
        var (session, _) = NewSession();
        SshConnectRequest request = Request("10.1.2.3", 22) with { JumpChain = [Request("127.0.0.1", UnusedPort()) with { Name = "bastion" }] };

        var ex = await Assert.ThrowsAsync<SshSessionException>(() => session.ConnectAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(SshErrorKind.ConnectionRefused, ex.Kind);
        Assert.StartsWith("Jump host bastion (127.0.0.1): Connection refused by 127.0.0.1:", ex.Message);
        Assert.Equal(0, ex.JumpHostIndex);
    }

    [Fact]
    public async Task FailedConnect_StopsTheTunnels()
    {
        var (session, _) = NewSession();
        int changes = 0;
        session.TunnelsChanged += () => Interlocked.Increment(ref changes);
        SshConnectRequest request = Request("127.0.0.1", UnusedPort()) with
        {
            Tunnels = [new PortForward { Kind = ForwardKind.Dynamic, BindPort = 1080 }, new PortForward { Kind = ForwardKind.Dynamic, BindPort = 1081, Enabled = false }],
        };

        await Assert.ThrowsAsync<SshSessionException>(() => session.ConnectAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(TunnelState.Stopped, Assert.Single(session.Tunnels).State);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task InvalidRequest_FailsWithoutConnecting()
    {
        var (session, states) = NewSession();

        var ex = await Assert.ThrowsAsync<SshSessionException>(() => session.ConnectAsync(Request("", 22), TestContext.Current.CancellationToken));

        Assert.Equal(SshErrorKind.InvalidRequest, ex.Kind);
        Assert.Equal(SessionState.Failed, session.State);
        Assert.Equal(ex.Message, session.LastError);
        Assert.Equal([SessionState.Connecting, SessionState.Failed], states.Select(s => s.Item1));
    }

    [Fact]
    public async Task BadPrivateKey_FailsWithKeyError()
    {
        var (session, _) = NewSession();
        SshConnectRequest request = Request("127.0.0.1", UnusedPort()) with { PrivateKey = "garbage" };

        var ex = await Assert.ThrowsAsync<SshSessionException>(() => session.ConnectAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(SshErrorKind.KeyError, ex.Kind);
    }

    [Fact]
    public async Task EncryptedKey_IsDecryptedOffTheCallersThread()
    {
        Assert.SkipUnless(SshKeygen.IsAvailable, "ssh-keygen is not installed");
        using var dir = new TempDirectory();
        string key = File.ReadAllText(SshKeygen.Generate(dir.Path, "ed25519", "secret", rounds: 16));
        var (session, _) = NewSession();
        SshConnectRequest request = Request("127.0.0.1", UnusedPort()) with { Password = null, PrivateKey = key, Passphrase = "secret" };

        // The bcrypt KDF of an encrypted OpenSSH key takes around a second: the (UI) caller must not wait for it.
        var watch = Stopwatch.StartNew();
        Task connect = session.ConnectAsync(request, TestContext.Current.CancellationToken);
        TimeSpan returnedAfter = watch.Elapsed;
        var ex = await Assert.ThrowsAsync<SshSessionException>(() => connect);

        Assert.InRange(returnedAfter, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
        Assert.Equal(SshErrorKind.ConnectionRefused, ex.Kind); // the key itself was fine
    }

    [Fact]
    public async Task ClosedPort_FailsWithConnectionRefused()
    {
        var (session, states) = NewSession();

        var ex = await Assert.ThrowsAsync<SshSessionException>(() => session.ConnectAsync(Request("127.0.0.1", UnusedPort()), TestContext.Current.CancellationToken));

        Assert.Equal(SshErrorKind.ConnectionRefused, ex.Kind);
        Assert.Contains("refused", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SessionState.Failed, session.State);
        Assert.Equal((SessionState.Failed, ex.Message), states.Last());
    }

    [Fact]
    public async Task UnresolvableHost_FailsWithDnsError()
    {
        var (session, _) = NewSession();

        var ex = await Assert.ThrowsAsync<SshSessionException>(() => session.ConnectAsync(Request("no-such-host.invalid", 22), TestContext.Current.CancellationToken));

        // A resolver that cannot answer at all (no network) may surface as a timeout instead.
        Assert.Contains(ex.Kind, new[] { SshErrorKind.DnsFailure, SshErrorKind.Timeout });
    }

    [Fact]
    public async Task SilentServer_TimesOut()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); // accepts TCP but never speaks SSH
        var (session, _) = NewSession();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var started = DateTime.UtcNow;
        var ex = await Assert.ThrowsAsync<SshSessionException>(() =>
            session.ConnectAsync(Request("127.0.0.1", port, TimeSpan.FromMilliseconds(800)), TestContext.Current.CancellationToken));

        Assert.Equal(SshErrorKind.Timeout, ex.Kind);
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromMilliseconds(700), TimeSpan.FromSeconds(10));
        Assert.Equal(SessionState.Failed, session.State);
    }

    [Fact]
    public async Task Cancellation_ClosesSession()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var (session, states) = NewSession();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ConnectAsync(Request("127.0.0.1", port), cts.Token));

        Assert.Equal(SessionState.Closed, session.State);
        Assert.Null(session.LastError);
        Assert.Equal(SessionState.Closed, states.Last().Item1);
    }

    [Fact]
    public async Task Disconnect_WhileConnecting_AbandonsAttempt()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var (session, states) = NewSession();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task connect = session.ConnectAsync(Request("127.0.0.1", port), TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        session.Disconnect();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        Assert.Equal(SessionState.Closed, session.State);
        Assert.Single(states, s => s.Item1 == SessionState.Closed);
    }

    [Fact]
    public async Task Session_IsSingleUse()
    {
        var (session, _) = NewSession();
        await Assert.ThrowsAsync<SshSessionException>(() => session.ConnectAsync(Request("", 22), TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConnectAsync(Request("h", 22), TestContext.Current.CancellationToken));
        session.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ConnectAsync(Request("h", 22), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void IdleSession_IgnoresSendResizeAndDisconnect()
    {
        var (session, states) = NewSession();

        Assert.False(session.Send([1, 2, 3]));
        session.Resize(100, 30, 800, 600);
        session.Disconnect();
        session.Dispose();

        Assert.Equal(SessionState.Idle, session.State);
        Assert.Empty(states);
    }
}
