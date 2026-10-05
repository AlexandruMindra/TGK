using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TGK.Core.Files;

/// <summary>
/// This computer's files, for the file browser's local pane: the same operations as on a host, with native paths.
/// Links are shown with their target's details and removed or renamed themselves, never their targets.
/// </summary>
public sealed class LocalFileSystem : IFileSystem
{
    private static readonly string[] ReservedWindowsNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    public static LocalFileSystem Instance { get; } = new();

    public string DisplayName => "this computer";

    public bool IsLocal => true;

    public string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public bool HasPermissions => !OperatingSystem.IsWindows();

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public string Join(string directory, string name) => Path.Combine(directory, name);

    public string Parent(string path) => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? path;

    public string NameOf(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path;

    public string Resolve(string typed, string current)
    {
        typed = typed.Trim();
        if (typed.Length == 0 || typed.Contains('\0'))
            throw new ArgumentException("Enter a folder.");
        if (typed == "~")
            return Home;
        if (typed.StartsWith("~/", StringComparison.Ordinal) || typed.StartsWith("~\\", StringComparison.Ordinal))
            typed = Path.Combine(Home, typed[2..]);
        return Path.GetFullPath(Path.IsPathRooted(typed) ? typed : Path.Combine(current, typed));
    }

    public bool IsWithin(string path, string folder)
    {
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return p.Equals(f, PathComparison) || p.StartsWith(f + Path.DirectorySeparatorChar, PathComparison);
    }

    public string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Enter a name.";
        if (name is "." or "..")
            return $"\"{name}\" can't be used as a name.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || (OperatingSystem.IsWindows() && name.Contains('\\')))
            return OperatingSystem.IsWindows() ? "A name can't contain \\ / : * ? \" < > | or control characters." : "A name can't contain \"/\".";
        if (OperatingSystem.IsWindows() && (name.EndsWith('.') || name.EndsWith(' ')
                || ReservedWindowsNames.Contains(name.Split('.')[0], StringComparer.OrdinalIgnoreCase)))
            return $"Windows can't use \"{name}\" as a name.";
        return name.Length > 255 ? "The name is too long (at most 255 characters)." : null;
    }

    public Task<IReadOnlyList<FileEntry>> ListAsync(string directory, CancellationToken ct) => Run(directory, "list", () =>
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, ReturnSpecialDirectories = false };
        var entries = new List<FileEntry>();
        foreach (FileSystemInfo info in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
        {
            ct.ThrowIfCancellationRequested();
            entries.Add(FromInfo(info));
        }
        return (IReadOnlyList<FileEntry>)entries;
    });

    public Task<FileEntry?> StatAsync(string path, CancellationToken ct) => Run(path, "read", () =>
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        bool exists = info.Exists || info.LinkTarget is not null;
        return exists ? FromInfo(info) : null;
    });

    public Task CreateDirectoryAsync(string path, CancellationToken ct) => Run(path, "create", () =>
    {
        EnsureMissing(path);
        Directory.CreateDirectory(path);
        return true;
    });

    public Task CreateFileAsync(string path, CancellationToken ct) => Run(path, "create", () =>
    {
        EnsureMissing(path);
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
        }
        return true;
    });

    public Task RenameAsync(FileEntry entry, string to, CancellationToken ct) => Run(entry.Path, "rename", () =>
    {
        EnsureMissing(to);
        // A link is renamed itself (rename(2) never follows it).
        if (entry.Kind == FileEntryKind.Directory)
            Directory.Move(entry.Path, to);
        else
            File.Move(entry.Path, to);
        return true;
    });

    public Task DeleteAsync(FileEntry entry, IProgress<string>? progress, CancellationToken ct) => Run(entry.Path, "delete", () =>
    {
        Delete(entry.Path, entry.Kind, progress, ct);
        return true;
    });

    // A folder's contents first; links (to files or folders) are removed as links.
    private static void Delete(string path, FileEntryKind kind, IProgress<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (kind == FileEntryKind.Directory)
        {
            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, ReturnSpecialDirectories = false };
            foreach (FileSystemInfo child in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options).ToList())
            {
                FileEntryKind childKind = child.LinkTarget is not null ? FileEntryKind.Symlink
                    : child is DirectoryInfo ? FileEntryKind.Directory : FileEntryKind.File;
                Delete(child.FullName, childKind, progress, ct);
            }
            Directory.Delete(path);
        }
        else if (kind == FileEntryKind.Symlink && Directory.Exists(path))
        {
            Directory.Delete(path); // a link to a folder: removes the link
        }
        else
        {
            File.SetAttributes(path, FileAttributes.Normal); // a read-only file on Windows
            File.Delete(path);
        }
        progress?.Report(path);
    }

    public Task SetPermissionsAsync(string path, int mode, CancellationToken ct) => Run(path, "change the permissions of", () =>
    {
        if (OperatingSystem.IsWindows())
            throw new FileOperationException("Windows has no Unix permissions.");
        File.SetUnixFileMode(path, (UnixFileMode)(mode & 0xFFF));
        return true;
    });

    private static void EnsureMissing(string path)
    {
        var file = new FileInfo(path);
        if (Directory.Exists(path) || file.Exists || file.LinkTarget is not null)
            throw new FileOperationException($"{(Directory.Exists(path) ? "A folder" : "A file")} named \"{Path.GetFileName(path)}\" already exists there.");
    }

    /// <summary>An entry for a local file, folder or link (with its target's details).</summary>
    internal static FileEntry FromInfo(FileSystemInfo info)
    {
        string? linkTarget = info.LinkTarget;
        if (linkTarget is not null)
        {
            FileSystemInfo? target = null;
            try
            {
                target = info.ResolveLinkTarget(returnFinalTarget: true);
            }
            catch (IOException)
            {
            }
            if (target is { Exists: true })
            {
                FileEntry resolved = Describe(target, info.FullName, info.Name, FileEntryKind.Symlink);
                return resolved with { LinkTarget = linkTarget, LinksToDirectory = target is DirectoryInfo };
            }
            return Describe(info, info.FullName, info.Name, FileEntryKind.Symlink) with { LinkTarget = linkTarget, IsBrokenLink = true };
        }
        FileEntryKind kind = info is DirectoryInfo ? FileEntryKind.Directory
            : info.Attributes.HasFlag(FileAttributes.Device) ? FileEntryKind.Other
            : FileEntryKind.File;
        return Describe(info, info.FullName, info.Name, kind);
    }

    private static FileEntry Describe(FileSystemInfo info, string path, string name, FileEntryKind kind)
    {
        int mode;
        if (OperatingSystem.IsWindows())
            mode = info is DirectoryInfo ? 0x1ED : info.Attributes.HasFlag(FileAttributes.ReadOnly) ? 0x124 : 0x1A4;
        else
            mode = (int)info.UnixFileMode;
        long size = info is FileInfo { Exists: true } file ? file.Length : 0;
        return new FileEntry(path, name, kind, size, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), mode, 0, 0)
        {
            HiddenAttribute = OperatingSystem.IsWindows() && info.Attributes.HasFlag(FileAttributes.Hidden),
        };
    }

    private static Task<T> Run<T>(string path, string action, Func<T> operation) => Task.Run(() =>
    {
        try
        {
            return operation();
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new FileOperationException($"Permission denied: you can't {action} {path}.", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new FileOperationException($"{path} does not exist (any more).", ex);
        }
        catch (FileNotFoundException ex)
        {
            throw new FileOperationException($"{path} does not exist (any more).", ex);
        }
        catch (IOException ex)
        {
            throw new FileOperationException($"Could not {action} {path}: {ex.Message.TrimEnd('.')}.", ex);
        }
    });

    private static async Task RunAsync(string path, string action, Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new FileOperationException($"Permission denied: you can't {action} {path}.", ex);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            throw new FileOperationException($"{path} does not exist (any more).", ex);
        }
        catch (IOException ex)
        {
            throw new FileOperationException($"Could not {action} {path}: {ex.Message.TrimEnd('.')}.", ex);
        }
    }

    public async Task<byte[]> ReadAsync(string path, long offset, int count, CancellationToken ct)
    {
        byte[] buffer = new byte[Math.Max(0, count)];
        int total = 0;
        await RunAsync(path, "read", async () =>
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
            if (offset > 0)
                stream.Seek(offset, SeekOrigin.Begin);
            while (total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
                if (read == 0)
                    break;
                total += read;
            }
        }).ConfigureAwait(false);
        return total == buffer.Length ? buffer : buffer[..total];
    }

    public async Task WriteAsync(string path, byte[] content, CancellationToken ct)
    {
        if (Directory.Exists(path))
            throw new FileOperationException($"{path} is a folder.");
        string target = File.Exists(path) && new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true) is { } link ? link.FullName : path;
        bool exists = File.Exists(target);
        string temp = Path.Combine(Parent(target), $".{NameOf(target)}.{Guid.NewGuid().ToString("N")[..8]}.tgk-part");
        await RunAsync(path, "write", async () =>
        {
            try
            {
                await File.WriteAllBytesAsync(temp, content, ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (exists)
            {
                // The folder is not writable but the file may be: write it in place.
                await File.WriteAllBytesAsync(target, content, ct).ConfigureAwait(false);
                return;
            }
            try
            {
                if (!exists)
                    File.Move(temp, target);
                else if (OperatingSystem.IsWindows())
                    File.Replace(temp, target, null, ignoreMetadataErrors: true); // keeps the original's attributes
                else
                {
                    File.SetUnixFileMode(temp, File.GetUnixFileMode(target));
                    File.Move(temp, target, overwrite: true);
                }
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }).ConfigureAwait(false);
    }
}
