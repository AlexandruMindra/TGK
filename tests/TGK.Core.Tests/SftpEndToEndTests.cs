using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using TGK.Core.Agents;
using TGK.Core.Files;
using TGK.Core.Ssh;
using Xunit;

namespace TGK.Core.Tests;

/// <summary>
/// The file browser's connection, operations and transfers against a real SFTP server (<c>TGK_E2E_SSH</c>, see
/// docs/DEV-TESTING.md). Remote files go under a fresh directory in /tmp, local ones in a temporary directory; both
/// are removed afterwards.
/// </summary>
[Trait("Category", "E2E")]
public sealed class SftpEndToEndTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly string _dir = $"/tmp/tgk-sftp-e2e-{Guid.NewGuid():N}";
    private readonly TempDirectory _local = new();
    private SftpConnection? _connection;
    private SftpFileSystem _files = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TGK_E2E_SSH")))
            return; // each test skips itself through Request()
        _connection = new SftpConnection(new RemoteConnectionEndToEndTests.TrustAll());
        await _connection.ConnectAsync(RemoteConnectionEndToEndTests.Request("TGK_E2E_SSH"), Ct);
        _files = new SftpFileSystem(_connection);
        await _files.CreateDirectoryAsync(_dir, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is { IsConnected: true } && await _files.StatAsync(_dir, CancellationToken.None) is { } root)
            await _files.DeleteAsync(root, null, CancellationToken.None);
        _connection?.Dispose();
        _local.Dispose();
    }

    private SftpClient Client => _connection!.Client;

    private void RequireServer() => RemoteConnectionEndToEndTests.Request("TGK_E2E_SSH");

    [Fact]
    public async Task Connects_AndListsWithKinds()
    {
        RequireServer();
        Assert.True(_connection!.IsConnected);
        Assert.StartsWith("/", _files.Home);
        Assert.StartsWith("SHA256:", _connection.HostKeyFingerprint);

        await _files.CreateDirectoryAsync($"{_dir}/sub", Ct);
        await _files.CreateFileAsync($"{_dir}/empty.txt", Ct);
        await ShellAsync($"ln -s {_dir}/sub {_dir}/to-sub && ln -s {_dir}/empty.txt {_dir}/to-file && ln -s {_dir}/missing {_dir}/dangling");

        var entries = (await _files.ListAsync(_dir, Ct)).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();

        Assert.Equal(["dangling", "empty.txt", "sub", "to-file", "to-sub"], entries.Select(e => e.Name));
        Assert.All(entries, e => Assert.Equal($"{_dir}/{e.Name}", e.Path));
        Assert.True(entries[0] is { Kind: FileEntryKind.Symlink, IsBrokenLink: true, IsDirectory: false });
        Assert.True(entries[1] is { Kind: FileEntryKind.File, Size: 0, IsDirectory: false });
        Assert.True(entries[2] is { Kind: FileEntryKind.Directory, IsDirectory: true });
        Assert.True(entries[3] is { Kind: FileEntryKind.Symlink, LinksToDirectory: false, IsBrokenLink: false });
        Assert.True(entries[4] is { Kind: FileEntryKind.Symlink, LinksToDirectory: true, IsDirectory: true });
        Assert.Equal($"{_dir}/sub", entries[4].LinkTarget);
    }

    [Fact]
    public async Task CreateRenameDelete_AndNamesThatExistAreRefused()
    {
        RequireServer();
        await _files.CreateDirectoryAsync($"{_dir}/a", Ct);
        await _files.CreateFileAsync($"{_dir}/a/one.txt", Ct);
        await _files.CreateDirectoryAsync($"{_dir}/a/deeper", Ct);
        await _files.CreateFileAsync($"{_dir}/a/deeper/two.txt", Ct);

        var taken = await Assert.ThrowsAsync<FileOperationException>(() => _files.CreateFileAsync($"{_dir}/a/one.txt", Ct));
        Assert.Contains("already exists", taken.Message);
        await Assert.ThrowsAsync<FileOperationException>(() => _files.CreateDirectoryAsync($"{_dir}/a", Ct));

        FileEntry one = (await _files.StatAsync($"{_dir}/a/one.txt", Ct))!;
        await _files.RenameAsync(one, $"{_dir}/a/renamed.txt", Ct);
        Assert.Null(await _files.StatAsync($"{_dir}/a/one.txt", Ct));
        FileEntry renamed = (await _files.StatAsync($"{_dir}/a/renamed.txt", Ct))!;
        var clash = await Assert.ThrowsAsync<FileOperationException>(() => _files.RenameAsync(renamed, $"{_dir}/a/deeper", Ct));
        Assert.Contains("already exists", clash.Message);

        var removed = new System.Collections.Concurrent.ConcurrentBag<string>();
        await _files.DeleteAsync((await _files.StatAsync($"{_dir}/a", Ct))!, new InlineProgress<string>(removed.Add), Ct);

        Assert.Null(await _files.StatAsync($"{_dir}/a", Ct));
        Assert.Equal(4, removed.Count);
    }

    [Fact]
    public async Task LinksAreDeletedAndRenamed_NotTheirTargets()
    {
        RequireServer();
        await _files.CreateDirectoryAsync($"{_dir}/target", Ct);
        await _files.CreateFileAsync($"{_dir}/target/keep.txt", Ct);
        await ShellAsync($"ln -s {_dir}/target {_dir}/link && ln -s target/keep.txt {_dir}/file-link");

        await _files.RenameAsync((await _files.StatAsync($"{_dir}/file-link", Ct))!, $"{_dir}/moved-link", Ct);
        FileEntry moved = (await _files.StatAsync($"{_dir}/moved-link", Ct))!;
        Assert.Equal(FileEntryKind.Symlink, moved.Kind);
        await _files.DeleteAsync(moved, null, Ct);
        await _files.DeleteAsync((await _files.StatAsync($"{_dir}/link", Ct))!, null, Ct);

        Assert.Null(await _files.StatAsync($"{_dir}/link", Ct));
        Assert.Null(await _files.StatAsync($"{_dir}/moved-link", Ct));
        Assert.NotNull(await _files.StatAsync($"{_dir}/target/keep.txt", Ct));
    }

    [Fact]
    public async Task Links_CarryTheirTargetsPermissions_AndUploadsGoWhereTheServerResolvesThem()
    {
        RequireServer();
        await _files.CreateDirectoryAsync($"{_dir}/real", Ct);
        await _files.CreateDirectoryAsync($"{_dir}/real/a", Ct);
        await _files.CreateFileAsync($"{_dir}/real/shared.txt", Ct);
        await _files.SetPermissionsAsync($"{_dir}/real/shared.txt", 0x180, Ct); // 0600
        // alias -> real/a, and real/a/cfg -> ../shared.txt: through alias, "cfg" leads to real/shared.txt.
        await ShellAsync($"ln -s real/a {_dir}/alias && ln -s ../shared.txt {_dir}/real/a/cfg");

        FileEntry cfg = (await _files.StatAsync($"{_dir}/alias/cfg", Ct))!;
        Assert.Equal(FileEntryKind.Symlink, cfg.Kind);
        Assert.Equal("600", cfg.OctalMode); // the target's, not the link's own 0777

        string local = Path.Combine(_local.Path, "cfg");
        File.WriteAllText(local, "through links");
        using var queue = new TransferQueue(Path.Combine(_local.Path, "relay"));
        await queue.Upload([local], _files, $"{_dir}/alias", _ => Task.FromResult(ConflictChoice.Replace)).Completion.WaitAsync(Timeout, Ct);

        Assert.Equal("through links", await ReadRemoteAsync($"{_dir}/real/shared.txt"));
        Assert.Equal("600", (await _files.StatAsync($"{_dir}/real/shared.txt", Ct))!.OctalMode);
        Assert.Equal(FileEntryKind.Symlink, (await _files.StatAsync($"{_dir}/real/a/cfg", Ct))!.Kind); // still a link
        Assert.Null(await _files.StatAsync($"{_dir}/shared.txt", Ct)); // nothing written where the text alone points

        string back = Path.Combine(_local.Path, "back");
        await queue.Download([(await _files.StatAsync($"{_dir}/alias/cfg", Ct))!], _files, back).Completion.WaitAsync(Timeout, Ct);
        Assert.Equal("through links", File.ReadAllText(Path.Combine(back, "cfg")));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(back, "cfg")));
    }

    [Fact]
    public async Task ClosingTheQueue_LetsTheRunningUploadRemoveItsPartialFile()
    {
        RequireServer();
        string big = Path.Combine(_local.Path, "big.bin");
        File.WriteAllBytes(big, new byte[64 * 1024 * 1024]);
        var queue = new TransferQueue(Path.Combine(_local.Path, "relay"));
        FileTransfer upload = queue.Upload([big], _files, _dir);
        while (upload.Snapshot() is { State: not TransferState.Running } p && !p.IsFinished)
            await Task.Delay(20, Ct);
        await Task.Delay(100, Ct);

        await queue.CloseAsync(TimeSpan.FromSeconds(10));

        Assert.True(upload.Snapshot().IsFinished);
        Assert.DoesNotContain(await _files.ListAsync(_dir, Ct), e => e.Name.Contains("tgk-part") || e.Name == "big.bin" && upload.Snapshot().State != TransferState.Done);
    }

    [Fact]
    public async Task Permissions_AreSetAndRead()
    {
        RequireServer();
        await _files.CreateFileAsync($"{_dir}/run.sh", Ct);

        await _files.SetPermissionsAsync($"{_dir}/run.sh", 0x1E8, Ct); // 0750

        FileEntry entry = (await _files.StatAsync($"{_dir}/run.sh", Ct))!;
        Assert.Equal("750", entry.OctalMode);
        Assert.Equal("-rwxr-x---", entry.Permissions);
    }

    [Fact]
    public async Task UploadThenDownload_CopiesFoldersContentTimesAndPermissions()
    {
        RequireServer();
        string source = Path.Combine(_local.Path, "project");
        Directory.CreateDirectory(Path.Combine(source, "src", "deep"));
        File.WriteAllText(Path.Combine(source, "README.md"), "hello ✓\n");
        File.WriteAllBytes(Path.Combine(source, "src", "deep", "data.bin"), Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray());
        File.WriteAllText(Path.Combine(source, "src", "run.sh"), "#!/bin/sh\necho hi\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path.Combine(source, "src", "run.sh"), (UnixFileMode)0x1ED); // 0755
        var stamp = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(source, "README.md"), stamp);
        string single = Path.Combine(_local.Path, "single.txt");
        File.WriteAllText(single, "one file");

        using var queue = new TransferQueue(Path.Combine(_local.Path, "relay"));
        FileTransfer upload = queue.Upload([source, single], _files, _dir);
        await upload.Completion.WaitAsync(Timeout, Ct);

        TransferProgress done = upload.Snapshot();
        Assert.Equal(TransferState.Done, done.State);
        Assert.Equal(4, done.TotalFiles);
        Assert.Equal(done.TotalBytes, done.DoneBytes);
        Assert.Equal(1.0, done.Fraction);
        FileEntry readme = (await _files.StatAsync($"{_dir}/project/README.md", Ct))!;
        Assert.Equal(stamp, readme.Modified.UtcDateTime);
        Assert.Equal(300_000, (await _files.StatAsync($"{_dir}/project/src/deep/data.bin", Ct))!.Size);
        if (!OperatingSystem.IsWindows())
            Assert.Equal("755", (await _files.StatAsync($"{_dir}/project/src/run.sh", Ct))!.OctalMode);
        Assert.DoesNotContain(await _files.ListAsync($"{_dir}/project", Ct), e => e.Name.Contains("tgk-part"));

        string back = Path.Combine(_local.Path, "back");
        FileTransfer download = queue.Download([(await _files.StatAsync($"{_dir}/project", Ct))!], _files, back);
        await download.Completion.WaitAsync(Timeout, Ct);

        Assert.Equal(TransferState.Done, download.Snapshot().State);
        Assert.Equal("hello ✓\n", File.ReadAllText(Path.Combine(back, "project", "README.md")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(source, "src", "deep", "data.bin")), File.ReadAllBytes(Path.Combine(back, "project", "src", "deep", "data.bin")));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(back, "project", "README.md")));
        Assert.Empty(Directory.GetFiles(back, "*tgk-part*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Conflicts_AreAskedOnce_AndSkipOrReplaceIsHonoured()
    {
        RequireServer();
        string a = Path.Combine(_local.Path, "a.txt"), b = Path.Combine(_local.Path, "b.txt");
        File.WriteAllText(a, "new a");
        File.WriteAllText(b, "new b");
        using var queue = new TransferQueue(Path.Combine(_local.Path, "relay"));
        await queue.Upload([a], _files, _dir).Completion.WaitAsync(Timeout, Ct);
        await _files.SetPermissionsAsync($"{_dir}/a.txt", 0x180, Ct); // 0600, kept when replaced
        File.WriteAllText(a, "newer a");

        string[]? asked = null;
        FileTransfer skip = queue.Upload([a, b], _files, _dir, names =>
        {
            asked = [.. names];
            return Task.FromResult(ConflictChoice.Skip);
        });
        await skip.Completion.WaitAsync(Timeout, Ct);
        Assert.Equal(["a.txt"], asked!);
        Assert.Equal(1, skip.Skipped);
        Assert.Equal("new a", await ReadRemoteAsync($"{_dir}/a.txt"));
        Assert.Equal("new b", await ReadRemoteAsync($"{_dir}/b.txt"));

        FileTransfer replace = queue.Upload([a], _files, _dir, _ => Task.FromResult(ConflictChoice.Replace));
        await replace.Completion.WaitAsync(Timeout, Ct);
        Assert.Equal("newer a", await ReadRemoteAsync($"{_dir}/a.txt"));
        Assert.Equal("600", (await _files.StatAsync($"{_dir}/a.txt", Ct))!.OctalMode);

        FileTransfer cancelled = queue.Upload([a], _files, _dir, _ => Task.FromResult(ConflictChoice.Cancel));
        await cancelled.Completion.WaitAsync(Timeout, Ct);
        Assert.Equal(TransferState.Cancelled, cancelled.Snapshot().State);
    }

    [Fact]
    public async Task CancelledDownload_LeavesNoPartialFile()
    {
        RequireServer();
        string big = Path.Combine(_local.Path, "big.bin");
        File.WriteAllBytes(big, new byte[8 * 1024 * 1024]);
        using var queue = new TransferQueue(Path.Combine(_local.Path, "relay"));
        await queue.Upload([big], _files, _dir).Completion.WaitAsync(Timeout, Ct);

        string target = Path.Combine(_local.Path, "out");
        FileTransfer download = queue.Download([(await _files.StatAsync($"{_dir}/big.bin", Ct))!], _files, target);
        download.Cancel();
        await download.Completion.WaitAsync(Timeout, Ct);

        Assert.Equal(TransferState.Cancelled, download.Snapshot().State);
        Assert.False(File.Exists(Path.Combine(target, "big.bin")));
        Assert.Empty(Directory.Exists(target) ? Directory.GetFiles(target) : []);
    }

    [Fact]
    public async Task EditedFile_IsUploadedBackWhenSaved()
    {
        RequireServer();
        await _files.CreateFileAsync($"{_dir}/notes.txt", Ct);
        using var edits = new EditedFiles(_local.Path);
        using var queue = new TransferQueue(Path.Combine(_local.Path, "relay"));
        FileEntry notes = (await _files.StatAsync($"{_dir}/notes.txt", Ct))!;
        string local = edits.LocalPathFor(notes.Path);
        await queue.DownloadFile(notes, _files, local).Completion.WaitAsync(Timeout, Ct);
        edits.Watch(notes.Path, local);
        var saved = new TaskCompletionSource<(string Remote, string Local)>(TaskCreationOptions.RunContinuationsAsynchronously);
        edits.Saved += (remote, path) => saved.TrySetResult((remote, path));

        File.WriteAllText(local, "edited ✓");
        (string remote, string path) = await saved.Task.WaitAsync(Timeout, Ct);
        await queue.UploadFile(path, _files, remote).Completion.WaitAsync(Timeout, Ct);

        Assert.Equal("edited ✓", await ReadRemoteAsync($"{_dir}/notes.txt"));
    }

    [Fact]
    public async Task BetweenTwoHosts_FilesGoThroughAPrivateBuffer_AndAMoveRemovesTheOriginals()
    {
        RequireServer();
        // A second connection plays host B (the same test server, another file system object).
        using var other = new SftpConnection(new RemoteConnectionEndToEndTests.TrustAll());
        await other.ConnectAsync(RemoteConnectionEndToEndTests.Request("TGK_E2E_SSH"), Ct);
        var hostB = new SftpFileSystem(other, "host B");
        await _files.CreateDirectoryAsync($"{_dir}/a", Ct);
        await _files.CreateDirectoryAsync($"{_dir}/a/inner", Ct);
        await _files.CreateDirectoryAsync($"{_dir}/b", Ct);
        string seed = Path.Combine(_local.Path, "seed");
        File.WriteAllBytes(seed, Enumerable.Range(0, 200_000).Select(i => (byte)(i * 7)).ToArray());
        string buffer = Path.Combine(_local.Path, "relay");
        using var queue = new TransferQueue(buffer);
        await queue.Upload([seed], _files, $"{_dir}/a/inner").Completion.WaitAsync(Timeout, Ct);
        await _files.SetPermissionsAsync($"{_dir}/a/inner/seed", 0x1E0, Ct); // 0740, carried to the copy

        FileTransfer copy = queue.Copy(_files, [(await _files.StatAsync($"{_dir}/a", Ct))!], hostB, $"{_dir}/b");
        await copy.Completion.WaitAsync(Timeout, Ct);

        Assert.Equal(TransferState.Done, copy.Snapshot().State);
        Assert.Equal(TransferKind.Copy, copy.Kind);
        Assert.Equal(File.ReadAllBytes(seed), await ReadRemoteBytesAsync($"{_dir}/b/a/inner/seed"));
        Assert.Equal("740", (await hostB.StatAsync($"{_dir}/b/a/inner/seed", Ct))!.OctalMode);
        Assert.NotNull(await _files.StatAsync($"{_dir}/a/inner/seed", Ct)); // a copy keeps the original
        Assert.Empty(Directory.Exists(buffer) ? Directory.GetFileSystemEntries(buffer) : []);

        await _files.CreateDirectoryAsync($"{_dir}/c", Ct);
        FileTransfer move = queue.Copy(hostB, [(await hostB.StatAsync($"{_dir}/b/a", Ct))!], _files, $"{_dir}/c", move: true);
        await move.Completion.WaitAsync(Timeout, Ct);
        Assert.Equal(TransferState.Done, move.Snapshot().State);
        Assert.Null(await hostB.StatAsync($"{_dir}/b/a", Ct));
        Assert.Equal(200_000, (await _files.StatAsync($"{_dir}/c/a/inner/seed", Ct))!.Size);
    }

    [Fact]
    public async Task WithinOneHost_AMoveIsARename_AndACopyGoesThroughTheBuffer()
    {
        RequireServer();
        await _files.CreateDirectoryAsync($"{_dir}/from", Ct);
        await _files.CreateDirectoryAsync($"{_dir}/to", Ct);
        await _files.CreateFileAsync($"{_dir}/from/x.txt", Ct);
        await _files.CreateFileAsync($"{_dir}/to/x.txt", Ct);
        using var queue = new TransferQueue(Path.Combine(_local.Path, "relay"));
        FileEntry x = (await _files.StatAsync($"{_dir}/from/x.txt", Ct))!;

        string[]? asked = null;
        FileTransfer move = queue.Copy(_files, [x], _files, $"{_dir}/to", move: true, names =>
        {
            asked = [.. names];
            return Task.FromResult(ConflictChoice.Replace);
        });
        await move.Completion.WaitAsync(Timeout, Ct);
        Assert.Equal(TransferState.Done, move.Snapshot().State);
        Assert.Equal(["x.txt"], asked!);
        Assert.Null(await _files.StatAsync($"{_dir}/from/x.txt", Ct));
        Assert.NotNull(await _files.StatAsync($"{_dir}/to/x.txt", Ct));

        FileTransfer copy = queue.Copy(_files, [(await _files.StatAsync($"{_dir}/to", Ct))!], _files, $"{_dir}/from");
        await copy.Completion.WaitAsync(Timeout, Ct);
        Assert.Equal(TransferState.Done, copy.Snapshot().State);
        Assert.NotNull(await _files.StatAsync($"{_dir}/from/to/x.txt", Ct));

        FileTransfer intoItself = queue.Copy(_files, [(await _files.StatAsync($"{_dir}/from", Ct))!], _files, $"{_dir}/from/to", move: true);
        await intoItself.Completion.WaitAsync(Timeout, Ct);
        Assert.Equal(TransferState.Failed, intoItself.Snapshot().State);
        Assert.Contains("into itself", intoItself.Snapshot().Error);
    }

    [Fact]
    public async Task DirectCopy_HostAStreamsToHostB_CheckingBsKey()
    {
        RequireServer();
        Assert.SkipWhen(!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/ssh"), "the test server's host needs an ssh client");
        SshConnectRequest request = RemoteConnectionEndToEndTests.Request("TGK_E2E_SSH");
        using var a = new RemoteConnection(new RemoteConnectionEndToEndTests.TrustAll());
        await a.ConnectAsync(request, Ct);
        await ShellAsync($"mkdir -p {_dir}/src/sub {_dir}/dst && printf 'one ✓' > {_dir}/src/sub/one.txt && printf two > \"{_dir}/src/it's two.txt\" && chmod 640 {_dir}/src/sub/one.txt");
        var target = new DirectTarget(request.Host, request.Port, request.Username, request.Password, null, null, _connection!.HostKey!);
        Assert.StartsWith("tgk-direct-target ssh-", DirectCopy.KnownHostsLine(target.HostKey));

        await DirectCopy.RunAsync(a, $"{_dir}/src", ["sub", "it's two.txt"], target, $"{_dir}/dst/new folder", Ct);

        Assert.Equal("one ✓", await ReadRemoteAsync($"{_dir}/dst/new folder/sub/one.txt"));
        Assert.Equal("two", await ReadRemoteAsync($"{_dir}/dst/new folder/it's two.txt"));
        Assert.Equal("640", (await _files.StatAsync($"{_dir}/dst/new folder/sub/one.txt", Ct))!.OctalMode);
        CommandResult leftovers = await a.RunAsync("ls -d ${TMPDIR:-/tmp}/tgk-direct-* 2>/dev/null | wc -l", Timeout, ct: Ct);
        Assert.Equal("0", leftovers.Stdout.Trim());

        // Another key for B: host A refuses to send anything.
        byte[] wrong = (byte[])target.HostKey.Clone();
        wrong[^1] ^= 0x55;
        var refused = await Assert.ThrowsAsync<FileOperationException>(() =>
            DirectCopy.RunAsync(a, $"{_dir}/src", ["sub"], target with { HostKey = wrong }, $"{_dir}/dst/refused", Ct));
        Assert.Contains("not the one trusted", refused.Message);
        Assert.Null(await _files.StatAsync($"{_dir}/dst/refused", Ct));

        var badPassword = await Assert.ThrowsAsync<FileOperationException>(() =>
            DirectCopy.RunAsync(a, $"{_dir}/src", ["sub"], target with { Password = "wrong" }, $"{_dir}/dst/denied", Ct));
        Assert.Contains("refused the sign-in", badPassword.Message);
    }

    [Fact]
    public async Task JumpHost_ConnectsThroughIt()
    {
        SshConnectRequest target = RemoteConnectionEndToEndTests.Request("TGK_E2E_SSH");
        using var c = new SftpConnection(new RemoteConnectionEndToEndTests.TrustAll());
        await c.ConnectAsync(target with { Host = "localhost", JumpChain = [target with { Name = "bastion" }] }, Ct);

        var files = new SftpFileSystem(c);
        Assert.NotNull(await files.StatAsync("/tmp", Ct));
        Assert.True(c.IsConnected);
    }

    [Fact]
    public async Task WrongPassword_IsAnAuthenticationFailure()
    {
        SshConnectRequest target = RemoteConnectionEndToEndTests.Request("TGK_E2E_SSH");
        using var c = new SftpConnection(new RemoteConnectionEndToEndTests.TrustAll());

        var error = await Assert.ThrowsAsync<SshSessionException>(() => c.ConnectAsync(target with { Password = "wrong" + target.Password }, Ct));

        Assert.Equal(SshErrorKind.AuthenticationFailed, error.Kind);
        Assert.False(c.IsConnected);
    }

    // Links are made with ln: servers disagree on the order of SFTP's symlink arguments (OpenSSH swaps them).
    private static async Task ShellAsync(string command)
    {
        using var shell = new RemoteConnection(new RemoteConnectionEndToEndTests.TrustAll());
        await shell.ConnectAsync(RemoteConnectionEndToEndTests.Request("TGK_E2E_SSH"), Ct);
        CommandResult result = await shell.RunAsync(command, Timeout, ct: Ct);
        Assert.True(result.ExitCode == 0, result.Stderr);
    }

    private async Task<byte[]> ReadRemoteBytesAsync(string path)
    {
        using var buffer = new MemoryStream();
        await Client.DownloadFileAsync(path, buffer, Ct);
        return buffer.ToArray();
    }

    private async Task<string> ReadRemoteAsync(string path)
    {
        using var buffer = new MemoryStream();
        await Client.DownloadFileAsync(path, buffer, Ct);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
