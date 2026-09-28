using System;
using TGK.Core.Models;
using TGK.Core.Ssh;
using Xunit;

namespace TGK.Core.Tests;

public class SshConnectRequestTests
{
    private static readonly SshConnectRequest Valid = new() { Host = "srv.example.com", Username = "ops", Password = "pw" };

    [Fact]
    public void Defaults_AreSensible()
    {
        Assert.Null(Valid.Validate());
        Assert.Equal(22, Valid.Port);
        Assert.Equal("xterm-256color", Valid.TermType);
        Assert.Equal(TimeSpan.FromSeconds(15), Valid.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), Valid.KeepAlive);
        Assert.Equal((80, 24), (Valid.Cols, Valid.Rows));
    }

    public static TheoryData<SshConnectRequest> InvalidRequests() =>
    [
        Valid with { Host = "" },
        Valid with { Host = "two words" },
        Valid with { Port = 0 },
        Valid with { Port = 65536 },
        Valid with { Username = " " },
        Valid with { Password = null, PrivateKey = null },
        Valid with { Password = "", PrivateKey = "  " },
        Valid with { Cols = 0 },
        Valid with { Rows = -1 },
        Valid with { TermType = "" },
        Valid with { ConnectTimeout = TimeSpan.Zero },
        Valid with { KeepAlive = TimeSpan.FromSeconds(-1) },
    ];

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void Validate_ReportsProblem(SshConnectRequest request)
    {
        Assert.False(string.IsNullOrWhiteSpace(request.Validate()));
    }

    [Fact]
    public void Validate_AcceptsKeyOnly_AndZeroKeepAlive()
    {
        Assert.Null((Valid with { Password = null, PrivateKey = "KEY", KeepAlive = TimeSpan.Zero }).Validate());
    }

    [Fact]
    public void ToString_NeverContainsSecrets()
    {
        var request = Valid with { Password = "hunter2", PrivateKey = "PRIVATE", Passphrase = "phrase" };
        string text = request.ToString();

        Assert.Equal("ops@srv.example.com:22", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("PRIVATE", text);
    }

    [Fact]
    public void ForHost_UsesHostUsernameOverIdentity_AndPasswordIdentity()
    {
        var identity = new Identity { Username = "identity-user", AuthKind = AuthKind.Password, Password = "pw", PrivateKey = "ignored" };
        var host = new HostEntry { Host = " h.example.com ", Port = 2200, Username = "host-user" };

        SshConnectRequest request = SshConnectRequest.ForHost(host, identity, 120, 40);

        Assert.Equal("h.example.com", request.Host);
        Assert.Equal(2200, request.Port);
        Assert.Equal("host-user", request.Username);
        Assert.Equal("pw", request.Password);
        Assert.Null(request.PrivateKey);
        Assert.Equal((120, 40), (request.Cols, request.Rows));
    }

    [Fact]
    public void ForHost_FallsBackToIdentityUsername_AndUsesKey()
    {
        var identity = new Identity { Username = "deploy", AuthKind = AuthKind.PrivateKey, PrivateKey = "KEY", Passphrase = "pp", Password = "unused" };
        var host = new HostEntry { Host = "h" };

        SshConnectRequest request = SshConnectRequest.ForHost(host, identity);

        Assert.Equal("deploy", request.Username);
        Assert.Equal("KEY", request.PrivateKey);
        Assert.Equal("pp", request.Passphrase);
        Assert.Null(request.Password);
    }
}
