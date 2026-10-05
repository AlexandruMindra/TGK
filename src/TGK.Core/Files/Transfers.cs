using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using TGK.Core.Agents;
using TGK.Core.Ssh;

namespace TGK.Core.Files;

/// <summary>Where a transfer goes, for its icon and wording.</summary>
public enum TransferKind
{
    /// <summary>From this computer to a host.</summary>
    Upload,

    /// <summary>From a host to this computer.</summary>
    Download,

    /// <summary>Between two hosts, within one host, or within this computer.</summary>
    Copy,
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

/// <summary>A consistent view of a transfer's progress (see <see cref="FileTransfer.Snapshot"/>).</summary>
public readonly record struct TransferProgress(TransferState State, long DoneBytes, long TotalBytes, int DoneFiles, int TotalFiles, string? Current, string? Error)
{
    public bool IsFinished => State is TransferState.Done or TransferState.Failed or TransferState.Cancelled;

    /// <summary>0..1, by bytes (by files when everything is empty).</summary>
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)DoneBytes / TotalBytes, 0, 1)
        : TotalFiles > 0 ? (double)DoneFiles / TotalFiles
        : State == TransferState.Done ? 1 : 0;
}

/// <summary>
/// One copy or move of files and folders (see <see cref="TransferQueue"/>): from this computer to a host, back, between
/// two folders of one host, or between two hosts. Folders are copied with everything in them; symbolic links inside
/// folders are skipped (never followed), so a link loop can't make a transfer endless. Files are written under a
/// temporary name and put in place once complete, so an interrupted transfer never leaves a half-written file behind;
/// modification times are kept, and so are permissions (a file that is replaced keeps its own).
/// </summary>
public sealed class FileTransfer
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TransferState _state = TransferState.Queued;
    private long _doneBytes, _totalBytes, _fileBytes;
    private int _doneFiles, _totalFiles;
    private string? _current, _error;

    internal FileTransfer(TransferKind kind, bool move, string title, string destination, Func<FileTransfer, CancellationToken, Task> run)
    {
        Kind = kind;
        IsMove = move;
        Title = title;
        Destination = destination;
        Run = run;
    }

    public TransferKind Kind { get; }

    /// <summary>The originals are removed once copied.</summary>
    public bool IsMove { get; }

    /// <summary>What is copied: one name, or e.g. "3 items".</summary>
    public string Title { get; }

    /// <summary>Where it goes, as shown: a folder (or file) path, with the host's name when it is not obvious.</summary>
    public string Destination { get; }

    /// <summary>The file system the items go to, when known (to refresh a pane showing it).</summary>
    public IFileSystem? Target { get; init; }

    /// <summary>The folder (or file) path the items go to on <see cref="Target"/>.</summary>
    public string? TargetPath { get; init; }

    /// <summary>The file system the items come from, when known (a move changes it too).</summary>
    public IFileSystem? Source { get; init; }

    /// <summary>The folder the items come from on <see cref="Source"/>, when they share one.</summary>
    public string? SourcePath { get; init; }

    /// <summary>A transfer whose bytes are not counted (copied by a host itself): its bar shows activity only.</summary>
    public bool Indeterminate { get; init; }

    /// <summary>Raised (on a background thread) once the transfer ended, however it ended.</summary>
    public event Action<FileTransfer>? Finished;

    /// <summary>Completes (never faults) when the transfer ended.</summary>
    public Task Completion => _completion.Task;

    /// <summary>Items skipped: they already existed and the user chose to skip them, or they are links inside folders.</summary>
    public int Skipped { get; internal set; }

    internal Func<FileTransfer, CancellationToken, Task> Run { get; }

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
/// Transfers run one after the other in the order they were added (browsing goes on meanwhile: its requests share the
/// connections). Each copies between any two file systems: this computer and hosts over SFTP. Between two hosts (or
/// two folders of one host, as SFTP has no copy) each file goes through a private buffer on this computer, so the hosts
/// never need to reach each other; a move within one file system is a rename. Thread-safe; events come on background
/// threads.
/// </summary>
public sealed class TransferQueue : IDisposable
{
    private const string PartSuffix = ".tgk-part";
    private const int MaxDepth = 128;

    private readonly string _bufferRoot;
    private readonly Lock _gate = new();
    private readonly List<FileTransfer> _transfers = [];
    private Task _tail = Task.CompletedTask;
    private bool _disposed;

    /// <param name="bufferRoot">Where files copied between hosts wait (default: <see cref="DefaultBufferRoot"/>).</param>
    public TransferQueue(string? bufferRoot = null) => _bufferRoot = bufferRoot ?? DefaultBufferRoot;

    /// <summary>A per-user folder for files on their way between hosts (next to the copies of edited files).</summary>
    public static string DefaultBufferRoot => Path.Combine(Path.GetDirectoryName(EditedFiles.DefaultRoot)!, OperatingSystem.IsLinux() ? "relay" : "tgk-relay");

    /// <summary>Raised (on any thread) when a transfer was added or finished.</summary>
    public event Action? Changed;

    /// <summary>The transfers added so far, oldest first (finished ones stay until <see cref="ClearFinished"/>).</summary>
    public IReadOnlyList<FileTransfer> Transfers
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
    /// Copies (or with <paramref name="move"/> moves) entries of <paramref name="source"/> into
    /// <paramref name="targetDirectory"/> of <paramref name="target"/>. Before anything is copied,
    /// <paramref name="resolveConflicts"/> is asked about the names that already exist there (null: replace them).
    /// A move within one file system renames; otherwise the originals are deleted once all of them were copied.
    /// </summary>
    public FileTransfer Copy(IFileSystem source, IReadOnlyList<FileEntry> entries, IFileSystem target, string targetDirectory, bool move = false,
        Func<IReadOnlyList<string>, Task<ConflictChoice>>? resolveConflicts = null)
    {
        FileEntry[] items = [.. entries];
        string title = items.Length == 1 ? items[0].Name : $"{items.Length} items";
        TransferKind kind = source.IsLocal && !target.IsLocal ? TransferKind.Upload
            : !source.IsLocal && target.IsLocal ? TransferKind.Download
            : TransferKind.Copy;
        string where = ReferenceEquals(source, target) || target.IsLocal ? targetDirectory : $"{target.DisplayName}:{targetDirectory}";
        return Add(new FileTransfer(kind, move, title, where, (t, ct) => CopyAsync(t, source, items, target, targetDirectory, move, resolveConflicts, ct))
        {
            Source = source, Target = target, TargetPath = targetDirectory,
            SourcePath = items.Select(e => source.Parent(e.Path)).Distinct().Count() == 1 ? source.Parent(items[0].Path) : null,
        });
    }

    /// <summary>Uploads local files and folders (picked, or dropped on the window) into a host's folder.</summary>
    public FileTransfer Upload(IReadOnlyList<string> localPaths, SftpFileSystem target, string remoteDirectory,
        Func<IReadOnlyList<string>, Task<ConflictChoice>>? resolveConflicts = null)
    {
        var entries = new List<FileEntry>();
        foreach (string path in localPaths.Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).Distinct())
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists && info.LinkTarget is null)
                throw new FileOperationException($"{path} no longer exists.");
            entries.Add(LocalFileSystem.FromInfo(info));
        }
        return Copy(LocalFileSystem.Instance, entries, target, remoteDirectory, move: false, resolveConflicts);
    }

    /// <summary>Downloads a host's files and folders into a local folder. Names Windows can't store are adapted.</summary>
    public FileTransfer Download(IReadOnlyList<FileEntry> entries, SftpFileSystem source, string localDirectory,
        Func<IReadOnlyList<string>, Task<ConflictChoice>>? resolveConflicts = null) =>
        Copy(source, entries, LocalFileSystem.Instance, localDirectory, move: false, resolveConflicts);

    /// <summary>Uploads one local file to exactly <paramref name="remotePath"/>, replacing what is there (e.g. a file edited locally).</summary>
    public FileTransfer UploadFile(string localPath, SftpFileSystem target, string remotePath) =>
        Add(new FileTransfer(TransferKind.Upload, false, RemotePath.FileName(remotePath), remotePath, async (t, ct) =>
        {
            var info = new FileInfo(localPath);
            if (!info.Exists)
                throw new FileOperationException($"{localPath} no longer exists.");
            t.SetTotals(info.Length, 1);
            t.SetState(TransferState.Running);
            t.StartFile(info.Name);
            await UploadOneAsync(t, target, localPath, remotePath, info.LastWriteTimeUtc, newMode: null, ct).ConfigureAwait(false);
            t.FileDone(info.Length);
        }) { Target = target, TargetPath = remotePath });

    /// <summary>Downloads one remote file to exactly <paramref name="localPath"/> (replacing it), e.g. to open it locally.</summary>
    public FileTransfer DownloadFile(FileEntry entry, SftpFileSystem source, string localPath) =>
        Add(new FileTransfer(TransferKind.Download, false, entry.Name, localPath, async (t, ct) =>
        {
            t.SetTotals(entry.Size, 1);
            t.SetState(TransferState.Running);
            t.StartFile(entry.Name);
            await DownloadOneAsync(t, source, entry.Path, localPath, entry.Modified.UtcDateTime, entry.Mode, weight: 1, ct).ConfigureAwait(false);
            t.FileDone(entry.Size);
        }) { Source = source });

    /// <summary>
    /// Adds a transfer that runs <paramref name="run"/> (e.g. a copy a host makes itself); it reports its own progress.
    /// </summary>
    public FileTransfer Add(TransferKind kind, bool move, string title, string destination, Func<FileTransfer, CancellationToken, Task> run,
        IFileSystem? source = null, string? sourcePath = null, IFileSystem? target = null, string? targetPath = null, bool indeterminate = false) =>
        Add(new FileTransfer(kind, move, title, destination, run)
        {
            Source = source, SourcePath = sourcePath, Target = target, TargetPath = targetPath, Indeterminate = indeterminate,
        });

    /// <summary>Cancels every transfer that has not finished.</summary>
    public void CancelAll()
    {
        foreach (FileTransfer transfer in Transfers)
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
    /// after itself (removed its partial file). Call before closing the connections.
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

    private FileTransfer Add(FileTransfer transfer)
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

    private static async Task ExecuteAsync(FileTransfer transfer)
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
        catch (Exception ex) when (ex is FileOperationException or SshSessionException or IOException or UnauthorizedAccessException)
        {
            transfer.Finish(TransferState.Failed, ex.Message);
        }
        catch (Exception ex)
        {
            CoreLog.Warn($"Transfer {transfer.Title} failed: {ex}");
            transfer.Finish(TransferState.Failed, ex.Message);
        }
    }

    // ---- copying between any two file systems ----

    /// <summary>One file or folder to create at the target, with what it is copied from.</summary>
    private sealed record Item(string Source, string Target, bool IsDirectory, long Size, DateTime Modified, int Mode);

    private async Task CopyAsync(FileTransfer t, IFileSystem source, FileEntry[] entries, IFileSystem target, string targetDirectory, bool move,
        Func<IReadOnlyList<string>, Task<ConflictChoice>>? resolveConflicts, CancellationToken ct)
    {
        bool same = ReferenceEquals(source, target);
        if (same && entries.Any(e => e.IsDirectory && target.IsWithin(targetDirectory, e.Path)))
            throw new FileOperationException("A folder can't be copied or moved into itself.");
        if (same && entries.All(e => target.Parent(e.Path) == targetDirectory))
            throw new FileOperationException("The items are already in that folder.");
        if (target.IsLocal)
            Directory.CreateDirectory(targetDirectory);

        // Name conflicts are decided per top-level item, before anything is copied.
        var roots = new List<FileEntry>();
        var conflicts = new List<string>();
        foreach (FileEntry entry in entries)
        {
            if (await target.StatAsync(target.Join(targetDirectory, TargetName(target, entry.Name)), ct).ConfigureAwait(false) is not null)
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

        // A move within one file system is a rename (a replaced item is removed first).
        if (move && same)
        {
            t.SetTotals(0, roots.Count);
            t.SetState(TransferState.Running);
            foreach (FileEntry entry in roots)
            {
                ct.ThrowIfCancellationRequested();
                t.StartFile(entry.Name);
                string to = target.Join(targetDirectory, entry.Name);
                if (conflicts.Contains(entry.Name) && await target.StatAsync(to, ct).ConfigureAwait(false) is { } existing)
                    await target.DeleteAsync(existing, null, ct).ConfigureAwait(false);
                await target.RenameAsync(entry, to, ct).ConfigureAwait(false);
                t.FileDone(0);
            }
            return;
        }

        var items = new List<Item>();
        foreach (FileEntry root in roots)
            await WalkAsync(t, source, root, target, target.Join(targetDirectory, TargetName(target, root.Name)), items, depth: 0, ct).ConfigureAwait(false);
        t.SetTotals(items.Where(i => !i.IsDirectory).Sum(i => i.Size), items.Count(i => !i.IsDirectory));
        t.SetState(TransferState.Running);

        string? buffer = null;
        try
        {
            foreach (Item item in items)
            {
                ct.ThrowIfCancellationRequested();
                if (item.IsDirectory)
                {
                    await EnsureDirectoryAsync(target, item.Target, ct).ConfigureAwait(false);
                    continue;
                }
                t.StartFile(source.NameOf(item.Source));
                switch (source, target)
                {
                    case (LocalFileSystem, LocalFileSystem):
                        await CopyLocalAsync(t, item, ct).ConfigureAwait(false);
                        break;
                    case (LocalFileSystem, SftpFileSystem remote):
                        await UploadOneAsync(t, remote, item.Source, item.Target, item.Modified, NewMode(item), ct).ConfigureAwait(false);
                        break;
                    case (SftpFileSystem remote, LocalFileSystem):
                        await DownloadOneAsync(t, remote, item.Source, item.Target, item.Modified, item.Mode, weight: 1, ct).ConfigureAwait(false);
                        break;
                    case (SftpFileSystem from, SftpFileSystem to):
                        buffer ??= CreateBuffer();
                        await RelayOneAsync(t, from, to, item, buffer, ct).ConfigureAwait(false);
                        break;
                    default:
                        throw new FileOperationException($"Copying from {source.DisplayName} to {target.DisplayName} is not supported.");
                }
                t.FileDone(item.Size);
            }
        }
        finally
        {
            if (buffer is not null)
                DeleteQuietly(buffer);
        }

        // A move removes the originals only once everything was copied.
        if (move)
        {
            ct.ThrowIfCancellationRequested();
            foreach (FileEntry root in roots)
                await source.DeleteAsync(root, null, ct).ConfigureAwait(false);
        }
    }

    // Names a Windows computer can't store are adapted there; hosts take them as they are.
    private static string TargetName(IFileSystem target, string name) => target.IsLocal ? LocalNames.ToLocal(name) : name;

    private static int? NewMode(Item item) => item.Mode > 0 ? item.Mode : null;

    // A folder is listed before its contents. A selected link is copied as what it points to; links met inside
    // folders are skipped, and so are devices, sockets and pipes.
    private static async Task WalkAsync(FileTransfer t, IFileSystem source, FileEntry entry, IFileSystem target, string targetPath,
        List<Item> items, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > MaxDepth)
            throw new FileOperationException($"{entry.Path} is nested more than {MaxDepth} folders deep; copy a folder further down instead.");
        if ((entry.Kind == FileEntryKind.Symlink && (depth > 0 || entry.IsBrokenLink)) || entry.Kind == FileEntryKind.Other)
        {
            t.Skipped++;
            return;
        }
        if (!entry.IsDirectory)
        {
            items.Add(new Item(entry.Path, targetPath, false, entry.Size, entry.Modified.UtcDateTime, entry.Mode));
            return;
        }
        items.Add(new Item(entry.Path, targetPath, true, 0, default, entry.Mode));
        IReadOnlyList<FileEntry> children = await source.ListAsync(entry.Path, ct).ConfigureAwait(false);
        foreach (FileEntry child in children.OrderBy(c => c.Name, StringComparer.Ordinal))
            await WalkAsync(t, source, child, target, target.Join(targetPath, TargetName(target, child.Name)), items, depth + 1, ct).ConfigureAwait(false);
    }

    private static async Task EnsureDirectoryAsync(IFileSystem target, string path, CancellationToken ct)
    {
        FileEntry? existing = await target.StatAsync(path, ct).ConfigureAwait(false);
        if (existing is { IsDirectory: true })
            return;
        if (existing is not null)
            throw new FileOperationException($"Can't create the folder {path}: a file has that name.");
        await target.CreateDirectoryAsync(path, ct).ConfigureAwait(false);
    }

    private string CreateBuffer()
    {
        string path = Path.Combine(_bufferRoot, Guid.NewGuid().ToString("N")[..12]);
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    // Between hosts (or two folders of one host): down into the private buffer, then up; each counts for half.
    private static async Task RelayOneAsync(FileTransfer t, SftpFileSystem from, SftpFileSystem to, Item item, string buffer, CancellationToken ct)
    {
        string local = Path.Combine(buffer, Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await DownloadOneAsync(t, from, item.Source, local, item.Modified, item.Mode, weight: 0.5, ct).ConfigureAwait(false);
            await UploadOneAsync(t, to, local, item.Target, item.Modified, NewMode(item), ct, progressOffset: item.Size / 2, weight: 0.5).ConfigureAwait(false);
        }
        finally
        {
            DeleteQuietly(local);
        }
    }

    // ---- this computer ----

    private static async Task CopyLocalAsync(FileTransfer t, Item item, CancellationToken ct)
    {
        if (Directory.Exists(item.Target))
            throw new FileOperationException($"Can't replace the folder {item.Target} with a file.");
        string directory = Path.GetDirectoryName(item.Target) ?? ".";
        string temp = Path.Combine(directory, $".{Path.GetFileName(item.Target)}.{Guid.NewGuid().ToString("N")[..8]}{PartSuffix}");
        bool placed = false;
        try
        {
            await using (var input = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true))
            await using (FileStream output = new(temp, PrivateCreate()))
            {
                byte[] chunk = new byte[256 * 1024];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    t.FileProgress(done);
                }
            }
            File.SetLastWriteTimeUtc(temp, item.Modified);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, File.Exists(item.Target) ? File.GetUnixFileMode(item.Target) : (UnixFileMode)(item.Mode & 0x1FF));
            File.Move(temp, item.Target, overwrite: true);
            placed = true;
        }
        finally
        {
            if (!placed)
                DeleteQuietly(temp);
        }
    }

    private static FileStreamOptions PrivateCreate()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 64 * 1024, Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; // private until it is complete
        return options;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ---- uploads ----

    /// <param name="newMode">The permissions of a new file (an existing one keeps its own); null: the local file's.</param>
    private static async Task UploadOneAsync(FileTransfer t, SftpFileSystem files, string localPath, string remotePath, DateTime modified, int? newMode,
        CancellationToken ct, long progressOffset = 0, double weight = 1)
    {
        FileEntry? existing = await files.StatAsync(remotePath, ct).ConfigureAwait(false);
        if (existing is { IsDirectory: true })
            throw new FileOperationException($"Can't replace the folder {remotePath} with a file.");
        // A link is replaced by writing through it: its target (where the server resolves it) gets the new content.
        string target = existing is { Kind: FileEntryKind.Symlink, IsBrokenLink: false }
            ? await files.RealPathAsync(remotePath, ct).ConfigureAwait(false)
            : remotePath;
        string temp = SftpFileSystem.Join(RemotePath.Directory(target), $".{RemotePath.FileName(target)}.{Guid.NewGuid().ToString("N")[..8]}{PartSuffix}");
        SftpClient client = files.OpenClient();
        bool placed = false;
        try
        {
            try
            {
                // The partial copy is private until it is complete and gets its final permissions.
                await files.Run(remotePath, "upload", async () =>
                {
                    await using (await client.OpenAsync(temp, FileMode.CreateNew, FileAccess.Write, ct).ConfigureAwait(false))
                    {
                    }
                }).ConfigureAwait(false);
                await TryAsync(() => Task.Run(() => client.ChangePermissions(temp, 600), ct)).ConfigureAwait(false);
                await SendAsync(temp, canOverride: true).ConfigureAwait(false);
            }
            catch (FileOperationException ex) when (existing is not null && ex.InnerException is SftpPermissionDeniedException)
            {
                // The folder is not writable but the file is (e.g. a config file one may edit): write it in place,
                // which keeps its owner and permissions.
                await files.DeleteQuietlyAsync(temp).ConfigureAwait(false);
                await SendAsync(target, canOverride: true).ConfigureAwait(false);
                placed = true;
                return;
            }
            ct.ThrowIfCancellationRequested();

            // An existing file (or a link's target) keeps its permissions; a new one gets the source's. Servers that
            // refuse to set them (or the time) still get the content.
            int mode = existing is { IsBrokenLink: false } ? existing.Mode : newMode ?? LocalMode(localPath);
            await TryAsync(async () =>
            {
                SftpFileAttributes attributes = await client.GetAttributesAsync(temp, ct).ConfigureAwait(false);
                attributes.LastWriteTimeUtc = modified;
                attributes.LastAccessTimeUtc = DateTime.UtcNow;
                attributes.SetPermissions(short.Parse(Convert.ToString(mode & 0xFFF, 8), CultureInfo.InvariantCulture));
                await Task.Run(() => client.SetAttributes(temp, attributes), ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
            await files.ReplaceAsync(temp, target, ct).ConfigureAwait(false);
            placed = true;
        }
        finally
        {
            if (!placed)
                await files.DeleteQuietlyAsync(temp).ConfigureAwait(false);
        }

        Task SendAsync(string path, bool canOverride) => files.Run(remotePath, "upload", async () =>
        {
            t.FileProgress(progressOffset);
            await using var source = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
            var progress = new SyncProgress<UploadFileProgressReport>(r => t.FileProgress(progressOffset + (long)(r.TotalBytesUploaded * weight)));
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

    private static async Task DownloadOneAsync(FileTransfer t, SftpFileSystem files, string remotePath, string localPath, DateTime modified, int mode,
        double weight, CancellationToken ct)
    {
        if (Directory.Exists(localPath))
            throw new FileOperationException($"Can't replace the folder {localPath} with a file.");
        string directory = Path.GetDirectoryName(localPath) ?? ".";
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, $".{Path.GetFileName(localPath)}.{Guid.NewGuid().ToString("N")[..8]}{PartSuffix}");
        SftpClient client = files.OpenClient();
        bool placed = false;
        try
        {
            await files.Run(remotePath, "download", async () =>
            {
                await using var target = new FileStream(temp, PrivateCreate());
                var progress = new SyncProgress<DownloadFileProgressReport>(r => t.FileProgress((long)(r.TotalBytesDownloaded * weight)));
                await client.DownloadFileAsync(remotePath, target, progress, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (modified != default)
                File.SetLastWriteTimeUtc(temp, modified);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, (UnixFileMode)(mode & 0x1FF) | UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, localPath, overwrite: true);
            placed = true;
        }
        finally
        {
            if (!placed)
                DeleteQuietly(temp);
        }
    }

    /// <summary>Reports on the thread that reports (unlike <see cref="Progress{T}"/>, which posts to a captured context).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
