using System;
using System.Collections.Generic;
using System.Threading;
using Blossom;
using TGK.Client.Controls;
using TGK.Core.Files;
using TGK.Core.Ssh;

namespace TGK.Client.Files;

// Keeps the folder shown up to date with changes made elsewhere (a command in a terminal, another program, another
// user): while the pane is on screen it lists the folder again every few seconds, in the background, and updates the
// list only when something changed (selection, cursor and scroll stay). SFTP has no change notifications, so this
// polls; a slow listing (a huge folder, a slow link) polls less often.
public sealed partial class FilesTabContent
{
    private const long MinPollMs = 2_000, MaxPollMs = 30_000;
    private const int PollSlowdown = 10; // the interval is at least this many times the last listing's duration

    private CancellationTokenSource? _poll;
    private long _nextPollMs;
    private int _listingVersion; // bumped by every Load: a poll that overlapped one is stale

    private void PollFolder()
    {
        if (!IsShown)
        {
            _nextPollMs = 0; // back on screen: look at once
            return;
        }
        if (_poll is not null || _loading || _closing || _drag is not null || !Connected
            || _path is not { } path || _fs is not { } fs || UiClock.NowMs < _nextPollMs)
            return;
        Poll(fs, path);
    }

    private async void Poll(IFileSystem fs, string path)
    {
        var cts = _poll = new CancellationTokenSource();
        int version = _listingVersion;
        long started = UiClock.NowMs;
        IReadOnlyList<FileEntry>? entries = null;
        try
        {
            entries = await fs.ListAsync(path, cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or FileOperationException or SshSessionException)
        {
            // Cancelled by a navigation, or a failure the next listing (or the Lost banner) reports.
        }
        catch (Exception ex)
        {
            Log.Error($"Watching {path} failed: {ex}"); // never out of this async void
        }
        finally
        {
            if (_poll == cts)
                _poll = null;
            cts.Dispose();
            _nextPollMs = UiClock.NowMs + Math.Clamp((UiClock.NowMs - started) * PollSlowdown, MinPollMs, MaxPollMs);
        }
        if (entries is null || cts.IsCancellationRequested || _closing || version != _listingVersion
            || _loading || fs != _fs || path != _path || SameEntries(entries, _list.Entries))
            return;
        _list.SetEntries(entries, select: null, keepSelection: true);
    }

    private void CancelPoll()
    {
        _poll?.Cancel();
        _poll = null;
    }

    private static bool SameEntries(IReadOnlyList<FileEntry> a, IReadOnlyList<FileEntry> b)
    {
        if (a.Count != b.Count)
            return false;
        var byPath = new Dictionary<string, FileEntry>(b.Count, StringComparer.Ordinal);
        foreach (FileEntry entry in b)
            byPath[entry.Path] = entry;
        foreach (FileEntry entry in a)
        {
            if (!byPath.TryGetValue(entry.Path, out FileEntry? old) || old != entry)
                return false;
        }
        return true;
    }
}
