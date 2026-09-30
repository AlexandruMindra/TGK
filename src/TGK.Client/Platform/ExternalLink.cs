using System;
using System.Diagnostics;
using Blossom;

namespace TGK.Client.Platform;

/// <summary>Opens web pages in the user's browser.</summary>
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
}
