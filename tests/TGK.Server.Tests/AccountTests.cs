using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TGK.Protocol;
using TGK.Protocol.Dtos;
using Xunit;

namespace TGK.Server.Tests;

public sealed class AccountTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Info_DescribesTheServer()
    {
        var info = await (await server.Client().GetAsync("/api/info")).ReadAsync<InfoResponse>();
        Assert.Equal("tgk-server", info.Name);
        Assert.Equal(1, info.Protocol);
        Assert.Equal("1.0.0", info.Version);
    }

    [Fact]
    public async Task Register_CreatesAccountAndSession()
    {
        var user = await server.RegisterAsync();
        Assert.Equal(HttpStatusCode.OK, (await server.Client(user.Token).GetAsync("/api/sessions")).StatusCode);
        var prelogin = await (await server.Client().PostJsonAsync("/api/prelogin", new PreloginRequest(user.Username))).ReadAsync<PreloginResponse>();
        Assert.Equal(user.Salt, prelogin.Salt);
    }

    [Fact]
    public async Task DeviceNames_AreStrippedOfControlCharacters()
    {
        var user = await server.RegisterAsync(deviceName: "\u001b[2J\u001b[31mFAKE\nroot\u202e signed in\u001b]52;c;eA==\u0007\u009b");
        var sessions = await (await server.Client(user.Token).GetAsync("/api/sessions")).ReadAsync<SessionInfo[]>();
        Assert.Equal("[2J[31mFAKEroot signed in]52;c;eA==", Assert.Single(sessions).DeviceName);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("has space")]
    [InlineData("slash/name")]
    [InlineData("a23456789012345678901234567890123")]
    public async Task Register_RejectsInvalidUsernames(string username)
    {
        var response = await server.Client().PostJsonAsync("/api/register", NewRegistration(username));
        await response.AssertErrorAsync(HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Register_RejectsTakenUsernameCaseInsensitively()
    {
        var user = await server.RegisterAsync("Taken.Name");
        var response = await server.Client().PostJsonAsync("/api/register", NewRegistration(user.Username.ToUpperInvariant()));
        await response.AssertErrorAsync(HttpStatusCode.Conflict, ErrorCodes.UsernameTaken);
    }

    [Fact]
    public async Task Register_RejectsWrongTotpCodeAndWeakKdf()
    {
        var request = NewRegistration("wrong-code");
        request = request with { TotpCode = server.WrongCode(request.TotpSecret) };
        await (await server.Client().PostJsonAsync("/api/register", request)).AssertErrorAsync(HttpStatusCode.BadRequest, ErrorCodes.TotpInvalid);

        var weak = NewRegistration("weak-kdf") with { Kdf = new KdfParams(KdfParams.Pbkdf2Sha256, FastCrypto.KdfIterations - 1) };
        await (await server.Client().PostJsonAsync("/api/register", weak)).AssertErrorAsync(HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Register_RefusedWhileRegistrationClosed()
    {
        server.Store.RegistrationOpen = false;
        try
        {
            Assert.False((await (await server.Client().GetAsync("/api/info")).ReadAsync<InfoResponse>()).RegistrationOpen);
            var response = await server.Client().PostJsonAsync("/api/register", NewRegistration("closed-reg"));
            await response.AssertErrorAsync(HttpStatusCode.Forbidden, ErrorCodes.RegistrationClosed);
        }
        finally
        {
            server.Store.RegistrationOpen = true;
        }
        Assert.True((await (await server.Client().GetAsync("/api/info")).ReadAsync<InfoResponse>()).RegistrationOpen);
    }

    [Fact]
    public async Task MalformedBodies_AreValidationErrors()
    {
        var client = server.Client();
        var missingField = await client.PostAsync("/api/login", new StringContent("""{"username":"x"}""", Encoding.UTF8, "application/json"));
        await missingField.AssertErrorAsync(HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        var notJson = await client.PostAsync("/api/login", new StringContent("{", Encoding.UTF8, "application/json"));
        await notJson.AssertErrorAsync(HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        await (await client.GetAsync("/api/nope")).AssertErrorAsync(HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Prelogin_DoesNotRevealWhetherUserExists()
    {
        var user = await server.RegisterAsync();
        var client = server.Client();
        var known = await client.PostJsonAsync("/api/prelogin", new PreloginRequest(user.Username.ToUpperInvariant()));
        var unknown = await client.PostJsonAsync("/api/prelogin", new PreloginRequest("Ghost-User"));
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Equal(PropertyNames(await known.Content.ReadAsStringAsync()), PropertyNames(await unknown.Content.ReadAsStringAsync()));
        Assert.Equal(user.Salt, (await known.ReadAsync<PreloginResponse>()).Salt);

        var fake = await unknown.ReadAsync<PreloginResponse>();
        Assert.Equal(16, fake.Salt.Length);
        Assert.Equal(KdfParams.Default, fake.Kdf);
        var again = await (await client.PostJsonAsync("/api/prelogin", new PreloginRequest("ghost-user"))).ReadAsync<PreloginResponse>();
        Assert.Equal(fake.Salt, again.Salt);
        var other = await (await client.PostJsonAsync("/api/prelogin", new PreloginRequest("ghost-user2"))).ReadAsync<PreloginResponse>();
        Assert.NotEqual(fake.Salt, other.Salt);

        static string PropertyNames(string json) =>
            string.Join(",", JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Login_ReturnsWrappedKeyAndNewSession()
    {
        var user = await server.RegisterAsync();
        var login = await server.SignInAsync(user);
        Assert.Equal(user.Username, login.Username);
        Assert.Equal(user.UserId, login.UserId);
        Assert.Equal(user.WrappedVaultKey, login.WrappedVaultKey);
        Assert.Equal(user.Salt, login.Salt);
        Assert.Equal(KdfParams.Default, login.Kdf);
        Assert.Equal(0, login.Revision);
        Assert.NotEqual(user.Token, login.Token);
        Assert.Equal(HttpStatusCode.OK, (await server.Client(login.Token).GetAsync("/api/sessions")).StatusCode);
    }

    [Fact]
    public async Task Login_BadKeyAndUnknownUser_LookIdentical()
    {
        var user = await server.RegisterAsync();
        var badKey = await server.LoginAsync(user, server.NextCode(user.Secret), authKey: RandomNumberGenerator.GetBytes(32));
        var unknown = await server.LoginAsync(user with { Username = "nobody-here" }, "123456");
        await badKey.AssertErrorAsync(HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        Assert.Equal(await badKey.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.Equal(badKey.StatusCode, unknown.StatusCode);
    }

    [Fact]
    public async Task Login_RequiresFreshTotpCode()
    {
        var user = await server.RegisterAsync();
        await (await server.LoginAsync(user, null)).AssertErrorAsync(HttpStatusCode.Unauthorized, ErrorCodes.TotpRequired);
        await (await server.LoginAsync(user, server.WrongCode(user.Secret))).AssertErrorAsync(HttpStatusCode.Unauthorized, ErrorCodes.TotpInvalid);

        string code = server.NextCode(user.Secret);
        Assert.Equal(HttpStatusCode.OK, (await server.LoginAsync(user, code)).StatusCode);
        await (await server.LoginAsync(user, code)).AssertErrorAsync(HttpStatusCode.Unauthorized, ErrorCodes.TotpInvalid); // replay
    }

    [Fact]
    public async Task Login_DisabledAccountIsRefused()
    {
        var user = await server.RegisterAsync();
        server.Store.SetDisabled(user.UserId, true);
        await (await server.LoginAsync(user, server.NextCode(user.Secret))).AssertErrorAsync(HttpStatusCode.Forbidden, ErrorCodes.AccountDisabled);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Client(user.Token).GetAsync("/api/sessions")).StatusCode);

        server.Store.SetDisabled(user.UserId, false);
        await server.SignInAsync(user);
    }

    [Fact]
    public async Task Login_AfterTotpReset_EnrollsNewAuthenticator()
    {
        var user = await server.RegisterAsync();
        server.Store.ResetTotp(user.UserId);
        await (await server.LoginAsync(user, server.NextCode(user.Secret))).AssertErrorAsync(HttpStatusCode.Forbidden, ErrorCodes.TotpSetupRequired);

        string secret = Totp.GenerateSecret();
        await (await server.LoginAsync(user, server.WrongCode(secret), newTotpSecret: secret))
            .AssertErrorAsync(HttpStatusCode.Unauthorized, ErrorCodes.TotpInvalid);
        Assert.Equal(HttpStatusCode.OK, (await server.LoginAsync(user, server.NextCode(secret), newTotpSecret: secret)).StatusCode);

        var enrolled = user with { Secret = secret };
        await server.SignInAsync(enrolled);
        await (await server.LoginAsync(user, server.NextCode(user.Secret))).AssertErrorAsync(HttpStatusCode.Unauthorized, ErrorCodes.TotpInvalid);
    }

    [Fact]
    public async Task Login_LocksOutAfterRepeatedFailures_ForKnownAndUnknownUsers()
    {
        var user = await server.RegisterAsync();
        var ghost = user with { Username = "ghost-lockout" };
        foreach (var target in new[] { user, ghost })
        {
            for (int i = 0; i < LoginThrottle.MaxFailures; i++)
            {
                await (await server.LoginAsync(target, "000000", authKey: RandomNumberGenerator.GetBytes(32)))
                    .AssertErrorAsync(HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
            }
            await (await server.LoginAsync(target, server.NextCode(user.Secret))).AssertErrorAsync(HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        }

        server.Clock.Advance(LoginThrottle.LockoutDuration);
        await server.SignInAsync(user);
    }

    [Fact]
    public async Task Logout_RevokesTheSession()
    {
        var user = await server.RegisterAsync();
        var client = server.Client(user.Token);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/logout", null)).StatusCode);
        await (await client.GetAsync("/api/sessions")).AssertErrorAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task ChangePassword_SwapsCredentialsAndCanRevokeOtherSessions()
    {
        var user = await server.RegisterAsync();
        var other = await server.SignInAsync(user);
        var client = server.Client(user.Token);
        byte[] newKey = RandomNumberGenerator.GetBytes(32), newSalt = RandomNumberGenerator.GetBytes(16), newWrapped = RandomNumberGenerator.GetBytes(61);

        var wrong = new ChangePasswordRequest(RandomNumberGenerator.GetBytes(32), server.NextCode(user.Secret), newKey, newSalt, KdfParams.Default, newWrapped);
        await (await client.PostJsonAsync("/api/account/password", wrong)).AssertErrorAsync(HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);

        var request = new ChangePasswordRequest(user.AuthKey, server.NextCode(user.Secret), newKey, newSalt, KdfParams.Default, newWrapped, RevokeOtherSessions: true);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostJsonAsync("/api/account/password", request)).StatusCode);

        await (await server.LoginAsync(user, server.NextCode(user.Secret))).AssertErrorAsync(HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        var login = await server.SignInAsync(user with { AuthKey = newKey });
        Assert.Equal(newWrapped, login.WrappedVaultKey);
        Assert.Equal(newSalt, login.Salt);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Client(other.Token).GetAsync("/api/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/sessions")).StatusCode);
    }

    private RegisterRequest NewRegistration(string username)
    {
        string secret = Totp.GenerateSecret();
        return new RegisterRequest(username, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16), KdfParams.Default,
            RandomNumberGenerator.GetBytes(61), secret, server.NextCode(secret), "test-device", "linux");
    }
}
