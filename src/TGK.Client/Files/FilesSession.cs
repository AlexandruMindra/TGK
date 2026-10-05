using System;
using System.Threading;
using TGK.Core.Files;
using TGK.Core.Models;
using TGK.Core.Ssh;

namespace TGK.Client.Files;

/// <summary>
/// One SFTP connection shared by the file panes of a host (a second pane on the same host signs in no second time, and
/// moves between its panes are renames). Counted: the last pane to let go closes it.
/// </summary>
public sealed class FilesSession
{
    private int _users = 1;

    public FilesSession(HostEntry host, SftpConnection connection, SshConnectRequest request)
    {
        Host = host;
        Connection = connection;
        Request = request;
        Files = new SftpFileSystem(connection, host.DisplayName);
    }

    /// <summary>The host as it was when connecting.</summary>
    public HostEntry Host { get; }

    public SftpConnection Connection { get; }

    public SftpFileSystem Files { get; }

    /// <summary>The request the connection was made with, credentials included (typed passwords too): for direct copies.</summary>
    public SshConnectRequest Request { get; }

    public bool IsConnected => Connection.IsConnected;

    /// <summary>One more pane uses the session.</summary>
    public FilesSession AddRef()
    {
        Interlocked.Increment(ref _users);
        return this;
    }

    /// <summary>A pane is done with the session; the last one closes the connection.</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _users) == 0)
            Connection.Dispose();
    }
}
