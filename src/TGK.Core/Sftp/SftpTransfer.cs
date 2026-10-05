using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using TGK.Core.Agents;
using TGK.Core.Ssh;

namespace TGK.Core.Sftp;

public enum TransferDirection
{
    Upload,
    Download,
}

public enum TransferState
{
    /// <summary>Waiting for the transfers before it.</summary>
    Queued,

    /// <summary>Finding the files (walking folders) and checking for name conflicts.</summary>
    Preparing,

    Running,
    Done,
    Failed,
    Cancelled,
}

/// <summary>What to do with items that already exist at the destination.</summary>
public enum ConflictChoice
{
    Replace,
    Skip,
    Cancel,
}

/// <summary>A consistent view of a transfer's progress (see <see cref="SftpTransfer.Snapshot"/>).</summary>
public readonly record struct TransferProgress(TransferState State, long DoneBytes, long TotalBytes, int DoneFiles, int TotalFiles, string? Current, string? Error)
{
    public bool IsFinished => State is TransferState.Done or TransferState.Failed or TransferState.Cancelled;

    /// <summary>0..1, by bytes (by files when everything is empty).</summary>
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)DoneBytes / TotalBytes, 0, 1)
        : TotalFiles > 0 ? (double)DoneFiles / TotalFiles
        : State == TransferState.Done ? 1 : 0;
}

/// <summary>
/// One upload or download of files and folders between this computer and a host (see <see cref="SftpTransferQueue"/>).
/// Folders are copied with everything in them; symbolic links inside folders are skipped (never followed), so a link
/// loop can't make a transfer endless. Files are written under a temporary name and put in place once complete, so an
/// interrupted transfer never leaves a half-written file behind; modification times are kept, and so are permissions
/// (an upload over an existing file keeps that file's permissions).
/// </summary>
public sealed class SftpTransfer
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TransferState _state = TransferState.Queued;
    private long _doneBytes, _totalBytes, _fileBytes;
    private int _doneFiles, _totalFiles;
    private string? _current, _error;

    internal SftpTransfer(TransferDirection direction, string title, string destination, Func<SftpTransfer, CancellationToken, Task> run)
    {
        Direction = direction;
        Title = title;
        Destination = destination;
        Run = run;
    }

    public TransferDirection Direction { get; }

    /// <summary>What is copied: one name, or e.g. "3 items".</summary>
    public string Title { get; }

    /// <summary>The folder (or file) it goes to: a remote path for uploads, a local one for downloads.</summary>
    public string Destination { get; }

    /// <summary>Raised (on a background thread) once the transfer ended, however it ended.</summary>
    public event Action<SftpTransfer>? Finished;

    /// <summary>Completes (never faults) when the transfer ended.</summary>
    public Task Completion => _completion.Task;

    /// <summary>Items skipped because they already existed and the user chose to skip them, or because they are links inside folders.</summary>
    public int Skipped { get; internal set; }

    internal Func<SftpTransfer, CancellationToken, Task> Run { get; }

    internal CancellationToken Token => _cancel.Token;

    public TransferProgress Snapshot()
    {
        lock (_gate)
            return new TransferProgress(_state, _doneBytes + _fileBytes, _totalBytes, _doneFiles, _totalFiles, _current, _error);
    }

    /// <summary>Stops the transfer (or drops it from the queue); what was not finished is removed.</summary>
    public void Cancel()
    {
        try
        {
            _cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal void SetState(TransferState state)
    {
        lock (_gate)
            _state = state;
    }

    internal void SetTotals(long bytes, int files)
    {
        lock (_gate)
        {
            _totalBytes = bytes;
            _totalFiles = files;
        }
    }

    internal void StartFile(string name)
    {
        lock (_gate)
        {
            _current = name;
            _fileBytes = 0;
        }
    }

    internal void FileProgress(long bytes)
    {
        lock (_gate)
            _fileBytes = bytes;
    }

    internal void FileDone(long size)
    {
        lock (_gate)
        {
            _doneBytes += size;
            _fileBytes = 0;
            _doneFiles++;
        }
    }

    internal void Finish(TransferState state, string? error = null)
    {
        lock (_gate)
        {
            if (_state is TransferState.Done or TransferState.Failed or TransferState.Cancelled)
                return;
            _state = state;
            _error = error;
            _current = null;
            _fileBytes = 0;
        }
        _cancel.Dispose();
        _completion.TrySetResult();
        Finished?.Invoke(this);
    }
}

/// <summary>
/// The transfers of one connection, run one after the other in the order they were added (browsing goes on meanwhile:
/// SFTP requests of both share the connection). Threading: everything here is thread-safe; events come on background
/// threads.
/// </summary>
public sealed class SftpTransferQueue : IDisposable
{
    private const string PartSuffix = ".tgk-part";
    private const int MaxDepth = 128;

    private readonly SftpFileSystem _files;
    private readonly Lock _gate = new();
    private readonly List<SftpTransfer> _transfers = [];
    private Task _tail = Task.CompletedTask;
    private bool _disposed;

    public SftpTransferQueue(SftpFileSystem files) => _files = files ?? throw new ArgumentNullException(nameof(files));

    /// <summary>Raised (on any thread) when a transfer was added or finished.</summary>
    public event Action? Changed;

    /// <summary>The transfers added so far, oldest first (finished ones stay until <see cref="ClearFinished"/>).</summary>
    public IReadOnlyList<SftpTransfer> Transfers
    {
        get
        {
            lock (_gate)
                return [.. _transfers];
        }
    }

    /// <summary>Removes the finished transfers from <see cref="Transfers"/>.</summary>
    public void ClearFinished()
    {
        lock (_gate)
            _transfers.RemoveAll(t => t.Snapshot().IsFinished);
        Changed?.Invoke();
    }

    /// <summary>
    /// Uploads local files and folders into <paramref name="remoteDirectory"/>. Before anything is copied,
    /// <paramref name="resolveConflicts"/> is asked about the names that already exist there (null: replace them).
    /// </summary>
    public SftpTransfer Upload(IReadOnlyList<string> localPaths, string remoteDirectory,
        Func<IReadOnlyList<string>, Task<ConflictChoice>>? resolveConflicts = null)
    {
        string[] paths = localPaths.Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).Distinct().ToArray();
        string title = paths.Length == 1 ? Path.GetFileName(paths[0]) : $"{paths.Length} items";
        return Add(new SftpTransfer(TransferDirection.Upload, title, remoteDirectory,
            (t, ct) => UploadAsync(t, paths, remoteDirectory, resolveConflicts, ct)));
    }

    /// <summary>Uploads one local file to exactly <paramref name="remotePath"/>, replacing what is there (e.g. a file edited locally).</summary>
    public SftpTransfer UploadFile(string localPath, string remotePath) =>
        Add(new SftpTransfer(TransferDirection.Upload, RemotePath.FileName(remotePath), remotePath, async (t, ct) =>
        {
            var info = new FileInfo(localPath);
            if (!info.Exists)
                throw new SftpOperationException($"{localPath} no longer exists.");
            t.SetTotals(info.Length, 1);
            t.SetState(TransferState.Running);
            await UploadOneAsync(t, new LocalItem(localPath, remotePath, false, info.Length, info.LastWriteTimeUtc), ct).ConfigureAwait(false);
        }));

    /// <summary>
    /// Downloads remote files and folders into <paramref name="localDirectory"/>. <paramref name="resolveConflicts"/>
    /// is asked about the names that already exist there (null: replace them). Names Windows can't store are adapted.
    /// </summary>
    public SftpTransfer Download(IReadOnlyList<SftpEntry> entries, string localDirectory,
        Func<IReadOnlyList<string>, Task<ConflictChoice>>? resolveConflicts = null)
    {
        SftpEntry[] items = [.. entries];
        string title = items.Length == 1 ? items[0].Name : $"{items.Length} items";
        return Add(new SftpTransfer(TransferDirection.Download, title, localDirectory,
            (t, ct) => DownloadAsync(t, items, localDirectory, resolveConflicts, ct)));
    }

    /// <summary>Downloads one remote file to exactly <paramref name="localPath"/> (replacing it), e.g. to open it locally.</summary>
    public SftpTransfer DownloadFile(SftpEntry entry, string localPath) =>
        Add(new SftpTransfer(TransferDirection.Download, entry.Name, localPath, async (t, ct) =>
        {
            t.SetTotals(entry.Size, 1);
            t.SetState(TransferState.Running);
            await DownloadOneAsync(t, new RemoteItem(entry.Path, localPath, false, entry.Size, entry.Modified.UtcDateTime, entry.Mode), ct).ConfigureAwait(false);
        }));

    /// <summary>Cancels every transfer that has not finished.</summary>
    public void CancelAll()
    {
        foreach (SftpTransfer transfer in Transfers)
            transfer.Cancel();
    }

    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
        CancelAll();
    }

    /// <summary>
    /// Cancels everything and waits (at most <paramref name="timeout"/>) until the running transfer has cleaned up
    /// after itself (removed its partial file). Call before closing the connection.
    /// </summary>
    public async Task CloseAsync(TimeSpan timeout)
    {
        Task tail;
        lock (_gate)
        {
            _disposed = true;
            tail = _tail;
        }
        CancelAll();
        await Task.WhenAny(tail, Task.Delay(timeout)).ConfigureAwait(false);
    }

    private SftpTransfer Add(SftpTransfer transfer)
    {
        transfer.Finished += _ => Changed?.Invoke();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _transfers.Add(transfer);
            Task previous = _tail;
            _tail = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                await ExecuteAsync(transfer).ConfigureAwait(false);
            });
        }
        Changed?.Invoke();
        return transfer;
    }

    private async Task ExecuteAsync(SftpTransfer transfer)
    {
        CancellationToken ct = transfer.Token;
        if (ct.IsCancellationRequested)
        {
            transfer.Finish(TransferState.Cancelled);
            return;
        }
        transfer.SetState(TransferState.Preparing);
        try
        {
            await transfer.Run(transfer, ct).ConfigureAwait(false);
            transfer.Finish(ct.IsCancellationRequested ? TransferState.Cancelled : TransferState.Done);
        }
        catch (OperationCanceledException)
        {
            transfer.Finish(TransferState.Cancelled);
        }
        catch (Exception ex) when (ex is SftpOperationException or SshSessionException or IOException or UnauthorizedAccessException)
        {
            transfer.Finish(TransferState.Failed, ex.Message);
        }
        catch (Exception ex)
        {
            CoreLog.Warn($"Transfer {transfer.Title} failed: {ex}");
            transfer.Finish(TransferState.Failed, ex.Message);
        }
    }

    // ---- uploads ----

    private sealed record LocalItem(string Local, string Remote, bool IsDirectory, long Size, DateTime Modified);

    private async Task UploadAsync(SftpTransfer t, string[] paths, string remoteDirectory,
        Func<IReadOnlyList<string>, Task<ConflictChoice>>? resolveConflicts, CancellationToken ct)
    {
        // Name conflicts are decided per top-level item, before anything is copied.
        var roots = new List<string>();
        var conflicts = new List<string>();
        foreach (string path in paths)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new SftpOperationException($"{path} no longer exists.");
            string name = Path.GetFileName(path);
            if (await _files.StatAsync(SftpFileSystem.Join(remoteDirectory, name), ct).ConfigureAwait(false) is not null)
                conflicts.Add(name);
            roots.Add(path);
        }
        if (conflicts.Count > 0 && resolveConflicts is not null)
        {
            switch (await resolveConflicts(conflicts).ConfigureAwait(false))
            {
                case ConflictChoice.Cancel:
                    throw new OperationCanceledException();
                case ConflictChoice.Skip:
                    roots.RemoveAll(p => conflicts.Contains(Path.GetFileName(p)));
                    t.Skipped += conflicts.Count;
                    break;
            }
        }

        var items = new List<LocalItem>();
        foreach (string root in roots)
            items.AddRange(WalkLocal(t, root, SftpFileSystem.Join(remoteDirectory, Path.GetFileName(root)), ct));
        t.SetTotals(items.Where(i => !i.IsDirectory).Sum(i => i.Size), items.Count(i => !i.IsDirectory));
        t.SetState(TransferState.Running);

        foreach (LocalItem item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.IsDirectory)
                await EnsureRemoteDirectoryAsync(item.Remote, ct).ConfigureAwait(false);
            else
                await UploadOneAsync(t, item, ct).ConfigureAwait(false);
        }
    }

    // A folder is listed before its contents; links inside folders are skipped.
    private static IEnumerable<LocalItem> WalkLocal(SftpTransfer t, string root, string remote, CancellationToken ct)
    {
        if (File.Exists(root))
        {
            var file = new FileInfo(root);
            yield return new LocalItem(root, remote, false, file.Length, file.LastWriteTimeUtc);
            yield break;
        }
        yield return new LocalItem(root, remote, true, 0, default);
        var pending = new Stack<(DirectoryInfo Dir, string Remote)>();
        pending.Push((new DirectoryInfo(root), remote));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            (DirectoryInfo dir, string target) = pending.Pop();
            FileSystemInfo[] children;
            try
            {
                children = dir.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                throw new SftpOperationException($"Can't read {dir.FullName}: {ex.Message}", ex);
            }
            foreach (FileSystemInfo child in children.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                if (child.LinkTarget is not null || child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    t.Skipped++;
                    continue;
                }
                string childRemote = SftpFileSystem.Join(target, child.Name);
                if (child is DirectoryInfo sub)
                {
                    yield return new LocalItem(sub.FullName, childRemote, true, 0, default);
                    pending.Push((sub, childRemote));
                }
                else if (child is FileInfo file)
                {
                    yield return new LocalItem(file.FullName, childRemote, false, file.Length, file.LastWriteTimeUtc);
                }
            }
        }
    }

    private async Task EnsureRemoteDirectoryAsync(string path, CancellationToken ct)
    {
        SftpEntry? existing = await _files.StatAsync(path, ct).ConfigureAwait(false);
        if (existing is { IsDirectory: true })
            return;
        if (existing is not null)
            throw new SftpOperationException($"Can't create the folder {path}: a file has that name.");
        await _files.Run(path, "create", () => _files.OpenClient().CreateDirectoryAsync(path, ct)).ConfigureAwait(false);
    }

    private async Task UploadOneAsync(SftpTransfer t, LocalItem item, CancellationToken ct)
    {
        t.StartFile(Path.GetFileName(item.Local));
        SftpEntry? existing = await _files.StatAsync(item.Remote, ct).ConfigureAwait(false);
        if (existing is { IsDirectory: true })
            throw new SftpOperationException($"Can't replace the folder {item.Remote} with a file.");
        // A link is replaced by writing through it: its target (where the server resolves it) gets the new content.
        string target = existing is { Kind: SftpEntryKind.Symlink, IsBrokenLink: false }
            ? await _files.RealPathAsync(item.Remote, ct).ConfigureAwait(false)
            : item.Remote;
        string temp = SftpFileSystem.Join(RemotePath.Directory(target), $".{RemotePath.FileName(target)}.{Guid.NewGuid().ToString("N")[..8]}{PartSuffix}");
        SftpClient client = _files.OpenClient();
        bool placed = false;
        try
        {
            try
            {
                // The partial copy is private until it is complete and gets its final permissions.
                await _files.Run(item.Remote, "upload", async () =>
                {
                    await using (await client.OpenAsync(temp, FileMode.CreateNew, FileAccess.Write, ct).ConfigureAwait(false))
                    {
                    }
                }).ConfigureAwait(false);
                await TryAsync(() => Task.Run(() => client.ChangePermissions(temp, 600), ct)).ConfigureAwait(false);
                await SendAsync(temp, canOverride: true).ConfigureAwait(false);
            }
            catch (SftpOperationException ex) when (existing is not null && ex.InnerException is SftpPermissionDeniedException)
            {
                // The folder is not writable but the file is (e.g. a config file one may edit): write it in place,
                // which keeps its owner and permissions.
                await _files.DeleteQuietlyAsync(temp).ConfigureAwait(false);
                await SendAsync(target, canOverride: true).ConfigureAwait(false);
                placed = true;
                t.FileDone(item.Size);
                return;
            }
            ct.ThrowIfCancellationRequested();

            // An existing file (or a link's target) keeps its permissions; a new one gets the local file's. Servers
            // that refuse to set them (or the time) still get the content.
            int mode = existing is { IsBrokenLink: false } ? existing.Mode : LocalMode(item.Local);
            await TryAsync(async () =>
            {
                SftpFileAttributes attributes = await client.GetAttributesAsync(temp, ct).ConfigureAwait(false);
                attributes.LastWriteTimeUtc = item.Modified;
                attributes.LastAccessTimeUtc = DateTime.UtcNow;
                attributes.SetPermissions(short.Parse(Convert.ToString(mode & 0xFFF, 8), System.Globalization.CultureInfo.InvariantCulture));
                await Task.Run(() => client.SetAttributes(temp, attributes), ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
            await _files.ReplaceAsync(temp, target, ct).ConfigureAwait(false);
            placed = true;
        }
        finally
        {
            if (!placed)
                await _files.DeleteQuietlyAsync(temp).ConfigureAwait(false);
        }
        t.FileDone(item.Size);

        Task SendAsync(string path, bool canOverride) => _files.Run(item.Remote, "upload", async () =>
        {
            t.FileProgress(0);
            await using var source = new FileStream(item.Local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
            var progress = new SyncProgress<UploadFileProgressReport>(r => t.FileProgress((long)r.TotalBytesUploaded));
            await client.UploadFileAsync(source, path, canOverride, progress, ct).ConfigureAwait(false);
        });
    }

    // Optional steps (permissions, times): a refusal is logged, not a failed transfer. Cancellation and a lost
    // connection still end it.
    private static async Task TryAsync(Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SshException and not SshConnectionException)
        {
            CoreLog.Warn($"Transfer: an optional step failed: {ex.Message}");
        }
    }

    /// <summary>The local file's permissions for its new remote copy: its own on Unix, 0644 (0755 for scripts) on Windows.</summary>
    private static int LocalMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                return (int)File.GetUnixFileMode(path) & 0x1FF;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return path.EndsWith(".sh", StringComparison.OrdinalIgnoreCase) ? 0x1ED : 0x1A4;
    }

    // ---- downloads ----

    private sealed record RemoteItem(string Remote, string Local, bool IsDirectory, long Size, DateTime Modified, int Mode);

    private async Task DownloadAsync(SftpTransfer t, SftpEntry[] entries, string localDirectory,
        Func<IReadOnlyList<string>, Task<ConflictChoice>>? resolveConflicts, CancellationToken ct)
    {
        Directory.CreateDirectory(localDirectory);
        var roots = new List<SftpEntry>();
        var conflicts = new List<string>();
        foreach (SftpEntry entry in entries)
        {
            string local = Path.Combine(localDirectory, LocalNames.ToLocal(entry.Name));
            if (File.Exists(local) || Directory.Exists(local))
                conflicts.Add(entry.Name);
            roots.Add(entry);
        }
        if (conflicts.Count > 0 && resolveConflicts is not null)
        {
            switch (await resolveConflicts(conflicts).ConfigureAwait(false))
            {
                case ConflictChoice.Cancel:
                    throw new OperationCanceledException();
                case ConflictChoice.Skip:
                    roots.RemoveAll(e => conflicts.Contains(e.Name));
                    t.Skipped += conflicts.Count;
                    break;
            }
        }

        var items = new List<RemoteItem>();
        foreach (SftpEntry root in roots)
            await WalkRemoteAsync(t, root, Path.Combine(localDirectory, LocalNames.ToLocal(root.Name)), items, depth: 0, ct).ConfigureAwait(false);
        t.SetTotals(items.Where(i => !i.IsDirectory).Sum(i => i.Size), items.Count(i => !i.IsDirectory));
        t.SetState(TransferState.Running);

        foreach (RemoteItem item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.IsDirectory)
            {
                if (File.Exists(item.Local))
                    throw new SftpOperationException($"Can't create the folder {item.Local}: a file has that name.");
                Directory.CreateDirectory(item.Local);
            }
            else
            {
                await DownloadOneAsync(t, item, ct).ConfigureAwait(false);
            }
        }
    }

    // A selected link to a folder is copied as that folder; links met inside folders are skipped.
    private async Task WalkRemoteAsync(SftpTransfer t, SftpEntry entry, string local, List<RemoteItem> items, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        bool top = depth == 0;
        if (depth > MaxDepth)
            throw new SftpOperationException($"{entry.Path} is nested more than {MaxDepth} folders deep; download a folder further down instead.");
        if (entry.Kind == SftpEntryKind.Symlink && (!top || entry.IsBrokenLink))
        {
            t.Skipped++;
            return;
        }
        if (entry.Kind == SftpEntryKind.Other)
        {
            t.Skipped++;
            return;
        }
        if (!entry.IsDirectory)
        {
            items.Add(new RemoteItem(entry.Path, local, false, entry.Size, entry.Modified.UtcDateTime, entry.Mode));
            return;
        }
        items.Add(new RemoteItem(entry.Path, local, true, 0, default, entry.Mode));
        IReadOnlyList<SftpEntry> children = await _files.ListAsync(entry.Path, ct).ConfigureAwait(false);
        foreach (SftpEntry child in children.OrderBy(c => c.Name, StringComparer.Ordinal))
            await WalkRemoteAsync(t, child, Path.Combine(local, LocalNames.ToLocal(child.Name)), items, depth + 1, ct).ConfigureAwait(false);
    }

    private async Task DownloadOneAsync(SftpTransfer t, RemoteItem item, CancellationToken ct)
    {
        t.StartFile(RemotePath.FileName(item.Remote));
        if (Directory.Exists(item.Local))
            throw new SftpOperationException($"Can't replace the folder {item.Local} with a file.");
        string directory = Path.GetDirectoryName(item.Local) ?? ".";
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, $".{Path.GetFileName(item.Local)}.{Guid.NewGuid().ToString("N")[..8]}{PartSuffix}");
        SftpClient client = _files.OpenClient();
        bool placed = false;
        try
        {
            await _files.Run(item.Remote, "download", async () =>
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 64 * 1024, Options = FileOptions.Asynchronous,
                };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; // private until it is complete
                await using var target = new FileStream(temp, options);
                var progress = new SyncProgress<DownloadFileProgressReport>(r => t.FileProgress((long)r.TotalBytesDownloaded));
                await client.DownloadFileAsync(item.Remote, target, progress, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (item.Modified != default)
                File.SetLastWriteTimeUtc(temp, item.Modified);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, (UnixFileMode)(item.Mode & 0x1FF) | UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, item.Local, overwrite: true);
            placed = true;
        }
        finally
        {
            if (!placed)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        t.FileDone(item.Size);
    }

    /// <summary>Reports on the thread that reports (unlike <see cref="Progress{T}"/>, which posts to a captured context).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
