using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TGK.Protocol;
using TGK.Protocol.Dtos;
using Xunit;

namespace TGK.Server.Tests;

public sealed class TestClock : TimeProvider
{
    // Starts at the real time: CLI commands run against the same database with the system clock.
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

public sealed record TestUser(string Username, string UserId, byte[] AuthKey, byte[] Salt, byte[] WrappedVaultKey, string Secret, string Token, string SessionId);

/// <summary>An in-memory server on its own temporary database, with a controllable clock. One per test class.</summary>
public class ServerFixture : WebApplicationFactory<Program>
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tgk-server-tests", Guid.NewGuid().ToString("N"));

    public string DbPath => Path.Combine(_directory, "tgk.db");

    public TestClock Clock { get; } = new();

    public Store Store => Services.GetRequiredService<Store>();

    protected virtual int RateLimit => 100_000;

    public long PullPageBytes { get; init; } = Store.DefaultPullPageBytes;

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(new ServerOptions { DbPath = DbPath, RateLimitPerMinute = RateLimit, PullPageBytes = PullPageBytes });
            services.AddSingleton<TimeProvider>(Clock);
        });

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    public HttpClient Client(string? token = null)
    {
        var client = CreateClient();
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Moves the clock to the next TOTP step and returns its code, so every sign-in uses a fresh step.</summary>
    public string NextCode(string secret)
    {
        Clock.Advance(TimeSpan.FromSeconds(Totp.PeriodSeconds));
        return Totp.ComputeCode(secret, Totp.GetStep(Clock.GetUtcNow()));
    }

    /// <summary>A code that is valid for no step near the current time.</summary>
    public string WrongCode(string secret) => Totp.ComputeCode(secret, Totp.GetStep(Clock.GetUtcNow()) + 1000);

    public async Task<TestUser> RegisterAsync(string? username = null, string? inviteCode = null, string deviceName = "test-device")
    {
        var (response, user) = await TryRegisterAsync(username, inviteCode, deviceName);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.ReadAsync<RegisterResponse>();
        return user with { UserId = body.UserId, Token = body.Token, SessionId = body.SessionId };
    }

    /// <summary>Posts a registration with fresh key material; the user's ids and token are empty (read them from the response).</summary>
    public async Task<(HttpResponseMessage Response, TestUser User)> TryRegisterAsync(string? username = null, string? inviteCode = null, string deviceName = "test-device")
    {
        username ??= "user-" + Guid.NewGuid().ToString("N")[..12];
        byte[] authKey = RandomNumberGenerator.GetBytes(32), salt = RandomNumberGenerator.GetBytes(16), wrapped = RandomNumberGenerator.GetBytes(61);
        string secret = Totp.GenerateSecret();
        var response = await Client().PostJsonAsync("/api/register",
            new RegisterRequest(username, authKey, salt, KdfParams.Default, wrapped, secret, NextCode(secret), deviceName, "linux", inviteCode));
        return (response, new TestUser(username, "", authKey, salt, wrapped, secret, "", ""));
    }

    public Task<HttpResponseMessage> LoginAsync(TestUser user, string? code, byte[]? authKey = null, string? newTotpSecret = null) =>
        Client().PostJsonAsync("/api/login",
            new LoginRequest(user.Username, authKey ?? user.AuthKey, "test-device", "linux", code, newTotpSecret));

    /// <summary>Signs in with a fresh code and returns the new session token.</summary>
    public async Task<LoginResponse> SignInAsync(TestUser user)
    {
        var response = await LoginAsync(user, NextCode(user.Secret));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<LoginResponse>();
    }
}

public static class HttpTestExtensions
{
    public static Task<HttpResponseMessage> PostJsonAsync<T>(this HttpClient client, string url, T body) =>
        client.PostAsJsonAsync(url, body, ProtocolJson.Options);

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(ProtocolJson.Options))!;

    public static async Task AssertErrorAsync(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.ReadAsync<ErrorResponse>()).Error);
    }
}
