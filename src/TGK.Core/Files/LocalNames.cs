using System;
using System.Linq;
using System.Text;

namespace TGK.Core.Files;

/// <summary>Remote file names as local ones: Windows can't store some characters and names that Unix allows.</summary>
public static class LocalNames
{
    private static readonly string[] ReservedWindowsNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>
    /// <paramref name="remoteName"/> as a file name on this computer. On Windows, <c>\ : * ? " &lt; &gt; |</c> and control
    /// characters become <c>_</c>, a trailing dot or space is dropped and reserved device names (CON, NUL…) get a
    /// <c>_</c> in front. Elsewhere only NUL is replaced.
    /// </summary>
    public static string ToLocal(string remoteName) => ToLocal(remoteName, OperatingSystem.IsWindows());

    internal static string ToLocal(string remoteName, bool windows)
    {
        if (!windows)
            return remoteName.Replace('\0', '_');
        var sb = new StringBuilder(remoteName.Length);
        foreach (char c in remoteName)
            sb.Append(c < 32 || "\\:*?\"<>|".Contains(c) ? '_' : c);
        string name = sb.ToString().TrimEnd('.', ' ');
        if (name.Length == 0)
            name = "_";
        string stem = name.Split('.')[0];
        return ReservedWindowsNames.Contains(stem, StringComparer.OrdinalIgnoreCase) ? "_" + name : name;
    }
}
