using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TGK.Core.Services;

/// <summary>An update could not be downloaded or installed; the message is for the user.</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>How a TGK installation is laid out, so an update knows what to replace.</summary>
public sealed record InstallLayout
{
    private InstallLayout(string root, string? archiveRoot, string executable)
    {
        Root = Path.GetFullPath(root);
        ArchiveRoot = archiveRoot;
        Executable = executable;
    }

    /// <summary>The installation folder (or, for <see cref="SingleFile"/>, the file).</summary>
    public string Root { get; }

    /// <summary>The top-level folder of the release archive that becomes <see cref="Root"/>; null for a single file.</summary>
    public string? ArchiveRoot { get; }

    /// <summary>The executable, relative to <see cref="Root"/> and to <see cref="ArchiveRoot"/> (empty for a single file).</summary>
    public string Executable { get; }

    public bool IsSingleFile => ArchiveRoot is null;

    /// <summary>
    /// A folder installed from an archive: <c>TGK/</c> (Linux, Windows: executable <c>TGK</c> / <c>TGK.exe</c>) or a
    /// macOS bundle <c>TGK.app/</c> (executable <c>Contents/MacOS/TGK</c>).
    /// </summary>
    public static InstallLayout Folder(string root, string archiveRoot, string executable) => new(root, archiveRoot, executable);

    /// <summary>A self-contained single file that is replaced as a whole (an AppImage).</summary>
    public static InstallLayout SingleFile(string path) => new(path, null, "");
}

/// <summary>
/// Installs a release over a TGK installation (<see cref="InstallLayout"/>): downloads the package for this platform,
/// checks its size and SHA-256 against what GitHub reports, unpacks an archive next to the installation and checks
/// that it holds the expected version (<see cref="DownloadAsync"/>); then swaps it in (<see cref="Apply"/>). The
/// running files are renamed aside rather than overwritten — allowed while they are in use, also on Windows — and
/// removed by <see cref="CleanUp"/> on a later start. A failed swap puts everything back.
/// </summary>
public sealed class UpdateInstaller : IDisposable
{
    /// <summary>Folder inside the installation where an update is downloaded and unpacked.</summary>
    public const string StagingFolder = ".tgk-update";

    /// <summary>Suffix of the files an update replaced (removed by <see cref="CleanUp"/>).</summary>
    public const string OldSuffix = ".tgk-old";

    private const long MaxPackageBytes = 1L << 30;
    private readonly HttpClient _http;

    /// <summary>A folder installed from a <c>TGK/</c> archive, with <paramref name="executable"/> (<c>TGK</c>, <c>TGK.exe</c>) in it.</summary>
    public UpdateInstaller(string installDir, string executable, HttpMessageHandler? handler = null)
        : this(InstallLayout.Folder(installDir, "TGK", executable), handler)
    {
    }

    public UpdateInstaller(InstallLayout layout, HttpMessageHandler? handler = null)
    {
        Layout = layout;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(10);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TGK-updater");
    }

    public InstallLayout Layout { get; }

    // The folder that holds the installation's files, and the staging folder: inside it, or next to a single file.
    private string InstallDir => Layout.IsSingleFile ? Path.GetDirectoryName(Layout.Root)! : Layout.Root;

    private string Staging => Path.Combine(InstallDir, StagingFolder);

    /// <summary>Why this installation can't update itself (e.g. its folder is read-only), or null when it can.</summary>
    public string? CheckInstallable()
    {
        if (!File.Exists(Layout.IsSingleFile ? Layout.Root : Path.Combine(Layout.Root, Layout.Executable)))
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
    /// Downloads (and for an archive unpacks) <paramref name="update"/>'s package next to the installation; returns
    /// what to pass to <see cref="Apply"/>. <paramref name="progress"/> gets 0..1 as the download goes.
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
            if (Layout.IsSingleFile)
            {
                MakeExecutable(archive); // the checksum is all there is to check: its contents are not readable here
                return archive;
            }
            string unpacked = Path.Combine(Staging, "unpacked");
            await Task.Run(() => Unpack(archive, unpacked, ct), ct).ConfigureAwait(false);
            File.Delete(archive);
            string root = Path.Combine(unpacked, Layout.ArchiveRoot!);
            string exe = Path.Combine(root, Layout.Executable);
            if (!File.Exists(exe))
                throw new UpdateException("The download does not contain TGK.");
            CheckVersion(Path.GetDirectoryName(exe)!, update.Version);
            MakeExecutable(exe);
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

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
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
    /// Moves <paramref name="staged"/> (from <see cref="DownloadAsync"/>) into the installation: each file it replaces
    /// is renamed to <c>name</c> + <see cref="OldSuffix"/> first; if anything fails, the new files are removed and the
    /// old ones renamed back, and <see cref="UpdateException"/> is thrown.
    /// </summary>
    public void Apply(string staged)
    {
        string source = Path.GetFullPath(staged);
        bool exists = Layout.IsSingleFile ? File.Exists(source) : Directory.Exists(source);
        if (!source.StartsWith(Staging + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !exists)
            throw new UpdateException("Nothing to install.");
        var done = new List<(string Target, string? Old)>();
        try
        {
            IEnumerable<(string File, string Target)> moves = Layout.IsSingleFile
                ? [(source, Layout.Root)]
                : Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                    .Select(file => (file, Path.Combine(Layout.Root, Path.GetRelativePath(source, file))))
                    .ToList(); // listed up front: the loop moves files out of the folder being listed
            foreach ((string file, string target) in moves)
            {
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
            IEnumerable<string> olds = Layout.IsSingleFile
                ? [Layout.Root + OldSuffix]
                : Directory.EnumerateFiles(Layout.Root, "*" + OldSuffix, SearchOption.AllDirectories);
            foreach (string old in olds)
            {
                try
                {
                    if (File.Exists(old))
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
