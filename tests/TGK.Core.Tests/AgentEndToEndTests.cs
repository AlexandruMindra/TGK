using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using TGK.Core.Agents;
using TGK.Core.Models;
using TGK.Core.Ssh;
using Xunit;

namespace TGK.Core.Tests;

/// <summary>
/// The whole agent path: an MCP client starts the real <c>tgk-mcp</c>, which relays to an <see cref="AgentEndpoint"/>
/// serving an <see cref="AgentToolbox"/>. Tests that reach a host need the E2E SSH server (<c>TGK_E2E_SSH</c>, see
/// docs/DEV-TESTING.md); the others run anywhere.
/// </summary>
public sealed class AgentEndToEndTests : IAsyncLifetime
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), $"tgk-agent-test-{Guid.NewGuid():N}"[..28]);
    private readonly FakeAgentHost _host = new();
    private AgentToolbox? _toolbox;
    private AgentEndpoint? _endpoint;
    private McpClient? _client;
    private readonly string _remoteDir = $"/tmp/tgk-agent-e2e-{Guid.NewGuid():N}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // tgk-mcp finds the endpoint in the config directory: XDG_CONFIG_HOME/tgk (APPDATA\TGK on Windows).
    private string ConfigDir => OperatingSystem.IsWindows() ? Path.Combine(_config, "TGK") : Path.Combine(_config, "tgk");

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(ConfigDir);
        _toolbox = new AgentToolbox(_host, new AgentActivity(Path.Combine(ConfigDir, "audit.log")));
        _endpoint = new AgentEndpoint(_toolbox, Path.Combine(ConfigDir, "agent.json"), Path.Combine(ConfigDir, "agent.sock"));
        _endpoint.Start();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
        if (_endpoint is not null)
            await _endpoint.DisposeAsync();
        _toolbox?.Dispose();
        try
        {
            Directory.Delete(_config, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<McpClient> StartClientAsync()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "tgk-mcp.exe" : "tgk-mcp");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "tgk",
            Command = exe,
            ShutdownTimeout = TimeSpan.FromSeconds(1), // how long disposing the client waits for the process
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["XDG_CONFIG_HOME"] = _config,
                ["APPDATA"] = _config,
                ["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            },
        });
        _client = await McpClient.CreateAsync(transport, new McpClientOptions { ClientInfo = new Implementation { Name = "test-agent", Version = "1.0" } }, cancellationToken: Ct);
        return _client;
    }

    private async Task<(string Text, bool IsError)> CallAsync(string tool, Dictionary<string, object?> args)
    {
        CallToolResult result = await _client!.CallToolAsync(tool, args, cancellationToken: Ct);
        return (string.Join("", result.Content.OfType<TextContentBlock>().Select(t => t.Text)), result.IsError == true);
    }

    [Fact]
    public async Task Tools_are_listed_and_hosts_filtered_by_access()
    {
        _host.Vault = Vault("127.0.0.1", 22);
        McpClient client = await StartClientAsync();

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: Ct);
        Assert.Equal(AgentTools.All.Select(t => t.Name).Order(), tools.Select(t => t.Name).Order());
        Assert.Equal("tgk", client.ServerInfo.Name);

        (string text, bool error) = await CallAsync(AgentTools.ListHosts, []);
        Assert.False(error);
        Assert.Contains("lab; address: test@127.0.0.1; access: full", text);
        Assert.Contains("prod; address: test@127.0.0.1; access: ask; group: Production", text);
        Assert.Contains("ro; address: test@127.0.0.1; access: read-only", text);
        Assert.DoesNotContain("hidden", text);
        Assert.DoesNotContain("secret-password", text);

        (text, error) = await CallAsync(AgentTools.ReadFile, new() { ["host"] = "hidden", ["path"] = "/etc/hosts" });
        Assert.True(error);
        Assert.Contains("no access to hidden", text);
        Assert.Equal(AgentOutcome.Denied, _toolbox!.Activity.Recent[0].Outcome);
        Assert.Equal("test-agent 1.0", _toolbox.Activity.Recent[0].Client);
        Assert.Single(_endpoint!.Sessions);
    }

    [Fact]
    public async Task A_wrong_token_is_refused()
    {
        string info = Path.Combine(_config, "forged.json");
        await File.WriteAllTextAsync(info, $$"""{ "socket": "{{_endpoint!.SocketPath.Replace("\\", "\\\\")}}", "token": "nope", "pid": 1 }""", Ct);
        var log = new StringWriter();

        int code = await AgentBridge.RunAsync(new MemoryStream(), new MemoryStream(), log, info, Ct);

        Assert.Equal(AgentBridge.ExitRefused, code);
        Assert.Contains("token is wrong", log.ToString());
        Assert.Equal(AgentBridge.ExitNotRunning, await AgentBridge.RunAsync(new MemoryStream(), new MemoryStream(), log, Path.Combine(_config, "none.json"), Ct));
    }

    [Fact]
    public async Task An_agent_hanging_up_cancels_its_calls_and_their_prompts()
    {
        _host.Vault = Vault("127.0.0.1", 22);
        var entered = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        _host.OnConnect = async ct =>
        {
            // Like a host key prompt nobody answers.
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally
            {
                cancelled.TrySetResult();
            }
        };
        McpClient client = await StartClientAsync();
        Task<CallToolResult> call = client.CallToolAsync(AgentTools.ReadFile, new Dictionary<string, object?> { ["host"] = "lab", ["path"] = "/etc/hosts" }, cancellationToken: Ct).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Single(_endpoint!.Sessions);

        await client.DisposeAsync();
        _client = null;

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        for (int i = 0; i < 50 && _endpoint.Sessions.Count > 0; i++)
            await Task.Delay(100, Ct);
        Assert.Empty(_endpoint.Sessions);
        _ = call.Exception; // the client went away mid-call
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Agent_reads_searches_and_edits_files_and_runs_commands()
    {
        SshConnectRequest target = RemoteConnectionEndToEndTests.Request("TGK_E2E_SSH");
        _host.Vault = Vault(target.Host, target.Port, target.Username, target.Password);
        await StartClientAsync();
        try
        {
            (string text, bool error) = await CallAsync(AgentTools.RunCommand, new() { ["host"] = "lab", ["command"] = $"mkdir -p {_remoteDir}/src && printf 'alpha\\nbeta\\ngamma\\n' > {_remoteDir}/src/app.conf && echo done" });
            Assert.False(error, text);
            Assert.Equal("exit code: 0\n--- stdout ---\ndone\n", text);

            (text, error) = await CallAsync(AgentTools.ReadFile, new() { ["host"] = "lab", ["path"] = $"{_remoteDir}/src/app.conf" });
            Assert.False(error, text);
            Assert.Equal("     1\talpha\n     2\tbeta\n     3\tgamma\n", text);

            (text, _) = await CallAsync(AgentTools.Glob, new() { ["host"] = "lab", ["pattern"] = "**/*.conf", ["path"] = _remoteDir });
            Assert.Equal($"{_remoteDir}/src/app.conf\n", text);
            (text, _) = await CallAsync(AgentTools.Grep, new() { ["host"] = "lab", ["pattern"] = "^b", ["path"] = _remoteDir });
            Assert.Contains("app.conf:2:beta", text);
            (text, _) = await CallAsync(AgentTools.ListDir, new() { ["host"] = "lab", ["path"] = $"{_remoteDir}/src" });
            Assert.Contains("app.conf", text);

            (text, error) = await CallAsync(AgentTools.EditFile, new() { ["host"] = "lab", ["path"] = $"{_remoteDir}/src/app.conf", ["old_string"] = "beta", ["new_string"] = "BETA" });
            Assert.False(error, text);
            Assert.Contains("2\tBETA", text);

            // A change made behind the agent's back must be read again first.
            await CallAsync(AgentTools.RunCommand, new() { ["host"] = "lab", ["command"] = $"echo delta >> {_remoteDir}/src/app.conf" });
            (text, error) = await CallAsync(AgentTools.EditFile, new() { ["host"] = "lab", ["path"] = $"{_remoteDir}/src/app.conf", ["old_string"] = "gamma", ["new_string"] = "GAMMA" });
            Assert.True(error);
            Assert.Contains("changed since it was read", text);

            // Unread files can't be overwritten; new ones can be created.
            (text, error) = await CallAsync(AgentTools.WriteFile, new() { ["host"] = "lab", ["path"] = $"{_remoteDir}/other.txt", ["content"] = "new\n" });
            Assert.False(error, text);
            (text, error) = await CallAsync(AgentTools.WriteFile, new() { ["host"] = "lab", ["path"] = $"{_remoteDir}/src/app.conf", ["content"] = "x" });
            Assert.True(error);
            Assert.Contains("Read", text);

            string local = Path.Combine(_config, "down.txt");
            (text, error) = await CallAsync(AgentTools.Download, new() { ["host"] = "lab", ["remote_path"] = $"{_remoteDir}/other.txt", ["local_path"] = local });
            Assert.False(error, text);
            Assert.Equal("new\n", await File.ReadAllTextAsync(local, Ct));
            (text, error) = await CallAsync(AgentTools.Upload, new() { ["host"] = "lab", ["local_path"] = local, ["remote_path"] = $"{_remoteDir}/up/copy.txt" });
            Assert.False(error, text);
            (text, _) = await CallAsync(AgentTools.Stat, new() { ["host"] = "lab", ["path"] = $"{_remoteDir}/up/copy.txt" });
            Assert.Contains("size: 4 bytes", text);

            (text, error) = await CallAsync(AgentTools.RunCommand, new() { ["host"] = "lab", ["command"] = "sleep 5", ["timeout_seconds"] = 1 });
            Assert.True(error);
            Assert.StartsWith("Stopped after 1 s", text);
        }
        finally
        {
            await CallAsync(AgentTools.RunCommand, new() { ["host"] = "lab", ["command"] = $"rm -rf {_remoteDir}" });
        }
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Approvals_and_read_only_access_are_enforced()
    {
        SshConnectRequest target = RemoteConnectionEndToEndTests.Request("TGK_E2E_SSH");
        _host.Vault = Vault(target.Host, target.Port, target.Username, target.Password);
        await StartClientAsync();
        try
        {
            // Ask: a read-only command runs at once; others wait for the user.
            (string text, bool error) = await CallAsync(AgentTools.RunCommand, new() { ["host"] = "prod", ["command"] = "echo hello" });
            Assert.False(error, text);
            Assert.Empty(_host.Requests);

            _host.Answers.Enqueue(ApprovalAnswer.Deny);
            (text, error) = await CallAsync(AgentTools.RunCommand, new() { ["host"] = "prod", ["command"] = $"mkdir {_remoteDir}" });
            Assert.True(error);
            Assert.Contains("did not approve", text);
            ApprovalRequest asked = Assert.Single(_host.Requests);
            Assert.Equal(("prod", "Run a command", $"mkdir {_remoteDir}", "test-agent 1.0"), (asked.Host, asked.Action, asked.Subject, asked.Client));

            _host.Answers.Enqueue(ApprovalAnswer.AllowSession);
            (text, error) = await CallAsync(AgentTools.RunCommand, new() { ["host"] = "prod", ["command"] = $"mkdir {_remoteDir}" });
            Assert.False(error, text);
            await CallAsync(AgentTools.RunCommand, new() { ["host"] = "prod", ["command"] = $"rmdir {_remoteDir}" }); // asks: denied (no answer queued)
            _host.Answers.Enqueue(ApprovalAnswer.AllowOnce);
            (text, error) = await CallAsync(AgentTools.WriteFile, new() { ["host"] = "prod", ["path"] = $"{_remoteDir}/f.txt", ["content"] = "one\ntwo\n" });
            Assert.False(error, text);
            Assert.Equal("Create a file", _host.Requests.Last().Action);
            Assert.Contains("+one", _host.Requests.Last().Detail);
            Assert.Equal(AgentOutcome.Ok, _toolbox!.Activity.Recent[0].Outcome);
            Assert.Equal("user", _toolbox.Activity.Recent[0].ApprovedBy);

            // sudo: always asked (here passwordless: the server runs as root), never in read-only mode.
            _host.Answers.Enqueue(ApprovalAnswer.AllowOnce);
            (text, error) = await CallAsync(AgentTools.RunCommand, new() { ["host"] = "prod", ["command"] = "id -u", ["sudo"] = true });
            Assert.False(error, text);
            Assert.Contains("--- stdout ---\n0\n", text);
            Assert.Equal("Run a command as root (sudo)", _host.Requests.Last().Action);

            // Read only: reading works, changing does not, and nobody is asked.
            int asks = _host.Requests.Count;
            (text, error) = await CallAsync(AgentTools.ReadFile, new() { ["host"] = "ro", ["path"] = $"{_remoteDir}/f.txt" });
            Assert.False(error, text);
            (text, error) = await CallAsync(AgentTools.EditFile, new() { ["host"] = "ro", ["path"] = $"{_remoteDir}/f.txt", ["old_string"] = "one", ["new_string"] = "1" });
            Assert.True(error);
            Assert.Contains("read-only", text);
            (text, error) = await CallAsync(AgentTools.RunCommand, new() { ["host"] = "ro", ["command"] = $"rm -rf {_remoteDir}" });
            Assert.True(error);
            (text, error) = await CallAsync(AgentTools.RunCommand, new() { ["host"] = "ro", ["command"] = "id", ["sudo"] = true });
            Assert.True(error);
            Assert.Contains("sudo is not available", text);
            Assert.Equal(asks, _host.Requests.Count);
        }
        finally
        {
            _host.Answers.Enqueue(ApprovalAnswer.AllowOnce);
            await CallAsync(AgentTools.RunCommand, new() { ["host"] = "lab", ["command"] = $"rm -rf {_remoteDir}" });
        }
    }

    private static VaultData Vault(string host, int port, string user = "test", string? password = "secret-password")
    {
        var identity = new Identity { Name = "test", Username = user, Password = password };
        var production = new HostGroup { Name = "Production", Options = { AgentAccess = AgentAccess.Ask } };
        HostEntry Host(string name, AgentAccess? access, Guid? group = null) => new()
        {
            Name = name, Host = host, Port = port, IdentityId = identity.Id, GroupId = group, Options = { AgentAccess = access },
        };
        return new VaultData
        {
            Identities = [identity],
            Groups = [production],
            Hosts = [Host("lab", AgentAccess.Full), Host("prod", null, production.Id), Host("ro", AgentAccess.ReadOnly), Host("hidden", null)],
        };
    }

    private sealed class FakeAgentHost : IAgentHost
    {
        public VaultData? Vault { get; set; }

        /// <summary>Answers to give, in order; with none left the request is denied.</summary>
        public ConcurrentQueue<ApprovalAnswer> Answers { get; } = new();

        public List<ApprovalRequest> Requests { get; } = [];

        /// <summary>Runs before connecting (e.g. to simulate a prompt that waits).</summary>
        public Func<CancellationToken, Task>? OnConnect { get; set; }

        public async Task<RemoteConnection> ConnectAsync(HostEntry host, SshConnectRequest request, CancellationToken ct)
        {
            if (OnConnect is { } hook)
                await hook(ct);
            var connection = new RemoteConnection(new RemoteConnectionEndToEndTests.TrustAll());
            try
            {
                await connection.ConnectAsync(request, ct);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
            return connection;
        }

        public Task<string?> SudoPasswordAsync(HostEntry host, SshConnectRequest request, string? error, CancellationToken ct) =>
            Task.FromResult(error is null ? request.Password : null);

        public Task<ApprovalAnswer> ApproveAsync(ApprovalRequest request, CancellationToken ct)
        {
            lock (Requests)
                Requests.Add(request);
            return Task.FromResult(Answers.TryDequeue(out ApprovalAnswer answer) ? answer : ApprovalAnswer.Deny);
        }
    }
}
