using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TGK.Core.Services;
using Xunit;

namespace TGK.Core.Tests;

public sealed class UpdateCheckerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AppVersion V(string text) => AppVersion.TryParse(text, out AppVersion v) ? v : throw new ArgumentException(text);

    [Theory]
    [InlineData("0.2.0", 0, 2, 0, null)]
    [InlineData("v1.10.3", 1, 10, 3, null)]
    [InlineData("0.2.0+77ccc8495d2d", 0, 2, 0, null)]
    [InlineData("0.3.0-nightly.57+abc", 0, 3, 0, "nightly.57")]
    [InlineData("Nightly 0.3.0-nightly.57", 0, 3, 0, "nightly.57")]
    [InlineData("TGK 0.4.1", 0, 4, 1, null)]
    public void Parses_the_versions_builds_and_releases_carry(string text, int major, int minor, int patch, string? pre)
    {
        Assert.True(AppVersion.TryParse(text, out AppVersion v));
        Assert.Equal(new AppVersion(major, minor, patch, pre), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nightly")]
    [InlineData("1.2")]
    public void Rejects_text_without_a_version(string? text) => Assert.False(AppVersion.TryParse(text, out _));

    [Theory]
    [InlineData("0.2.0", "0.2.1")]
    [InlineData("0.2.9", "0.10.0")]
    [InlineData("0.3.0-nightly.5", "0.3.0")]
    [InlineData("0.3.0-nightly.9", "0.3.0-nightly.10")]
    [InlineData("0.3.0-ci.99", "0.3.0-nightly.1")]
    [InlineData("0.2.0", "0.3.0-nightly.1")]
    public void Orders_like_semantic_versions(string older, string newer)
    {
        Assert.True(V(older) < V(newer));
        Assert.True(V(newer) > V(older));
        Assert.Equal(0, V(newer).CompareTo(V(newer)));
    }

    /// <summary>GitHub's releases API: path → (status, JSON body); counts the requests.</summary>
    private sealed class FakeGitHub(Dictionary<string, (HttpStatusCode Status, object? Body)> routes) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public string? UserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            UserAgent = request.Headers.UserAgent.ToString();
            if (!routes.TryGetValue(path, out var route))
                route = (HttpStatusCode.NotFound, new { message = "Not Found" });
            var response = new HttpResponseMessage(route.Status);
            if (route.Body is not null)
                response.Content = new StringContent(route.Body as string ?? JsonSerializer.Serialize(route.Body), Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        }
    }

    private const string Latest = "/repos/AlexandruMindra/TGK/releases/latest";
    private const string Nightly = "/repos/AlexandruMindra/TGK/releases/tags/nightly";

    private static FakeGitHub GitHub(string? stableTag, string? nightlyName) => new(new()
    {
        [Latest] = stableTag is null ? (HttpStatusCode.NotFound, null) : (HttpStatusCode.OK, new { tag_name = stableTag, name = $"TGK {stableTag[1..]}", html_url = "https://evil.example/" }),
        [Nightly] = nightlyName is null ? (HttpStatusCode.NotFound, null) : (HttpStatusCode.OK, new { tag_name = "nightly", name = nightlyName, prerelease = true }),
    });

    [Fact]
    public async Task A_newer_stable_release_is_found_with_a_link_to_its_page()
    {
        using var checker = new UpdateChecker(GitHub("v0.3.0", "Nightly 0.4.0-nightly.80"), "0.2.0");

        UpdateInfo? update = await checker.CheckAsync(V("0.2.0"), Ct);

        Assert.Equal(V("0.3.0"), update!.Version);
        Assert.Equal("https://github.com/AlexandruMindra/TGK/releases/tag/v0.3.0", update.Url); // never the response's link
    }

    [Fact]
    public async Task Up_to_date_or_ahead_finds_nothing_and_release_builds_ignore_nightlies()
    {
        var github = GitHub("v0.2.0", "Nightly 0.3.0-nightly.80");
        using var checker = new UpdateChecker(github);

        Assert.Null(await checker.CheckAsync(V("0.2.0"), Ct));
        Assert.Null(await checker.CheckAsync(V("0.3.0"), Ct)); // a local build of the next version
        Assert.DoesNotContain(Nightly, github.Requests);
    }

    [Fact]
    public async Task Nightly_builds_hear_about_newer_nightlies_and_releases()
    {
        using var checker = new UpdateChecker(GitHub("v0.2.0", "Nightly 0.3.0-nightly.80"));

        Assert.Equal("https://github.com/AlexandruMindra/TGK/releases/tag/nightly", (await checker.CheckAsync(V("0.3.0-nightly.79"), Ct))!.Url);
        Assert.Null(await checker.CheckAsync(V("0.3.0-nightly.80"), Ct));
        // The release a nightly was building up to is newer than the nightly.
        using var released = new UpdateChecker(GitHub("v0.3.0", null));
        Assert.Equal(V("0.3.0"), (await released.CheckAsync(V("0.3.0-nightly.80"), Ct))!.Version);
    }

    [Fact]
    public async Task No_releases_yet_is_not_an_error()
    {
        using var checker = new UpdateChecker(GitHub(null, null));
        Assert.Null(await checker.CheckAsync(V("0.1.0-nightly.3"), Ct));
    }

    [Fact]
    public async Task Tags_that_are_not_versions_are_ignored()
    {
        using var checker = new UpdateChecker(GitHub("v9.9.9/../../evil", null));
        Assert.Null(await checker.CheckAsync(V("0.2.0"), Ct));
    }

    [Fact]
    public async Task Failures_throw_so_a_manual_check_can_say_so()
    {
        using var limited = new UpdateChecker(new FakeGitHub(new() { [Latest] = (HttpStatusCode.Forbidden, new { message = "rate limited" }) }));
        await Assert.ThrowsAsync<HttpRequestException>(() => limited.CheckAsync(V("0.2.0"), Ct));

        using var garbage = new UpdateChecker(new FakeGitHub(new() { [Latest] = (HttpStatusCode.OK, "[1, 2") }));
        await Assert.ThrowsAnyAsync<JsonException>(() => garbage.CheckAsync(V("0.2.0"), Ct));
    }

    [Fact]
    public async Task Sends_a_user_agent_with_the_version()
    {
        var github = GitHub("v0.2.0", null);
        using var checker = new UpdateChecker(github, "0.2.0");
        await checker.CheckAsync(V("0.2.0"), Ct);
        Assert.Equal("TGK/0.2.0", github.UserAgent);
    }
}
