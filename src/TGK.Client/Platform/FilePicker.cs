using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TGK.Client.Platform;

/// <summary>
/// Native "open file" dialog: GetOpenFileNameW on Windows, zenity or kdialog on Linux. Runs off the UI thread so
/// the app keeps rendering while the dialog is open.
/// </summary>
public static class FilePicker
{
    /// <summary>True when a native picker is available on this system.</summary>
    public static bool IsAvailable => OperatingSystem.IsWindows() || FindLinuxTool() is not null;

    /// <summary>
    /// Shows the picker starting in <paramref name="initialDirectory"/>. Completes with the chosen path, or null when
    /// cancelled. Throws <see cref="PlatformNotSupportedException"/> when no picker exists (see <see cref="IsAvailable"/>).
    /// </summary>
    public static Task<string?> PickFileAsync(string title, string? initialDirectory = null)
    {
        if (OperatingSystem.IsWindows())
            return RunOnStaThread(() => PickWindows(title, initialDirectory));
        string tool = FindLinuxTool() ?? throw new PlatformNotSupportedException("No file picker (zenity or kdialog) is installed.");
        return Task.Run(() => PickLinux(tool, title, initialDirectory));
    }

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

    private static string? PickLinux(string tool, string title, string? initialDirectory)
    {
        string start = initialDirectory is not null && Directory.Exists(initialDirectory)
            ? initialDirectory.TrimEnd('/') + "/"
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/";
        var psi = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (tool == "zenity")
        {
            psi.ArgumentList.Add("--file-selection");
            psi.ArgumentList.Add("--title=" + title);
            psi.ArgumentList.Add("--filename=" + start);
        }
        else
        {
            psi.ArgumentList.Add("--title");
            psi.ArgumentList.Add(title);
            psi.ArgumentList.Add("--getopenfilename");
            psi.ArgumentList.Add(start);
        }
        using Process process = Process.Start(psi) ?? throw new PlatformNotSupportedException($"Could not start {tool}.");
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        string path = output.Trim();
        return process.ExitCode == 0 && path.Length > 0 ? path : null;
    }

    private static Task<string?> RunOnStaThread(Func<string?> func)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
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

    private static string? PickWindows(string title, string? initialDirectory)
    {
        const int MaxPath = 32768;
        IntPtr buffer = Marshal.AllocHGlobal(MaxPath * sizeof(char));
        try
        {
            Marshal.WriteInt16(buffer, 0);
            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                lpstrFilter = "All files\0*.*\0Private keys (*.pem, *.key, id_*)\0*.pem;*.key;id_*\0\0",
                lpstrFile = buffer,
                nMaxFile = MaxPath,
                lpstrTitle = title,
                lpstrInitialDir = initialDirectory,
                Flags = OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir | OfnExplorer,
            };
            if (GetOpenFileNameW(ref ofn))
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

    private const int OfnPathMustExist = 0x800, OfnFileMustExist = 0x1000, OfnNoChangeDir = 0x8, OfnExplorer = 0x80000;

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

    [DllImport("comdlg32.dll")]
    private static extern int CommDlgExtendedError();
}
