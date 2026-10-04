using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Agents;
using TGK.Core.Ssh;
using Xunit;

namespace TGK.Core.Tests;

/// <summary>
/// Commands and files over a real <see cref="RemoteConnection"/> (see docs/DEV-TESTING.md): <c>TGK_E2E_SSH</c> is a test
/// server with exec and SFTP (<c>scripts/e2e-sshd.py</c>); <c>TGK_E2E_SSH_NOSFTP</c> optionally one without SFTP, for
/// the command fallback. Files are created under a fresh directory in /tmp and removed afterwards.
/// </summary>
[Trait("Category", "E2E")]
public sealed class RemoteConnectionEndToEndTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly string _dir = $"/tmp/tgk-e2e-{Guid.NewGuid():N}";
    private RemoteConnection? _sftpConnection;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_sftpConnection is { IsConnected: true } c)
            await c.RunAsync($"rm -rf {_dir}", Timeout);
        _sftpConnection?.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Command_ReturnsExitCodeAndBothStreams()
    {
        using RemoteConnection c = await ConnectAsync("TGK_E2E_SSH");

        CommandResult result = await c.RunAsync("echo out; echo err >&2; exit 3", Timeout, ct: Ct);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("out\n", result.Stdout);
        Assert.Equal("err\n", result.Stderr);
        Assert.False(result.TimedOut);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Command_GetsInput_AndLongOutputIsCut()
    {
        using RemoteConnection c = await ConnectAsync("TGK_E2E_SSH");

        CommandResult echoed = await c.RunAsync("cat", Timeout, input: Encoding.UTF8.GetBytes("hello ✓"), ct: Ct);
        Assert.Equal("hello ✓", echoed.Stdout);

        CommandResult flood = await c.RunAsync("head -c 300000 /dev/zero | tr '\\0' x", Timeout, maxOutput: 1000, ct: Ct);
        Assert.Equal(0, flood.ExitCode);
        Assert.True(flood.Truncated);
        Assert.Equal(1000, flood.Stdout.Length);
    }

    [Fact]
    public async Task Command_ThatRunsTooLong_IsStopped()
    {
        using RemoteConnection c = await ConnectAsync("TGK_E2E_SSH");

        CommandResult result = await c.RunAsync("echo started; sleep 30", TimeSpan.FromSeconds(1), ct: Ct);

        Assert.True(result.TimedOut);
        Assert.Null(result.ExitCode);
        Assert.True(result.Duration < TimeSpan.FromSeconds(15), $"took {result.Duration}");
        // The connection stays usable.
        Assert.Equal("ok\n", (await c.RunAsync("echo ok", Timeout, ct: Ct)).Stdout);
    }

    [Fact]
    public async Task JumpHost_CommandsAndFilesGoThroughIt()
    {
        SshConnectRequest target = Request("TGK_E2E_SSH");
        using var c = new RemoteConnection(new TrustAll());
        await c.ConnectAsync(target with { Host = "localhost", JumpChain = [target with { Name = "bastion" }] }, Ct);

        Assert.Equal("via\n", (await c.RunAsync("echo via", Timeout, ct: Ct)).Stdout);
        var files = new RemoteFiles(c);
        Assert.StartsWith("/", await files.HomeAsync(Ct));
        Assert.False(files.UsesCommands);
    }

    [Fact]
    public async Task Sftp_WriteReadListStat()
    {
        _sftpConnection = await ConnectAsync("TGK_E2E_SSH");
        var files = new RemoteFiles(_sftpConnection);
        await FilesRoundTrip(files);
        Assert.False(files.UsesCommands);
    }

    [Fact]
    public async Task WithoutSftp_FilesGoThroughCommands()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TGK_E2E_SSH_NOSFTP")), "TGK_E2E_SSH_NOSFTP is not set");
        using RemoteConnection c = await ConnectAsync("TGK_E2E_SSH_NOSFTP");
        var files = new RemoteFiles(c);
        try
        {
            await FilesRoundTrip(files);
            Assert.True(files.UsesCommands);
        }
        finally
        {
            await c.RunAsync($"rm -rf {_dir}", Timeout);
        }
    }

    [Fact]
    public async Task Write_KeepsPermissions_AndFollowsSymlinks()
    {
        _sftpConnection = await ConnectAsync("TGK_E2E_SSH");
        var files = new RemoteFiles(_sftpConnection);
        await files.CreateDirectoryAsync(_dir, Ct);
        await _sftpConnection.RunAsync($"printf old > {_dir}/script.sh && chmod 750 {_dir}/script.sh && ln -s script.sh {_dir}/link", Timeout, ct: Ct);

        await files.WriteAsync($"{_dir}/link", Encoding.UTF8.GetBytes("new"), Ct);

        CommandResult check = await _sftpConnection.RunAsync($"stat -c %a {_dir}/script.sh; cat {_dir}/script.sh; echo; [ -L {_dir}/link ] && echo link; ls -a {_dir} | grep -c tgk-", Timeout, ct: Ct);
        Assert.Equal("750\nnew\nlink\n0\n", check.Stdout);
    }

    private async Task FilesRoundTrip(RemoteFiles files)
    {
        await files.CreateDirectoryAsync($"{_dir}/sub", Ct);
        byte[] content = Encoding.UTF8.GetBytes("line 1\nline 2 ✓\n");

        await files.WriteAsync($"{_dir}/a.txt", content, Ct);
        await files.WriteAsync($"{_dir}/a.txt", content.Concat("line 3\n"u8.ToArray()).ToArray(), Ct);

        Assert.Equal("line 1\nline 2 ✓\nline 3\n", Encoding.UTF8.GetString(await files.ReadAsync($"{_dir}/a.txt", 1 << 20, Ct)));
        RemoteEntry? stat = await files.StatAsync($"{_dir}/a.txt", Ct);
        Assert.NotNull(stat);
        Assert.Equal(RemoteEntryKind.File, stat.Kind);
        Assert.Equal(content.Length + 7, stat.Size);
        Assert.Null(await files.StatAsync($"{_dir}/missing", Ct));

        (var entries, bool more) = await files.ListAsync(_dir, 10, Ct);
        Assert.False(more);
        Assert.Equal(["a.txt", "sub"], entries.Select(e => e.Name));
        Assert.Equal(RemoteEntryKind.Directory, entries[1].Kind);

        var tooLarge = await Assert.ThrowsAsync<RemoteFileException>(() => files.ReadAsync($"{_dir}/a.txt", 5, Ct));
        Assert.Contains("larger than", tooLarge.Message);
        var directory = await Assert.ThrowsAsync<RemoteFileException>(() => files.ReadAsync($"{_dir}/sub", 1 << 20, Ct));
        Assert.Contains("is a directory", directory.Message);
        await Assert.ThrowsAsync<RemoteFileException>(() => files.ReadAsync($"{_dir}/missing", 1 << 20, Ct));
    }

    private static async Task<RemoteConnection> ConnectAsync(string variable)
    {
        var connection = new RemoteConnection(new TrustAll());
        await connection.ConnectAsync(Request(variable), Ct);
        return connection;
    }

    internal static SshConnectRequest Request(string variable)
    {
        string? spec = Environment.GetEnvironmentVariable(variable);
        Assert.SkipWhen(string.IsNullOrEmpty(spec), $"{variable} is not set (user:password@host:port)");
        int at = spec!.LastIndexOf('@'), colon = spec.IndexOf(':'), portColon = spec.LastIndexOf(':');
        return new SshConnectRequest
        {
            Username = spec[..colon],
            Password = spec[(colon + 1)..at],
            Host = spec[(at + 1)..portColon],
            Port = int.Parse(spec[(portColon + 1)..], CultureInfo.InvariantCulture),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
    }

    internal sealed class TrustAll : IHostKeyVerifier
    {
        public Task<bool> VerifyAsync(HostKeyInfo info, CancellationToken ct) => Task.FromResult(true);
    }
}
