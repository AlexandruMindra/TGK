using System.Linq;
using TGK.Client.Main;
using TGK.Core.Models;
using Xunit;

namespace TGK.Client.Tests;

public class QuickConnectSearchTests
{
    private static VaultData Vault()
    {
        var identity = new Identity { Name = "deploy key", Username = "deploy" };
        var group = new HostGroup { Name = "Production" };
        var vault = new VaultData { Identities = [identity], Groups = [group] };
        vault.Hosts.Add(new HostEntry { Name = "web-01", Host = "10.0.0.5", Username = "root", GroupId = group.Id });
        vault.Hosts.Add(new HostEntry { Name = "db", Host = "db.internal", Port = 2222, IdentityId = identity.Id, GroupId = group.Id });
        vault.Hosts.Add(new HostEntry { Name = "router", Host = "192.168.1.1", Username = "admin" });
        return vault;
    }

    private static string[] Names(string query) => QuickConnect.Search(query, Vault()).Select(h => h.Name).ToArray();

    [Fact]
    public void Matches_by_name_user_host_and_group()
    {
        Assert.Equal(["web-01"], Names("web"));
        Assert.Equal(["router"], Names("admin"));
        Assert.Equal(["db"], Names("deploy")); // the identity's user
        Assert.Equal(["router"], Names("192.168"));
        Assert.Equal(["db", "web-01"], Names("production"));
        Assert.Empty(Names("   "));
    }

    [Fact]
    public void User_and_host_parts_of_a_word_with_at()
    {
        Assert.Equal(["web-01"], Names("root@10.0"));
        Assert.Equal(["web-01"], Names("root@"));
        Assert.Equal(["router"], Names("@192"));
        Assert.Equal(["web-01"], Names("root@web")); // the name counts as the host part too
        Assert.Equal(["db"], Names("deploy@db.internal:2222"));
        Assert.Empty(Names("admin@10.0"));
    }

    [Fact]
    public void Every_word_must_match_and_ssh_options_are_ignored()
    {
        Assert.Equal(["web-01"], Names("production root"));
        Assert.Equal(["router"], Names("ssh -p 22 admin@192.168.1.1"));
        Assert.Empty(Names("production admin"));
    }

    [Fact]
    public void Name_prefix_matches_come_first()
    {
        var vault = new VaultData();
        vault.Hosts.Add(new HostEntry { Name = "a-backup", Host = "backup.example" });
        vault.Hosts.Add(new HostEntry { Name = "backup", Host = "10.1.1.1" });
        Assert.Equal(["backup", "a-backup"], QuickConnect.Search("backup", vault).Select(h => h.Name).ToArray());
    }

    [Fact]
    public void FindSaved_matches_address_port_and_user()
    {
        VaultData vault = Vault();
        Assert.Equal("web-01", QuickConnect.FindSaved(new HostEntry { Host = "10.0.0.5", Username = "root" }, vault)?.Name);
        Assert.Equal("db", QuickConnect.FindSaved(new HostEntry { Host = "DB.internal", Port = 2222, Username = "deploy" }, vault)?.Name);
        Assert.Null(QuickConnect.FindSaved(new HostEntry { Host = "10.0.0.5", Username = "admin" }, vault));
        Assert.Null(QuickConnect.FindSaved(new HostEntry { Host = "db.internal", Username = "deploy" }, vault)); // port 22
    }
}
