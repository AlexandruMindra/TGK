using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace TGK.Core.Sftp;

/// <summary>
/// Remote files opened in a program on this computer: each is downloaded into a private temporary folder and watched,
/// and every time it is saved there (and its content really changed) <see cref="Saved"/> asks for it to be uploaded
/// back. <see cref="Dispose"/> stops watching and deletes the copies.
/// </summary>
/// <remarks>
/// Editors save in different ways (in place, or a new file renamed over the old one, sometimes several writes in a
/// row), so the whole folder is watched and a save is reported once the file has been quiet for a moment.
/// </remarks>
public sealed class EditedFiles : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(600);

    private readonly string _root;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Edited> _files = new(StringComparer.Ordinal); // by local path
    private bool _disposed;

    /// <param name="root">
    /// Where the copies go (default: <see cref="DefaultRoot"/>); a fresh folder only this user can open is created inside it.
    /// </param>
    public EditedFiles(string? root = null)
    {
        string parent = root ?? DefaultRoot;
        _root = Path.Combine(parent, Guid.NewGuid().ToString("N")[..12]);
        RemoveStale(parent);
    }

    // Copies left behind when TGK did not close normally: removed once they are a few days old.
    private static void RemoveStale(string parent)
    {
        try
        {
            if (!Directory.Exists(parent))
                return;
            foreach (DirectoryInfo old in new DirectoryInfo(parent).GetDirectories()
                .Where(d => d.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-3) && d.LinkTarget is null))
                old.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CoreLog.Warn($"Could not remove old edited copies in {parent}: {ex.Message}");
        }
    }

    /// <summary>
    /// A per-user place for the copies: the temporary folder on Windows and macOS (per user there), on Linux the
    /// cache folder (<c>$XDG_CACHE_HOME</c>, default <c>~/.cache</c>): never the shared <c>/tmp</c>, nor the runtime
    /// folder, which is a small RAM disk.
    /// </summary>
    public static string DefaultRoot
    {
        get
        {
            if (!OperatingSystem.IsLinux())
                return Path.Combine(Path.GetTempPath(), "tgk-edit");
            string? cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            string root = !string.IsNullOrWhiteSpace(cache) && Path.IsPathRooted(cache)
                ? cache
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
            return Path.Combine(root, "tgk", "edit");
        }
    }

    /// <summary>
    /// Raised on a background thread when an opened file was saved with new content: (remote path, local path).
    /// Upload the local file to the remote path.
    /// </summary>
    public event Action<string, string>? Saved;

    /// <summary>
    /// A local path for a copy of <paramref name="remotePath"/>, in a folder of its own (so its name stays the remote
    /// one, which editors show and use to pick a syntax). Download into it, then call <see cref="Watch"/>.
    /// </summary>
    public string LocalPathFor(string remotePath)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_files.Values.FirstOrDefault(f => f.Remote == remotePath) is { } known)
                return known.Local;
        }
        string folder = Path.Combine(_root, Guid.NewGuid().ToString("N")[..8]);
        CreatePrivateDirectory(folder);
        return Path.Combine(folder, LocalNames.ToLocal(Agents.RemotePath.FileName(remotePath)));
    }

    /// <summary>Starts (or keeps) watching the downloaded copy at <paramref name="localPath"/> of <paramref name="remotePath"/>.</summary>
    public void Watch(string remotePath, string localPath)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_files.TryGetValue(localPath, out Edited? existing))
            {
                existing.Hash = Hash(localPath); // re-downloaded: this is the content the server has
                return;
            }
            var watcher = new FileSystemWatcher(Path.GetDirectoryName(localPath)!)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                IncludeSubdirectories = false,
            };
            var edited = new Edited(remotePath, localPath, watcher) { Hash = Hash(localPath) };
            edited.Timer = new Timer(_ => Check(edited), null, Timeout.Infinite, Timeout.Infinite);
            FileSystemEventHandler changed = (_, e) => Touch(edited, e.FullPath);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Renamed += (_, e) => Touch(edited, e.FullPath);
            watcher.EnableRaisingEvents = true;
            _files[localPath] = edited;
        }
    }

    /// <summary>The local copy of <paramref name="remotePath"/> when it is already opened (and watched), else null.</summary>
    public string? LocalCopyOf(string remotePath)
    {
        lock (_gate)
            return _files.Values.FirstOrDefault(f => f.Remote == remotePath)?.Local;
    }

    /// <summary>The remote paths currently opened.</summary>
    public IReadOnlyList<string> Opened
    {
        get
        {
            lock (_gate)
                return _files.Values.Select(f => f.Remote).ToList();
        }
    }

    /// <summary>Records that <paramref name="localPath"/>'s current content is what the server now has (after an upload).</summary>
    public void MarkUploaded(string localPath, byte[] hash)
    {
        lock (_gate)
        {
            if (_files.TryGetValue(localPath, out Edited? edited))
                edited.Hash = hash;
        }
    }

    public void Dispose()
    {
        Edited[] files;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            files = [.. _files.Values];
            _files.Clear();
        }
        foreach (Edited edited in files)
        {
            edited.Watcher.Dispose();
            edited.Timer?.Dispose();
        }
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CoreLog.Warn($"Could not remove the edited copies in {_root}: {ex.Message}");
        }
    }

    /// <summary>SHA-256 of a file's content; empty when it can't be read right now.</summary>
    public static byte[] Hash(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return SHA256.HashData(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Touch(Edited edited, string path)
    {
        if (!string.Equals(path, edited.Local, StringComparison.Ordinal))
            return; // the editor's own temporary or backup files
        lock (_gate)
        {
            if (!_disposed)
                edited.Timer?.Change(Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    private void Check(Edited edited)
    {
        byte[] hash = Hash(edited.Local);
        lock (_gate)
        {
            if (_disposed || hash.Length == 0 || hash.AsSpan().SequenceEqual(edited.Hash))
                return;
            edited.Hash = hash;
        }
        Saved?.Invoke(edited.Remote, edited.Local);
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private sealed class Edited(string remote, string local, FileSystemWatcher watcher)
    {
        public string Remote { get; } = remote;
        public string Local { get; } = local;
        public FileSystemWatcher Watcher { get; } = watcher;
        public Timer? Timer { get; set; }
        public byte[] Hash { get; set; } = [];
    }
}
