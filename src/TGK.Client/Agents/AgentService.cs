using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blossom;
using TGK.Client.Dialogs;
using TGK.Client.Platform;
using TGK.Client.Views;
using TGK.Core.Agents;
using TGK.Core.Models;
using TGK.Core.Ssh;

namespace TGK.Client.Agents;

/// <summary>
/// Agents (MCP) in the app: serves <see cref="AgentToolbox"/> on the local endpoint while "Allow agents" is on, asks
/// the user for approvals, passwords and host keys with the usual dialogs, and tells the UI what agents are doing.
/// See docs/AGENTS.md.
/// </summary>
public sealed class AgentService : IAgentHost, IAsyncDisposable
{
    /// <summary>How long an approval waits for the user before it counts as a refusal.</summary>
    public static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(2);

    private readonly TgkApplication _app;
    private readonly Dictionary<string, string> _passwords = []; // typed for agents in this run, by user@host:port
    private readonly Lock _gate = new();
    private readonly LinkedList<(AgentActivityEntry Entry, string Result)> _transcript = new();
    private AgentEndpoint? _endpoint;

    /// <summary>Calls kept for the agent log tab, with the text the agent got back (cut at <see cref="MaxTranscriptChars"/>).</summary>
    public const int MaxTranscript = 200, MaxTranscriptChars = 16_000;

    public AgentService(TgkApplication app)
    {
        _app = app;
        Activity = new AgentActivity(AgentActivity.DefaultLogPath);
        Toolbox = new AgentToolbox(this, Activity);
        Activity.Added += _ => Notify();
        Toolbox.RunningChanged += Notify;
        Toolbox.Pool.Changed += Notify;
        Toolbox.CallFinished += (entry, result) =>
        {
            string kept = result.Length > MaxTranscriptChars ? result[..MaxTranscriptChars] + "\n[…]\n" : result;
            lock (_gate)
            {
                _transcript.AddLast((entry, kept));
                while (_transcript.Count > MaxTranscript)
                    _transcript.RemoveFirst();
            }
            UiThread.Post(() => CallFinished?.Invoke(entry, kept));
        };
    }

    /// <summary>Raised on the UI thread after each agent call, with the text the agent got back.</summary>
    public event Action<AgentActivityEntry, string>? CallFinished;

    /// <summary>The recent calls with their results, oldest first (for the agent log tab).</summary>
    public IReadOnlyList<(AgentActivityEntry Entry, string Result)> Transcript
    {
        get
        {
            lock (_gate)
                return [.. _transcript];
        }
    }

    public AgentActivity Activity { get; }

    public AgentToolbox Toolbox { get; }

    /// <summary>Serving agents now.</summary>
    public bool IsRunning => _endpoint is not null;

    /// <summary>Why agents could not be served (the endpoint failed to start), or null.</summary>
    public string? Error { get; private set; }

    /// <summary>The agents connected now.</summary>
    public IReadOnlyList<AgentSession> Sessions => _endpoint?.Sessions ?? [];

    /// <summary>Raised on the UI thread when sessions, running calls, connections or the activity changed.</summary>
    public event Action? Changed;

    /// <summary>The command an MCP client runs to reach TGK: <c>tgk-mcp</c> next to TGK (the AppImage with <c>--mcp</c>).</summary>
    public static (string Command, string[] Args) BridgeCommand
    {
        get
        {
            if (Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage && File.Exists(appImage))
                return (appImage, ["--mcp"]);
            return (Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "tgk-mcp.exe" : "tgk-mcp"), []);
        }
    }

    /// <summary>The Claude Code command that registers TGK for all of the user's projects.</summary>
    public static string ClaudeSetupCommand
    {
        get
        {
            (string command, string[] args) = BridgeCommand;
            string quoted = command.Contains(' ') ? $"\"{command}\"" : command;
            return $"claude mcp add --scope user tgk -- {quoted}{string.Concat(args.Select(a => " " + a))}";
        }
    }

    /// <summary>The <c>mcpServers</c> entry for clients configured with JSON (<c>.mcp.json</c>, Claude Desktop, …).</summary>
    public static string JsonConfig
    {
        get
        {
            (string command, string[] args) = BridgeCommand;
            string json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["mcpServers"] = new Dictionary<string, object> { ["tgk"] = new { command, args } },
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            return json;
        }
    }

    /// <summary>Starts or stops serving to match the preference. UI thread.</summary>
    public async Task ApplyAsync(bool enabled)
    {
        if (enabled && _endpoint is null)
        {
            var endpoint = new AgentEndpoint(Toolbox);
            try
            {
                endpoint.Start();
                endpoint.SessionsChanged += Notify;
                _endpoint = endpoint;
                Error = null;
                Log.Info($"Serving agents on {endpoint.SocketPath}.");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
            {
                Error = ex.Message;
                Log.Warning($"Could not serve agents: {ex.Message}");
                await endpoint.DisposeAsync();
            }
        }
        else if (!enabled && _endpoint is { } running)
        {
            _endpoint = null;
            Error = null;
            running.SessionsChanged -= Notify;
            await running.DisposeAsync();
            Toolbox.Pool.CloseAll();
            Log.Info("Stopped serving agents.");
        }
        Changed?.Invoke();
    }

    /// <summary>Ends the agents' sessions and connections (sign-out, lock).</summary>
    public void OnSignedOut()
    {
        _endpoint?.DisconnectAll();
        Toolbox.Pool.CloseAll();
        lock (_gate)
            _passwords.Clear();
    }

    /// <summary>Ends the agents' sessions; they may reconnect.</summary>
    public void DisconnectAll() => _endpoint?.DisconnectAll();

    public async ValueTask DisposeAsync()
    {
        if (_endpoint is { } endpoint)
        {
            _endpoint = null;
            await endpoint.DisposeAsync().ConfigureAwait(false); // also at exit, when the UI thread no longer runs continuations
        }
        Toolbox.Dispose();
    }

    private void Notify() => UiThread.Post(() => Changed?.Invoke());

    // ---- IAgentHost (called on worker threads) ----

    public VaultData? Vault => _app.Services.Vault.IsLoggedIn ? _app.Services.Vault.Current : null;

    public async Task<RemoteConnection> ConnectAsync(HostEntry host, SshConnectRequest request, CancellationToken ct)
    {
        request = await CompleteAsync(host, request, ct).ConfigureAwait(false);
        for (int attempt = 0; ; attempt++)
        {
            var connection = new RemoteConnection(
                new KnownHostsVerifier(_app.Services.Vault, (info, token) => AskAsync(view => HostKeyDialog.ShowAsync(view, info, token, "for an agent"), false)),
                (prompt, token) => AskAsync(view => SignInPromptDialog.ShowAsync(view,
                    $"{prompt.Username}@{(prompt.Port == 22 ? prompt.Host : $"{prompt.Host}:{prompt.Port}")} (for an agent)", prompt, true, token), (string?)null));
            try
            {
                await connection.ConnectAsync(request, ct).ConfigureAwait(false);
                return connection;
            }
            catch (SshSessionException ex) when (ex.Kind == SshErrorKind.AuthenticationFailed && attempt < 2 && !ct.IsCancellationRequested
                && ex.JumpHostIndex is null && string.IsNullOrWhiteSpace(request.PrivateKey) && host.IdentityId is null)
            {
                // A password typed for the agent was wrong: ask again.
                connection.Dispose();
                lock (_gate)
                    _passwords.Remove(Key(request));
                request = await CompleteAsync(host, request with { Password = null }, ct, ex.Message).ConfigureAwait(false);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }
    }

    public async Task<ApprovalAnswer> ApproveAsync(ApprovalRequest request, CancellationToken ct)
    {
        try
        {
            return await AskAsync(view => AgentApprovalDialog.ShowAsync(view, request, ApprovalTimeout, ct), ApprovalAnswer.Deny).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ApprovalAnswer.Deny;
        }
    }

    public async Task<string?> SudoPasswordAsync(HostEntry host, SshConnectRequest request, string? error, CancellationToken ct)
    {
        string key = "sudo|" + Key(request);
        lock (_gate)
        {
            if (error is null && _passwords.TryGetValue(key, out string? remembered))
                return remembered;
            if (error is not null)
                _passwords.Remove(key);
        }
        // The account password is usually the sudo password: try the saved one first.
        if (error is null && !string.IsNullOrEmpty(request.Password))
            return request.Password;
        string target = $"{request.Username}@{(request.Port == 22 ? request.Host : $"{request.Host}:{request.Port}")}";
        string? password = await AskAsync(view => new PromptDialog(view, "sudo password", target,
            $"An agent wants to run a command as root on {host.DisplayName}. Enter the sudo password of {request.Username}; it is kept until TGK closes or you sign out.",
            "Password", "Continue", isPassword: true, error: error, guardEnter: true).ShowAsync(ct), (string?)null).ConfigureAwait(false);
        if (password is not null)
        {
            lock (_gate)
                _passwords[key] = password;
        }
        return password;
    }

    /// <summary>Asks for what the vault lacks to sign in (usernames, passwords of the host and its jump hosts).</summary>
    private async Task<SshConnectRequest> CompleteAsync(HostEntry host, SshConnectRequest request, CancellationToken ct, string? error = null)
    {
        var hops = new List<SshConnectRequest>();
        foreach (SshConnectRequest hop in request.JumpChain)
            hops.Add(await CompleteHopAsync(hop, $"jump host {hop.Name ?? hop.Host} of {host.DisplayName}", null, ct).ConfigureAwait(false));
        return await CompleteHopAsync(request, host.DisplayName, error, ct).ConfigureAwait(false) with { JumpChain = hops };
    }

    private async Task<SshConnectRequest> CompleteHopAsync(SshConnectRequest hop, string what, string? error, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hop.Username))
        {
            string? user = await AskAsync(view => new PromptDialog(view, "Username required", what,
                $"An agent wants to connect to {what}, which has no saved username.", "Username", "Continue", guardEnter: true).ShowAsync(ct), (string?)null).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(user))
                throw new OperationCanceledException("No username was entered.");
            hop = hop with { Username = user.Trim() };
        }
        if (!string.IsNullOrWhiteSpace(hop.PrivateKey) || !string.IsNullOrEmpty(hop.Password))
            return hop;
        lock (_gate)
        {
            if (error is null && _passwords.TryGetValue(Key(hop), out string? remembered))
                return hop with { Password = remembered };
        }
        string target = $"{hop.Username}@{(hop.Port == 22 ? hop.Host : $"{hop.Host}:{hop.Port}")}";
        string? password = await AskAsync(view => new PromptDialog(view, "Password required", target,
            $"An agent wants to connect to {what}, which has no saved password. It is used for the agent's connection only and kept until TGK closes or you sign out.",
            "Password", "Connect", isPassword: true, error: error, guardEnter: true).ShowAsync(ct), (string?)null).ConfigureAwait(false);
        if (password is null)
            throw new OperationCanceledException("No password was entered.");
        lock (_gate)
            _passwords[Key(hop)] = password;
        return hop with { Password = password };
    }

    private static string Key(SshConnectRequest r) => $"{r.Username}@{r.Host}:{r.Port}";

    /// <summary>Shows a dialog on the main view (drawing attention to the window); <paramref name="fallback"/> when TGK is locked.</summary>
    private Task<T> AskAsync<T>(Func<MainView, Task<T>> show, T fallback) => UiThread.InvokeAsync(() =>
    {
        if (_app.Main is not { } main || !_app.Services.Vault.IsLoggedIn)
            return Task.FromResult(fallback);
        AppWindow.RequestAttention();
        return show(main);
    });
}
