using System;
using System.Diagnostics;
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

    // Types Windows runs instead of opening: a file from a server is never started as a program.
    private static readonly string[] WindowsProgramTypes =
        [".exe", ".com", ".bat", ".cmd", ".msi", ".msp", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".scr",
         ".lnk", ".hta", ".cpl", ".pif", ".reg", ".jar", ".application", ".msc", ".appref-ms", ".url", ".scf", ".inf", ".gadget", ".mht", ".mhtml"];

    /// <summary>
    /// Opens a local file in the program the system uses for its type, or with <paramref name="asText"/> in the text
    /// editor. On Windows, programs and scripts (.exe, .bat, .ps1…) always open in the text editor, never run.
    /// False when nothing could be started.
    /// </summary>
    public static bool OpenFile(string path, bool asText = false)
    {
        if (!System.IO.File.Exists(path))
            return false;
        try
        {
            ProcessStartInfo start;
            if (OperatingSystem.IsWindows())
            {
                bool program = Array.Exists(WindowsProgramTypes, t => path.EndsWith(t, StringComparison.OrdinalIgnoreCase));
                if (asText || program)
                {
                    start = new ProcessStartInfo(WindowsTextEditor()) { UseShellExecute = false };
                    start.ArgumentList.Add(path);
                }
                else
                {
                    start = new ProcessStartInfo(path) { UseShellExecute = true };
                }
            }
            else
            {
                start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
                if (asText && OperatingSystem.IsMacOS())
                    start.ArgumentList.Add("-t"); // the default text editor
                start.ArgumentList.Add(path);
            }
            using Process? process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not open {path}: {ex.Message}");
            return false;
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
