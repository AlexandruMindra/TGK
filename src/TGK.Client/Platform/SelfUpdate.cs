using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Blossom;
using TGK.Core.Services;

namespace TGK.Client.Platform;

/// <summary>
/// Whether and how this copy of TGK can install updates itself. Only published release builds can: the AppImage
/// (replaced as a whole), the macOS <c>TGK.app</c> bundle, or the self-contained folder from a release archive or the
/// Windows installer, each started through its own executable, on a platform that has release packages. A development
/// build (<c>dotnet run</c>, <c>dotnet TGK.dll</c>) only offers the download page.
/// </summary>
public static class SelfUpdate
{
    private static readonly string BaseDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    private static readonly string Executable = OperatingSystem.IsWindows() ? "TGK.exe" : "TGK";

    // Set by the AppImage runtime to the .AppImage file (the app itself runs from a read-only mount).
    private static readonly string? AppImage = OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } path
        && File.Exists(path) ? Path.GetFullPath(path) : null;

    // The .app bundle when running from <bundle>/Contents/MacOS.
    private static readonly string? Bundle = OperatingSystem.IsMacOS() && Path.GetFileName(BaseDir) == "MacOS"
        && Path.GetDirectoryName(Path.GetDirectoryName(BaseDir)) is { } bundle && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? bundle : null;

    /// <summary>The release package this build installs from, or null when it can't update itself.</summary>
    public static string? PackageName { get; } = IsReleaseBuild() ? UpdateChecker.PackageName(RuntimeInformation.RuntimeIdentifier, AppImage is not null) : null;

    /// <summary>What an update replaces here.</summary>
    public static InstallLayout Layout { get; } = AppImage is { } image ? InstallLayout.SingleFile(image)
        : Bundle is { } app ? InstallLayout.Folder(app, "TGK.app", "Contents/MacOS/TGK")
        : InstallLayout.Folder(BaseDir, "TGK", Executable);

    public static UpdateInstaller CreateInstaller() => new(Layout);

    private static bool IsReleaseBuild()
    {
        string? process = Environment.ProcessPath;
        return process is not null
            && string.Equals(Path.GetFileName(process), Executable, StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(BaseDir, "System.Private.CoreLib.dll")); // self-contained
    }

    /// <summary>Starts the (just updated) app as a new process; false when it could not be started.</summary>
    public static bool Relaunch()
    {
        try
        {
            ProcessStartInfo start;
            if (AppImage is { } image)
                start = new ProcessStartInfo(image);
            else if (Bundle is { } bundle)
            {
                // Through LaunchServices, so it is a normal app launch (Dock icon, a new instance).
                start = new ProcessStartInfo("open");
                start.ArgumentList.Add("-n");
                start.ArgumentList.Add(bundle);
            }
            else
                start = new ProcessStartInfo(Path.Combine(BaseDir, Executable)) { WorkingDirectory = BaseDir };
            start.UseShellExecute = false;
            using Process? process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not restart TGK after the update: {ex.Message}");
            return false;
        }
    }
}
