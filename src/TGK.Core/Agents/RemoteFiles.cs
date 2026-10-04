using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using TGK.Core.Ssh;

namespace TGK.Core.Agents;

public enum RemoteEntryKind
{
    File,
    Directory,
    Symlink,
    Other,
}

/// <summary>One file system entry on a remote host (not following a symbolic link).</summary>
/// <param name="Mode">The permission bits (e.g. 0644).</param>
/// <param name="Uid">The owner's user id, when known.</param>
public sealed record RemoteEntry(string Path, string Name, RemoteEntryKind Kind, long Size, DateTimeOffset Modified, int Mode, long? Uid)
{
    /// <summary><c>rwxr-xr-x</c> style.</summary>
    public string Permissions
    {
        get
        {
            var sb = new StringBuilder(9);
            for (int shift = 6; shift >= 0; shift -= 3)
            {
                int bits = (Mode >> shift) & 7;
                sb.Append((bits & 4) != 0 ? 'r' : '-').Append((bits & 2) != 0 ? 'w' : '-').Append((bits & 1) != 0 ? 'x' : '-');
            }
            return sb.ToString();
        }
    }
}

/// <summary>A file operation failed for a reason worth telling the agent (missing file, no permission, too large…).</summary>
public sealed class RemoteFileException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Files on one remote host: over SFTP when the server offers it, else through commands (<c>stat</c>, <c>cat</c>,
/// <c>mv</c>), which also works on small devices with Dropbear and BusyBox. Paths are absolute (see
/// <see cref="RemotePath.Resolve"/>); <see cref="HomeAsync"/> gives the base for relative ones.
/// </summary>
public sealed class RemoteFiles
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    private readonly RemoteConnection _connection;
    private string? _home;

    public RemoteFiles(RemoteConnection connection) => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public RemoteConnection Connection => _connection;

    /// <summary>True once SFTP turned out to be unavailable and commands are used instead.</summary>
    public bool UsesCommands => _connection.SftpUnavailableReason is not null;

    /// <summary>The user's home directory on the host (the base of relative paths).</summary>
    public async Task<string> HomeAsync(CancellationToken ct)
    {
        if (_home is { } known)
            return known;
        string home;
        if (await SftpAsync(ct).ConfigureAwait(false) is { } sftp)
        {
            home = sftp.WorkingDirectory;
        }
        else
        {
            CommandResult result = await RunAsync("printf '%s' \"$HOME\"", ct).ConfigureAwait(false);
            home = result.ExitCode == 0 && result.Stdout.StartsWith('/') ? result.Stdout.Trim() : "/";
        }
        return _home = string.IsNullOrEmpty(home) ? "/" : RemotePath.Normalize(home);
    }

    public async Task<string> ResolveAsync(string path, CancellationToken ct) => RemotePath.Resolve(await HomeAsync(ct).ConfigureAwait(false), path);

    /// <summary>The entry at <paramref name="path"/> without following a symbolic link; null when nothing is there.</summary>
    public async Task<RemoteEntry?> StatAsync(string path, CancellationToken ct)
    {
        if (await SftpAsync(ct).ConfigureAwait(false) is { } sftp)
        {
            try
            {
                SftpFileAttributes attributes = await sftp.GetAttributesAsync(path, ct).ConfigureAwait(false);
                return FromSftp(path, attributes);
            }
            catch (SftpPathNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (ex is SshException and not SshConnectionException)
            {
                throw Describe(ex, path);
            }
        }
        CommandResult result = await RunAsync($"stat -c '%F\t%s\t%Y\t%a\t%u\t%n' -- {RemotePath.Quote(path)}", ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            if (result.Stderr.Contains("No such file", StringComparison.OrdinalIgnoreCase))
                return null;
            throw new RemoteFileException(FirstLine(result.Stderr, $"Could not read the attributes of {path}."));
        }
        return ParseStat(result.Stdout.TrimEnd('\n'), path);
    }

    /// <summary>The entries of a directory, sorted by name, at most <paramref name="max"/> (and whether there were more).</summary>
    public async Task<(IReadOnlyList<RemoteEntry> Entries, bool More)> ListAsync(string path, int max, CancellationToken ct)
    {
        var entries = new List<RemoteEntry>();
        bool more = false;
        if (await SftpAsync(ct).ConfigureAwait(false) is { } sftp)
        {
            try
            {
                await foreach (ISftpFile file in sftp.ListDirectoryAsync(path, ct).ConfigureAwait(false))
                {
                    if (file.Name is "." or "..")
                        continue;
                    if (entries.Count >= max)
                    {
                        more = true;
                        break;
                    }
                    entries.Add(FromSftp(path.TrimEnd('/') + "/" + file.Name, file.Attributes) with { Name = file.Name });
                }
            }
            catch (Exception ex) when (ex is SshException and not SshConnectionException)
            {
                throw Describe(ex, path);
            }
        }
        else
        {
            string dir = RemotePath.Quote(path.TrimEnd('/') is { Length: > 0 } d ? d : "/");
            string script = $"cd -- {dir} || exit 1; for f in * .[!.]* ..?*; do if [ -e \"$f\" ] || [ -L \"$f\" ]; then stat -c '%F\t%s\t%Y\t%a\t%u\t%n' -- \"$f\"; fi; done | head -n {max + 1}";
            CommandResult result = await RunAsync(script, ct).ConfigureAwait(false);
            if (result.ExitCode != 0 && result.Stdout.Length == 0)
                throw new RemoteFileException(FirstLine(result.Stderr, $"Could not list {path}."));
            foreach (string line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (entries.Count >= max)
                {
                    more = true;
                    break;
                }
                if (ParseStat(line, null) is { } entry)
                    entries.Add(entry with { Path = path.TrimEnd('/') + "/" + entry.Name });
            }
        }
        entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return (entries, more);
    }

    /// <summary>The whole content of a file (following symbolic links).</summary>
    /// <exception cref="RemoteFileException">Missing, a directory, unreadable, or larger than <paramref name="maxBytes"/>.</exception>
    public async Task<byte[]> ReadAsync(string path, long maxBytes, CancellationToken ct)
    {
        if (await SftpAsync(ct).ConfigureAwait(false) is { } sftp)
        {
            try
            {
                await using SftpFileStream stream = await sftp.OpenAsync(path, FileMode.Open, FileAccess.Read, ct).ConfigureAwait(false);
                var buffer = new MemoryStream();
                byte[] chunk = new byte[64 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > maxBytes)
                        throw TooLarge(path, maxBytes);
                    buffer.Write(chunk, 0, read);
                }
                return buffer.ToArray();
            }
            catch (Exception ex) when (ex is SshException and not SshConnectionException)
            {
                // Opening a directory fails with a generic error: say what it is.
                if (await StatAsync(path, ct).ConfigureAwait(false) is { Kind: RemoteEntryKind.Directory })
                    throw new RemoteFileException($"{path} is a directory.");
                throw Describe(ex, path);
            }
        }
        string q = RemotePath.Quote(path);
        CommandResult result = await _connection.RunAsync($"if [ -d {q} ]; then echo 'is a directory' >&2; exit 2; fi; cat -- {q}",
            CommandTimeout, (int)Math.Min(int.MaxValue - 1, maxBytes + 1), keepBytes: true, ct: ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            string error = FirstLine(result.Stderr, $"Could not read {path}.");
            throw new RemoteFileException(error.Contains("is a directory", StringComparison.Ordinal) ? $"{path} is a directory." : error);
        }
        if (result.Truncated)
            throw TooLarge(path, maxBytes);
        return result.StdoutBytes ?? [];
    }

    /// <summary>
    /// Replaces (or creates) a file so that it is never seen half written: the content goes to a temporary file in the
    /// same directory, which then takes the original's place by rename, with the original's permissions. A file owned
    /// by another user, or a server that can't rename over a file, is written in place instead (keeping its owner).
    /// A symbolic link is followed: its target is replaced.
    /// </summary>
    public async Task WriteAsync(string path, byte[] content, CancellationToken ct)
    {
        RemoteEntry? existing = await StatAsync(path, ct).ConfigureAwait(false);
        if (existing is { Kind: RemoteEntryKind.Symlink })
        {
            path = await RealPathAsync(path, ct).ConfigureAwait(false) ?? path;
            existing = await StatAsync(path, ct).ConfigureAwait(false);
        }
        if (existing is { Kind: RemoteEntryKind.Directory })
            throw new RemoteFileException($"{path} is a directory.");
        if (await SftpAsync(ct).ConfigureAwait(false) is { } sftp)
            await WriteSftpAsync(sftp, path, content, existing, ct).ConfigureAwait(false);
        else
            await WriteCommandAsync(path, content, existing, ct).ConfigureAwait(false);
    }

    /// <summary>Creates a directory and its missing parents.</summary>
    public async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        CommandResult result = await RunAsync($"mkdir -p -- {RemotePath.Quote(path)}", ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new RemoteFileException(FirstLine(result.Stderr, $"Could not create {path}."));
    }

    /// <summary>Where a symbolic link finally points (absolute), or null when that can't be told.</summary>
    public async Task<string?> RealPathAsync(string path, CancellationToken ct)
    {
        CommandResult result = await RunAsync($"readlink -f -- {RemotePath.Quote(path)}", ct).ConfigureAwait(false);
        string target = result.Stdout.Trim();
        return result.ExitCode == 0 && target.StartsWith('/') ? target : null;
    }

    private async Task WriteSftpAsync(SftpClient sftp, string path, byte[] content, RemoteEntry? existing, CancellationToken ct)
    {
        string temp = TempName(path);
        try
        {
            await using (SftpFileStream stream = await sftp.OpenAsync(temp, FileMode.CreateNew, FileAccess.Write, ct).ConfigureAwait(false))
                await stream.WriteAsync(content, ct).ConfigureAwait(false);
            SftpFileAttributes made = await sftp.GetAttributesAsync(temp, ct).ConfigureAwait(false);
            // Renaming would hand someone else's file to this user: write it in place instead.
            bool inPlace = existing?.Uid is { } owner && owner != made.UserId;
            if (!inPlace)
            {
                if (existing is not null)
                    sftp.ChangePermissions(temp, short.Parse(Convert.ToString(existing.Mode & 0x1FF, 8), CultureInfo.InvariantCulture)); // octal digits written as a decimal number
                try
                {
                    sftp.RenameFile(temp, path, isPosix: true);
                    return;
                }
                catch (SshException ex) when (ex is not SshConnectionException)
                {
                    // No posix-rename extension (and plain SFTP rename refuses an existing target).
                    if (existing is null)
                    {
                        sftp.RenameFile(temp, path);
                        return;
                    }
                }
            }
            await using (SftpFileStream target = await sftp.OpenAsync(path, FileMode.Create, FileAccess.Write, ct).ConfigureAwait(false))
                await target.WriteAsync(content, ct).ConfigureAwait(false);
            await DeleteQuietlyAsync(sftp, temp).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SshException and not SshConnectionException)
        {
            await DeleteQuietlyAsync(sftp, temp).ConfigureAwait(false);
            throw Describe(ex, path);
        }
        catch
        {
            await DeleteQuietlyAsync(sftp, temp).ConfigureAwait(false);
            throw;
        }
    }

    private async Task WriteCommandAsync(string path, byte[] content, RemoteEntry? existing, CancellationToken ct)
    {
        string q = RemotePath.Quote(path);
        string temp = RemotePath.Quote(TempName(path));
        string keepMode = existing is null ? "" : $"chmod {Convert.ToString(existing.Mode, 8)} {temp} 2>/dev/null; ";
        string script = $"umask 022; cat > {temp} || {{ rm -f {temp}; exit 1; }}; {keepMode}mv -f {temp} {q} || {{ rm -f {temp}; exit 1; }}";
        CommandResult result = await _connection.RunAsync(script, CommandTimeout, input: content, ct: ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new RemoteFileException(FirstLine(result.Stderr, $"Could not write {path}."));
    }

    private static async Task DeleteQuietlyAsync(SftpClient sftp, string path)
    {
        try
        {
            await sftp.DeleteFileAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Already gone, or never created.
        }
    }

    private static string TempName(string path) =>
        $"{RemotePath.Directory(path).TrimEnd('/')}/.{RemotePath.FileName(path)}.tgk-{Guid.NewGuid().ToString("N")[..8]}.tmp";

    /// <summary>The SFTP client, or null when the server has none (then commands are used).</summary>
    private async Task<SftpClient?> SftpAsync(CancellationToken ct)
    {
        if (_connection.SftpUnavailableReason is not null)
            return null;
        try
        {
            return await _connection.GetSftpAsync(ct).ConfigureAwait(false);
        }
        catch (SftpUnavailableException)
        {
            return null;
        }
    }

    private Task<CommandResult> RunAsync(string command, CancellationToken ct) =>
        _connection.RunAsync(command, CommandTimeout, ct: ct);

    private static RemoteEntry FromSftp(string path, SftpFileAttributes a)
    {
        RemoteEntryKind kind = a.IsSymbolicLink ? RemoteEntryKind.Symlink
            : a.IsDirectory ? RemoteEntryKind.Directory
            : a.IsRegularFile ? RemoteEntryKind.File
            : RemoteEntryKind.Other;
        int mode = (a.OwnerCanRead ? 0x100 : 0) | (a.OwnerCanWrite ? 0x80 : 0) | (a.OwnerCanExecute ? 0x40 : 0)
            | (a.GroupCanRead ? 0x20 : 0) | (a.GroupCanWrite ? 0x10 : 0) | (a.GroupCanExecute ? 0x8 : 0)
            | (a.OthersCanRead ? 0x4 : 0) | (a.OthersCanWrite ? 0x2 : 0) | (a.OthersCanExecute ? 0x1 : 0)
            | (a.IsUIDBitSet ? 0x800 : 0) | (a.IsGroupIDBitSet ? 0x400 : 0) | (a.IsStickyBitSet ? 0x200 : 0);
        return new RemoteEntry(path, RemotePath.FileName(path), kind, a.Size, new DateTimeOffset(a.LastWriteTimeUtc, TimeSpan.Zero), mode, a.UserId);
    }

    // "%F\t%s\t%Y\t%a\t%u\t%n" of GNU and BusyBox stat.
    private static RemoteEntry? ParseStat(string line, string? path)
    {
        string[] f = line.Split('\t', 6);
        if (f.Length < 6 || !long.TryParse(f[1], CultureInfo.InvariantCulture, out long size)
            || !long.TryParse(f[2], CultureInfo.InvariantCulture, out long mtime))
            return null;
        RemoteEntryKind kind = f[0] switch
        {
            "directory" => RemoteEntryKind.Directory,
            "symbolic link" => RemoteEntryKind.Symlink,
            _ when f[0].Contains("regular", StringComparison.Ordinal) => RemoteEntryKind.File,
            _ => RemoteEntryKind.Other,
        };
        int mode = 0;
        try
        {
            mode = Convert.ToInt32(f[3], 8);
        }
        catch (FormatException)
        {
        }
        long? uid = long.TryParse(f[4], CultureInfo.InvariantCulture, out long u) ? u : null;
        string name = RemotePath.FileName(f[5]);
        return new RemoteEntry(path ?? f[5], name, kind, size, DateTimeOffset.FromUnixTimeSeconds(mtime), mode, uid);
    }

    private static RemoteFileException Describe(Exception ex, string path) => ex switch
    {
        SftpPathNotFoundException => new RemoteFileException($"{path} does not exist.", ex),
        SftpPermissionDeniedException => new RemoteFileException($"Permission denied: {path}.", ex),
        _ => new RemoteFileException($"{path}: {ex.Message}", ex),
    };

    private static RemoteFileException TooLarge(string path, long max) =>
        new($"{path} is larger than {max / (1024 * 1024)} MB; read it in parts with run_command (e.g. head, tail, sed -n) instead.");

    private static string FirstLine(string text, string fallback)
    {
        string line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return line.Length > 0 ? line : fallback;
    }
}
