using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Blossom;
using TGK.Core.Services;

namespace TGK.Client.Platform;

/// <summary>
/// Whether this copy of TGK can install updates itself: only a published release build (the self-contained folder
/// from a release archive, started through its own executable) on a platform that has release archives. A
/// development build (<c>dotnet run</c>, <c>dotnet TGK.dll</c>) only offers the download page.
/// </summary>
public static class SelfUpdate
{
    public static string InstallDir { get; } = AppContext.BaseDirectory;

    public static string Executable { get; } = OperatingSystem.IsWindows() ? "TGK.exe" : "TGK";

    /// <summary>The release archive this build installs from, or null when it can't update itself.</summary>
    public static string? PackageName { get; } = IsReleaseInstall() ? UpdateChecker.PackageName(RuntimeInformation.RuntimeIdentifier) : null;

    public static UpdateInstaller CreateInstaller() => new(InstallDir, Executable);

    private static bool IsReleaseInstall()
    {
        string? process = Environment.ProcessPath;
        return process is not null
            && string.Equals(Path.GetFileName(process), Executable, StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(InstallDir, "System.Private.CoreLib.dll")); // self-contained
    }

    /// <summary>Starts the (just updated) executable as a new process; false when it could not be started.</summary>
    public static bool Relaunch()
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(Path.Combine(InstallDir, Executable))
            {
                UseShellExecute = false,
                WorkingDirectory = InstallDir,
            });
            return process is not null;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not restart TGK after the update: {ex.Message}");
            return false;
        }
    }
}
