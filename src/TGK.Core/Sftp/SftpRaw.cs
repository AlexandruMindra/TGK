using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace TGK.Core.Sftp;

/// <summary>
/// SFTP requests on an exact path. SSH.NET's public methods first ask the server for the canonical path (realpath),
/// which follows a symbolic link: deleting or renaming a link through them would delete or rename its target. These go
/// to the protocol session directly (it is internal to SSH.NET, so it is reached by reflection); when that ever stops
/// working, <see cref="IsAvailable"/> turns false and callers fall back to the public methods for anything but links.
/// </summary>
/// <remarks>The requests are synchronous (they wait for the server's answer): call them off the UI thread.</remarks>
internal static class SftpRaw
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly PropertyInfo? SessionProperty = typeof(SftpClient).GetProperty("SftpSession", Instance);
    private static readonly Dictionary<string, MethodInfo?> Methods = [];

    /// <summary>The exact-path requests can be made on <paramref name="client"/>.</summary>
    public static bool IsAvailable(SftpClient client) =>
        Session(client) is { } session
        && Find(session, "RequestRemove", typeof(string)) is not null
        && Find(session, "RequestRmDir", typeof(string)) is not null
        && Find(session, "RequestRename", typeof(string), typeof(string)) is not null
        && Find(session, "RequestPosixRename", typeof(string), typeof(string)) is not null
        && Find(session, "RequestLStat", typeof(string)) is not null
        && Find(session, "RequestStat", typeof(string)) is not null
        && Find(session, "RequestReadLink", typeof(string), typeof(bool)) is not null
        && Find(session, "RequestRealPath", typeof(string), typeof(bool)) is not null;

    /// <summary>Removes a file or symbolic link (never its target).</summary>
    public static void Remove(SftpClient client, string path) => Invoke(client, "RequestRemove", [typeof(string)], path);

    /// <summary>Removes an empty directory.</summary>
    public static void RemoveDirectory(SftpClient client, string path) => Invoke(client, "RequestRmDir", [typeof(string)], path);

    /// <summary>Renames; the server refuses when <paramref name="newPath"/> exists.</summary>
    public static void Rename(SftpClient client, string oldPath, string newPath) =>
        Invoke(client, "RequestRename", [typeof(string), typeof(string)], oldPath, newPath);

    /// <summary>Renames over an existing <paramref name="newPath"/> atomically (OpenSSH's posix-rename extension).</summary>
    /// <exception cref="NotSupportedException">The server does not offer the extension.</exception>
    public static void PosixRename(SftpClient client, string oldPath, string newPath) =>
        Invoke(client, "RequestPosixRename", [typeof(string), typeof(string)], oldPath, newPath);

    /// <summary>The attributes of <paramref name="path"/> itself (a link is not followed).</summary>
    public static SftpFileAttributes LStat(SftpClient client, string path) =>
        (SftpFileAttributes)Invoke(client, "RequestLStat", [typeof(string)], path)!;

    /// <summary>The attributes of what <paramref name="path"/> leads to (a link is followed).</summary>
    public static SftpFileAttributes Stat(SftpClient client, string path) =>
        (SftpFileAttributes)Invoke(client, "RequestStat", [typeof(string)], path)!;

    /// <summary>What a symbolic link contains; null when the server can't tell.</summary>
    public static string? ReadLink(SftpClient client, string path) =>
        Invoke(client, "RequestReadLink", [typeof(string), typeof(bool)], path, true) is KeyValuePair<string, SftpFileAttributes>[] { Length: > 0 } names
            ? names[0].Key
            : null;

    /// <summary>The absolute path with every link resolved (the server's realpath); null when the server can't tell.</summary>
    public static string? RealPath(SftpClient client, string path) =>
        Invoke(client, "RequestRealPath", [typeof(string), typeof(bool)], path, true) is KeyValuePair<string, SftpFileAttributes>[] { Length: > 0 } names
            ? names[0].Key
            : null;

    private static object? Session(SftpClient client) => SessionProperty?.GetValue(client);

    private static MethodInfo? Find(object session, string name, params Type[] parameters)
    {
        string key = name + "/" + parameters.Length;
        lock (Methods)
        {
            if (!Methods.TryGetValue(key, out MethodInfo? method))
                Methods[key] = method = session.GetType().GetMethod(name, Instance, parameters);
            return method;
        }
    }

    private static object? Invoke(SftpClient client, string name, Type[] parameters, params object[] arguments)
    {
        object session = Session(client) ?? throw new NotSupportedException("The SFTP session is not available.");
        MethodInfo method = Find(session, name, parameters) ?? throw new NotSupportedException($"SSH.NET has no {name}.");
        try
        {
            return method.Invoke(session, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw; // unreachable
        }
    }
}
