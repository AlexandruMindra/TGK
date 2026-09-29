using System;
using System.Collections.Generic;
using System.Linq;

namespace TGK.Server;

/// <summary>
/// Locks a username for <see cref="LockoutDuration"/> after <see cref="MaxFailures"/> consecutive failed sign-ins.
/// Unknown usernames are tracked the same way, so a lockout reveals nothing about which accounts exist. In memory only.
/// </summary>
public sealed class LoginThrottle(TimeProvider clock)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);
    private const int MaxEntries = 10_000;

    private readonly Dictionary<string, (int Failures, DateTimeOffset LockedUntil)> _entries = new(StringComparer.OrdinalIgnoreCase);

    public bool IsLocked(string username)
    {
        lock (_entries)
            return _entries.TryGetValue(username, out var entry) && entry.LockedUntil > clock.GetUtcNow();
    }

    public void RecordFailure(string username)
    {
        var now = clock.GetUtcNow();
        lock (_entries)
        {
            if (_entries.Count >= MaxEntries)
            {
                foreach (string key in _entries.Where(e => e.Value.LockedUntil <= now).Select(e => e.Key).ToList())
                    _entries.Remove(key);
            }
            _entries.TryGetValue(username, out var entry);
            int failures = entry.Failures + 1;
            _entries[username] = failures >= MaxFailures ? (0, now + LockoutDuration) : (failures, entry.LockedUntil);
        }
    }

    public void Reset(string username)
    {
        lock (_entries)
            _entries.Remove(username);
    }
}
