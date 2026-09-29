using System;
using System.Text.Json;
using TGK.Protocol;
using TGK.Protocol.Dtos;
using Xunit;

namespace TGK.Protocol.Tests;

public class ProtocolJsonTests
{
    [Fact]
    public void Dtos_UseCamelCaseAndBase64()
    {
        var request = new LoginRequest("bob", [1, 2, 3], "laptop", "linux", TotpCode: "123456");
        string json = JsonSerializer.Serialize(request, ProtocolJson.Options);

        Assert.Equal("""{"username":"bob","authKey":"AQID","deviceName":"laptop","platform":"linux","totpCode":"123456","newTotpSecret":null}""", json);
        LoginRequest copy = JsonSerializer.Deserialize<LoginRequest>(json, ProtocolJson.Options)!;
        Assert.Equal([1, 2, 3], copy.AuthKey);
        Assert.Equal("123456", copy.TotpCode);
    }

    [Fact]
    public void OptionalMembersMayBeOmitted()
    {
        LoginRequest request = JsonSerializer.Deserialize<LoginRequest>(
            """{"username":"bob","authKey":"AQID","deviceName":"d","platform":"p"}""", ProtocolJson.Options)!;
        Assert.Null(request.TotpCode);
        Assert.Null(request.NewTotpSecret);
    }

    [Theory]
    [InlineData("""{"authKey":"AQID","deviceName":"d","platform":"p"}""")]                  // missing required
    [InlineData("""{"username":null,"authKey":"AQID","deviceName":"d","platform":"p"}""")]  // null non-nullable
    public void RejectsMissingOrNullRequiredMembers(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LoginRequest>(json, ProtocolJson.Options));

    [Fact]
    public void VaultChange_KeepsExplicitNullData()
    {
        var push = new VaultPushRequest([new VaultChange("a", null), new VaultChange("b", [0xFF])]);
        string json = JsonSerializer.Serialize(push, ProtocolJson.Options);

        Assert.Equal("""{"changes":[{"id":"a","data":null},{"id":"b","data":"/w=="}]}""", json);
        Assert.Null(JsonSerializer.Deserialize<VaultPushRequest>(json, ProtocolJson.Options)!.Changes[0].Data);
    }

    [Fact]
    public void Apply_ConfiguresExistingOptions()
    {
        var options = new JsonSerializerOptions();
        ProtocolJson.Apply(options);
        Assert.Equal("""{"error":"not_found","message":"m"}""", JsonSerializer.Serialize(new ErrorResponse(ErrorCodes.NotFound, "m"), options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ErrorResponse>("""{"error":"x"}""", options));
    }

    [Fact]
    public void SessionInfo_RoundTrips()
    {
        var info = new SessionInfo("s1", "laptop", "linux", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1), null, true);
        Assert.Equal(info, JsonSerializer.Deserialize<SessionInfo>(JsonSerializer.Serialize(info, ProtocolJson.Options), ProtocolJson.Options));
    }
}
