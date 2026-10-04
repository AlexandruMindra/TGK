using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Models;
using TGK.Core.Ssh;

namespace TGK.Core.Agents;

/// <summary>
/// Agent connections, one per saved host, reused between calls (from any agent session) and closed after a while
/// without use. A connection that dropped, or whose host's connection settings changed, is replaced on the next call.
/// </summary>
public sealed class ConnectionPool : IDisposable
{
    /// <summary>How long an unused connection stays open.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    private readonly Func<HostEntry, CancellationToken, Task<RemoteConnection>> _connect;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Timer _sweeper;
    private bool _disposed;
    private int _generation; // bumped by CloseAll: a connection opened across it is not kept

    /// <param name="connect">Opens a connection to a saved host (credentials, prompts and route are the caller's business).</param>
    public ConnectionPool(Func<HostEntry, CancellationToken, Task<RemoteConnection>> connect)
    {
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        _sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Raised (on any thread) when the set of open connections changed.</summary>
    public event Action? Changed;

    /// <summary>The hosts with an open connection.</summary>
    public IReadOnlyList<Guid> OpenHosts
    {
        get
        {
            lock (_gate)
                return _entries.Where(e => e.Value.Files is { Connection.IsConnected: true }).Select(e => e.Key).ToList();
        }
    }

    /// <summary>
    /// The files (and through them the connection) of <paramref name="host"/>, connecting when needed, for one call:
    /// dispose the lease when done (a connection in use is never closed as idle).
    /// <paramref name="routeKey"/> identifies the connection settings; a different one replaces the connection.
    /// </summary>
    public async Task<Lease> AcquireAsync(HostEntry host, string routeKey, CancellationToken ct)
    {
        Entry entry;
        int generation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            generation = _generation;
            if (!_entries.TryGetValue(host.Id, out entry!))
                _entries[host.Id] = entry = new Entry();
        }
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (entry.Files is { } files && files.Connection.IsConnected && entry.RouteKey == routeKey)
                return Use(entry, files);
            RemoteFiles? old = entry.Files;
            entry.Files = null;
            old?.Connection.Dispose();

            RemoteConnection connection = await _connect(host, ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || generation != _generation)
                {
                    connection.Dispose();
                    throw new OperationCanceledException("Agent connections were closed meanwhile.");
                }
            }
            entry.Files = new RemoteFiles(connection);
            entry.RouteKey = routeKey;
            entry.LastUsed = DateTime.UtcNow;
            entry.Facts.Clear();
            Changed?.Invoke();
            return Use(entry, entry.Files);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private Lease Use(Entry entry, RemoteFiles files)
    {
        lock (_gate)
        {
            entry.InUse++;
            entry.LastUsed = DateTime.UtcNow;
        }
        return new Lease(files, () =>
        {
            lock (_gate)
            {
                entry.InUse--;
                entry.LastUsed = DateTime.UtcNow;
            }
        });
    }

    /// <summary>Something learned about a host's server while it stays connected (e.g. whether it has <c>rg</c>).</summary>
    public string? GetFact(Guid hostId, string key)
    {
        lock (_gate)
            return _entries.TryGetValue(hostId, out Entry? e) && e.Facts.TryGetValue(key, out string? v) ? v : null;
    }

    public void SetFact(Guid hostId, string key, string value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(hostId, out Entry? e))
                e.Facts[key] = value;
        }
    }

    /// <summary>Closes every connection (agents turned off, the vault locked or signed out).</summary>
    public void CloseAll()
    {
        List<RemoteFiles> open;
        lock (_gate)
        {
            _generation++;
            open = _entries.Values.Select(e => e.Files).OfType<RemoteFiles>().ToList();
            foreach (Entry e in _entries.Values)
                e.Files = null;
        }
        foreach (RemoteFiles files in open)
            files.Connection.Dispose();
        if (open.Count > 0)
            Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
        _sweeper.Dispose();
        CloseAll();
    }

    private void Sweep()
    {
        var closed = new List<RemoteFiles>();
        DateTime now = DateTime.UtcNow;
        lock (_gate)
        {
            foreach (Entry e in _entries.Values)
            {
                // A connection in use holds the gate; skip it rather than wait.
                if (e.Files is { } files && e.InUse == 0 && (now - e.LastUsed > IdleTimeout || !files.Connection.IsConnected) && e.Gate.Wait(0))
                {
                    try
                    {
                        e.Files = null;
                        closed.Add(files);
                    }
                    finally
                    {
                        e.Gate.Release();
                    }
                }
            }
        }
        foreach (RemoteFiles files in closed)
            files.Connection.Dispose();
        if (closed.Count > 0)
            Changed?.Invoke();
    }

    /// <summary>One call's use of a pooled connection.</summary>
    public sealed class Lease(RemoteFiles files, Action release) : IDisposable
    {
        private Action? _release = release;

        public RemoteFiles Files { get; } = files;
        public RemoteConnection Connection => Files.Connection;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private sealed class Entry
    {
        public int InUse;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public RemoteFiles? Files;
        public string RouteKey = "";
        public DateTime LastUsed;
        public Dictionary<string, string> Facts { get; } = [];
    }
}
