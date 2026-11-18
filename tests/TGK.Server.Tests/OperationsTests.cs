using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace TGK.Server.Tests;

/// <summary>What running a server needs: backups while it serves, its version, a reverse proxy on another machine.</summary>
public sealed class OperationsTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Backup_CopiesTheDatabaseConsistentlyWhileTheServerRuns()
    {
        var user = await server.RegisterAsync();
        string target = Path.Combine(Path.GetTempPath(), "tgk-server-tests", Guid.NewGuid().ToString("N"), "backups", "tgk.db");
        try
        {
            var (code, output, _) = Run("backup", target);
            Assert.Equal(0, code);
            Assert.Contains("Backed up", output);
            Assert.False(File.Exists(target + ".partial"));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(target));

            // The copy is a working database: a server started on it knows the account.
            var store = new Store(new Db(target), TimeProvider.System);
            Assert.NotNull(store.FindUser(user.Username));

            SqliteConnection.ClearAllPools(); // let go of the copy (Windows can't replace an open file)
            Assert.Equal(0, Run("backup", target).Code); // replaces an older backup
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(target))!, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Backup_RefusesBadTargets()
    {
        Assert.Equal(2, Run("backup").Code);
        Assert.Equal(2, Run("backup", server.DbPath).Code);
        var error = new StringWriter();
        string missing = Path.Combine(Path.GetTempPath(), "tgk-server-tests", Guid.NewGuid().ToString("N"), "none.db");
        Assert.Equal(1, Cli.Run(["--db", missing, "backup", missing + ".bak"], new StringReader(""), new StringWriter(), error));
        Assert.Contains("No database", error.ToString());
        Assert.False(File.Exists(missing)); // a backup never creates the database
    }

    [Fact]
    public void Version_IsPrinted()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+", Run("--version").Output);
        Assert.Equal(Run("--version").Output, Run("version").Output);
    }

    [Fact]
    public void KnownProxies_AreParsed()
    {
        Assert.Equal(["192.168.1.10", "10.0.0.0/24", "fd00::1"], ServerApp.ParseProxies(" 192.168.1.10, 10.0.0.0/24;fd00::1 "));
        Assert.Null(ServerApp.ParseProxies("proxy.lan"));
        Assert.Null(ServerApp.ParseProxies("10.0.0.0/33"));
        Assert.Equal(2, Run("users", "--known-proxy", "proxy.lan").Code);
    }

    [Theory]
    [InlineData("192.168.1.10", "203.0.113.7")] // the known proxy: its client's address
    [InlineData("10.0.0.42", "203.0.113.7")]    // a proxy in the known network
    [InlineData("127.0.0.1", "203.0.113.7")]    // loopback stays trusted
    [InlineData("192.168.1.11", "192.168.1.11")] // anyone else: the header is ignored
    public async Task ForwardedHeaders_AreHonouredOnlyFromTrustedProxies(string peer, string expected)
    {
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance,
            Options.Create(ServerApp.ForwardedOptions(["192.168.1.10", "10.0.0.0/24"])));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.7";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        await middleware.Invoke(context);
        Assert.Equal(IPAddress.Parse(expected), context.Connection.RemoteIpAddress);
        Assert.Equal(expected != peer, context.Request.Scheme == "https");
    }

    private (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = Cli.Run(["--db", server.DbPath, .. args], new StringReader(""), output, error);
        return (code, output.ToString(), error.ToString());
    }
}
