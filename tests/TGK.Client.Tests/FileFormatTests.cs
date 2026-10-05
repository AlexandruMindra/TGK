using System;
using System.Linq;
using TGK.Client.Files;
using TGK.Core.Files;
using Xunit;

namespace TGK.Client.Tests;

public class FileFormatTests
{
    private static FileEntry File(string name, long size = 0, int day = 1) =>
        new($"/d/{name}", name, FileEntryKind.File, size, new DateTimeOffset(2025, 1, day, 0, 0, 0, TimeSpan.Zero), 0x1A4, 0, 0);

    private static FileEntry Folder(string name) =>
        new($"/d/{name}", name, FileEntryKind.Directory, 4096, DateTimeOffset.UnixEpoch, 0x1ED, 0, 0);

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(51234, "50 KB")]
    [InlineData(1048575, "1.0 MB")]
    [InlineData(2400000, "2.3 MB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5.0 GB")]
    public void Sizes_are_short_and_1024_based(long bytes, string expected) => Assert.Equal(expected, FileFormat.Size(bytes));

    [Fact]
    public void Dates_show_the_time_when_recent_and_the_year_otherwise()
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        string recent = FileFormat.Date(now.AddDays(-3), now);
        string old = FileFormat.Date(now.AddYears(-2), now);
        Assert.Matches(@"^Oct \d{1,2} \d\d:\d\d$", recent);
        Assert.Matches(@"^Oct \d{1,2} 2024$", old);
        Assert.EndsWith("2027", FileFormat.Date(now.AddYears(1), now)); // in the future: the year
    }

    [Theory]
    [InlineData(5, "5s")]
    [InlineData(125, "2m 05s")]
    [InlineData(4800, "1h 20m")]
    public void Durations_are_short(int seconds, string expected) => Assert.Equal(expected, FileFormat.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Names_sort_naturally_with_folders_first()
    {
        var entries = new[] { File("file10.log"), File("File2.log"), Folder("zeta"), File("file1.log"), Folder("Alpha") };

        Assert.Equal(["Alpha", "zeta", "file1.log", "File2.log", "file10.log"],
            FileFormat.Sort(entries, FileSortColumn.Name, descending: false).Select(e => e.Name));
        Assert.Equal(["zeta", "Alpha", "file10.log", "File2.log", "file1.log"],
            FileFormat.Sort(entries, FileSortColumn.Name, descending: true).Select(e => e.Name));
    }

    [Fact]
    public void Other_columns_sort_with_names_breaking_ties()
    {
        var entries = new[] { File("b", 10, day: 3), File("a", 10, day: 1), File("c", 5, day: 2), Folder("dir") };

        Assert.Equal(["dir", "c", "a", "b"], FileFormat.Sort(entries, FileSortColumn.Size, false).Select(e => e.Name));
        Assert.Equal(["dir", "b", "a", "c"], FileFormat.Sort(entries, FileSortColumn.Size, true).Select(e => e.Name));
        Assert.Equal(["dir", "a", "c", "b"], FileFormat.Sort(entries, FileSortColumn.Modified, false).Select(e => e.Name));
    }

    [Fact]
    public void Natural_order_compares_digit_runs_by_value()
    {
        Assert.True(FileFormat.CompareNames("v2", "v10") < 0);
        Assert.True(FileFormat.CompareNames("v010", "v9") > 0);
        Assert.True(FileFormat.CompareNames("a", "B") < 0);
        Assert.NotEqual(0, FileFormat.CompareNames("a", "A")); // still a total order
        Assert.Equal(0, FileFormat.CompareNames("same", "same"));
    }

    [Fact]
    public void Filter_and_hidden_files()
    {
        Assert.True(FileFormat.Shows(File("notes.txt"), "", showHidden: false));
        Assert.False(FileFormat.Shows(File(".bashrc"), "", showHidden: false));
        Assert.True(FileFormat.Shows(File(".bashrc"), "", showHidden: true));
        Assert.True(FileFormat.Shows(File("Notes.TXT"), "notes", showHidden: false));
        Assert.False(FileFormat.Shows(File("todo.md"), "notes", showHidden: false));
    }

    [Theory]
    [InlineData("/var/log", "/var")]
    [InlineData("/var", "/")]
    [InlineData("/", "/")]
    [InlineData("/var/log/", "/var")]
    public void Parent_folders(string path, string parent) => Assert.Equal(parent, FileFormat.Parent(path));

    [Fact]
    public void Summaries_and_descriptions()
    {
        Assert.Equal("Empty folder", FileFormat.Summary([]));
        Assert.Equal("1 file", FileFormat.Summary([File("a")]));
        Assert.Equal("2 folders", FileFormat.Summary([Folder("a"), Folder("b")]));
        Assert.Equal("1 folder, 2 files", FileFormat.Summary([Folder("a"), File("b"), File("c")]));
        Assert.Equal("“a”", FileFormat.Describe([File("a")]));
        Assert.Equal("“a” and “b”", FileFormat.Describe([File("a"), File("b")]));
        Assert.Equal("3 items", FileFormat.Describe([File("a"), File("b"), File("c")]));
    }

    [Theory]
    [InlineData("755", 0x1ED)]
    [InlineData("0644", 0x1A4)]
    [InlineData("4755", 0x9ED)]
    [InlineData(" 600 ", 0x180)]
    [InlineData("78", null)]
    [InlineData("75", null)]
    [InlineData("12345", null)]
    [InlineData("rwx", null)]
    public void Octal_modes_are_parsed(string text, int? mode) => Assert.Equal(mode, FileFormat.ParseMode(text));

    [Fact]
    public void Control_characters_in_names_are_shown_as_question_marks()
    {
        Assert.Equal("plain.txt", FileFormat.Printable("plain.txt"));
        Assert.Equal("evil?name?", FileFormat.Printable("evil\nname\u202e"));
        Assert.Equal("report?fdp.exe", FileFormat.Printable("report\u202Efdp.exe"));
        Assert.Equal("a?b", FileFormat.Printable("a\u001bb"));
    }
}
