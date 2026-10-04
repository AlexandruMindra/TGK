using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace TGK.Core.Agents;

public enum AgentOutcome
{
    Ok,
    Failed,
    Denied,
}

/// <summary>One agent call, as shown in TGK's activity list and written to the audit log. Never holds file contents.</summary>
/// <param name="Client">The MCP client's name (e.g. <c>claude-code 2.1.0</c>).</param>
/// <param name="Summary">The command, or the path(s), the call was about.</param>
/// <param name="ApprovedBy"><c>policy</c>, <c>user</c>, <c>session</c> (approved earlier for the session), or null when it did not run.</param>
public sealed record AgentActivityEntry(
    DateTimeOffset Time, string Client, string? Host, string Tool, string Summary, AgentOutcome Outcome, string? Detail,
    TimeSpan Duration, string? ApprovedBy);

/// <summary>
/// The record of what agents did: the most recent calls in memory (for the activity list) and an append-only JSON
/// lines file (the audit log, rotated at <see cref="MaxLogBytes"/> to one previous file).
/// </summary>
public sealed class AgentActivity
{
    public const int MaxRecent = 300;
    public const long MaxLogBytes = 2 * 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly LinkedList<AgentActivityEntry> _recent = new();
    private readonly string? _logPath;

    /// <param name="logPath">The audit log file; null keeps the record in memory only.</param>
    public AgentActivity(string? logPath)
    {
        _logPath = logPath;
    }

    /// <summary>The default audit log: <c>logs/agent-audit.log</c> in the config directory.</summary>
    public static string DefaultLogPath => Path.Combine(AppPaths.ConfigDirectory, "logs", "agent-audit.log");

    public string? LogPath => _logPath;

    /// <summary>Raised (on the calling thread, any thread) after an entry was added.</summary>
    public event Action<AgentActivityEntry>? Added;

    /// <summary>The most recent entries, newest first.</summary>
    public IReadOnlyList<AgentActivityEntry> Recent
    {
        get
        {
            lock (_gate)
                return [.. _recent];
        }
    }

    public void Add(AgentActivityEntry entry)
    {
        lock (_gate)
        {
            _recent.AddFirst(entry);
            while (_recent.Count > MaxRecent)
                _recent.RemoveLast();
            Write(entry);
        }
        Added?.Invoke(entry);
    }

    public void Clear()
    {
        lock (_gate)
            _recent.Clear();
    }

    // Under the lock: one writer at a time.
    private void Write(AgentActivityEntry entry)
    {
        if (_logPath is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            var info = new FileInfo(_logPath);
            if (info.Exists && info.Length > MaxLogBytes)
                File.Move(_logPath, _logPath + ".1", overwrite: true);
            string line = JsonSerializer.Serialize(new
            {
                time = entry.Time,
                client = entry.Client,
                host = entry.Host,
                tool = entry.Tool,
                summary = entry.Summary,
                outcome = entry.Outcome.ToString().ToLowerInvariant(),
                detail = entry.Detail,
                ms = (long)entry.Duration.TotalMilliseconds,
                approvedBy = entry.ApprovedBy,
            });
            File.AppendAllText(_logPath, line + "\n");
            if (!OperatingSystem.IsWindows() && !info.Exists)
                File.SetUnixFileMode(_logPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CoreLog.Warn($"Could not write the agent audit log: {ex.Message}");
        }
    }
}
