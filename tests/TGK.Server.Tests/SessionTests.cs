using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using TGK.Protocol.Dtos;
using Xunit;

namespace TGK.Server.Tests;

public sealed class SessionTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task List_MarksCurrentSession_AndRevokeOneSignsItOut()
    {
        var user = await server.RegisterAsync();
        var second = await server.SignInAsync(user);
        var third = await server.SignInAsync(user);
        var client = server.Client(user.Token);

        var sessions = await (await client.GetAsync("/api/sessions")).ReadAsync<SessionInfo[]>();
        Assert.Equal(3, sessions.Length);
        Assert.Equal(user.SessionId, Assert.Single(sessions, s => s.Current).Id);
        Assert.All(sessions, s => Assert.Equal("test-device", s.DeviceName));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/sessions/{second.SessionId}")).StatusCode);
        await (await server.Client(second.Token).GetAsync("/api/sessions")).AssertErrorAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
        Assert.Equal(HttpStatusCode.OK, (await server.Client(third.Token).GetAsync("/api/sessions")).StatusCode);
        var remaining = await (await client.GetAsync("/api/sessions")).ReadAsync<SessionInfo[]>();
        Assert.Equal(new[] { user.SessionId, third.SessionId }.Order(), remaining.Select(s => s.Id).Order());
    }

    [Fact]
    public async Task Revoke_OtherUsersSession_IsNotFound()
    {
        var alice = await server.RegisterAsync();
        var bob = await server.RegisterAsync();
        var response = await server.Client(alice.Token).DeleteAsync($"/api/sessions/{bob.SessionId}");
        await response.AssertErrorAsync(HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Equal(HttpStatusCode.OK, (await server.Client(bob.Token).GetAsync("/api/sessions")).StatusCode);
    }

    [Fact]
    public async Task RevokeOthers_KeepsOnlyTheCurrentSession()
    {
        var user = await server.RegisterAsync();
        var others = new[] { await server.SignInAsync(user), await server.SignInAsync(user) };
        var client = server.Client(user.Token);

        var result = await (await client.PostAsync("/api/sessions/revoke-others", null)).ReadAsync<RevokeOthersResponse>();
        Assert.Equal(2, result.Revoked);
        foreach (var other in others)
            Assert.Equal(HttpStatusCode.Unauthorized, (await server.Client(other.Token).GetAsync("/api/sessions")).StatusCode);
        Assert.Single(await (await client.GetAsync("/api/sessions")).ReadAsync<SessionInfo[]>());
    }

    [Fact]
    public async Task Sessions_SlideWithUse_AndExpireWhenIdle()
    {
        var active = await server.RegisterAsync();
        var idle = await server.RegisterAsync();
        for (int i = 0; i < 2; i++)
        {
            server.Clock.Advance(TimeSpan.FromDays(20));
            Assert.Equal(HttpStatusCode.OK, (await server.Client(active.Token).GetAsync("/api/sessions")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Client(idle.Token).GetAsync("/api/sessions")).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer not-a-real-token")]
    [InlineData("Basic dXNlcjpwYXNz")]
    public async Task MissingOrBogusToken_IsUnauthorized(string? header)
    {
        var client = server.Client();
        if (header is not null)
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", header);
        await (await client.GetAsync("/api/sessions")).AssertErrorAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
    }
}
