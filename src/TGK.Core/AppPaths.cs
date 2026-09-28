using System;
using System.IO;

namespace TGK.Core;

/// <summary>Per-user locations for TGK's local files.</summary>
public static class AppPaths
{
    /// <summary>
    /// Linux/macOS: <c>$XDG_CONFIG_HOME/tgk</c> (default <c>~/.config/tgk</c>); Windows: <c>%APPDATA%\TGK</c>.
    /// The directory is not created here; writers create it on demand.
    /// </summary>
    public static string ConfigDirectory { get; } = ResolveConfigDirectory();

    private static string ResolveConfigDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TGK");

        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string root = !string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(root, "tgk");
    }
}
