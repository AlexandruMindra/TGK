using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TGK.Core.Services;

/// <summary>An update could not be downloaded or installed; the message is for the user.</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Installs a release over a TGK installation (the folder the release archive unpacks to): downloads the archive for
/// this platform, checks its size and SHA-256 against what GitHub reports, unpacks it next to the installation and
/// checks that it holds the expected version (<see cref="DownloadAsync"/>); then swaps the files in
/// (<see cref="Apply"/>). The running files are renamed aside rather than overwritten — allowed while they are in use,
/// also on Windows — and removed by <see cref="CleanUp"/> on a later start. A failed swap puts everything back.
/// </summary>
public sealed class UpdateInstaller : IDisposable
{
    /// <summary>Folder inside the installation where an update is downloaded and unpacked.</summary>
    public const string StagingFolder = ".tgk-update";

    /// <summary>Suffix of the files an update replaced (removed by <see cref="CleanUp"/>).</summary>
    public const string OldSuffix = ".tgk-old";

    private const long MaxPackageBytes = 1L << 30;
    private readonly HttpClient _http;

    /// <param name="installDir">The installation folder (where <paramref name="executable"/> is).</param>
    /// <param name="executable">File name of the app's executable: <c>TGK</c>, or <c>TGK.exe</c> on Windows.</param>
    public UpdateInstaller(string installDir, string executable, HttpMessageHandler? handler = null)
    {
        InstallDir = Path.GetFullPath(installDir);
        Executable = executable;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(10);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TGK-updater");
    }

    public string InstallDir { get; }

    public string Executable { get; }

    private string Staging => Path.Combine(InstallDir, StagingFolder);

    /// <summary>Why this installation can't update itself (e.g. its folder is read-only), or null when it can.</summary>
    public string? CheckInstallable()
    {
        if (!File.Exists(Path.Combine(InstallDir, Executable)))
            return "TGK is not running from an installed release.";
        string probe = Path.Combine(InstallDir, $".tgk-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"TGK's folder ({InstallDir}) can't be written to.";
        }
    }

    /// <summary>
    /// Downloads and unpacks <paramref name="update"/>'s package next to the installation; returns the unpacked
    /// folder to pass to <see cref="Apply"/>. <paramref name="progress"/> gets 0..1 as the download goes.
    /// </summary>
    /// <exception cref="UpdateException">No package, a failed or damaged download, or an unexpected archive.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        UpdatePackage package = update.Package ?? throw new UpdateException("This release has no download for this platform.");
        if (package.Size > MaxPackageBytes)
            throw new UpdateException("The download is unexpectedly large.");
        DeleteStaging();
        Directory.CreateDirectory(Staging);
        string archive = Path.Combine(Staging, package.Name);
        try
        {
            await DownloadFileAsync(package, archive, progress, ct).ConfigureAwait(false);
            string unpacked = Path.Combine(Staging, "unpacked");
            await Task.Run(() => Unpack(archive, unpacked, ct), ct).ConfigureAwait(false);
            File.Delete(archive);
            string root = Path.Combine(unpacked, "TGK");
            if (!File.Exists(Path.Combine(root, Executable)))
                throw new UpdateException("The download does not contain TGK.");
            CheckVersion(root, update.Version);
            if (!OperatingSystem.IsWindows())
            {
                string exe = Path.Combine(root, Executable);
                File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            return root;
        }
        catch (Exception ex) when (ex is not UpdateException and not OperationCanceledException)
        {
            DeleteStaging();
            throw new UpdateException($"The update could not be downloaded: {ex.Message}", ex);
        }
        catch
        {
            DeleteStaging();
            throw;
        }
    }

    private async Task DownloadFileAsync(UpdatePackage package, string path, IProgress<double>? progress, CancellationToken ct)
    {
        using HttpResponseMessage response = await _http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new UpdateException($"The download failed: GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        long total = package.Size > 0 ? package.Size : response.Content.Headers.ContentLength ?? 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            byte[] buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                received += read;
                if (received > MaxPackageBytes || (package.Size > 0 && received > package.Size))
                    throw new UpdateException("The download is larger than the release says.");
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                if (total > 0)
                    progress?.Report(Math.Min(1, (double)received / total));
            }
            if (package.Size > 0 && received != package.Size)
                throw new UpdateException("The download was cut short.");
        }
        if (package.Sha256 is { } expected && Convert.ToHexStringLower(hash.GetHashAndReset()) != expected)
            throw new UpdateException("The download is damaged (its checksum does not match the release).");
    }

    // Both extractors refuse entries (and links) that would land outside `destination`.
    private static void Unpack(string archive, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, destination);
            return;
        }
        if (!archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("Unknown archive format.");
        ct.ThrowIfCancellationRequested();
        using FileStream file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: false);
    }

    // The unpacked app must be the version the release announced (TGK.dll's informational version, "+sha" aside).
    private static void CheckVersion(string root, AppVersion expected)
    {
        string dll = Path.Combine(root, "TGK.dll");
        string? product = File.Exists(dll) ? FileVersionInfo.GetVersionInfo(dll).ProductVersion : null;
        if (!AppVersion.TryParse(product, out AppVersion actual) || actual.CompareTo(expected) != 0)
            throw new UpdateException($"The download holds version {product ?? "(unknown)"}, not {expected}.");
    }

    /// <summary>
    /// Moves the files of <paramref name="staged"/> (from <see cref="DownloadAsync"/>) into the installation. Each
    /// file it replaces is renamed to <c>name</c> + <see cref="OldSuffix"/> first; if anything fails, the new files are
    /// removed and the old ones renamed back, and <see cref="UpdateException"/> is thrown.
    /// </summary>
    public void Apply(string staged)
    {
        string source = Path.GetFullPath(staged);
        if (!source.StartsWith(Staging + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !Directory.Exists(source))
            throw new UpdateException("Nothing to install.");
        var done = new List<(string Target, string? Old)>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, file);
                string target = Path.Combine(InstallDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string? old = null;
                if (File.Exists(target))
                {
                    old = target + OldSuffix;
                    if (File.Exists(old))
                        File.Delete(old);
                    File.Move(target, old);
                }
                done.Add((target, old));
                File.Move(file, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            for (int i = done.Count - 1; i >= 0; i--)
            {
                (string target, string? old) = done[i];
                try
                {
                    if (old is not null && File.Exists(old))
                        File.Move(old, target, overwrite: true);
                    else if (old is null && File.Exists(target))
                        File.Delete(target);
                }
                catch (Exception undo) when (undo is IOException or UnauthorizedAccessException)
                {
                    CoreLog.Warn($"Could not restore {target} after a failed update: {undo.Message}");
                }
            }
            throw new UpdateException($"The update could not be installed: {ex.Message}", ex);
        }
        DeleteStaging();
    }

    /// <summary>Removes what an earlier update left behind: the replaced files and the staging folder. Best effort.</summary>
    public void CleanUp()
    {
        DeleteStaging();
        try
        {
            foreach (string old in Directory.EnumerateFiles(InstallDir, "*" + OldSuffix, SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(old);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still in use (the previous process is exiting): the next start retries.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CoreLog.Warn($"Could not clean up after an update: {ex.Message}");
        }
    }

    /// <summary>Discards a downloaded update that was not installed.</summary>
    public void DeleteStaging()
    {
        try
        {
            if (Directory.Exists(Staging))
                Directory.Delete(Staging, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CoreLog.Warn($"Could not remove {Staging}: {ex.Message}");
        }
    }

    public void Dispose() => _http.Dispose();
}
