using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TGK.Core.Sftp;

namespace TGK.Client.Files;

/// <summary>The columns the file list can be sorted by.</summary>
public enum FileSortColumn
{
    Name,
    Size,
    Modified,
    Permissions,
}

/// <summary>How the file browser writes sizes, dates and counts, and orders entries (UI-free, so it is unit-tested).</summary>
public static class FileFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>"0 B", "512 B", "1.5 KB", "34 KB", "2.0 GB" (1024-based, one decimal below 10).</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1024)
            return $"{Math.Max(0, bytes)} B";
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        string number = value < 10 ? value.ToString("0.0", CultureInfo.InvariantCulture) : Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        if (number == "1024" && unit < Units.Length - 1)
            return $"1.0 {Units[unit + 1]}";
        return $"{number} {Units[unit]}";
    }

    /// <summary>A transfer rate, e.g. "2.4 MB/s".</summary>
    public static string Speed(double bytesPerSecond) => Size((long)Math.Max(0, bytesPerSecond)) + "/s";

    /// <summary>
    /// A modification time in local time, the way <c>ls -l</c> does: "Oct 5 14:05" within the last six months,
    /// "Oct 5 2023" otherwise.
    /// </summary>
    public static string Date(DateTimeOffset when, DateTimeOffset now)
    {
        DateTime local = when.ToLocalTime().DateTime;
        bool recent = when <= now.AddDays(1) && now - when < TimeSpan.FromDays(182);
        return local.ToString(recent ? "MMM d HH:mm" : "MMM d yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>"1 item", "3 items".</summary>
    public static string Count(int count, string noun) => $"{count:N0} {noun}{(count == 1 ? "" : "s")}";

    /// <summary>A remaining-time estimate, e.g. "12s", "3m 05s", "1h 20m".</summary>
    public static string Duration(TimeSpan time)
    {
        if (time.TotalSeconds < 60)
            return $"{Math.Max(1, (int)Math.Ceiling(time.TotalSeconds))}s";
        if (time.TotalMinutes < 60)
            return $"{(int)time.TotalMinutes}m {time.Seconds:00}s";
        return $"{(int)time.TotalHours}h {time.Minutes:00}m";
    }

    /// <summary>
    /// Orders entries for the list: folders (and links to folders) first, then the column, ties by name. Names compare
    /// naturally ("file2" before "file10") and without case first.
    /// </summary>
    public static List<SftpEntry> Sort(IEnumerable<SftpEntry> entries, FileSortColumn column, bool descending)
    {
        Comparison<SftpEntry> byColumn = column switch
        {
            FileSortColumn.Size => (a, b) => a.Size.CompareTo(b.Size),
            FileSortColumn.Modified => (a, b) => a.Modified.CompareTo(b.Modified),
            FileSortColumn.Permissions => (a, b) => string.CompareOrdinal(a.Permissions, b.Permissions),
            _ => (a, b) => CompareNames(a.Name, b.Name),
        };
        List<SftpEntry> sorted = [.. entries];
        sorted.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
                return a.IsDirectory ? -1 : 1; // folders stay on top in both directions
            int result = byColumn(a, b);
            if (result == 0 && column != FileSortColumn.Name)
                result = CompareNames(a.Name, b.Name);
            return descending ? -result : result;
        });
        return sorted;
    }

    /// <summary>Natural, case-insensitive order (runs of digits compare by value); ordinal as the tie-break.</summary>
    public static int CompareNames(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i]))
                    i++;
                while (j < b.Length && char.IsAsciiDigit(b[j]))
                    j++;
                string da = a[si..i].TrimStart('0'), db = b[sj..j].TrimStart('0');
                int byLength = da.Length.CompareTo(db.Length);
                if (byLength != 0)
                    return byLength;
                int byDigits = string.CompareOrdinal(da, db);
                if (byDigits != 0)
                    return byDigits;
                continue;
            }
            int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
            if (c != 0)
                return c;
            i++;
            j++;
        }
        int byRest = (a.Length - i).CompareTo(b.Length - j);
        return byRest != 0 ? byRest : string.CompareOrdinal(a, b);
    }

    /// <summary>Whether <paramref name="entry"/> passes the list's name filter (case-insensitive substring) and hidden-files setting.</summary>
    public static bool Shows(SftpEntry entry, string filter, bool showHidden) =>
        (showHidden || !entry.IsHidden) && (filter.Length == 0 || entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));

    /// <summary>The parent of an absolute remote path ("/" stays "/").</summary>
    public static string Parent(string path)
    {
        int slash = path.TrimEnd('/').LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    /// <summary>A short description of several entries for dialogs: "notes.txt", "notes.txt and logs", "3 items".</summary>
    public static string Describe(IReadOnlyList<SftpEntry> entries) => entries.Count switch
    {
        0 => "nothing",
        1 => $"“{entries[0].Name}”",
        2 => $"“{entries[0].Name}” and “{entries[1].Name}”",
        _ => Count(entries.Count, "item"),
    };

    /// <summary>"3 folders, 12 files" (only the parts that are there), for a folder's status line.</summary>
    public static string Summary(IReadOnlyCollection<SftpEntry> entries)
    {
        int folders = entries.Count(e => e.IsDirectory), files = entries.Count - folders;
        if (entries.Count == 0)
            return "Empty folder";
        if (folders == 0)
            return Count(files, "file");
        return files == 0 ? Count(folders, "folder") : $"{Count(folders, "folder")}, {Count(files, "file")}";
    }

    /// <summary>"755" or "4755" as a permission mode; null when it is not 3 or 4 octal digits.</summary>
    public static int? ParseMode(string text)
    {
        text = text.Trim();
        if (text.Length is < 3 or > 4 || text.Any(c => c is < '0' or > '7'))
            return null;
        return Convert.ToInt32(text, 8);
    }

    /// <summary>
    /// A remote name as shown: control characters (a line break in a name, say) and the invisible marks that reorder
    /// text (which can make "evil\u202Etxt.exe" look like "evilexe.txt") become "?".
    /// </summary>
    public static string Printable(string name)
    {
        if (!name.Any(Hidden))
            return name;
        var sb = new StringBuilder(name.Length);
        foreach (char ch in name)
            sb.Append(Hidden(ch) ? '?' : ch);
        return sb.ToString();

        static bool Hidden(char ch) => char.IsControl(ch) || ch is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069' or '\u200E' or '\u200F' or '\u061C';
    }
}
