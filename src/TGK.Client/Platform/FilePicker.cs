using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TGK.Client.Platform;

/// <summary>A file type offered by the picker, e.g. ("TGK backup", ["*.tgkbackup"]); "All files" is always added after it.</summary>
public sealed record FileFilter(string Name, params string[] Patterns);

/// <summary>
/// Native "open file", "save file" and "choose folder" dialogs: GetOpenFileNameW / GetSaveFileNameW on Windows, zenity or kdialog on
/// Linux, the system dialogs through AppleScript (osascript) on macOS. Run off the UI thread so the app keeps
/// rendering while the dialog is open.
/// </summary>
public static class FilePicker
{
    /// <summary>The Windows open dialog's filter when none is given (private keys).</summary>
    private const string DefaultWindowsFilter = "All files\0*.*\0Private keys (*.pem, *.key, id_*)\0*.pem;*.key;id_*\0\0";

    /// <summary>True when a native picker is available on this system.</summary>
    public static bool IsAvailable => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || FindLinuxTool() is not null;

    /// <summary>
    /// Shows the picker starting in <paramref name="initialDirectory"/>. Completes with the chosen path, or null when
    /// cancelled. Throws <see cref="PlatformNotSupportedException"/> when no picker exists (see <see cref="IsAvailable"/>).
    /// </summary>
    public static Task<string?> PickFileAsync(string title, string? initialDirectory = null, FileFilter? filter = null)
    {
        if (OperatingSystem.IsWindows())
            return RunOnStaThread(() => ShowWindows(title, initialDirectory, null, filter, save: false));
        if (OperatingSystem.IsMacOS())
            return Task.Run(() => ShowMac(title, StartDirectory(initialDirectory), null));
        string tool = FindLinuxTool() ?? throw new PlatformNotSupportedException("No file picker (zenity or kdialog) is installed.");
        return Task.Run(() => ShowLinux(tool, title, StartDirectory(initialDirectory), filter, save: false));
    }

    /// <summary>
    /// Shows the "save as" dialog in <paramref name="initialDirectory"/> with <paramref name="defaultName"/> filled in;
    /// it asks before overwriting an existing file. Completes with the chosen path, or null when cancelled. Throws
    /// <see cref="PlatformNotSupportedException"/> when no picker exists (see <see cref="IsAvailable"/>).
    /// </summary>
    public static Task<string?> SaveFileAsync(string title, string defaultName, string? initialDirectory = null, FileFilter? filter = null)
    {
        if (OperatingSystem.IsWindows())
            return RunOnStaThread(() => ShowWindows(title, initialDirectory, defaultName, filter, save: true));
        if (OperatingSystem.IsMacOS())
            return Task.Run(() => ShowMac(title, StartDirectory(initialDirectory), defaultName));
        string tool = FindLinuxTool() ?? throw new PlatformNotSupportedException("No file picker (zenity or kdialog) is installed.");
        return Task.Run(() => ShowLinux(tool, title, StartDirectory(initialDirectory) + defaultName, filter, save: true));
    }

    /// <summary>
    /// Like <see cref="PickFileAsync"/>, but several files can be chosen. Completes with the chosen paths, or an empty
    /// list when cancelled.
    /// </summary>
    public static Task<IReadOnlyList<string>> PickFilesAsync(string title, string? initialDirectory = null)
    {
        if (OperatingSystem.IsWindows())
            return RunOnStaThread(() => ShowWindowsMulti(title, initialDirectory));
        if (OperatingSystem.IsMacOS())
            return Task.Run(() => Lines(RunMac(title, StartDirectory(initialDirectory),
                "set picked to choose file with prompt (item 1 of argv) default location (POSIX file (item 2 of argv)) with multiple selections allowed",
                "set out to \"\"",
                "repeat with f in picked",
                "set out to out & POSIX path of f & linefeed",
                "end repeat",
                "out")));
        string tool = FindLinuxTool() ?? throw new PlatformNotSupportedException("No file picker (zenity or kdialog) is installed.");
        return Task.Run(() => Lines(RunLinux(tool, tool == "zenity"
            ? ["--file-selection", "--multiple", "--separator=\n", "--title=" + title, "--filename=" + StartDirectory(initialDirectory)]
            : ["--title", title, "--getopenfilename", StartDirectory(initialDirectory), "--multiple", "--separate-output"])));
    }

    /// <summary>Shows a folder picker. Completes with the chosen folder, or null when cancelled.</summary>
    public static Task<string?> PickFolderAsync(string title, string? initialDirectory = null)
    {
        if (OperatingSystem.IsWindows())
            return RunOnStaThread(() => ShowWindowsFolder(title));
        if (OperatingSystem.IsMacOS())
            return Task.Run(() => Single(RunMac(title, StartDirectory(initialDirectory),
                "POSIX path of (choose folder with prompt (item 1 of argv) default location (POSIX file (item 2 of argv)))")));
        string tool = FindLinuxTool() ?? throw new PlatformNotSupportedException("No file picker (zenity or kdialog) is installed.");
        return Task.Run(() => Single(RunLinux(tool, tool == "zenity"
            ? ["--file-selection", "--directory", "--title=" + title, "--filename=" + StartDirectory(initialDirectory)]
            : ["--title", title, "--getexistingdirectory", StartDirectory(initialDirectory)])));
    }

    private static IReadOnlyList<string> Lines(string? output) =>
        output is null ? [] : output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? Single(string? output) => output?.Trim() is { Length: > 0 } path ? path.TrimEnd('/') is { Length: > 0 } p ? p : "/" : null;

    // The tool's output when it exited normally (a cancelled dialog exits with an error).
    private static string? RunLinux(string tool, string[] arguments)
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments)
            psi.ArgumentList.Add(argument);
        using Process process = Process.Start(psi) ?? throw new PlatformNotSupportedException($"Could not start {tool}.");
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output : null;
    }

    // Runs an AppleScript whose argv is (title, start folder); lines are passed as separate -e arguments, never spliced.
    private static string? RunMac(string title, string start, params string[] body)
    {
        var psi = new ProcessStartInfo("osascript") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add("on run argv");
        foreach (string line in body)
        {
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(line);
        }
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add("end run");
        psi.ArgumentList.Add(title);
        psi.ArgumentList.Add(start);
        using Process process = Process.Start(psi) ?? throw new PlatformNotSupportedException("Could not start osascript.");
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output : null;
    }

    private static string StartDirectory(string? directory) =>
        directory is not null && Directory.Exists(directory)
            ? directory.TrimEnd('/') + "/"
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/";

    private static string? FindLinuxTool()
    {
        if (!OperatingSystem.IsLinux())
            return null;
        string[] dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (string tool in new[] { "zenity", "kdialog" })
        {
            foreach (string dir in dirs)
            {
                if (File.Exists(Path.Combine(dir, tool)))
                    return tool;
            }
        }
        return null;
    }

    private static string? ShowLinux(string tool, string title, string start, FileFilter? filter, bool save)
    {
        var psi = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (tool == "zenity")
        {
            psi.ArgumentList.Add("--file-selection");
            if (save)
            {
                psi.ArgumentList.Add("--save");
                psi.ArgumentList.Add("--confirm-overwrite");
            }
            psi.ArgumentList.Add("--title=" + title);
            psi.ArgumentList.Add("--filename=" + start);
            if (filter is not null)
            {
                psi.ArgumentList.Add($"--file-filter={filter.Name} | {string.Join(' ', filter.Patterns)}");
                psi.ArgumentList.Add("--file-filter=All files | *");
            }
        }
        else
        {
            psi.ArgumentList.Add("--title");
            psi.ArgumentList.Add(title);
            psi.ArgumentList.Add(save ? "--getsavefilename" : "--getopenfilename");
            psi.ArgumentList.Add(start);
            if (filter is not null)
                psi.ArgumentList.Add($"{string.Join(' ', filter.Patterns)}|{filter.Name}\n*|All files");
        }
        using Process process = Process.Start(psi) ?? throw new PlatformNotSupportedException($"Could not start {tool}.");
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        string path = output.Trim();
        return process.ExitCode == 0 && path.Length > 0 ? path : null;
    }

    // "choose file" / "choose file name" (with defaultName: a save dialog). Title, folder and name are passed as
    // arguments (argv), never spliced into the script. Cancel exits with an error: null.
    private static string? ShowMac(string title, string start, string? defaultName)
    {
        var psi = new ProcessStartInfo("osascript")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add("on run argv");
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(defaultName is null
            ? "POSIX path of (choose file with prompt (item 1 of argv) default location (POSIX file (item 2 of argv)))"
            : "POSIX path of (choose file name with prompt (item 1 of argv) default location (POSIX file (item 2 of argv)) default name (item 3 of argv))");
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add("end run");
        psi.ArgumentList.Add(title);
        psi.ArgumentList.Add(start);
        if (defaultName is not null)
            psi.ArgumentList.Add(defaultName);
        using Process process = Process.Start(psi) ?? throw new PlatformNotSupportedException("Could not start osascript.");
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        string path = output.Trim();
        return process.ExitCode == 0 && path.Length > 0 ? path : null;
    }

    private static Task<T> RunOnStaThread<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        { IsBackground = true, Name = "TGK file picker" };
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static string? ShowWindows(string title, string? initialDirectory, string? defaultName, FileFilter? filter, bool save)
    {
        const int MaxPath = 32768;
        IntPtr buffer = Marshal.AllocHGlobal(MaxPath * sizeof(char));
        try
        {
            Marshal.WriteInt16(buffer, 0);
            if (defaultName is not null)
            {
                char[] name = (defaultName + "\0").ToCharArray();
                Marshal.Copy(name, 0, buffer, Math.Min(name.Length, MaxPath));
            }
            string? extension = filter?.Patterns.Length > 0 && filter.Patterns[0].StartsWith("*.", StringComparison.Ordinal) ? filter.Patterns[0][2..] : null;
            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                lpstrFilter = filter is null ? DefaultWindowsFilter
                    : $"{filter.Name} ({string.Join(", ", filter.Patterns)})\0{string.Join(';', filter.Patterns)}\0All files\0*.*\0\0",
                lpstrFile = buffer,
                nMaxFile = MaxPath,
                lpstrTitle = title,
                lpstrInitialDir = initialDirectory,
                lpstrDefExt = save ? extension : null,
                Flags = OfnPathMustExist | OfnNoChangeDir | OfnExplorer | (save ? OfnOverwritePrompt : OfnFileMustExist),
            };
            if (save ? GetSaveFileNameW(ref ofn) : GetOpenFileNameW(ref ofn))
                return Marshal.PtrToStringUni(buffer);
            int error = CommDlgExtendedError();
            if (error != 0)
                throw new Win32Exception($"The file dialog failed (error 0x{error:X}).");
            return null; // cancelled
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // OFN_ALLOWMULTISELECT: one file comes back as its full path; several as the folder, then each name, NUL-separated.
    private static IReadOnlyList<string> ShowWindowsMulti(string title, string? initialDirectory)
    {
        const int MaxChars = 1 << 20;
        IntPtr buffer = Marshal.AllocHGlobal(MaxChars * sizeof(char));
        try
        {
            unsafe
            {
                NativeMemory.Clear((void*)buffer, MaxChars * sizeof(char)); // the result list ends with two NULs
            }
            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                lpstrFilter = "All files\0*.*\0\0",
                lpstrFile = buffer,
                nMaxFile = MaxChars,
                lpstrTitle = title,
                lpstrInitialDir = initialDirectory,
                Flags = OfnPathMustExist | OfnNoChangeDir | OfnExplorer | OfnFileMustExist | OfnAllowMultiSelect,
            };
            if (!GetOpenFileNameW(ref ofn))
            {
                int error = CommDlgExtendedError();
                if (error != 0)
                    throw new Win32Exception($"The file dialog failed (error 0x{error:X}).");
                return []; // cancelled
            }
            var parts = new List<string>();
            int offset = 0;
            while (true)
            {
                string part = Marshal.PtrToStringUni(buffer + offset * sizeof(char)) ?? "";
                if (part.Length == 0)
                    break;
                parts.Add(part);
                offset += part.Length + 1;
            }
            return parts.Count <= 1 ? parts : parts.Skip(1).Select(name => Path.Combine(parts[0], name)).ToList();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // The shell's folder browser (SHBrowseForFolder with the resizable new-style dialog; it starts at the desktop).
    // The thread is STA.
    private static string? ShowWindowsFolder(string title)
    {
        IntPtr displayName = Marshal.AllocHGlobal(260 * sizeof(char)); // MAX_PATH, as the API assumes
        IntPtr path = Marshal.AllocHGlobal(32768 * sizeof(char));
        IntPtr list = IntPtr.Zero;
        try
        {
            var info = new BrowseInfo { pszDisplayName = displayName, lpszTitle = title, ulFlags = BifReturnOnlyFsDirs | BifNewDialogStyle | BifEditBox };
            list = SHBrowseForFolderW(ref info);
            if (list == IntPtr.Zero)
                return null; // cancelled
            return SHGetPathFromIDListEx(list, path, 32768, 0) ? Marshal.PtrToStringUni(path) : null;
        }
        finally
        {
            if (list != IntPtr.Zero)
                Marshal.FreeCoTaskMem(list);
            Marshal.FreeHGlobal(path);
            Marshal.FreeHGlobal(displayName);
        }
    }

    private const int OfnAllowMultiSelect = 0x200;
    private const uint BifReturnOnlyFsDirs = 0x1, BifEditBox = 0x10, BifNewDialogStyle = 0x40;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BrowseInfo
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        public string? lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolderW(ref BrowseInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDListEx(IntPtr pidl, IntPtr path, int pathLength, int options);

    private const int OfnOverwritePrompt = 0x2, OfnPathMustExist = 0x800, OfnFileMustExist = 0x1000, OfnNoChangeDir = 0x8, OfnExplorer = 0x80000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string? lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSaveFileNameW(ref OpenFileName ofn);

    [DllImport("comdlg32.dll")]
    private static extern int CommDlgExtendedError();
}
