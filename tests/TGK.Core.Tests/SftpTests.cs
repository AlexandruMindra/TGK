using System;
using System.IO;
using System.Threading.Tasks;
using TGK.Core.Sftp;
using Xunit;

namespace TGK.Core.Tests;

/// <summary>The file browser's pieces that need no server: entries, names, local copies of edited files.</summary>
public sealed class SftpTests
{
    private static SftpEntry Entry(SftpEntryKind kind, int mode) =>
        new("/x/name", "name", kind, 0, DateTimeOffset.UnixEpoch, mode, 1000, 1000);

    [Theory]
    [InlineData(SftpEntryKind.File, 0x1A4, "-rw-r--r--", "644")]
    [InlineData(SftpEntryKind.Directory, 0x1ED, "drwxr-xr-x", "755")]
    [InlineData(SftpEntryKind.Symlink, 0x1FF, "lrwxrwxrwx", "777")]
    [InlineData(SftpEntryKind.File, 0x9ED, "-rwsr-xr-x", "4755")]
    [InlineData(SftpEntryKind.File, 0x5A4, "-rw-r-Sr--", "2644")]
    [InlineData(SftpEntryKind.Directory, 0x3FF, "drwxrwxrwt", "1777")]
    public void Permissions_AreShownLikeLs(SftpEntryKind kind, int mode, string expected, string octal)
    {
        SftpEntry entry = Entry(kind, mode);
        Assert.Equal(expected, entry.Permissions);
        Assert.Equal(octal, entry.OctalMode);
    }

    [Fact]
    public void Directories_IncludeLinksToThem_AndDotNamesAreHidden()
    {
        Assert.True(Entry(SftpEntryKind.Directory, 0).IsDirectory);
        Assert.True((Entry(SftpEntryKind.Symlink, 0) with { LinksToDirectory = true }).IsDirectory);
        Assert.False(Entry(SftpEntryKind.Symlink, 0).IsDirectory);
        Assert.True((Entry(SftpEntryKind.File, 0) with { Name = ".bashrc" }).IsHidden);
        Assert.False(Entry(SftpEntryKind.File, 0).IsHidden);
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
}
