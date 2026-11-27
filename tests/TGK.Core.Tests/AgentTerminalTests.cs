using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Agents;
using TGK.Core.Models;
using TGK.Core.Ssh;
using Xunit;

namespace TGK.Core.Tests;

/// <summary>Agents and the SSH terminal tabs open in the app: listing, reading, typing and the rules for it.</summary>
public sealed class AgentTerminalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tgk-terminal-test-{Guid.NewGuid():N}"[..30]);
    private readonly FakeHost _host = new();
    private readonly AgentToolbox _toolbox;
    private readonly AgentSession _session = new("test-agent");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public AgentTerminalTests()
    {
        Directory.CreateDirectory(_dir);
        _toolbox = new AgentToolbox(_host, new AgentActivity(Path.Combine(_dir, "audit.log")));
        var identity = new Identity { Name = "id", Username = "deploy" };
        HostEntry Saved(string name, AgentAccess? access) => new() { Name = name, Host = "10.0.0.5", IdentityId = identity.Id, Options = { AgentAccess = access } };
        HostEntry full = Saved("lab", AgentAccess.Full), ask = Saved("prod", AgentAccess.Ask), ro = Saved("ro", AgentAccess.ReadOnly), off = Saved("hidden", AgentAccess.Off);
        _host.Vault = new VaultData { Identities = [identity], Hosts = [full, ask, ro, off] }; // the defaults: off
        _host.Terminals.Add(new FakeTerminal("t1", full));
        _host.Terminals.Add(new FakeTerminal("t2", ask));
        _host.Terminals.Add(new FakeTerminal("t3", ro));
        _host.Terminals.Add(new FakeTerminal("t4", off));
        _host.Terminals.Add(new FakeTerminal("t5", new HostEntry { Name = "", Host = "192.168.1.9", Username = "pi" }) { Title = "pi@raspberry: ~" });
    }

    public void Dispose()
    {
        _toolbox.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Sessions_are_listed_by_their_hosts_access_and_quick_connect_tabs_follow_the_defaults()
    {
        (string text, bool error) = await CallAsync(AgentTools.ListSessions, new());
        Assert.False(error, text);
        Assert.Contains("t1: lab; address: deploy@10.0.0.5; connected; access: full; saved host", text);
        Assert.Contains("t2: prod;", text);
        Assert.Contains("t3: ro;", text);
        Assert.DoesNotContain("t4", text);
        Assert.DoesNotContain("t5", text); // the defaults are off

        _host.Vault!.Defaults.AgentAccess = AgentAccess.Ask;
        (text, _) = await CallAsync(AgentTools.ListSessions, new());
        Assert.Contains("t5: 192.168.1.9; address: pi@192.168.1.9; connected; access: ask; Quick Connect", text);
        Assert.Contains("title: pi@raspberry: ~", text);
        Assert.DoesNotContain("t4", text); // a saved host's own setting still counts

        (text, _) = await CallAsync(AgentTools.ListHosts, new());
        Assert.Contains("terminal tab(s) open in TGK can be used too", text);
    }

    [Fact]
    public async Task Reading_shows_the_screen_cursor_and_history()
    {
        var tab = _host.Terminal("t3");
        tab.History.AddRange(["old 1", "old 2", "old 3"]);
        tab.Screen.AddRange(["deploy@ro:~$ uptime", " 10:00 up 3 days", "deploy@ro:~$ ", "", ""]);
        tab.CursorRow = 2;

        (string text, bool error) = await CallAsync(AgentTools.ReadTerminal, new() { ["session"] = "t3", ["scrollback"] = 2 });

        Assert.False(error, text);
        Assert.Contains("Terminal tab t3: ro (deploy@10.0.0.5), connected; 80x24; cursor at line 3, column 1", text);
        Assert.Contains("--- history (last 2 of 3 lines) ---\nold 2\nold 3\n--- screen ---\ndeploy@ro:~$ uptime\n 10:00 up 3 days\ndeploy@ro:~$ \n", text);
        Assert.EndsWith("deploy@ro:~$ \n", text); // blank rows below the cursor are dropped
        Assert.Equal("ro (tab t3)", _toolbox.Activity.Recent[0].Host);
    }

    [Fact]
    public async Task Hidden_and_unknown_tabs_cannot_be_used()
    {
        (string text, bool error) = await CallAsync(AgentTools.ReadTerminal, new() { ["session"] = "t4" });
        Assert.True(error);
        Assert.Contains("No open terminal tab \"t4\"", text);

        (text, error) = await CallAsync(AgentTools.RunCommand, new() { ["session"] = "t1", ["host"] = "lab", ["command"] = "id" });
        Assert.True(error);
        Assert.Contains("host or session, not both", text);

        (text, error) = await CallAsync(AgentTools.RunCommand, new() { ["command"] = "id" });
        Assert.True(error);
        Assert.Contains("session (an open terminal tab", text);
    }

    [Fact]
    public async Task Commands_over_a_tab_that_is_not_connected_fail_clearly()
    {
        _host.Terminal("t1").Connected = false;
        (string text, bool error) = await CallAsync(AgentTools.RunCommand, new() { ["session"] = "t1", ["command"] = "id" });
        Assert.True(error);
        Assert.Contains("Terminal tab t1 (lab) is not connected", text);
    }

    [Fact]
    public async Task Typing_in_ask_mode_waits_for_approval_and_returns_the_answer()
    {
        var tab = _host.Terminal("t2");
        tab.Screen.Add("deploy@prod:~$ ");

        (string text, bool error) = await CallAsync(AgentTools.SendToTerminal, new() { ["session"] = "t2", ["text"] = "ls -la", ["keys"] = new[] { "enter" } });
        Assert.True(error);
        Assert.Contains("did not approve", text);
        Assert.Empty(tab.Sent);
        ApprovalRequest asked = Assert.Single(_host.Requests);
        Assert.Equal("prod (tab t2)", asked.Host);
        Assert.Equal("Type into a terminal", asked.Action);
        Assert.Equal("ls -la [enter]", asked.Subject);

        _host.Answers.Enqueue(ApprovalAnswer.AllowOnce);
        (text, error) = await CallAsync(AgentTools.SendToTerminal, new() { ["session"] = "t2", ["text"] = "ls -la", ["keys"] = new[] { "enter" }, ["wait_seconds"] = 5 });
        Assert.False(error, text);
        Assert.Equal(("ls -la", "enter"), Assert.Single(tab.Sent));
        Assert.StartsWith("Sent.\n", text);
        Assert.Contains("output of ls -la", text);
    }

    [Fact]
    public async Task Read_only_access_reads_but_never_types()
    {
        (string text, bool error) = await CallAsync(AgentTools.SendToTerminal, new() { ["session"] = "t3", ["keys"] = new[] { "ctrl+c" } });
        Assert.True(error);
        Assert.Contains("read-only access", text);
        Assert.Empty(_host.Terminal("t3").Sent);
        Assert.Empty(_host.Requests);
    }

    [Fact]
    public async Task Full_access_types_freely_except_the_hosts_own_protected_paths()
    {
        var tab = _host.Terminal("t1");
        (string text, bool error) = await CallAsync(AgentTools.SendToTerminal, new() { ["session"] = "t1", ["text"] = "uptime\n", ["wait_seconds"] = 0 });
        Assert.False(error, text);
        Assert.Equal(("uptime\r", ""), Assert.Single(tab.Sent)); // a line break is Enter
        (_, error) = await CallAsync(AgentTools.SendToTerminal, new() { ["session"] = "t1", ["text"] = "sudo cat /home/deploy/.ssh/id_ed25519", ["wait_seconds"] = 0 });
        Assert.False(error);
        Assert.Empty(_host.Requests);

        _host.Vault!.FindHost(tab.Host.Id)!.Options.AgentProtectedPaths = ["/srv/secrets/**"];
        (_, error) = await CallAsync(AgentTools.SendToTerminal, new() { ["session"] = "t1", ["text"] = "cat /srv/secrets/db.txt" });
        Assert.True(error);
        Assert.Single(_host.Requests);
        Assert.Equal(2, tab.Sent.Count);
    }

    [Fact]
    public async Task Unknown_keys_and_empty_input_are_refused()
    {
        (string text, bool error) = await CallAsync(AgentTools.SendToTerminal, new() { ["session"] = "t1", ["keys"] = new[] { "hyper+x" } });
        Assert.True(error);
        Assert.Contains("Unknown modifier", text);
        (text, error) = await CallAsync(AgentTools.SendToTerminal, new() { ["session"] = "t1" });
        Assert.True(error);
        Assert.Contains("Give text, keys or both", text);
    }

    [Theory]
    [InlineData("enter", "enter", false, false, false)]
    [InlineData("Return", "enter", false, false, false)]
    [InlineData("esc", "escape", false, false, false)]
    [InlineData("ctrl+c", "c", true, false, false)]
    [InlineData("C-C", "c", true, false, false)]
    [InlineData("alt+b", "b", false, true, false)]
    [InlineData("shift+tab", "tab", false, false, true)]
    [InlineData("ctrl+left", "left", true, false, false)]
    [InlineData("space", " ", false, false, false)]
    [InlineData("q", "q", false, false, false)]
    [InlineData("-", "-", false, false, false)]
    [InlineData("F12", "f12", false, false, false)]
    public void Keys_are_parsed(string spec, string key, bool ctrl, bool alt, bool shift) =>
        Assert.Equal(new TerminalKeyPress(key, ctrl, alt, shift), TerminalKeyPress.Parse(spec));

    [Theory]
    [InlineData("")]
    [InlineData("ctrl+")]
    [InlineData("super+a")]
    [InlineData("enterr")]
    public void Bad_keys_are_refused(string spec) => Assert.Throws<FormatException>(() => TerminalKeyPress.Parse(spec));

    [Fact]
    public void Typing_policy()
    {
        static AgentRules Rules(AgentAccess access, params string[] paths) => new(access, [], paths);
        Assert.Equal(AgentVerdict.Deny, AgentPolicy.Type(Rules(AgentAccess.Off), "ls").Verdict);
        Assert.Equal(AgentVerdict.Deny, AgentPolicy.Type(Rules(AgentAccess.ReadOnly), "ls").Verdict);
        Assert.Equal(AgentVerdict.Ask, AgentPolicy.Type(Rules(AgentAccess.Ask), "ls").Verdict);
        Assert.Equal(AgentVerdict.Ask, AgentPolicy.Type(Rules(AgentAccess.Ask), "").Verdict);
        Assert.Equal(AgentVerdict.Allow, AgentPolicy.Type(Rules(AgentAccess.Full), "systemctl restart nginx").Verdict);
        Assert.Equal(AgentVerdict.Allow, AgentPolicy.Type(Rules(AgentAccess.Full), "").Verdict);
        Assert.Equal(AgentVerdict.Allow, AgentPolicy.Type(Rules(AgentAccess.Full), "sudo -i").Verdict);
        Assert.Equal(AgentVerdict.Allow, AgentPolicy.Type(Rules(AgentAccess.Full), "cat ~/.ssh/config").Verdict);
        Assert.Equal(AgentVerdict.Allow, AgentPolicy.Type(Rules(AgentAccess.Full), "cp key.pem /tmp").Verdict);
        Assert.Equal(AgentVerdict.Ask, AgentPolicy.Type(Rules(AgentAccess.Full, "/srv/secrets/**"), "vim /srv/secrets/app.yml").Verdict);
        Assert.Equal(AgentVerdict.Allow, AgentPolicy.Type(Rules(AgentAccess.Full, "/srv/secrets/**"), "vim /srv/app.yml").Verdict);
    }

    private async Task<(string Text, bool Error)> CallAsync(string tool, Dictionary<string, object> args)
    {
        JsonElement json = JsonSerializer.SerializeToElement(args);
        ToolOutcome outcome = await _toolbox.CallAsync(_session, tool, json, Ct);
        return (outcome.Text, outcome.IsError);
    }

    private sealed class FakeHost : IAgentHost
    {
        public VaultData? Vault { get; set; }

        public List<FakeTerminal> Terminals { get; } = [];

        public ConcurrentQueue<ApprovalAnswer> Answers { get; } = new();

        public List<ApprovalRequest> Requests { get; } = [];

        public FakeTerminal Terminal(string id) => Terminals.Single(t => t.Id == id);

        public Task<IReadOnlyList<IAgentTerminal>> TerminalsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IAgentTerminal>>([.. Terminals]);

        public Task<RemoteConnection> ConnectAsync(HostEntry host, SshConnectRequest request, CancellationToken ct) =>
            throw new SshSessionException(SshErrorKind.HostUnreachable, "No network in this test.");

        public Task<string?> SudoPasswordAsync(HostEntry host, SshConnectRequest request, string? error, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<ApprovalAnswer> ApproveAsync(ApprovalRequest request, CancellationToken ct)
        {
            lock (Requests)
                Requests.Add(request);
            return Task.FromResult(Answers.TryDequeue(out ApprovalAnswer answer) ? answer : ApprovalAnswer.Deny);
        }
    }

    /// <summary>A terminal that answers every input with one line of output, a moment later.</summary>
    private sealed class FakeTerminal(string id, HostEntry host) : IAgentTerminal
    {
        private long _output;

        public string Id { get; } = id;
        public string Title { get; set; } = host.DisplayName;
        public HostEntry Host { get; } = host;
        public bool Connected { get; set; } = true;
        public bool IsConnected => Connected;
        public long OutputVersion => Interlocked.Read(ref _output);
        public List<string> Screen { get; } = [];
        public List<string> History { get; } = [];
        public int CursorRow { get; set; }
        public List<(string Text, string Keys)> Sent { get; } = [];

        public Task<TerminalSnapshot> ReadAsync(int scrollbackLines, CancellationToken ct)
        {
            lock (Screen)
            {
                return Task.FromResult(new TerminalSnapshot([.. Screen], History.TakeLast(scrollbackLines).ToList(), History.Count,
                    CursorRow, 0, 80, 24, false));
            }
        }

        public Task<bool> SendAsync(string text, IReadOnlyList<TerminalKeyPress> keys, CancellationToken ct)
        {
            if (!Connected)
                return Task.FromResult(false);
            Sent.Add((text, string.Join(' ', keys)));
            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                lock (Screen)
                {
                    Screen.Add($"output of {text}");
                    CursorRow = Screen.Count - 1;
                }
                Interlocked.Increment(ref _output);
            });
            return Task.FromResult(true);
        }

        public RemoteConnection? AttachConnection() => null;
    }
}
