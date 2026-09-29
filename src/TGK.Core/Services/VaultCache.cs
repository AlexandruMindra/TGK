using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TGK.Core.Services;

/// <summary>An item as last confirmed by the server (still sealed with the vault key).</summary>
internal sealed record CachedItem(string Id, long Revision, byte[] Data);

/// <summary>A local change not yet accepted by the server; null <see cref="Data"/> = delete.</summary>
internal sealed record PendingChange(string Id, byte[]? Data);

internal sealed class VaultCacheState
{
    public string ServerUrl { get; set; } = "";
    public string UserId { get; set; } = "";
    public long Revision { get; set; }
    public List<CachedItem> Items { get; set; } = [];
    public List<PendingChange> Pending { get; set; } = [];

    /// <summary>Host item id → when this device last connected to it (kept per device, never synced).</summary>
    public Dictionary<string, DateTimeOffset> LastConnected { get; set; } = [];
}

/// <summary>
/// The "keep me signed in" copy of the vault: server items exactly as downloaded (sealed) plus the outbox of
/// unsent changes, in one user-only file. Only one account per device is kept signed in, so there is one file.
/// After a session ended with changes it could not push, the file holds just that outbox (no items).
/// </summary>
internal sealed class VaultCache(string baseDirectory)
{
    public const string FileName = "vault-cache.json";

    public string FilePath { get; } = Path.Combine(baseDirectory, FileName);

    /// <summary>The cached state for this account, or null when missing, unreadable or for another account.</summary>
    public VaultCacheState? Load(string serverUrl, string userId)
    {
        try
        {
            VaultCacheState? state = TgkJson.ReadFile<VaultCacheState>(FilePath);
            return state is not null && state.ServerUrl == serverUrl && state.UserId == userId ? state : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            CoreLog.Warn($"Ignoring unreadable vault cache: {ex.Message}");
            return null;
        }
    }

    /// <summary>Writes atomically (0600 on Unix); failures are logged, the cache is only an optimization.</summary>
    public void Save(VaultCacheState state)
    {
        try
        {
            TgkJson.WriteFileAtomic(FilePath, state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CoreLog.Warn($"Could not write the vault cache: {ex.Message}");
        }
    }

    public void Delete()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CoreLog.Warn($"Could not delete the vault cache: {ex.Message}");
        }
    }
}
