using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Files;
using Xunit;

namespace TGK.Core.Tests;

/// <summary>The file browser's pieces that need no server: entries, names, local copies of edited files.</summary>
public sealed class SftpTests
{
    private static FileEntry Entry(FileEntryKind kind, int mode) =>
        new("/x/name", "name", kind, 0, DateTimeOffset.UnixEpoch, mode, 1000, 1000);

    [Theory]
    [InlineData(FileEntryKind.File, 0x1A4, "-rw-r--r--", "644")]
    [InlineData(FileEntryKind.Directory, 0x1ED, "drwxr-xr-x", "755")]
    [InlineData(FileEntryKind.Symlink, 0x1FF, "lrwxrwxrwx", "777")]
    [InlineData(FileEntryKind.File, 0x9ED, "-rwsr-xr-x", "4755")]
    [InlineData(FileEntryKind.File, 0x5A4, "-rw-r-Sr--", "2644")]
    [InlineData(FileEntryKind.Directory, 0x3FF, "drwxrwxrwt", "1777")]
    public void Permissions_AreShownLikeLs(FileEntryKind kind, int mode, string expected, string octal)
    {
        FileEntry entry = Entry(kind, mode);
        Assert.Equal(expected, entry.Permissions);
        Assert.Equal(octal, entry.OctalMode);
    }

    [Fact]
    public void Directories_IncludeLinksToThem_AndDotNamesAreHidden()
    {
        Assert.True(Entry(FileEntryKind.Directory, 0).IsDirectory);
        Assert.True((Entry(FileEntryKind.Symlink, 0) with { LinksToDirectory = true }).IsDirectory);
        Assert.False(Entry(FileEntryKind.Symlink, 0).IsDirectory);
        Assert.True((Entry(FileEntryKind.File, 0) with { Name = ".bashrc" }).IsHidden);
        Assert.False(Entry(FileEntryKind.File, 0).IsHidden);
    }

    [Theory]
    [InlineData("report.pdf", null)]
    [InlineData("", "Enter a name.")]
    [InlineData("..", "\"..\" can't be used as a name.")]
    [InlineData("a/b", "A name can't contain \"/\".")]
    [InlineData("line\nbreak", "A name can't contain line breaks.")]
    public void Names_AreValidated(string name, string? problem) => Assert.Equal(problem, SftpFileSystem.ValidateName(name));

    [Theory]
    [InlineData("notes.txt", "notes.txt")]
    [InlineData("a:b*c?.txt", "a_b_c_.txt")]
    [InlineData("trailing. ", "trailing")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("console.log", "console.log")]
    [InlineData("...", "_")]
    public void RemoteNames_BecomeValidWindowsNames(string remote, string local) => Assert.Equal(local, LocalNames.ToLocal(remote, windows: true));

    [Fact]
    public void RemoteNames_StayAsTheyAreOnUnix() => Assert.Equal("a:b*c?.txt", LocalNames.ToLocal("a:b*c?.txt", windows: false));

    [Fact]
    public void Join_AddsOneSlash()
    {
        Assert.Equal("/a/b", SftpFileSystem.Join("/a", "b"));
        Assert.Equal("/a/b", SftpFileSystem.Join("/a/", "b"));
        Assert.Equal("/b", SftpFileSystem.Join("/", "b"));
    }

    [Fact]
    public async Task EditedFiles_ReportSavesWithNewContentOnly_AndCleanUp()
    {
        using var temp = new TempDirectory();
        string root;
        var saves = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using (var edits = new EditedFiles(temp.Path))
        {
            string local = edits.LocalPathFor("/srv/app/config.yml");
            Assert.Equal("config.yml", Path.GetFileName(local));
            File.WriteAllText(local, "a: 1\n");
            edits.Watch("/srv/app/config.yml", local);
            Assert.Equal(local, edits.LocalPathFor("/srv/app/config.yml"));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(local)!));
            var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            edits.Saved += (remote, _) =>
            {
                saves.Enqueue(remote);
                saved.TrySetResult();
            };

            File.WriteAllText(local, "a: 1\n"); // same content: not a save
            await Task.Delay(1500, TestContext.Current.CancellationToken);
            Assert.Empty(saves);

            File.WriteAllText(local, "a: 2\n");
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(["/srv/app/config.yml"], saves);
            root = Path.GetDirectoryName(Path.GetDirectoryName(local)!)!;
        }
        Assert.False(Directory.Exists(root));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LocalFiles_ListCreateRenameDelete_AndLinksAreLeftAlone()
    {
        using var temp = new TempDirectory();
        LocalFileSystem local = LocalFileSystem.Instance;
        string root = temp.Path;
        await local.CreateDirectoryAsync(local.Join(root, "dir"), Ct);
        await local.CreateFileAsync(Path.Combine(root, "dir", "a.txt"), Ct);
        await Assert.ThrowsAsync<FileOperationException>(() => local.CreateFileAsync(Path.Combine(root, "dir", "a.txt"), Ct));
        File.WriteAllText(local.Join(root, "target.txt"), "keep");
        bool links = !OperatingSystem.IsWindows();
        if (links)
        {
            File.CreateSymbolicLink(Path.Combine(root, "dir", "link.txt"), local.Join(root, "target.txt"));
            File.SetUnixFileMode(local.Join(root, "target.txt"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var entries = (await local.ListAsync(local.Join(root, "dir"), Ct)).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(links ? ["a.txt", "link.txt"] : ["a.txt"], entries.Select(e => e.Name));
        if (links)
        {
            FileEntry link = entries[1];
            Assert.Equal(FileEntryKind.Symlink, link.Kind);
            Assert.Equal(4, link.Size); // the target's
            Assert.Equal("600", link.OctalMode);
            await local.RenameAsync(link, Path.Combine(root, "dir", "renamed.txt"), Ct);
            Assert.Equal(FileEntryKind.Symlink, (await local.StatAsync(Path.Combine(root, "dir", "renamed.txt"), Ct))!.Kind);
        }

        await local.DeleteAsync((await local.StatAsync(local.Join(root, "dir"), Ct))!, null, Ct);
        Assert.False(Directory.Exists(local.Join(root, "dir")));
        Assert.Equal("keep", File.ReadAllText(local.Join(root, "target.txt"))); // a link inside was removed, not followed
        Assert.Null(await local.StatAsync(local.Join(root, "missing"), Ct));
    }

    [Fact]
    public async Task LocalFiles_CopyAndMoveWithinThisComputer()
    {
        using var temp = new TempDirectory();
        LocalFileSystem local = LocalFileSystem.Instance;
        string from = Path.Combine(temp.Path, "from"), to = Path.Combine(temp.Path, "to");
        Directory.CreateDirectory(Path.Combine(from, "deep"));
        Directory.CreateDirectory(to);
        File.WriteAllText(Path.Combine(from, "deep", "f.txt"), "content");
        var stamp = new DateTime(2023, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(from, "deep", "f.txt"), stamp);
        using var queue = new TransferQueue(Path.Combine(temp.Path, "relay"));

        FileTransfer copy = queue.Copy(local, [(await local.StatAsync(Path.Combine(from, "deep"), Ct))!], local, to);
        await copy.Completion.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal(TransferState.Done, copy.Snapshot().State);
        Assert.Equal("content", File.ReadAllText(Path.Combine(to, "deep", "f.txt")));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(to, "deep", "f.txt")));

        FileTransfer move = queue.Copy(local, [(await local.StatAsync(Path.Combine(from, "deep", "f.txt"), Ct))!], local, to, move: true);
        await move.Completion.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal(TransferState.Done, move.Snapshot().State);
        Assert.False(File.Exists(Path.Combine(from, "deep", "f.txt")));
        Assert.True(File.Exists(Path.Combine(to, "f.txt")));
        Assert.Empty(Directory.GetFiles(temp.Path, "*tgk-part*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LocalFiles_ReadRanges_AndSaveInPlaceOfTheFile()
    {
        using var temp = new TempDirectory();
        LocalFileSystem local = LocalFileSystem.Instance;
        string file = Path.Combine(temp.Path, "notes.txt");
        await local.WriteAsync(file, "hello world"u8.ToArray(), Ct);
        Assert.Equal("world", System.Text.Encoding.UTF8.GetString(await local.ReadAsync(file, 6, 100, Ct)));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        await local.WriteAsync(file, "changed"u8.ToArray(), Ct);
        Assert.Equal("changed", File.ReadAllText(file));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        Assert.Single(Directory.GetFiles(temp.Path)); // no temporary file left
        await Assert.ThrowsAsync<FileOperationException>(() => local.ReadAsync(Path.Combine(temp.Path, "missing"), 0, 1, Ct));
    }

    [Fact]
    public void LocalFiles_PathsAndNames()
    {
        LocalFileSystem local = LocalFileSystem.Instance;
        string home = local.Home;
        Assert.Equal(home, local.Resolve("~", "/anywhere"));
        Assert.Equal(Path.Combine(home, "docs"), local.Resolve("~/docs", "/anywhere"));
        string current = Path.Combine(home, "a");
        Assert.Equal(Path.Combine(home, "a", "b"), local.Resolve("b", current));
        Assert.Equal(home, local.Resolve("..", current));
        Assert.True(local.IsWithin(Path.Combine(home, "a", "b"), Path.Combine(home, "a")));
        Assert.False(local.IsWithin(Path.Combine(home, "ab"), Path.Combine(home, "a")));
        Assert.Equal(Path.Combine(home, "a"), local.Parent(Path.Combine(home, "a", "b")));
        Assert.Null(local.ValidateName("notes.txt"));
        Assert.NotNull(local.ValidateName("a/b"));
        Assert.NotNull(local.ValidateName(".."));
    }

    [Fact]
    public void DirectCopy_KnowsKeyTypes_AndKeepsSecretsOutOfTheScript()
    {
        byte[] blob = [0, 0, 0, 11, .. "ssh-ed25519"u8.ToArray(), 0, 0, 0, 2, 1, 2];
        Assert.Equal("ssh-ed25519", DirectCopy.KeyType(blob));
        Assert.Throws<ArgumentException>(() => DirectCopy.KeyType([0, 0, 1, 0, 65]));

        var target = new DirectTarget("10.0.0.2", 2222, "deploy", "s3cret-pw", null, null, blob);
        string script = DirectCopy.Script("tgk-direct-x", "/srv/it's", ["a b", "c"], target, "/dst");
        Assert.DoesNotContain("s3cret-pw", script);
        Assert.Contains("-p 2222 -l 'deploy'", script);
        Assert.Contains("StrictHostKeyChecking=yes", script);
        Assert.Contains("cd -- '/srv/it'\\''s'", script);
        Assert.Contains("tar -cf - -- 'a b' 'c'", script);
        string input = System.Text.Encoding.ASCII.GetString(DirectCopy.Input(target, null));
        Assert.DoesNotContain("s3cret-pw", input); // base64 on the command's input, never in its arguments
        Assert.Contains(Convert.ToBase64String("s3cret-pw"u8.ToArray()), input);

        Assert.Null(target.Validate());
        Assert.NotNull((target with { Password = null }).Validate());
        // Any key SSH.NET reads goes to host A converted to OpenSSH's format (PuTTY ones included).
        Assert.Null((target with { Password = null, PrivateKey = "PuTTY-User-Key-File-3: ssh-ed25519" }).Validate());
        Assert.Throws<FileOperationException>(() => DirectCopy.KeyFor(target with { PrivateKey = "not a key" }));
        Assert.NotNull((target with { Host = "a b" }).Validate());
    }
}
