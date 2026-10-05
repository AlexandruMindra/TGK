using System;
using System.Text;

namespace TGK.Core.Sftp;

public enum SftpEntryKind
{
    File,
    Directory,
    Symlink,
    Other,
}

/// <summary>
/// One entry of a remote directory, as the server lists it. A symbolic link stays a link (<see cref="Kind"/>), but
/// carries its target's permissions, owner, size and time (a link's own are meaningless); a broken link keeps its own.
/// </summary>
/// <param name="Path">Absolute, normalized.</param>
/// <param name="Mode">The permission bits including setuid, setgid and sticky (e.g. 0755).</param>
public sealed record SftpEntry(string Path, string Name, SftpEntryKind Kind, long Size, DateTimeOffset Modified, int Mode, long Uid, long Gid)
{
    /// <summary>Where a symbolic link points, as stored in the link (may be relative); null for other entries or when unreadable.</summary>
    public string? LinkTarget { get; init; }

    /// <summary>A symbolic link whose target is a directory (it is browsed like one).</summary>
    public bool LinksToDirectory { get; init; }

    /// <summary>A symbolic link whose target does not exist (or can't be read).</summary>
    public bool IsBrokenLink { get; init; }

    /// <summary>A directory, or a symbolic link to one.</summary>
    public bool IsDirectory => Kind == SftpEntryKind.Directory || (Kind == SftpEntryKind.Symlink && LinksToDirectory);

    /// <summary>Hidden by Unix convention (the name starts with a dot).</summary>
    public bool IsHidden => Name.StartsWith('.');

    /// <summary><c>ls -l</c> style, e.g. <c>drwxr-xr-x</c> or <c>-rwsr-x---</c>.</summary>
    public string Permissions
    {
        get
        {
            var sb = new StringBuilder(10);
            sb.Append(Kind switch { SftpEntryKind.Directory => 'd', SftpEntryKind.Symlink => 'l', SftpEntryKind.Other => '?', _ => '-' });
            AppendTriplet(sb, Mode >> 6, (Mode & 0x800) != 0, 's');
            AppendTriplet(sb, Mode >> 3, (Mode & 0x400) != 0, 's');
            AppendTriplet(sb, Mode, (Mode & 0x200) != 0, 't');
            return sb.ToString();
        }
    }

    /// <summary>The permission bits as octal digits, e.g. <c>755</c> (or <c>4755</c> with special bits).</summary>
    public string OctalMode => Convert.ToString(Mode & 0xFFF, 8).PadLeft(3, '0');

    private static void AppendTriplet(StringBuilder sb, int bits, bool special, char specialChar)
    {
        sb.Append((bits & 4) != 0 ? 'r' : '-').Append((bits & 2) != 0 ? 'w' : '-');
        bool exec = (bits & 1) != 0;
        sb.Append(special ? (exec ? specialChar : char.ToUpperInvariant(specialChar)) : exec ? 'x' : '-');
    }
}

/// <summary>A file operation failed for a reason worth showing the user (missing file, no permission, name taken…).</summary>
public sealed class SftpOperationException(string message, Exception? inner = null) : Exception(message, inner);
