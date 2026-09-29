using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using TGK.Protocol;
using TGK.Protocol.Dtos;
using Xunit;

namespace TGK.Server.Tests;

public sealed class VaultTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task PushAndPull_TrackRevisionsAndTombstones()
    {
        var client = server.Client((await server.RegisterAsync()).Token);

        var first = await PushAsync(client, new VaultChange("a", [1]), new VaultChange("b", [2]));
        Assert.Equal(2, first.Revision);
        Assert.Equal([new AppliedChange("a", 1), new AppliedChange("b", 2)], first.Applied);

        var all = await PullAsync(client, 0);
        Assert.Equal(2, all.Revision);
        Assert.Equal(["a", "b"], all.Items.Select(i => i.Id));

        var second = await PushAsync(client, new VaultChange("a", [3]), new VaultChange("b", null));
        Assert.Equal(4, second.Revision);

        var delta = await PullAsync(client, 2);
        Assert.Equal(4, delta.Revision);
        Assert.Collection(delta.Items,
            a => { Assert.Equal(("a", 3L, false), (a.Id, a.Revision, a.Deleted)); Assert.Equal([3], a.Data!); },
            b => { Assert.Equal(("b", 4L, true), (b.Id, b.Revision, b.Deleted)); Assert.Null(b.Data); });

        var none = await PullAsync(client, 4);
        Assert.Equal(4, none.Revision);
        Assert.Empty(none.Items);
    }

    [Fact]
    public async Task LastWriteWins_AcrossDevicesAndWithinARequest()
    {
        var user = await server.RegisterAsync();
        var laptop = server.Client(user.Token);
        var desktop = server.Client((await server.SignInAsync(user)).Token);

        await PushAsync(laptop, new VaultChange("x", [1]));
        await PushAsync(desktop, new VaultChange("x", [2]), new VaultChange("x", [3]));

        var item = Assert.Single((await PullAsync(laptop, 0)).Items);
        Assert.Equal([3], item.Data!);
        Assert.Equal(3, item.Revision);
    }

    [Fact]
    public async Task Users_CannotSeeEachOthersItems()
    {
        var alice = server.Client((await server.RegisterAsync()).Token);
        var bob = server.Client((await server.RegisterAsync()).Token);

        await PushAsync(alice, new VaultChange("shared-id", [1]));
        var bobView = await PullAsync(bob, 0);
        Assert.Equal(0, bobView.Revision);
        Assert.Empty(bobView.Items);

        await PushAsync(bob, new VaultChange("shared-id", [2]));
        Assert.Equal([1], Assert.Single((await PullAsync(alice, 0)).Items).Data!);
        Assert.Equal([2], Assert.Single((await PullAsync(bob, 0)).Items).Data!);
    }

    [Fact]
    public async Task LargePulls_ComeInPages()
    {
        var user = await server.RegisterAsync();
        await PushAsync(server.Client(user.Token), new VaultChange("a", new byte[100]), new VaultChange("b", null), new VaultChange("c", new byte[100]));

        var first = server.Store.Pull(user.UserId, 0, maxBytes: 250);
        Assert.Equal((true, 2L), (first.HasMore, first.Revision));
        Assert.Equal(["a", "b"], first.Items.Select(i => i.Id));
        var rest = server.Store.Pull(user.UserId, first.Revision, maxBytes: 250);
        Assert.Equal((false, 3L), (rest.HasMore, rest.Revision));
        Assert.Equal("c", Assert.Single(rest.Items).Id);
        Assert.False(server.Store.Pull(user.UserId, 0).HasMore);
    }

    [Fact]
    public async Task Quota_RefusesGrowth_ButAllowsShrinking()
    {
        var user = await server.RegisterAsync();
        Assert.NotNull(server.Store.Push(user.UserId, [new VaultChange("a", new byte[60]), new VaultChange("b", new byte[40])], 3, 100));

        Assert.Null(server.Store.Push(user.UserId, [new VaultChange("c", [1])], 3, 100)); // bytes
        Assert.Null(server.Store.Push(user.UserId, [new VaultChange("c", []), new VaultChange("d", [])], 3, 100)); // items
        Assert.Equal(2, server.Store.Pull(user.UserId, 0).Revision); // nothing applied
        Assert.NotNull(server.Store.Push(user.UserId, [new VaultChange("a", new byte[10]), new VaultChange("c", new byte[10])], 3, 100));
        Assert.NotNull(server.Store.Push(user.UserId, [new VaultChange("b", null)], 2, 10)); // over the new limits, but smaller
    }

    [Fact]
    public async Task Limits_AreEnforced()
    {
        var client = server.Client((await server.RegisterAsync()).Token);

        var tooMany = Enumerable.Range(0, ProtocolConstants.MaxChangesPerRequest + 1).Select(i => new VaultChange($"i{i}", [1])).ToArray();
        await (await client.PostJsonAsync("/api/vault", new VaultPushRequest(tooMany))).AssertErrorAsync(HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);

        var bigItem = new VaultPushRequest([new VaultChange("big", new byte[ProtocolConstants.MaxItemBytes + 1])]);
        await (await client.PostJsonAsync("/api/vault", bigItem)).AssertErrorAsync(HttpStatusCode.RequestEntityTooLarge, ErrorCodes.PayloadTooLarge);

        var hugeBody = new StringContent(new string(' ', ProtocolConstants.MaxRequestBytes + 1), Encoding.UTF8, "application/json");
        await (await client.PostAsync("/api/vault", hugeBody)).AssertErrorAsync(HttpStatusCode.RequestEntityTooLarge, ErrorCodes.PayloadTooLarge);

        foreach (string id in new[] { "", new string('x', ProtocolConstants.MaxItemIdLength + 1) })
        {
            await (await client.PostJsonAsync("/api/vault", new VaultPushRequest([new VaultChange(id, [1])])))
                .AssertErrorAsync(HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        }

        var missingData = new StringContent("""{"changes":[{"id":"a"}]}""", Encoding.UTF8, "application/json");
        await (await client.PostAsync("/api/vault", missingData)).AssertErrorAsync(HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        await (await client.GetAsync("/api/vault?since=-1")).AssertErrorAsync(HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        await (await client.GetAsync("/api/vault?since=abc")).AssertErrorAsync(HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);

        Assert.Equal(0, (await PullAsync(client, 0)).Revision); // nothing was applied
    }

    [Fact]
    public async Task Vault_RequiresSession()
    {
        await (await server.Client().GetAsync("/api/vault")).AssertErrorAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
        await (await server.Client().PostJsonAsync("/api/vault", new VaultPushRequest([]))).AssertErrorAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
    }

    private static async Task<VaultPushResponse> PushAsync(HttpClient client, params VaultChange[] changes)
    {
        var response = await client.PostJsonAsync("/api/vault", new VaultPushRequest(changes));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<VaultPushResponse>();
    }

    private static async Task<VaultPullResponse> PullAsync(HttpClient client, long since)
    {
        var response = await client.GetAsync($"/api/vault?since={since}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<VaultPullResponse>();
    }
}
