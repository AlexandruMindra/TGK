using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace TGK.Core.Tests;

/// <summary>A unique temporary directory, deleted on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tgk-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Generates real keys with the system's ssh-keygen.</summary>
internal static class SshKeygen
{
    public static bool IsAvailable { get; } = Probe();

    /// <summary>Creates a key pair in <paramref name="directory"/> and returns the private key path.</summary>
    /// <param name="format">Optional ssh-keygen <c>-m</c> format, e.g. <c>PEM</c> for a legacy RSA key.</param>
    public static string Generate(string directory, string type, string passphrase, int? bits = null, string? format = null)
    {
        string path = System.IO.Path.Combine(directory, $"id_{type}_{Guid.NewGuid():N}");
        var args = new List<string> { "-q", "-t", type, "-N", passphrase, "-C", "tgk-test", "-f", path };
        if (bits is { } b)
            args.AddRange(["-b", b.ToString()]);
        if (format is not null)
            args.AddRange(["-m", format]);
        Run([.. args]);
        return path;
    }

    /// <summary>The fingerprint as printed by <c>ssh-keygen -l</c>, e.g. <c>SHA256:abc...</c>.</summary>
    public static string Fingerprint(string publicKeyPath)
    {
        // Output: "256 SHA256:xxxx tgk-test (ED25519)"
        string output = Run("-l", "-E", "sha256", "-f", publicKeyPath);
        return output.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
    }

    private static string Run(params string[] arguments)
    {
        var info = new ProcessStartInfo("ssh-keygen")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ssh-keygen failed: {error}");
        return output.Trim();
    }

    private static bool Probe()
    {
        try
        {
            using var dir = new TempDirectory();
            Generate(dir.Path, "ed25519", "");
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
