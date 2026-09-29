using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using TGK.Protocol;
using TGK.Protocol.Dtos;
using Xunit;

namespace TGK.Server.Tests;

/// <summary>Admin commands run against the same database file the in-memory server uses.</summary>
public sealed class CliTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Registration_CanBeToggled()
    {
        Assert.Equal(0, Run("", "registration", "off").Code);
        Assert.False((await (await server.Client().GetAsync("/api/info")).ReadAsync<InfoResponse>()).RegistrationOpen);
        Assert.Contains("closed", Run("", "registration", "status").Output);
        Assert.Equal(0, Run("", "registration", "on").Code);
        Assert.True(server.Store.RegistrationOpen);
    }

    [Fact]
    public async Task UserCreate_InvitesAUserWhoCreatesTheAccountInTheApp()
    {
        var (code, output, _) = Run("", "user", "create", "cli-made");
        Assert.Equal(0, code);
        string invite = output.Split('\n').Select(l => l.Trim()).Single(l => l.Length == 19 && l.Count(c => c == '-') == 3);
        Assert.Contains("invited until", Run("", "users").Output);
        Assert.Equal(1, Run("", "user", "show", "cli-made").Code);
        Assert.Null(server.Store.FindUser("cli-made")); // no password, vault key or authenticator exists yet

        Assert.Equal(0, Run("", "registration", "off").Code);
        try
        {
            // The name is reserved for the code holder; the code works while registration is off, once.
            await (await server.TryRegisterAsync("cli-made")).Response.AssertErrorAsync(HttpStatusCode.Conflict, ErrorCodes.UsernameTaken);
            await (await server.TryRegisterAsync("cli-made", "AAAA-BBBB-CCCC-DDDD")).Response.AssertErrorAsync(HttpStatusCode.Forbidden, ErrorCodes.InviteInvalid);
            await (await server.TryRegisterAsync("someone-else", invite)).Response.AssertErrorAsync(HttpStatusCode.Forbidden, ErrorCodes.InviteInvalid);
            await (await server.TryRegisterAsync("someone-else")).Response.AssertErrorAsync(HttpStatusCode.Forbidden, ErrorCodes.RegistrationClosed);
            var user = await server.RegisterAsync("CLI-Made", invite.ToLowerInvariant().Replace("-", " "));
            Assert.Null(server.Store.FindInvite("cli-made"));
            await (await server.TryRegisterAsync("other", invite)).Response.AssertErrorAsync(HttpStatusCode.Forbidden, ErrorCodes.InviteInvalid);
            await server.SignInAsync(user);
        }
        finally
        {
            Run("", "registration", "on");
        }

        Assert.Equal(1, Run("", "user", "create", "cli-made").Code); // exists now
        Assert.Equal(0, Run("", "user", "create", "cli-later").Code);
        Assert.Equal(0, Run("", "user", "delete", "cli-later").Code);
        Assert.Null(server.Store.FindInvite("cli-later"));
        server.Clock.Advance(Store.InviteLifetime);
        Assert.Equal(0, Run("", "user", "create", "cli-expired").Code);
        server.Clock.Advance(Store.InviteLifetime + TimeSpan.FromMinutes(1));
        Assert.Null(server.Store.FindInvite("cli-expired"));
    }

    [Fact]
    public async Task UserCommands_ManageAccounts()
    {
        var user = await server.RegisterAsync();

        var list = Run("", "users");
        Assert.Equal(0, list.Code);
        Assert.Contains(user.Username, list.Output);
        Assert.Contains("USERNAME", list.Output);

        var show = Run("", "user", "show", user.Username);
        Assert.Contains(user.UserId, show.Output);
        Assert.Contains("test-device", show.Output);

        Assert.Equal(0, Run("", "user", "disable", user.Username).Code);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Client(user.Token).GetAsync("/api/sessions")).StatusCode);
        await (await server.LoginAsync(user, server.NextCode(user.Secret))).AssertErrorAsync(HttpStatusCode.Forbidden, ErrorCodes.AccountDisabled);
        Assert.Equal(0, Run("", "user", "enable", user.Username).Code);
        var session = await server.SignInAsync(user);

        Assert.Equal(0, Run("", "user", "revoke-sessions", user.Username).Code);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Client(session.Token).GetAsync("/api/sessions")).StatusCode);

        Assert.Equal(0, Run("", "user", "reset-totp", user.Username).Code);
        Assert.Null(server.Store.FindUser(user.Username)!.TotpSecret);

        Assert.Equal(1, Run("n\n", "user", "delete", user.Username).Code);
        Assert.NotNull(server.Store.FindUser(user.Username));
        Assert.Equal(0, Run("", "user", "delete", user.Username, "--yes").Code);
        Assert.Null(server.Store.FindUser(user.Username));
    }

    [Fact]
    public void BadInput_ReportsErrors()
    {
        Assert.Equal(1, Run("", "user", "show", "no-such-user").Code);
        Assert.Equal(2, Run("", "frobnicate").Code);
        Assert.Equal(2, Run("", "users", "--bogus").Code);
        Assert.Equal(2, Run("", "users", "--db").Code);
        Assert.Contains("registration on|off|status", Run("", "--help").Output);
    }

    private (int Code, string Output, string Error) Run(string input, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = Cli.Run(["--db", server.DbPath, .. args], new StringReader(input), output, error);
        return (code, output.ToString(), error.ToString());
    }
}
