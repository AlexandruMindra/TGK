using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace TGK.Core.Pty;

/// <summary>A shell a local terminal can run.</summary>
/// <param name="Id">What the preference stores: a Unix shell's path, or a Windows shell's name (<c>pwsh</c>, <c>cmd</c>…).</param>
/// <param name="Name">For menus, e.g. <c>zsh</c> or <c>PowerShell</c>.</param>
/// <param name="LoginShell">Started as a login shell (macOS terminals do, so the profile files run).</param>
public sealed record LocalShell(string Id, string Name, string Path, IReadOnlyList<string> Args, bool LoginShell = false)
{
    /// <summary>
    /// How to start it in a terminal of <paramref name="cols"/>×<paramref name="rows"/>, in the home folder, with the
    /// variables terminal programs look for (TERM, COLORTERM, TERM_PROGRAM).
    /// </summary>
    public PtyStartInfo StartInfo(int cols, int rows, string programVersion)
    {
        var env = new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "TGK",
            ["TERM_PROGRAM_VERSION"] = programVersion,
            // From the terminal TGK itself may have been started in: the shell would size itself by them.
            ["COLUMNS"] = null,
            ["LINES"] = null,
        };
        if (!OperatingSystem.IsWindows())
        {
            env["TERM"] = "xterm-256color";
            env["COLORTERM"] = "truecolor";
            // A desktop session may start TGK without a locale; shells and programs then assume ASCII.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LANG")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LC_ALL"))
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LC_CTYPE")))
                env["LANG"] = OperatingSystem.IsMacOS() ? "en_US.UTF-8" : "C.UTF-8";
        }
        string? argZero = LoginShell ? "-" + System.IO.Path.GetFileName(Path) : null;
        return new PtyStartInfo(Path, Args, cols, rows, LocalShells.HomeFolder, env, argZero);
    }
}

/// <summary>The shells of this computer that a local terminal can run, and the default one.</summary>
public static class LocalShells
{
    /// <summary>Where local terminals start.</summary>
    public static string HomeFolder => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home ? home : Environment.CurrentDirectory;

    /// <summary>The shells found on this computer, the default first.</summary>
    public static IReadOnlyList<LocalShell> Available() => OperatingSystem.IsWindows() ? WindowsShells() : UnixShells();

    /// <summary>The shell with <paramref name="id"/> (the preference), else the default one; null when none is found.</summary>
    public static LocalShell? Find(string? id)
    {
        IReadOnlyList<LocalShell> shells = Available();
        return shells.FirstOrDefault(s => id is not null && string.Equals(s.Id, id, StringComparison.Ordinal)) ?? shells.FirstOrDefault();
    }

    // ---- Unix ----

    // Listed in /etc/shells, but not shells to work in: they refuse logins, restrict, or are terminal multiplexers.
    private static readonly HashSet<string> NotShells = new(StringComparer.Ordinal)
    {
        "nologin", "false", "true", "rbash", "rksh", "rzsh", "git-shell", "screen", "tmux",
    };

    // The user's shell ($SHELL) first, then the others in /etc/shells, each once (/bin/bash and /usr/bin/bash are often
    // the same file).
    private static List<LocalShell> UnixShells()
    {
        var shells = new List<LocalShell>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool login = OperatingSystem.IsMacOS();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || !File.Exists(path) || !seen.Add(RealPath(path)))
                return;
            shells.Add(new LocalShell(path, System.IO.Path.GetFileName(path), path, [], login));
        }
        Add(Environment.GetEnvironmentVariable("SHELL"));
        try
        {
            foreach (string line in File.ReadAllLines("/etc/shells"))
            {
                string path = line.Trim();
                if (path.StartsWith('#') || NotShells.Contains(System.IO.Path.GetFileName(path)))
                    continue;
                Add(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        Add("/bin/bash");
        Add("/bin/zsh");
        Add("/bin/sh");
        return shells;
    }

    private static string RealPath(string path)
    {
        IntPtr resolved = realpath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
            return path;
        try
        {
            return Marshal.PtrToStringUTF8(resolved) ?? path;
        }
        finally
        {
            free(resolved);
        }
    }

    [DllImport("libc")]
    private static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);

    [DllImport("libc")]
    private static extern void free(IntPtr p);

    // ---- Windows ----

    // PowerShell 7 (pwsh) when installed, else Windows PowerShell; then Command Prompt, and WSL and Git Bash when present.
    private static List<LocalShell> WindowsShells()
    {
        var shells = new List<LocalShell>();
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if ((FindOnPath("pwsh.exe") ?? Existing(System.IO.Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"))) is { } pwsh)
            shells.Add(new LocalShell("pwsh", "PowerShell 7", pwsh, ["-NoLogo"]));
        if (Existing(System.IO.Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe")) is { } powershell)
            shells.Add(new LocalShell("powershell", "Windows PowerShell", powershell, ["-NoLogo"]));
        if ((Environment.GetEnvironmentVariable("ComSpec") is { } comspec && File.Exists(comspec) ? comspec : Existing(System.IO.Path.Combine(system, "cmd.exe"))) is { } cmd)
            shells.Add(new LocalShell("cmd", "Command Prompt", cmd, []));
        if (Existing(System.IO.Path.Combine(system, "wsl.exe")) is { } wsl)
            shells.Add(new LocalShell("wsl", "WSL", wsl, []));
        if (Existing(System.IO.Path.Combine(programFiles, "Git", "bin", "bash.exe")) is { } gitBash)
            shells.Add(new LocalShell("git-bash", "Git Bash", gitBash, ["--login", "-i"]));
        return shells;
    }

    private static string? Existing(string path) => File.Exists(path) ? path : null;

    private static string? FindOnPath(string file)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = System.IO.Path.Combine(dir.Trim('"'), file);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // An invalid PATH entry.
            }
        }
        return null;
    }
}
