using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using TGK.Core.Agents;
using TGK.Core.Ssh;

namespace TGK.Core.Sftp;

/// <summary>
/// The files of one host over an <see cref="SftpConnection"/>, for the file browser: listing, creating, renaming,
/// deleting and permissions. Paths are absolute and normalized (<see cref="RemotePath"/>). Calls may run at once.
/// </summary>
/// <remarks>
/// Failures the user can act on throw <see cref="SftpOperationException"/> with a readable message; a dropped
/// connection throws <see cref="SshSessionException"/> (<see cref="SshErrorKind.ConnectionLost"/>) and marks the
/// connection lost.
/// </remarks>
public sealed class SftpFileSystem
{
    private readonly SftpConnection _connection;

    public SftpFileSystem(SftpConnection connection) => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public SftpConnection Connection => _connection;

    /// <summary>The directory the server starts in (the user's home directory on OpenSSH).</summary>
    public string Home => RemotePath.Normalize(Client.WorkingDirectory is { Length: > 0 } dir ? dir : "/");

    private SftpClient Client => _connection.Client;

    /// <summary>The entries of a directory (without <c>.</c> and <c>..</c>), in the server's order. Links are resolved to tell directories apart.</summary>
    public async Task<IReadOnlyList<SftpEntry>> ListAsync(string directory, CancellationToken ct)
    {
        SftpClient client = Client;
        var entries = new List<SftpEntry>();
        await Run(directory, "list", async () =>
        {
            await foreach (ISftpFile file in client.ListDirectoryAsync(directory, ct).ConfigureAwait(false))
            {
                if (file.Name is "." or "..")
                    continue;
                entries.Add(FromAttributes(Join(directory, file.Name), file.Attributes));
            }
        }).ConfigureAwait(false);

        // Where links lead decides whether they open like folders: ask for all of them at once.
        int[] links = Enumerable.Range(0, entries.Count).Where(i => entries[i].Kind == SftpEntryKind.Symlink).ToArray();
        if (links.Length > 0)
        {
            SftpEntry[] resolved = await Task.WhenAll(links.Select(i => ResolveLinkAsync(client, entries[i], ct))).ConfigureAwait(false);
            for (int i = 0; i < links.Length; i++)
                entries[links[i]] = resolved[i];
        }
        return entries;
    }

    /// <summary>The entry at <paramref name="path"/> without following a link at its end; null when nothing is there.</summary>
    public async Task<SftpEntry?> StatAsync(string path, CancellationToken ct)
    {
        SftpClient client = Client;
        try
        {
            SftpFileAttributes attributes = SftpRaw.IsAvailable(client)
                ? await Task.Run(() => SftpRaw.LStat(client, path), ct).ConfigureAwait(false)
                : await client.GetAttributesAsync(path, ct).ConfigureAwait(false);
            SftpEntry entry = FromAttributes(path, attributes);
            return entry.Kind == SftpEntryKind.Symlink ? await ResolveLinkAsync(client, entry, ct).ConfigureAwait(false) : entry;
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            throw Describe(ex, path, "read");
        }
    }

    /// <summary>Creates a directory (its parent must exist).</summary>
    public Task CreateDirectoryAsync(string path, CancellationToken ct) =>
        Run(path, "create", async () =>
        {
            await EnsureMissingAsync(path, ct).ConfigureAwait(false);
            await Client.CreateDirectoryAsync(path, ct).ConfigureAwait(false);
        });

    /// <summary>Creates an empty file; fails when something already has that name.</summary>
    public Task CreateFileAsync(string path, CancellationToken ct) =>
        Run(path, "create", async () =>
        {
            await EnsureMissingAsync(path, ct).ConfigureAwait(false);
            await using SftpFileStream stream = await Client.OpenAsync(path, FileMode.CreateNew, FileAccess.Write, ct).ConfigureAwait(false);
        });

    /// <summary>Renames or moves an entry (a link itself, never its target); fails when <paramref name="to"/> exists.</summary>
    public async Task RenameAsync(SftpEntry entry, string to, CancellationToken ct)
    {
        await Run(to, "rename", () => EnsureMissingAsync(to, ct)).ConfigureAwait(false);
        SftpClient client = Client;
        await Run(entry.Path, "rename", async () =>
        {
            if (SftpRaw.IsAvailable(client))
                await Task.Run(() => SftpRaw.Rename(client, entry.Path, to), ct).ConfigureAwait(false);
            else if (entry.Kind == SftpEntryKind.Symlink)
                throw LinksUnsupported(entry.Path);
            else
                await client.RenameFileAsync(entry.Path, to, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes an entry; a directory with everything in it (links inside are removed, never followed). A link is
    /// removed itself, never its target. <paramref name="progress"/> gets each path as it is removed.
    /// </summary>
    public async Task DeleteAsync(SftpEntry entry, IProgress<string>? progress, CancellationToken ct)
    {
        SftpClient client = Client;
        bool raw = SftpRaw.IsAvailable(client);
        if (!raw && entry.Kind == SftpEntryKind.Symlink)
            throw LinksUnsupported(entry.Path);
        if (entry.Kind == SftpEntryKind.Directory)
        {
            foreach (SftpEntry child in await ListAsync(entry.Path, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                await DeleteAsync(child, progress, ct).ConfigureAwait(false);
            }
            await Run(entry.Path, "delete", () => raw
                ? Task.Run(() => SftpRaw.RemoveDirectory(client, entry.Path), ct)
                : client.DeleteDirectoryAsync(entry.Path, ct)).ConfigureAwait(false);
        }
        else
        {
            await Run(entry.Path, "delete", () => raw
                ? Task.Run(() => SftpRaw.Remove(client, entry.Path), ct)
                : client.DeleteFileAsync(entry.Path, ct)).ConfigureAwait(false);
        }
        progress?.Report(entry.Path);
    }

    /// <summary>Sets the permission bits (e.g. 0755; setuid, setgid and sticky included). A link's target is changed.</summary>
    public Task SetPermissionsAsync(string path, int mode, CancellationToken ct)
    {
        SftpClient client = Client;
        // SSH.NET takes the octal digits written as a decimal number (0755 as 755).
        short digits = short.Parse(Convert.ToString(mode & 0xFFF, 8), CultureInfo.InvariantCulture);
        return Run(path, "change the permissions of", () => Task.Run(() => client.ChangePermissions(path, digits), ct));
    }

    /// <summary>
    /// Puts <paramref name="temp"/> in <paramref name="target"/>'s place, replacing it if it exists (atomically when
    /// the server offers posix-rename). Used to finish uploads.
    /// </summary>
    internal async Task ReplaceAsync(string temp, string target, CancellationToken ct)
    {
        SftpClient client = Client;
        await Run(target, "write", async () =>
        {
            if (!SftpRaw.IsAvailable(client))
            {
                if (await client.ExistsAsync(target, ct).ConfigureAwait(false))
                    await client.DeleteFileAsync(target, ct).ConfigureAwait(false);
                await client.RenameFileAsync(temp, target, ct).ConfigureAwait(false);
                return;
            }
            await Task.Run(() =>
            {
                try
                {
                    SftpRaw.PosixRename(client, temp, target);
                }
                catch (NotSupportedException)
                {
                    // Plain SFTP rename refuses an existing target: remove it first.
                    try
                    {
                        SftpRaw.Remove(client, target);
                    }
                    catch (SftpPathNotFoundException)
                    {
                    }
                    SftpRaw.Rename(client, temp, target);
                }
            }, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>Removes a leftover file, ignoring every error (e.g. a partial upload after a failure).</summary>
    internal async Task DeleteQuietlyAsync(string path)
    {
        try
        {
            SftpClient client = Client;
            if (SftpRaw.IsAvailable(client))
                await Task.Run(() => SftpRaw.Remove(client, path)).ConfigureAwait(false);
            else
                await client.DeleteFileAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Already gone, never created, or the connection dropped: nothing more to do.
        }
    }

    internal SftpClient OpenClient() => Client;

    /// <summary><paramref name="name"/> inside <paramref name="directory"/>.</summary>
    public static string Join(string directory, string name) => directory.TrimEnd('/') + "/" + name;

    /// <summary>
    /// Whether <paramref name="name"/> can be a file name on the server: not empty, not <c>.</c> or <c>..</c>, no slash,
    /// NUL or line break. Returns the problem, or null when fine.
    /// </summary>
    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Enter a name.";
        if (name is "." or "..")
            return $"\"{name}\" can't be used as a name.";
        if (name.Contains('/'))
            return "A name can't contain \"/\".";
        if (name.AsSpan().IndexOfAny('\0', '\n', '\r') >= 0)
            return "A name can't contain line breaks.";
        return name.Length > 255 ? "The name is too long (at most 255 characters)." : null;
    }

    /// <summary>Runs one operation on <paramref name="path"/>, translating failures (see the remarks of the class).</summary>
    internal async Task Run(string path, string action, Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            throw Describe(ex, path, action);
        }
    }

    internal Exception Describe(Exception ex, string path, string action)
    {
        switch (ex)
        {
            case SftpOperationException or OperationCanceledException:
                return ex;
            case SshSessionException { Kind: SshErrorKind.ConnectionLost } lost:
                _connection.MarkLost(lost.Message);
                return lost;
            case SshConnectionException or SocketException or ObjectDisposedException or SshOperationTimeoutException when !_connection.IsConnected:
                string reason = $"The connection to {_connection.Request?.Host ?? "the server"} was lost.";
                _connection.MarkLost(reason);
                return new SshSessionException(SshErrorKind.ConnectionLost, reason, ex);
            case SftpPathNotFoundException:
                return new SftpOperationException($"{path} does not exist (any more).", ex);
            case SftpPermissionDeniedException:
                return new SftpOperationException($"Permission denied: you can't {action} {path}.", ex);
            case SshOperationTimeoutException:
                return new SftpOperationException($"The server did not answer in time ({action} {path}).", ex);
            default:
                return new SftpOperationException($"Could not {action} {path}: {ex.Message.TrimEnd('.')}.", ex);
        }
    }

    private static bool IsFailure(Exception ex) =>
        ex is SshException or SocketException or ObjectDisposedException or SshSessionException or IOException or NotSupportedException or InvalidOperationException;

    private async Task EnsureMissingAsync(string path, CancellationToken ct)
    {
        if (await StatAsync(path, ct).ConfigureAwait(false) is { } existing)
            throw new SftpOperationException($"{(existing.IsDirectory ? "A folder" : "A file")} named \"{existing.Name}\" already exists there.");
    }

    // Reads the link and stats its target; a link that leads nowhere stays a link (IsBrokenLink).
    private static async Task<SftpEntry> ResolveLinkAsync(SftpClient client, SftpEntry link, CancellationToken ct)
    {
        string? target = null;
        try
        {
            if (SftpRaw.IsAvailable(client))
                target = await Task.Run(() => SftpRaw.ReadLink(client, link.Path), ct).ConfigureAwait(false);
            // GetAttributes asks for the canonical path first, which follows the link.
            SftpFileAttributes attributes = await client.GetAttributesAsync(link.Path, ct).ConfigureAwait(false);
            return link with { LinkTarget = target, LinksToDirectory = attributes.IsDirectory, Size = attributes.IsRegularFile ? attributes.Size : link.Size };
        }
        catch (Exception ex) when (ex is SshException and not SshConnectionException || ex is NotSupportedException)
        {
            return link with { LinkTarget = target, IsBrokenLink = true };
        }
    }

    internal static SftpEntry FromAttributes(string path, SftpFileAttributes a)
    {
        SftpEntryKind kind = a.IsSymbolicLink ? SftpEntryKind.Symlink
            : a.IsDirectory ? SftpEntryKind.Directory
            : a.IsRegularFile ? SftpEntryKind.File
            : SftpEntryKind.Other;
        int mode = (a.OwnerCanRead ? 0x100 : 0) | (a.OwnerCanWrite ? 0x80 : 0) | (a.OwnerCanExecute ? 0x40 : 0)
            | (a.GroupCanRead ? 0x20 : 0) | (a.GroupCanWrite ? 0x10 : 0) | (a.GroupCanExecute ? 0x8 : 0)
            | (a.OthersCanRead ? 0x4 : 0) | (a.OthersCanWrite ? 0x2 : 0) | (a.OthersCanExecute ? 0x1 : 0)
            | (a.IsUIDBitSet ? 0x800 : 0) | (a.IsGroupIDBitSet ? 0x400 : 0) | (a.IsStickyBitSet ? 0x200 : 0);
        return new SftpEntry(path, RemotePath.FileName(path), kind, a.Size, new DateTimeOffset(a.LastWriteTimeUtc, TimeSpan.Zero), mode, a.UserId, a.GroupId);
    }

    private static SftpOperationException LinksUnsupported(string path) =>
        new($"{path} is a symbolic link, which can't be changed safely on this connection.");
}
