using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Blossom;

namespace TGK.Client.Platform;

/// <summary>Opens web pages in the user's browser, folders in the file manager and files in their default program.</summary>
public static class ExternalLink
{
    /// <summary>Opens <paramref name="url"/> (https only); false when no browser could be started.</summary>
    public static bool Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;
        try
        {
            ProcessStartInfo start;
            if (OperatingSystem.IsWindows())
                start = new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true };
            else
            {
                // The URL is one argument, never parsed by a shell.
                start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
                start.ArgumentList.Add(uri.AbsoluteUri);
            }
            using Process? process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not open {uri.AbsoluteUri}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Shows a local folder in the file manager; false when none could be started.</summary>
    public static bool OpenFolder(string path)
    {
        if (!System.IO.Directory.Exists(path))
            return false;
        try
        {
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(path);
            using Process? process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not open {path}: {ex.Message}");
            return false;
        }
    }

    // Types opened in their own program: documents, media and plain data, which those programs show rather than run.
    // Everything else (programs, scripts, installers, disk images, shortcuts, macro-enabled documents, unknown types)
    // opens in the text editor: a file from a server is never started as a program.
    private static readonly HashSet<string> ViewableTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".toml", ".ini", ".conf", ".cfg",
        ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".tif", ".tiff", ".heic", ".avif",
        ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".mp4", ".mkv", ".mov", ".webm", ".avi",
        ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".rtf", ".epub",
        ".zip", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".7z",
    };

    /// <summary>
    /// Opens a local copy of a remote file: in the program the system uses for its type when that is a document,
    /// media or plain data, otherwise (or with <paramref name="asText"/>) in the text editor, so programs and scripts
    /// are never run. On Windows the copy is marked as downloaded from the internet. False when nothing could be
    /// started.
    /// </summary>
    public static bool OpenFile(string path, bool asText = false)
    {
        if (!System.IO.File.Exists(path))
            return false;
        bool viewable = !asText && ViewableTypes.Contains(System.IO.Path.GetExtension(path));
        try
        {
            if (OperatingSystem.IsWindows())
            {
                MarkDownloaded(path);
                return Start(viewable ? new ProcessStartInfo(path) { UseShellExecute = true } : Program(WindowsTextEditor(), path));
            }
            if (OperatingSystem.IsMacOS())
                return Start(viewable ? Program("open", path) : Program("open", "-t", path)); // -t: the default text editor
            if (viewable)
                return Start(Program("xdg-open", path));
            return OpenAsTextOnLinux(path);
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not open {path}: {ex.Message}");
            return false;
        }
    }

    // The desktop's text editor (the default application for text/plain), started through gtk-launch or gio; failing
    // that, xdg-open only when the file itself is text.
    private static bool OpenAsTextOnLinux(string path)
    {
        string? editor = Output("xdg-mime", "query", "default", "text/plain")?.Trim();
        if (editor is { Length: > 0 } && editor.EndsWith(".desktop", StringComparison.Ordinal))
        {
            if (TryStart(Program("gtk-launch", editor, path)))
                return true;
            string? file = FindDesktopFile(editor);
            if (file is not null && TryStart(Program("gio", "launch", file, path)))
                return true;
        }
        return Output("xdg-mime", "query", "filetype", path)?.Trim().StartsWith("text/", StringComparison.Ordinal) == true
            && Start(Program("xdg-open", path));
    }

    private static string? FindDesktopFile(string id)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d : System.IO.Path.Combine(home, ".local", "share");
        string dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } dirs ? dirs : "/usr/local/share:/usr/share";
        foreach (string dir in new[] { dataHome }.Concat(dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries)))
        {
            string candidate = System.IO.Path.Combine(dir, "applications", id);
            if (System.IO.File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static ProcessStartInfo Program(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        return start;
    }

    private static bool Start(ProcessStartInfo start)
    {
        using Process? process = Process.Start(start);
        return process is not null;
    }

    private static bool TryStart(ProcessStartInfo start)
    {
        try
        {
            using Process? process = Process.Start(start);
            if (process is null)
                return false;
            // gtk-launch fails at once (exit code) when it can't start the application.
            return !process.WaitForExit(1500) || process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // not installed
        }
    }

    private static string? Output(string file, params string[] arguments)
    {
        try
        {
            ProcessStartInfo start = Program(file, arguments);
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            using Process? process = Process.Start(start);
            if (process is null)
                return null;
            string output = process.StandardOutput.ReadToEnd();
            return process.WaitForExit(3000) && process.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    // The Mark of the Web: Windows (SmartScreen, Office's Protected View) then treats the copy as downloaded.
    private static void MarkDownloaded(string path)
    {
        try
        {
            System.IO.File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Not NTFS (e.g. a FAT drive): nothing to mark.
        }
    }

    // The program Windows opens .txt files with (e.g. Notepad++ or VS Code when the user chose it), else Notepad.
    private static string WindowsTextEditor()
    {
        uint size = 1024;
        var buffer = new System.Text.StringBuilder((int)size);
        return AssocQueryStringW(AssocFNone, AssocStrExecutable, ".txt", null, buffer, ref size) == 0 && System.IO.File.Exists(buffer.ToString())
            ? buffer.ToString()
            : "notepad.exe";
    }

    private const int AssocFNone = 0, AssocStrExecutable = 2;

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int AssocQueryStringW(int flags, int str, string assoc, string? extra, System.Text.StringBuilder? output, ref uint size);
}
