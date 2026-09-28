using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Ssh;
using TGK.Terminal;
using Xunit;

namespace TGK.Core.Tests;

/// <summary>
/// Real SSH sessions against a test server (see docs/DEV-TESTING.md). Skipped unless <c>TGK_E2E_SSH</c> is set to
/// <c>user:password@host:port</c>; the key test also needs <c>TGK_E2E_SSH_KEY</c> (path to an authorized private key).
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

    private sealed record Target(string User, string Password, string Host, int Port)
    {
        public static Target FromEnvironment()
        {
            string? spec = Environment.GetEnvironmentVariable("TGK_E2E_SSH");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "TGK_E2E_SSH is not set (user:password@host:port)");
            int at = spec!.LastIndexOf('@'), colon = spec.IndexOf(':'), portColon = spec.LastIndexOf(':');
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
        public HostKeyInfo? Seen { get; private set; }

        public Task<bool> VerifyAsync(HostKeyInfo info, CancellationToken ct)
        {
            Seen = info;
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

        public HostKeyInfo? HostKey => _verifier.Seen;

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

        /// <summary>Polls the rendered screen until a line matches (the shell echoes asynchronously).</summary>
        public async Task WaitForLineAsync(Func<string, bool> match)
        {
            DateTime deadline = DateTime.UtcNow + WaitTimeout;
            string screen = "";
            while (DateTime.UtcNow < deadline)
            {
                lock (_emulator.SyncRoot)
                    screen = _emulator.GetText(0, 0, _emulator.Rows - 1, _emulator.Cols);
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
