using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TGK.Core.Files;

/// <summary>
/// The files of one place the file browser shows: a host over SFTP (<see cref="SftpFileSystem"/>) or this computer
/// (<see cref="LocalFileSystem"/>). Paths are absolute in the place's own form (POSIX on a host, native here); the
/// path helpers below are the only way to build them. Failures the user can act on throw
/// <see cref="FileOperationException"/>.
/// </summary>
public interface IFileSystem
{
    /// <summary>Where the files are, for messages: a host's name, or "this computer".</summary>
    string DisplayName { get; }

    /// <summary>This computer's files.</summary>
    bool IsLocal { get; }

    /// <summary>The folder to start in (the user's home folder).</summary>
    string Home { get; }

    /// <summary>Whether <see cref="SetPermissionsAsync"/> works here (Unix permission bits).</summary>
    bool HasPermissions { get; }

    /// <summary><paramref name="name"/> inside <paramref name="directory"/>.</summary>
    string Join(string directory, string name);

    /// <summary>The folder containing <paramref name="path"/> (the root stays the root).</summary>
    string Parent(string path);

    /// <summary>The last part of <paramref name="path"/>.</summary>
    string NameOf(string path);

    /// <summary>
    /// The absolute form of something typed: absolute, <c>~</c>-relative or relative to <paramref name="current"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Not a usable path.</exception>
    string Resolve(string typed, string current);

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    bool IsWithin(string path, string folder);

    /// <summary>The problem with <paramref name="name"/> as a new file or folder name here, or null when it is fine.</summary>
    string? ValidateName(string name);

    /// <summary>The entries of a folder (without <c>.</c> and <c>..</c>).</summary>
    Task<IReadOnlyList<FileEntry>> ListAsync(string directory, CancellationToken ct);

    /// <summary>The entry at <paramref name="path"/> (a link stays a link, with its target's details); null when nothing is there.</summary>
    Task<FileEntry?> StatAsync(string path, CancellationToken ct);

    Task CreateDirectoryAsync(string path, CancellationToken ct);

    Task CreateFileAsync(string path, CancellationToken ct);

    /// <summary>Renames or moves an entry within this place; fails when <paramref name="to"/> exists.</summary>
    Task RenameAsync(FileEntry entry, string to, CancellationToken ct);

    /// <summary>Deletes an entry; a folder with everything in it. A link is removed itself, never its target.</summary>
    Task DeleteAsync(FileEntry entry, IProgress<string>? progress, CancellationToken ct);

    Task SetPermissionsAsync(string path, int mode, CancellationToken ct);

    /// <summary>
    /// At most <paramref name="count"/> bytes of a file from <paramref name="offset"/> on (fewer at its end); a link is
    /// followed. For the text editor (and the end of a log).
    /// </summary>
    Task<byte[]> ReadAsync(string path, long offset, int count, CancellationToken ct);

    /// <summary>
    /// Replaces the content of a file (or creates it) so that it is never seen half written: the content goes beside
    /// it and then takes its place, with its permissions and owner. A file whose folder can't be written to, or whose
    /// owner or group could not be kept, is written in place instead. A link is followed: its target gets the content.
    /// </summary>
    Task WriteAsync(string path, byte[] content, CancellationToken ct);
}
