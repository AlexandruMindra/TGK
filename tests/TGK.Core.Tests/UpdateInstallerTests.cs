using System;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Services;
using Xunit;

namespace TGK.Core.Tests;

public sealed class UpdateInstallerTests : IDisposable
{
    private const string Url = "https://github.com/AlexandruMindra/TGK/releases/download/v9.9.9/";
    private readonly TempDirectory _dir = new();
    private readonly string _install;
    private readonly string _exe = OperatingSystem.IsWindows() ? "TGK.exe" : "TGK";

    // Any assembly with a version resource stands in for TGK.dll: the tests' own, built with the product version.
    private static readonly string VersionDll = typeof(UpdateInstallerTests).Assembly.Location;
    private static readonly AppVersion DllVersion = AppVersion.TryParse(FileVersionInfo.GetVersionInfo(VersionDll).ProductVersion, out AppVersion v) ? v : default;

    public UpdateInstallerTests()
    {
        _install = Path.Combine(_dir.Path, "TGK");
        Directory.CreateDirectory(Path.Combine(_install, "assets"));
        File.WriteAllText(Path.Combine(_install, _exe), "old exe");
        File.WriteAllText(Path.Combine(_install, "assets", "icon.svg"), "old icon");
        File.WriteAllText(Path.Combine(_install, "only-in-old.txt"), "stays");
    }

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Server(byte[] body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? Requested { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
        }
    }

    // A release archive: TGK/<exe>, TGK/TGK.dll (with `dll`'s version), TGK/assets/icon.svg and a new file.
    private byte[] Archive(bool zip, string? dll = null, string root = "TGK")
    {
        string src = Path.Combine(_dir.Path, "src-" + Guid.NewGuid().ToString("N"));
        string app = Path.Combine(src, root);
        Directory.CreateDirectory(Path.Combine(app, "assets"));
        File.WriteAllText(Path.Combine(app, _exe), "new exe");
        File.Copy(dll ?? VersionDll, Path.Combine(app, "TGK.dll"));
        File.WriteAllText(Path.Combine(app, "assets", "icon.svg"), "new icon");
        File.WriteAllText(Path.Combine(app, "new-file.txt"), "added");
        var output = new MemoryStream();
        if (zip)
        {
            ZipFile.CreateFromDirectory(src, output);
        }
        else
        {
            using var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true);
            TarFile.CreateFromDirectory(src, gzip, includeBaseDirectory: false);
        }
        return output.ToArray();
    }

    private static UpdateInfo Update(byte[] archive, bool zip, string? sha = null, AppVersion? version = null)
    {
        string name = zip ? "TGK-win-x64.zip" : "TGK-linux-x64.tar.gz";
        return new UpdateInfo(version ?? DllVersion, "https://github.com/AlexandruMindra/TGK/releases/tag/v9.9.9",
            new UpdatePackage(name, Url + name, archive.Length, sha ?? Convert.ToHexStringLower(SHA256.HashData(archive))));
    }

    private string Read(params string[] path) => File.ReadAllText(Path.Combine([_install, .. path]));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Downloads_checks_unpacks_and_swaps_in_the_new_version(bool zip)
    {
        byte[] archive = Archive(zip);
        var server = new Server(archive);
        using var installer = new UpdateInstaller(_install, _exe, server);
        Assert.Null(installer.CheckInstallable());
        double last = 0;

        string staged = await installer.DownloadAsync(Update(archive, zip), new SyncProgress(p => last = p), Ct);

        Assert.StartsWith(Url, server.Requested);
        Assert.Equal(1.0, last);
        Assert.Equal("old exe", Read(_exe)); // nothing changes before Apply
        installer.Apply(staged);
        Assert.Equal("new exe", Read(_exe));
        Assert.Equal("new icon", Read("assets", "icon.svg"));
        Assert.Equal("added", Read("new-file.txt"));
        Assert.Equal("stays", Read("only-in-old.txt"));
        Assert.Equal("old exe", Read(_exe + UpdateInstaller.OldSuffix)); // renamed aside while it may be running
        Assert.False(Directory.Exists(Path.Combine(_install, UpdateInstaller.StagingFolder)));
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(Path.Combine(_install, _exe)).HasFlag(UnixFileMode.UserExecute));

        installer.CleanUp();
        Assert.False(File.Exists(Path.Combine(_install, _exe + UpdateInstaller.OldSuffix)));
        Assert.False(File.Exists(Path.Combine(_install, "assets", "icon.svg" + UpdateInstaller.OldSuffix)));
    }

    [Fact]
    public async Task A_damaged_download_is_rejected_and_removed()
    {
        byte[] archive = Archive(zip: false);
        using var installer = new UpdateInstaller(_install, _exe, new Server(archive));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => installer.DownloadAsync(Update(archive, false, sha: new string('0', 64)), ct: Ct));

        Assert.Contains("checksum", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_install, UpdateInstaller.StagingFolder)));
        Assert.Equal("old exe", Read(_exe));
    }

    [Fact]
    public async Task A_short_download_or_an_http_error_is_rejected()
    {
        byte[] archive = Archive(zip: true);
        using var truncated = new UpdateInstaller(_install, _exe, new Server(archive[..(archive.Length / 2)]));
        await Assert.ThrowsAsync<UpdateException>(() => truncated.DownloadAsync(Update(archive, true), ct: Ct));

        using var missing = new UpdateInstaller(_install, _exe, new Server([], HttpStatusCode.NotFound));
        await Assert.ThrowsAsync<UpdateException>(() => missing.DownloadAsync(Update(archive, true), ct: Ct));
    }

    [Fact]
    public async Task An_archive_with_another_version_or_layout_is_rejected()
    {
        byte[] archive = Archive(zip: true);
        using var installer = new UpdateInstaller(_install, _exe, new Server(archive));
        var other = new AppVersion(DllVersion.Major + 1, 0, 0);
        var ex = await Assert.ThrowsAsync<UpdateException>(() => installer.DownloadAsync(Update(archive, true, version: other), ct: Ct));
        Assert.Contains("not " + other, ex.Message);

        byte[] wrongRoot = Archive(zip: true, root: "Something");
        using var installer2 = new UpdateInstaller(_install, _exe, new Server(wrongRoot));
        await Assert.ThrowsAsync<UpdateException>(() => installer2.DownloadAsync(Update(wrongRoot, true), ct: Ct));
    }

    [Fact]
    public async Task A_failed_swap_puts_the_old_files_back()
    {
        byte[] archive = Archive(zip: false);
        using var installer = new UpdateInstaller(_install, _exe, new Server(archive));
        string staged = await installer.DownloadAsync(Update(archive, false), ct: Ct);
        // A directory where the new file must go makes that move fail after others succeeded.
        Directory.CreateDirectory(Path.Combine(_install, "new-file.txt"));

        Assert.Throws<UpdateException>(() => installer.Apply(staged));

        Assert.Equal("old exe", Read(_exe));
        Assert.Equal("old icon", Read("assets", "icon.svg"));
        Assert.False(File.Exists(Path.Combine(_install, _exe + UpdateInstaller.OldSuffix)));
    }

    [Fact]
    public void Apply_only_takes_folders_it_staged()
    {
        using var installer = new UpdateInstaller(_install, _exe);
        Assert.Throws<UpdateException>(() => installer.Apply(_dir.Path));
    }

    [Fact]
    public void A_folder_without_the_app_is_not_installable()
    {
        using var installer = new UpdateInstaller(_dir.Path, _exe);
        Assert.NotNull(installer.CheckInstallable());
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
