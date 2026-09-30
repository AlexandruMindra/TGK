using System.Reflection;
using TGK.Core.Services;

namespace TGK.Client;

/// <summary>This build's version (<c>X.Y.Z</c>, <c>X.Y.Z-nightly.N</c>, ...; see docs/RELEASING.md).</summary>
public static class AppInfo
{
    /// <summary>The informational version without its build metadata (the commit).</summary>
    public static string VersionText { get; } = ReadVersion();

    public static AppVersion Version { get; } = AppVersion.TryParse(VersionText, out AppVersion v) ? v : new AppVersion(0, 0, 0);

    private static string ReadVersion()
    {
        string? info = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info))
            return "0.0.0";
        int plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
}
