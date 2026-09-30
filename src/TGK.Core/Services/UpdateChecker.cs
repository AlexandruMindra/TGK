using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TGK.Core.Services;

/// <summary>
/// A client version as the builds carry it (docs/RELEASING.md): <c>X.Y.Z</c> for releases, <c>X.Y.Z-nightly.N</c> for
/// builds of <c>main</c>, <c>X.Y.Z-ci.N</c> for pull requests; build metadata (<c>+sha</c>) is ignored. Ordered like
/// semantic versions: a pre-release comes before its release.
/// </summary>
public readonly partial record struct AppVersion(int Major, int Minor, int Patch, string? PreRelease = null) : IComparable<AppVersion>
{
    public bool IsNightly => PreRelease?.StartsWith("nightly.", StringComparison.Ordinal) == true;

    /// <summary>Parses the first version found in <paramref name="text"/> (e.g. <c>v0.3.0</c>, <c>Nightly 0.3.0-nightly.57</c>).</summary>
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        Match m = text is null ? Match.Empty : VersionPattern().Match(text);
        if (!m.Success
            || !int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            || !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minor)
            || !int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int patch))
            return false;
        version = new AppVersion(major, minor, patch, m.Groups[4].Success ? m.Groups[4].Value : null);
        return true;
    }

    public int CompareTo(AppVersion other)
    {
        int c = Major.CompareTo(other.Major);
        if (c == 0)
            c = Minor.CompareTo(other.Minor);
        if (c == 0)
            c = Patch.CompareTo(other.Patch);
        if (c != 0)
            return c;
        if (PreRelease is null || other.PreRelease is null)
            return (PreRelease is null ? 1 : 0) - (other.PreRelease is null ? 1 : 0);
        // Dot-separated identifiers: numbers numerically (and before words), words by ordinal order, fewer first.
        string[] a = PreRelease.Split('.'), b = other.PreRelease.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool an = long.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out long ai);
            bool bn = long.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out long bi);
            c = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (c != 0)
                return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";

    [GeneratedRegex(@"(?<![\d.])(\d{1,9})\.(\d{1,9})\.(\d{1,9})(?:-([0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*))?")]
    private static partial Regex VersionPattern();
}

/// <summary>Which builds a client updates to (per device; see <see cref="Models.ClientPrefs.UpdateChannel"/>).</summary>
public enum UpdateChannel
{
    /// <summary>Releases only (tags <c>vX.Y.Z</c>).</summary>
    Stable,

    /// <summary>The rolling <c>nightly</c> build of <c>main</c> too, and releases when they are newer.</summary>
    Nightly,
}

/// <summary>
/// A newer version, its release page and, when the release has one for this platform, the archive to install it from.
/// </summary>
public sealed record UpdateInfo(AppVersion Version, string Url, UpdatePackage? Package = null);

/// <summary>A release archive: its download link, size and SHA-256 (hex, as GitHub reports it; null when it doesn't).</summary>
public sealed record UpdatePackage(string Name, string Url, long Size, string? Sha256);

/// <summary>
/// Asks GitHub for the newest TGK release: the latest stable release (tag <c>vX.Y.Z</c>) and, on the nightly channel
/// (by default: for nightly builds), the rolling <c>nightly</c> pre-release too. Only public release
/// metadata is read; nothing about the user is sent (just a User-Agent with the version, as GitHub requires).
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    public const string Repository = "AlexandruMindra/TGK";

    /// <summary>The releases page, for anything that has no page of its own.</summary>
    public const string ReleasesUrl = $"https://github.com/{Repository}/releases";

    private const string Api = $"https://api.github.com/repos/{Repository}/releases";
    private static readonly Regex StableTag = new(@"^v\d{1,9}\.\d{1,9}\.\d{1,9}$", RegexOptions.CultureInvariant);

    private readonly HttpClient _http;

    public UpdateChecker(HttpMessageHandler? handler = null, string? userAgent = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TGK", userAgent ?? "update-check"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>
    /// The release package a build installs from (what CI publishes; docs/RELEASING.md): the archive for its runtime
    /// identifier, or the AppImage when it runs as one. Null for platforms without builds.
    /// </summary>
    public static string? PackageName(string runtimeIdentifier, bool appImage = false) => (runtimeIdentifier, appImage) switch
    {
        ("linux-x64", true) => "TGK-x86_64.AppImage",
        ("linux-x64", false) => "TGK-linux-x64.tar.gz",
        ("win-x64", false) => "TGK-win-x64.zip",
        ("osx-arm64", false) => "TGK-osx-arm64.tar.gz",
        ("osx-x64", false) => "TGK-osx-x64.tar.gz",
        _ => null,
    };

    /// <summary>
    /// The newest release after <paramref name="current"/>, or null when it is the newest; with
    /// <paramref name="package"/> (see <see cref="PackageName"/>) it includes that archive when the release has it. Throws
    /// <see cref="HttpRequestException"/> (also for unexpected responses), <see cref="JsonException"/> or
    /// <see cref="OperationCanceledException"/> (timeout) when GitHub can't be asked. <paramref name="channel"/>: null
    /// follows the build (a nightly build is on the nightly channel). Never a downgrade: a nightly build on the stable
    /// channel stays until a release newer than it comes out.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(AppVersion current, string? package = null, CancellationToken ct = default, UpdateChannel? channel = null)
    {
        UpdateInfo? best = null;
        // Links are built here from the tag rather than taken from the response, so only this repository's pages and
        // files are ever opened or downloaded.
        if (await GetReleaseAsync($"{Api}/latest", package, ct).ConfigureAwait(false) is { } stable
            && stable.Tag is { } tag && StableTag.IsMatch(tag) && AppVersion.TryParse(tag, out AppVersion version) && version > current)
            best = new UpdateInfo(version, $"{ReleasesUrl}/tag/{tag}", Package(tag, package, stable.Asset));
        if ((channel ?? DefaultChannel(current)) == UpdateChannel.Nightly
            && await GetReleaseAsync($"{Api}/tags/nightly", package, ct).ConfigureAwait(false) is { } nightly
            && AppVersion.TryParse(nightly.Name, out AppVersion nightlyVersion) && nightlyVersion.IsNightly
            && nightlyVersion > current && (best is null || nightlyVersion > best.Version))
            best = new UpdateInfo(nightlyVersion, $"{ReleasesUrl}/tag/nightly", Package("nightly", package, nightly.Asset));
        return best;
    }

    /// <summary>The channel of a build that has no channel chosen: the kind of build it is.</summary>
    public static UpdateChannel DefaultChannel(AppVersion current) => current.IsNightly ? UpdateChannel.Nightly : UpdateChannel.Stable;

    private static UpdatePackage? Package(string tag, string? name, (long Size, string? Sha256)? asset) =>
        name is null || asset is not { } a ? null : new UpdatePackage(name, $"{ReleasesUrl}/download/{tag}/{name}", a.Size, a.Sha256);

    // Null when there is no such release (404: nothing released yet, or no nightly right now). Asset: the size and
    // SHA-256 of the archive named `package`, when the release has it.
    private async Task<(string? Tag, string? Name, (long Size, string? Sha256)? Asset)?> GetReleaseAsync(string url, string? package, CancellationToken ct)
    {
        using HttpResponseMessage response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Unexpected release format.");
        if (root.TryGetProperty("draft", out JsonElement draft) && draft.ValueKind == JsonValueKind.True)
            return null;
        (long, string?)? asset = null;
        if (package is not null && root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement a in assets.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object || Text(a, "name") != package)
                    continue;
                long size = a.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long n) ? n : 0;
                string? digest = Text(a, "digest") is { } d && d.StartsWith("sha256:", StringComparison.Ordinal) && d.Length == 71 ? d[7..].ToLowerInvariant() : null;
                if (Text(a, "state") is null or "uploaded")
                    asset = (size, digest);
            }
        }
        return (Text(root, "tag_name"), Text(root, "name"), asset);
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void Dispose() => _http.Dispose();
}
