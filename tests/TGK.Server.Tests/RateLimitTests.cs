using System.Net;
using System.Threading.Tasks;
using TGK.Protocol.Dtos;
using Xunit;

namespace TGK.Server.Tests;

public sealed class LowRateLimitFixture : ServerFixture
{
    protected override int RateLimit => 3;
}

public sealed class RateLimitTests(LowRateLimitFixture server) : IClassFixture<LowRateLimitFixture>
{
    [Fact]
    public async Task AuthEndpoints_ShareAPerIpRateLimit()
    {
        var client = server.Client();
        for (int i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostJsonAsync("/api/prelogin", new PreloginRequest("someone"))).StatusCode);

        await (await client.PostJsonAsync("/api/prelogin", new PreloginRequest("someone"))).AssertErrorAsync(HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        await (await client.PostJsonAsync("/api/login", new LoginRequest("someone", new byte[32], "d", "p")))
            .AssertErrorAsync(HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/info")).StatusCode); // not limited
    }
}
