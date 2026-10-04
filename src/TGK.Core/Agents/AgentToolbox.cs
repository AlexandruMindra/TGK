using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Models;
using TGK.Core.Ssh;

namespace TGK.Core.Agents;

public enum ApprovalAnswer
{
    Deny,
    AllowOnce,

    /// <summary>Allow this and the same request again for the rest of the agent's session.</summary>
    AllowSession,
}

/// <summary>An operation waiting for the user's approval.</summary>
/// <param name="Action">What kind of thing is asked, e.g. "Run a command", "Change a file".</param>
/// <param name="Subject">The command or the path.</param>
/// <param name="Detail">More to look at: the diff of a file change, the content of a new file. Null when none.</param>
/// <param name="Reason">Why approval is needed (from the policy).</param>
public sealed record ApprovalRequest(string Client, string Host, string Action, string Subject, string? Detail, string Reason);

/// <summary>What the toolbox needs from the application around it (the GUI, or a headless runner).</summary>
public interface IAgentHost
{
    /// <summary>The vault as it is now; null while it is locked or nobody is signed in.</summary>
    VaultData? Vault { get; }

    /// <summary>
    /// Opens a connection to <paramref name="host"/> with <paramref name="request"/> (its effective options and jump
    /// hosts), asking the user for what the vault lacks (a password, an unknown host key, a one-time code).
    /// </summary>
    Task<RemoteConnection> ConnectAsync(HostEntry host, SshConnectRequest request, CancellationToken ct);

    /// <summary>Asks the user; no answer within a reasonable time is <see cref="ApprovalAnswer.Deny"/>.</summary>
    Task<ApprovalAnswer> ApproveAsync(ApprovalRequest request, CancellationToken ct);

    /// <summary>
    /// The sudo password of the host's user (the saved password, one typed earlier, or asked now); null when the user
    /// declined. <paramref name="error"/> says why a previous one did not work (it is asked again then).
    /// </summary>
    Task<string?> SudoPasswordAsync(HostEntry host, SshConnectRequest request, string? error, CancellationToken ct);
}

/// <summary>One agent's connection to TGK (one MCP session): its name, approvals and the files it has read.</summary>
public sealed class AgentSession
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _approved = [];
    private readonly Dictionary<string, string> _read = [];

    public AgentSession(string client = "agent") => Client = client;

    /// <summary>The MCP client's name and version, once known.</summary>
    public string Client { get; set; }

    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;

    /// <summary>Calls running at once (more wait).</summary>
    internal SemaphoreSlim Calls { get; } = new(4, 4);

    internal bool IsApproved(string key)
    {
        lock (_gate)
            return _approved.Contains(key);
    }

    internal void Approve(string key)
    {
        lock (_gate)
            _approved.Add(key);
    }

    internal string? ReadHash(Guid host, string path)
    {
        lock (_gate)
            return _read.TryGetValue($"{host}|{path}", out string? hash) ? hash : null;
    }

    internal void SetRead(Guid host, string path, string hash)
    {
        lock (_gate)
            _read[$"{host}|{path}"] = hash;
    }
}

/// <summary>The result of a tool call: text for the agent, and whether it is an error.</summary>
public sealed record ToolOutcome(string Text, bool IsError)
{
    public static ToolOutcome Ok(string text) => new(text, false);
    public static ToolOutcome Error(string text) => new(text, true);
}

/// <summary>
/// The tools agents call (<see cref="AgentTools"/>): finds the host, asks the policy, gets the user's approval when
/// needed, does the work over a pooled connection and records it. Independent of the MCP transport.
/// </summary>
public sealed class AgentToolbox : IDisposable
{
    public const long MaxFileBytes = 5 * 1024 * 1024;
    public const long MaxTransferBytes = 50 * 1024 * 1024;
    public const int MaxReadLines = 2000;
    public const int MaxLineLength = 2000;
    public const int MaxListEntries = 1000;
    public const int MaxGlobResults = 500;
    private const int MaxResultChars = 200_000;
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxCommandTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(60);

    private readonly IAgentHost _host;
    private readonly ConnectionPool _pool;
    private int _running;

    public AgentToolbox(IAgentHost host, AgentActivity activity)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _pool = new ConnectionPool(ConnectAsync);
    }

    public AgentActivity Activity { get; }

    public ConnectionPool Pool => _pool;

    /// <summary>Calls running now (including ones waiting for approval or a connection).</summary>
    public int Running => Volatile.Read(ref _running);

    /// <summary>Raised (on any thread) when a call started or ended.</summary>
    public event Action? RunningChanged;

    /// <summary>Raised (on any thread) after each call with its record and the text the agent got back.</summary>
    public event Action<AgentActivityEntry, string>? CallFinished;

    public void Dispose() => _pool.Dispose();

    /// <summary>Runs tool <paramref name="name"/> for <paramref name="session"/>; never throws except for cancellation.</summary>
    public async Task<ToolOutcome> CallAsync(AgentSession session, string name, JsonElement arguments, CancellationToken ct)
    {
        var call = new Call(this, session, name, arguments);
        await session.Calls.WaitAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _running);
        RunningChanged?.Invoke();
        try
        {
            ToolOutcome outcome = name switch
            {
                AgentTools.ListHosts => ListHosts(call),
                AgentTools.RunCommand => await RunCommandAsync(call, ct).ConfigureAwait(false),
                AgentTools.ReadFile => await ReadFileAsync(call, ct).ConfigureAwait(false),
                AgentTools.ListDir => await ListDirAsync(call, ct).ConfigureAwait(false),
                AgentTools.Stat => await StatAsync(call, ct).ConfigureAwait(false),
                AgentTools.Glob => await GlobAsync(call, ct).ConfigureAwait(false),
                AgentTools.Grep => await GrepAsync(call, ct).ConfigureAwait(false),
                AgentTools.EditFile => await EditFileAsync(call, ct).ConfigureAwait(false),
                AgentTools.WriteFile => await WriteFileAsync(call, ct).ConfigureAwait(false),
                AgentTools.Upload => await UploadAsync(call, ct).ConfigureAwait(false),
                AgentTools.Download => await DownloadAsync(call, ct).ConfigureAwait(false),
                _ => throw new ToolException($"Unknown tool {name}."),
            };
            return call.Finish(outcome);
        }
        catch (ToolException ex)
        {
            return call.Finish(ToolOutcome.Error(ex.Message), ex.Denied ? AgentOutcome.Denied : AgentOutcome.Failed);
        }
        catch (RemoteFileException ex)
        {
            return call.Finish(ToolOutcome.Error(ex.Message));
        }
        catch (SshSessionException ex)
        {
            return call.Finish(ToolOutcome.Error(call.Host is { } h ? $"{h.DisplayName}: {ex.Message}" : ex.Message));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            call.Finish(ToolOutcome.Error("Cancelled."));
            throw;
        }
        catch (OperationCanceledException)
        {
            return call.Finish(ToolOutcome.Error("Cancelled (a prompt was declined or timed out)."), AgentOutcome.Denied);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CoreLog.Warn($"Agent tool {name} failed: {ex}");
            return call.Finish(ToolOutcome.Error($"{name} failed: {ex.Message}"));
        }
        finally
        {
            session.Calls.Release();
            Interlocked.Decrement(ref _running);
            RunningChanged?.Invoke();
        }
    }

    // ---- Tools ----

    private ToolOutcome ListHosts(Call call)
    {
        VaultData vault = Vault();
        call.Summary = "";
        var lines = new List<string>();
        foreach (HostEntry host in vault.Hosts.OrderBy(h => vault.FindGroup(h.GroupId)?.Name ?? "").ThenBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            AgentRules rules = AgentRules.For(vault, host);
            if (rules.Access == AgentAccess.Off)
                continue;
            var parts = new List<string> { $"{host.DisplayName}", $"address: {Address(vault, host)}", $"access: {AccessName(rules.Access)}" };
            if (vault.FindGroup(host.GroupId) is { } group)
                parts.Add($"group: {group.Name}");
            IReadOnlyList<HostEntry> jumps = EffectiveOptions.ResolveJumpChain(vault, host, out _);
            if (jumps.Count > 0)
                parts.Add("via " + string.Join(" → ", jumps.Select(j => j.DisplayName)));
            if (!string.IsNullOrWhiteSpace(host.Notes))
                parts.Add($"notes: {OneLine(host.Notes, 200)}");
            lines.Add("- " + string.Join("; ", parts));
        }
        if (lines.Count == 0)
        {
            return ToolOutcome.Ok("No hosts are open to agents. In TGK, the user can allow agents per host or group: " +
                "edit the host (or group) → Connection → Agent access.");
        }
        return ToolOutcome.Ok($"{lines.Count} host(s) open to agents (access: read-only = reads and read-only commands only; " +
            "ask = changes need the user's approval; full = no approval except protected paths):\n" + string.Join('\n', lines));
    }

    private async Task<ToolOutcome> RunCommandAsync(Call call, CancellationToken ct)
    {
        string command = call.String("command");
        string? cwd = call.OptionalString("cwd");
        bool sudo = call.Bool("sudo") ?? false;
        int seconds = call.Int("timeout_seconds") ?? (int)DefaultCommandTimeout.TotalSeconds;
        TimeSpan timeout = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, (int)MaxCommandTimeout.TotalSeconds));
        call.Summary = command;
        using ConnectionPool.Lease lease = await call.ConnectAsync(ct).ConfigureAwait(false);
        string home = await lease.Files.HomeAsync(ct).ConfigureAwait(false);
        string line = command;
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            string dir = RemotePath.Resolve(home, cwd);
            call.Summary = $"{command}   (in {RemotePath.Tilde(home, dir)})";
            line = $"cd -- {RemotePath.Quote(dir)} && {command}";
            await call.ApproveAsync(AgentPolicy.Read(call.Rules, dir, home), "Run a command", call.Summary, null, $"run|{line}", ct).ConfigureAwait(false);
        }
        if (sudo)
        {
            call.Summary = "sudo: " + call.Summary;
            await call.ApproveAsync(AgentPolicy.Sudo(call.Rules), "Run a command as root (sudo)", call.Summary, null, $"sudo|{line}", ct).ConfigureAwait(false);
        }
        else
        {
            string wd = string.IsNullOrWhiteSpace(cwd) ? home : RemotePath.Resolve(home, cwd);
            await call.ApproveAsync(AgentPolicy.Run(call.Rules, command, home, wd), "Run a command", call.Summary, null, $"run|{line}", ct).ConfigureAwait(false);
        }

        CommandResult result = sudo
            ? await RunSudoAsync(call, lease, line, timeout, ct).ConfigureAwait(false)
            : await lease.Connection.RunAsync(line, timeout, ct: ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        if (result.TimedOut)
            sb.Append(CultureInfo.InvariantCulture, $"Stopped after {timeout.TotalSeconds:0} s (timeout_seconds can be raised up to {MaxCommandTimeout.TotalSeconds:0}).\n");
        else if (result.ExitCode is { } code)
            sb.Append(CultureInfo.InvariantCulture, $"exit code: {code}\n");
        else
            sb.Append(CultureInfo.InvariantCulture, $"ended by signal {result.ExitSignal ?? "(unknown)"}\n");
        if (result.Stdout.Length > 0)
            sb.Append("--- stdout ---\n").Append(result.Stdout).Append(result.Stdout.EndsWith('\n') ? "" : "\n");
        if (result.Stderr.Length > 0)
            sb.Append("--- stderr ---\n").Append(result.Stderr).Append(result.Stderr.EndsWith('\n') ? "" : "\n");
        if (result.Stdout.Length == 0 && result.Stderr.Length == 0)
            sb.Append("(no output)\n");
        if (result.Truncated)
            sb.Append(CultureInfo.InvariantCulture, $"[output cut at {RemoteConnection.DefaultMaxOutput / 1024} KB per stream; narrow it down with head, tail or grep]\n");
        call.Detail = result.TimedOut ? "timed out" : result.ExitCode is { } c ? $"exit {c}" : $"signal {result.ExitSignal}";
        return new ToolOutcome(sb.ToString(), result.TimedOut);
    }

    /// <summary>
    /// Runs <paramref name="line"/> through sudo. A passwordless sudo runs it directly (never sending a password it
    /// could read); otherwise the password goes to sudo on stdin, with cached credentials ignored so that sudo always
    /// consumes it. A wrong password is asked for again (twice at most).
    /// </summary>
    private async Task<CommandResult> RunSudoAsync(Call call, ConnectionPool.Lease lease, string line, TimeSpan timeout, CancellationToken ct)
    {
        string wrapped = $"sh -c {RemotePath.Quote(line)}";
        CommandResult probe = await lease.Connection.RunAsync("sudo -n true", TimeSpan.FromSeconds(15), ct: ct).ConfigureAwait(false);
        if (probe.ExitCode == 0)
            return await lease.Connection.RunAsync($"sudo -n -- {wrapped} </dev/null", timeout, ct: ct).ConfigureAwait(false);
        if (probe.Stderr.Contains("not found", StringComparison.OrdinalIgnoreCase))
            throw new ToolException($"{call.Host!.DisplayName} has no sudo.");

        VaultData vault = Vault();
        SshConnectRequest request = SshConnectRequest.ForHost(vault, call.Host!);
        string? error = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            string password = await _host.SudoPasswordAsync(call.Host!, request, error, ct).ConfigureAwait(false)
                ?? throw new ToolException("The user did not give the sudo password.", denied: true);
            byte[] input = Encoding.UTF8.GetBytes(password + "\n");
            CommandResult result = await lease.Connection.RunAsync($"sudo -S -k -p '' -- {wrapped}", timeout, input: input, ct: ct).ConfigureAwait(false);
            bool wrong = result.ExitCode != 0 && (result.Stderr.Contains("incorrect password", StringComparison.OrdinalIgnoreCase)
                || result.Stderr.Contains("Sorry, try again", StringComparison.Ordinal));
            if (!wrong)
                return result;
            error = $"sudo did not accept the password for {request.Username}@{request.Host}.";
        }
        throw new ToolException("sudo did not accept the password.");
    }

    private async Task<ToolOutcome> ReadFileAsync(Call call, CancellationToken ct)
    {
        using ConnectionPool.Lease lease = await call.ConnectAsync(ct).ConfigureAwait(false);
        string path = await call.PathAsync(lease.Files, "path", ct).ConfigureAwait(false);
        int offset = Math.Max(1, call.Int("offset") ?? 1);
        int limit = Math.Clamp(call.Int("limit") ?? MaxReadLines, 1, MaxReadLines);
        await call.ApproveReadAsync(path, ct).ConfigureAwait(false);

        byte[] bytes = await lease.Files.ReadAsync(path, MaxFileBytes, ct).ConfigureAwait(false);
        string hash = TextContent.Hash(bytes);
        call.Session.SetRead(call.Host!.Id, path, hash);
        TextContent? text = TextContent.Decode(bytes);
        if (text is null)
        {
            return ToolOutcome.Error($"{call.Display(path)} is a binary file ({bytes.Length:N0} bytes). Use download to copy it here, " +
                "or run_command with xxd, file or strings.");
        }
        if (bytes.Length == 0)
            return ToolOutcome.Ok($"{call.Display(path)} is empty.");
        (string numbered, int total, int first, int last) = text.Numbered(offset, limit, MaxLineLength);
        if (first > total)
            return ToolOutcome.Error($"{call.Display(path)} has only {total} lines.");
        var sb = new StringBuilder(numbered);
        if (first > 1 || last < total)
            sb.Append(CultureInfo.InvariantCulture, $"[lines {first}-{last} of {total}{(last < total ? $"; pass offset={last + 1} for more" : "")}]\n");
        if (text.IsLatin1)
            sb.Append("[not valid UTF-8: read as Latin-1, and written back that way]\n");
        call.Detail = $"{bytes.Length} bytes";
        return ToolOutcome.Ok(Cap(sb.ToString()));
    }

    private async Task<ToolOutcome> ListDirAsync(Call call, CancellationToken ct)
    {
        using ConnectionPool.Lease lease = await call.ConnectAsync(ct).ConfigureAwait(false);
        string path = await call.PathAsync(lease.Files, "path", ct, optional: true).ConfigureAwait(false);
        await call.ApproveReadAsync(path, ct).ConfigureAwait(false);
        (IReadOnlyList<RemoteEntry> entries, bool more) = await lease.Files.ListAsync(path, MaxListEntries, ct).ConfigureAwait(false);
        if (entries.Count == 0)
            return ToolOutcome.Ok($"{call.Display(path)} is empty.");
        var sb = new StringBuilder($"{call.Display(path)}:\n");
        foreach (RemoteEntry e in entries)
            sb.Append(EntryLine(e)).Append('\n');
        if (more)
            sb.Append(CultureInfo.InvariantCulture, $"[only the first {MaxListEntries} entries; use glob or run_command to narrow down]\n");
        call.Detail = $"{entries.Count} entries";
        return ToolOutcome.Ok(sb.ToString());
    }

    private async Task<ToolOutcome> StatAsync(Call call, CancellationToken ct)
    {
        using ConnectionPool.Lease lease = await call.ConnectAsync(ct).ConfigureAwait(false);
        string path = await call.PathAsync(lease.Files, "path", ct).ConfigureAwait(false);
        await call.ApproveReadAsync(path, ct).ConfigureAwait(false);
        RemoteEntry? entry = await lease.Files.StatAsync(path, ct).ConfigureAwait(false);
        if (entry is null)
            return ToolOutcome.Ok($"{call.Display(path)} does not exist.");
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"path: {path}\ntype: {KindName(entry.Kind)}\nsize: {entry.Size} bytes\n");
        sb.Append(CultureInfo.InvariantCulture, $"permissions: {entry.Permissions} ({Convert.ToString(entry.Mode & 0xFFF, 8).PadLeft(4, '0')})\n");
        if (entry.Uid is { } uid)
            sb.Append(CultureInfo.InvariantCulture, $"owner uid: {uid}\n");
        sb.Append(CultureInfo.InvariantCulture, $"modified: {entry.Modified:yyyy-MM-dd HH:mm:ss} UTC\n");
        if (entry.Kind == RemoteEntryKind.Symlink && await lease.Files.RealPathAsync(path, ct).ConfigureAwait(false) is { } target)
            sb.Append(CultureInfo.InvariantCulture, $"points to: {target}\n");
        return ToolOutcome.Ok(sb.ToString());
    }

    private async Task<ToolOutcome> GlobAsync(Call call, CancellationToken ct)
    {
        string pattern = call.String("pattern").Trim();
        using ConnectionPool.Lease lease = await call.ConnectAsync(ct).ConfigureAwait(false);
        string home = await lease.Files.HomeAsync(ct).ConfigureAwait(false);
        string root = await call.PathAsync(lease.Files, "path", ct, optional: true).ConfigureAwait(false);
        if (pattern.StartsWith('/') || pattern.StartsWith("~/", StringComparison.Ordinal))
        {
            // An absolute pattern: search from its fixed part.
            string absolute = RemotePath.Resolve(home, pattern);
            int wildcard = absolute.IndexOfAny(['*', '?']);
            root = wildcard < 0 ? RemotePath.Directory(absolute) : RemotePath.Directory(absolute[..wildcard] + "x");
            pattern = absolute[(root.TrimEnd('/').Length + 1)..];
        }
        call.Summary = $"{pattern}   (in {RemotePath.Tilde(home, root)})";
        await call.ApproveReadAsync(root, ct).ConfigureAwait(false);

        string full = root.TrimEnd('/') + "/" + pattern;
        // find's -path lets * cross slashes, so it over-matches; the exact match is checked here.
        string findPattern = full.Replace("**/", "*").Replace("**", "*");
        string script = $"find {RemotePath.Quote(root)} \\( -name .git -o -name node_modules \\) -prune -o -path {RemotePath.Quote(findPattern)} -print 2>/dev/null | head -n 20000";
        CommandResult result = await lease.Connection.RunAsync(script, SearchTimeout, maxOutput: 4 * 1024 * 1024, ct: ct).ConfigureAwait(false);
        string[] matches = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => RemotePath.Matches(full, p, home))
            .Order(StringComparer.Ordinal)
            .ToArray();
        call.Detail = $"{matches.Length} matches";
        if (matches.Length == 0)
            return ToolOutcome.Ok(result.TimedOut ? "No matches before the search timed out." : "No matches.");
        var sb = new StringBuilder();
        foreach (string m in matches.Take(MaxGlobResults))
            sb.Append(m).Append('\n');
        if (matches.Length > MaxGlobResults)
            sb.Append(CultureInfo.InvariantCulture, $"[{matches.Length - MaxGlobResults} more; use a narrower pattern or path]\n");
        if (result.TimedOut)
            sb.Append("[the search timed out; results may be incomplete]\n");
        return ToolOutcome.Ok(sb.ToString());
    }

    private async Task<ToolOutcome> GrepAsync(Call call, CancellationToken ct)
    {
        string pattern = call.String("pattern");
        string? glob = call.OptionalString("glob");
        bool ignoreCase = call.Bool("ignore_case") ?? false;
        int context = Math.Clamp(call.Int("context") ?? 0, 0, 10);
        int max = Math.Clamp(call.Int("max_results") ?? 200, 1, 1000);
        using ConnectionPool.Lease lease = await call.ConnectAsync(ct).ConfigureAwait(false);
        string home = await lease.Files.HomeAsync(ct).ConfigureAwait(false);
        string path = await call.PathAsync(lease.Files, "path", ct, optional: true).ConfigureAwait(false);
        call.Summary = $"{pattern}   (in {RemotePath.Tilde(home, path)}{(glob is null ? "" : $", {glob}")})";
        await call.ApproveReadAsync(path, ct).ConfigureAwait(false);

        string? hasRg = _pool.GetFact(call.Host!.Id, "rg");
        if (hasRg is null)
        {
            CommandResult probe = await lease.Connection.RunAsync("command -v rg >/dev/null 2>&1 && echo yes || echo no", TimeSpan.FromSeconds(15), ct: ct).ConfigureAwait(false);
            hasRg = probe.Stdout.Trim() == "yes" ? "yes" : "no";
            _pool.SetFact(call.Host.Id, "rg", hasRg);
        }
        // Protected paths below the search: the built-in ones are left out of it; the host's own need approval like
        // reading them (refused in read-only mode).
        if (AgentPolicy.ProtectedBelow(call.Rules, path, home, builtIn: false) is { } below)
        {
            var decision = call.Rules.Access == AgentAccess.ReadOnly
                ? AgentDecision.Deny($"The search covers the protected path {RemotePath.Tilde(home, below)}; search below or beside it.")
                : AgentDecision.Ask($"The search covers the protected path {RemotePath.Tilde(home, below)}.");
            await call.ApproveAsync(decision, "Search a protected path", call.Display(path), null, $"read|{path}", ct).ConfigureAwait(false);
        }
        string q = RemotePath.Quote(pattern), where = RemotePath.Quote(path);
        string rgExcludes = string.Concat(AgentPolicy.SearchExcludedNames.Concat(AgentPolicy.SearchExcludedDirs).Select(n => $" -g {RemotePath.Quote("!" + n)}"));
        string grepExcludes = string.Concat(AgentPolicy.SearchExcludedNames.Select(n => $" --exclude={RemotePath.Quote(n)}"))
            + string.Concat(AgentPolicy.SearchExcludedDirs.Append(".git").Append("node_modules").Select(n => $" --exclude-dir={RemotePath.Quote(n)}"));
        string script = hasRg == "yes"
            ? $"rg --line-number --no-heading --color never --max-columns 500 --max-columns-preview{(ignoreCase ? " -i" : "")}{(context > 0 ? $" -C {context}" : "")}{(glob is null ? "" : $" -g {RemotePath.Quote(glob)}")}{rgExcludes} -e {q} -- {where} 2>&1 | head -n {max + 1}"
            : $"grep -rnIE{(ignoreCase ? "i" : "")}{(context > 0 ? $" -C {context}" : "")}{grepExcludes}{(glob is null ? "" : $" --include={RemotePath.Quote(glob)}")} -e {q} -- {where} 2>&1 | head -n {max + 1}";
        CommandResult result = await lease.Connection.RunAsync(script, SearchTimeout, ct: ct).ConfigureAwait(false);
        string[] lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        call.Detail = $"{Math.Min(lines.Length, max)} lines";
        if (lines.Length == 0)
            return ToolOutcome.Ok(result.TimedOut ? "No matches before the search timed out." : "No matches.");
        var sb = new StringBuilder();
        foreach (string line in lines.Take(max))
            sb.Append(line).Append('\n');
        if (lines.Length > max)
            sb.Append(CultureInfo.InvariantCulture, $"[more than {max} lines; narrow the pattern or path, or raise max_results]\n");
        if (result.TimedOut)
            sb.Append("[the search timed out; results may be incomplete]\n");
        return ToolOutcome.Ok(Cap(sb.ToString()));
    }

    private async Task<ToolOutcome> EditFileAsync(Call call, CancellationToken ct)
    {
        string oldText = call.String("old_string", allowEmpty: true);
        string newText = call.String("new_string", allowEmpty: true);
        bool all = call.Bool("replace_all") ?? false;
        using ConnectionPool.Lease lease = await call.ConnectAsync(ct).ConfigureAwait(false);
        string path = await call.PathAsync(lease.Files, "path", ct).ConfigureAwait(false);
        string home = await lease.Files.HomeAsync(ct).ConfigureAwait(false);

        AgentDecision decision = AgentPolicy.Write(call.Rules, path, home);
        if (decision.Verdict == AgentVerdict.Deny)
            throw new ToolException(decision.Reason, denied: true);
        byte[] current = await ReadForChangeAsync(call, lease.Files, path, mustExist: true, ct).ConfigureAwait(false) ?? [];
        TextContent text = TextContent.Decode(current) ?? throw new ToolException($"{call.Display(path)} is a binary file; it can't be edited as text.");
        (TextContent result, int count) = text.Replace(oldText, newText, all);

        string diff = TextDiff.Unified(text.Normalized, result.Normalized);
        await call.ApproveAsync(decision, "Change a file", call.Display(path), diff, $"write|{path}", ct).ConfigureAwait(false);
        byte[] bytes = result.Encode();
        await lease.Files.WriteAsync(path, bytes, ct).ConfigureAwait(false);
        call.Session.SetRead(call.Host!.Id, path, TextContent.Hash(bytes));

        int line = result.LineOf(newText.Length > 0 ? newText : "");
        (string snippet, _, _, _) = result.Numbered(Math.Max(1, line - 3), newText.Split('\n').Length + 6, MaxLineLength);
        call.Detail = $"{count} replacement(s)";
        return ToolOutcome.Ok($"Edited {call.Display(path)} ({count} replacement{(count == 1 ? "" : "s")}). Around the change:\n{snippet}");
    }

    private async Task<ToolOutcome> WriteFileAsync(Call call, CancellationToken ct)
    {
        string content = call.String("content", allowEmpty: true);
        using ConnectionPool.Lease lease = await call.ConnectAsync(ct).ConfigureAwait(false);
        string path = await call.PathAsync(lease.Files, "path", ct).ConfigureAwait(false);
        string home = await lease.Files.HomeAsync(ct).ConfigureAwait(false);
        AgentDecision decision = AgentPolicy.Write(call.Rules, path, home);
        if (decision.Verdict == AgentVerdict.Deny)
            throw new ToolException(decision.Reason, denied: true);

        byte[]? current = await ReadForChangeAsync(call, lease.Files, path, mustExist: false, ct).ConfigureAwait(false);
        TextContent? old = current is null ? null : TextContent.Decode(current);
        TextContent result = old is null ? TextContent.New(content) : old.With(old.ToFileNewlines(content));
        byte[] bytes = result.Encode();
        if (bytes.LongLength > MaxFileBytes)
            throw new ToolException($"The content is larger than {MaxFileBytes / (1024 * 1024)} MB; use upload instead.");
        string detail = current is null
            ? Preview(content)
            : old is null ? "(replaces a binary file)" : TextDiff.Unified(old.Normalized, result.Normalized);
        await call.ApproveAsync(decision, current is null ? "Create a file" : "Replace a file", call.Display(path), detail, $"write|{path}", ct).ConfigureAwait(false);

        if (current is null)
            await lease.Files.CreateDirectoryAsync(RemotePath.Directory(path), ct).ConfigureAwait(false);
        await lease.Files.WriteAsync(path, bytes, ct).ConfigureAwait(false);
        call.Session.SetRead(call.Host!.Id, path, TextContent.Hash(bytes));
        call.Detail = $"{bytes.Length} bytes";
        return ToolOutcome.Ok($"{(current is null ? "Created" : "Replaced")} {call.Display(path)} ({bytes.Length:N0} bytes).");
    }

    private async Task<ToolOutcome> UploadAsync(Call call, CancellationToken ct)
    {
        string local = LocalPath(call.String("local_path"));
        using ConnectionPool.Lease lease = await call.ConnectAsync(ct).ConfigureAwait(false);
        string path = await call.PathAsync(lease.Files, "remote_path", ct).ConfigureAwait(false);
        string home = await lease.Files.HomeAsync(ct).ConfigureAwait(false);
        call.Summary = $"{local} → {path}";
        var info = new FileInfo(local);
        if (!info.Exists)
            throw new ToolException($"{local} does not exist on this computer.");
        if (info.Length > MaxTransferBytes)
            throw new ToolException($"{local} is larger than {MaxTransferBytes / (1024 * 1024)} MB.");
        AgentDecision decision = AgentPolicy.Write(call.Rules, path, home);
        if (decision.Verdict == AgentVerdict.Deny)
            throw new ToolException(decision.Reason, denied: true);
        RemoteEntry? existing = await lease.Files.StatAsync(path, ct).ConfigureAwait(false);
        await call.ApproveAsync(decision, existing is null ? "Upload a file" : "Upload over a file", call.Display(path),
            $"From this computer: {local} ({info.Length:N0} bytes){(existing is null ? "" : $"\nReplaces {existing.Size:N0} bytes on the host.")}", $"write|{path}", ct).ConfigureAwait(false);
        byte[] bytes = await File.ReadAllBytesAsync(local, ct).ConfigureAwait(false);
        if (existing is null)
            await lease.Files.CreateDirectoryAsync(RemotePath.Directory(path), ct).ConfigureAwait(false);
        await lease.Files.WriteAsync(path, bytes, ct).ConfigureAwait(false);
        call.Session.SetRead(call.Host!.Id, path, TextContent.Hash(bytes));
        call.Detail = $"{bytes.Length} bytes";
        return ToolOutcome.Ok($"Uploaded {local} to {call.Display(path)} ({bytes.Length:N0} bytes).");
    }

    private async Task<ToolOutcome> DownloadAsync(Call call, CancellationToken ct)
    {
        string local = LocalPath(call.String("local_path"));
        bool overwrite = call.Bool("overwrite") ?? false;
        using ConnectionPool.Lease lease = await call.ConnectAsync(ct).ConfigureAwait(false);
        string path = await call.PathAsync(lease.Files, "remote_path", ct).ConfigureAwait(false);
        call.Summary = $"{path} → {local}";
        if (File.Exists(local) && !overwrite)
            throw new ToolException($"{local} already exists on this computer; pass overwrite to replace it.");
        if (Directory.Exists(local))
            throw new ToolException($"{local} is a directory; give the file's full path.");
        await call.ApproveReadAsync(path, ct).ConfigureAwait(false);
        byte[] bytes = await lease.Files.ReadAsync(path, MaxTransferBytes, ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        await File.WriteAllBytesAsync(local, bytes, ct).ConfigureAwait(false);
        call.Detail = $"{bytes.Length} bytes";
        return ToolOutcome.Ok($"Downloaded {call.Display(path)} to {local} ({bytes.Length:N0} bytes).");
    }

    // ---- Helpers ----

    /// <summary>
    /// The current bytes of a file about to be changed; it must have been read in this session and be unchanged since
    /// (a file that does not exist needs no read). Null when it does not exist.
    /// </summary>
    private static async Task<byte[]?> ReadForChangeAsync(Call call, RemoteFiles files, string path, bool mustExist, CancellationToken ct)
    {
        RemoteEntry? entry = await files.StatAsync(path, ct).ConfigureAwait(false);
        if (entry is null)
        {
            if (mustExist)
                throw new ToolException($"{call.Display(path)} does not exist; use write_file to create it.");
            return null;
        }
        if (entry.Kind == RemoteEntryKind.Directory)
            throw new ToolException($"{call.Display(path)} is a directory.");
        string? readHash = call.Session.ReadHash(call.Host!.Id, path);
        if (readHash is null)
            throw new ToolException($"Read {call.Display(path)} with read_file before changing it.");
        byte[] current = await files.ReadAsync(path, MaxFileBytes, ct).ConfigureAwait(false);
        if (TextContent.Hash(current) != readHash)
            throw new ToolException($"{call.Display(path)} changed since it was read (by someone else, or a command). Read it again before changing it.");
        return current;
    }

    private async Task<RemoteConnection> ConnectAsync(HostEntry host, CancellationToken ct)
    {
        VaultData vault = Vault();
        SshConnectRequest request = SshConnectRequest.ForHost(vault, vault.FindHost(host.Id) ?? host);
        return await _host.ConnectAsync(host, request, ct).ConfigureAwait(false);
    }

    private VaultData Vault() => _host.Vault ?? throw new ToolException("TGK is locked or signed out; the user has to unlock it first.");

    private static string RouteKey(VaultData vault, HostEntry host)
    {
        SshConnectRequest r = SshConnectRequest.ForHost(vault, host);
        return $"{r}|{r.LegacyAlgorithms}|{string.Join(",", r.JumpChain.Select(j => $"{j}|{j.LegacyAlgorithms}"))}|{host.IdentityId}";
    }

    private static string LocalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new ToolException($"{path} is not an absolute path on this computer.");
        return Path.GetFullPath(path);
    }

    private static string Address(VaultData vault, HostEntry host)
    {
        string user = string.IsNullOrWhiteSpace(host.Username) ? vault.FindIdentity(host.IdentityId)?.Username ?? "" : host.Username;
        string address = host.Port == 22 ? host.Host : $"{host.Host}:{host.Port}";
        return user.Length > 0 ? $"{user}@{address}" : address;
    }

    private static string AccessName(AgentAccess access) => access switch
    {
        AgentAccess.ReadOnly => "read-only",
        AgentAccess.Ask => "ask",
        AgentAccess.Full => "full",
        _ => "off",
    };

    private static string KindName(RemoteEntryKind kind) => kind switch
    {
        RemoteEntryKind.File => "file",
        RemoteEntryKind.Directory => "directory",
        RemoteEntryKind.Symlink => "symbolic link",
        _ => "other (device, socket or pipe)",
    };

    private static string EntryLine(RemoteEntry e)
    {
        char type = e.Kind switch { RemoteEntryKind.Directory => 'd', RemoteEntryKind.Symlink => 'l', RemoteEntryKind.File => '-', _ => '?' };
        string name = e.Kind == RemoteEntryKind.Directory ? e.Name + "/" : e.Kind == RemoteEntryKind.Symlink ? e.Name + " ->" : e.Name;
        return string.Create(CultureInfo.InvariantCulture, $"{type}{e.Permissions} {e.Size,12} {e.Modified:yyyy-MM-dd HH:mm}  {name}");
    }

    private static string OneLine(string text, int max)
    {
        string line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length > max ? line[..max] + "…" : line;
    }

    private static string Preview(string content)
    {
        string[] lines = content.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        string shown = string.Join('\n', lines.Take(200).Select(l => "+" + l));
        return lines.Length > 200 ? shown + $"\n… {lines.Length - 200} more lines" : shown;
    }

    private static string Cap(string text) =>
        text.Length <= MaxResultChars ? text : text[..MaxResultChars] + $"\n[result cut at {MaxResultChars / 1000}k characters]\n";

    /// <summary>A refusal or a problem with the call, told to the agent as is.</summary>
    private sealed class ToolException(string message, bool denied = false) : Exception(message)
    {
        public bool Denied { get; } = denied;
    }

    /// <summary>One tool call in progress: its arguments, host, approvals and the record of it.</summary>
    private sealed class Call(AgentToolbox toolbox, AgentSession session, string tool, JsonElement args)
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private string? _approvedBy = "policy";
        private bool _finished;

        public AgentSession Session { get; } = session;
        public HostEntry? Host { get; private set; }
        public AgentRules Rules { get; private set; } = new(AgentAccess.Off, [], []);
        public string Summary { get; set; } = "";
        public string? Detail { get; set; }
        private string _home = "/";

        public string Display(string path) => RemotePath.Tilde(_home, path);

        /// <summary>Finds the host (open to agents) and leases its connection.</summary>
        public async Task<ConnectionPool.Lease> ConnectAsync(CancellationToken ct)
        {
            VaultData vault = toolbox.Vault();
            string spec = String("host").Trim();
            HostEntry host = FindHost(vault, spec);
            Host = host;
            Rules = AgentRules.For(vault, host);
            if (Rules.Access == AgentAccess.Off)
                throw new ToolException($"Agents have no access to {host.DisplayName}. The user can allow it in TGK (edit the host → Agent access).", denied: true);
            string route;
            try
            {
                route = RouteKey(vault, host);
            }
            catch (SshSessionException ex)
            {
                throw new ToolException($"{host.DisplayName}: {ex.Message}");
            }
            ConnectionPool.Lease lease = await toolbox._pool.AcquireAsync(host, route, ct).ConfigureAwait(false);
            try
            {
                _home = await lease.Files.HomeAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
            return lease;
        }

        public async Task<string> PathAsync(RemoteFiles files, string name, CancellationToken ct, bool optional = false)
        {
            string? raw = optional ? OptionalString(name) : String(name);
            string home = await files.HomeAsync(ct).ConfigureAwait(false);
            try
            {
                string path = raw is null ? home : RemotePath.Resolve(home, raw);
                if (Summary.Length == 0)
                    Summary = RemotePath.Tilde(home, path);
                return path;
            }
            catch (ArgumentException ex)
            {
                throw new ToolException($"{name}: {ex.Message}");
            }
        }

        public async Task ApproveReadAsync(string path, CancellationToken ct)
        {
            AgentDecision decision = AgentPolicy.Read(Rules, path, _home);
            await ApproveAsync(decision, "Read a protected path", Display(path), null, $"read|{path}", ct).ConfigureAwait(false);
        }

        /// <summary>Goes on when the decision allows it or the user approves; throws a refusal otherwise.</summary>
        public async Task ApproveAsync(AgentDecision decision, string action, string subject, string? detail, string key, CancellationToken ct)
        {
            switch (decision.Verdict)
            {
                case AgentVerdict.Allow:
                    return;
                case AgentVerdict.Deny:
                    _approvedBy = null;
                    throw new ToolException(decision.Reason, denied: true);
            }
            string sessionKey = $"{Host!.Id}|{key}";
            if (Session.IsApproved(sessionKey))
            {
                _approvedBy = "session";
                return;
            }
            var request = new ApprovalRequest(Session.Client, Host.DisplayName, action, subject, detail, decision.Reason);
            ApprovalAnswer answer = await toolbox._host.ApproveAsync(request, ct).ConfigureAwait(false);
            switch (answer)
            {
                case ApprovalAnswer.AllowSession:
                    Session.Approve(sessionKey);
                    _approvedBy = "user";
                    return;
                case ApprovalAnswer.AllowOnce:
                    _approvedBy = "user";
                    return;
                default:
                    _approvedBy = null;
                    throw new ToolException($"The user did not approve this ({action.ToLowerInvariant()}: {request.Subject}).", denied: true);
            }
        }

        public ToolOutcome Finish(ToolOutcome outcome, AgentOutcome? kind = null)
        {
            if (_finished)
                return outcome;
            _finished = true;
            AgentOutcome result = kind ?? (outcome.IsError ? AgentOutcome.Failed : AgentOutcome.Ok);
            string? detail = result == AgentOutcome.Ok ? Detail : OneLine(outcome.Text, 300);
            var entry = new AgentActivityEntry(DateTimeOffset.UtcNow, Session.Client, Host?.DisplayName, tool,
                Summary.Length > 0 ? Summary : SummaryFromArgs(), result, detail, Stopwatch.GetElapsedTime(_started),
                result == AgentOutcome.Ok ? _approvedBy : null);
            toolbox.Activity.Add(entry);
            toolbox.CallFinished?.Invoke(entry, outcome.Text);
            return outcome;
        }

        private string SummaryFromArgs()
        {
            foreach (string name in new[] { "command", "path", "remote_path", "pattern" })
            {
                if (OptionalString(name) is { } value)
                    return value;
            }
            return "";
        }

        public string String(string name, bool allowEmpty = false)
        {
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
            {
                string s = value.GetString()!;
                if (allowEmpty || !string.IsNullOrWhiteSpace(s))
                    return s;
            }
            throw new ToolException($"{name} is required (a string).");
        }

        public string? OptionalString(string name) =>
            args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()
                : null;

        public int? Int(string name)
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out JsonElement value))
                return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n))
                return n;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), CultureInfo.InvariantCulture, out int parsed))
                return parsed;
            if (value.ValueKind == JsonValueKind.Null)
                return null;
            throw new ToolException($"{name} must be an integer.");
        }

        public bool? Bool(string name)
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out JsonElement value))
                return null;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.String when bool.TryParse(value.GetString(), out bool b) => b,
                _ => throw new ToolException($"{name} must be true or false."),
            };
        }

        private static HostEntry FindHost(VaultData vault, string spec)
        {
            if (spec.Length == 0)
                throw new ToolException("host is required.");
            if (Guid.TryParse(spec, out Guid id) && vault.FindHost(id) is { } byId)
                return byId;
            List<HostEntry> matches = vault.Hosts.Where(h => string.Equals(h.DisplayName.Trim(), spec, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                matches = vault.Hosts.Where(h => string.Equals(h.Host.Trim(), spec, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Address(vault, h), spec, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            // Only hosts open to agents count when the name is shared.
            if (matches.Count > 1)
            {
                List<HostEntry> open = matches.Where(h => AgentRules.For(vault, h).Access != AgentAccess.Off).ToList();
                if (open.Count > 0)
                    matches = open;
            }
            return matches.Count switch
            {
                1 => matches[0],
                0 => throw new ToolException($"No saved host is called \"{spec}\". Use list_hosts to see the hosts open to agents."),
                _ => throw new ToolException($"Several hosts are called \"{spec}\"; use one of their ids: " +
                    string.Join(", ", matches.Select(h => $"{h.Id} ({Address(vault, h)})")) + "."),
            };
        }
    }
}
