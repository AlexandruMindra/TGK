using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using TGK.Core.Agents;
using TGK.Core.Models;
using Xunit;

namespace TGK.Core.Tests;

public class ShellCommandTests
{
    [Theory]
    [InlineData("ls -la /var/log")]
    [InlineData("cat /etc/os-release | grep -i ubuntu")]
    [InlineData("df -h && free -m; uptime")]
    [InlineData("git status")]
    [InlineData("git log --oneline -5")]
    [InlineData("systemctl status nginx")]
    [InlineData("journalctl -u nginx -n 50 --no-pager")]
    [InlineData("find /etc -name '*.conf' 2>/dev/null")]
    [InlineData("grep -rn 'listen' /etc/nginx 2>&1 | head")]
    [InlineData("docker ps -a")]
    [InlineData("env")]
    [InlineData("/usr/bin/ls")]
    [InlineData("echo \"a;b\" 'c|d'")]
    public void Read_only_commands_are_recognized(string line) => Assert.True(ShellCommand.Parse(line).IsReadOnly, line);

    [Theory]
    [InlineData("rm -rf /tmp/x")]
    [InlineData("echo hi > /etc/motd")]
    [InlineData("echo hi >> file")]
    [InlineData("cat a | tee b")]
    [InlineData("ls; reboot")]
    [InlineData("ls && rm x")]
    [InlineData("echo $(rm -rf ~)")]
    [InlineData("echo `id`")]
    [InlineData("echo \"$(id)\"")]
    [InlineData("find / -delete")]
    [InlineData("find . -exec rm {} \\;")]
    [InlineData("systemctl restart nginx")]
    [InlineData("git push")]
    [InlineData("git")]
    [InlineData("crontab")]
    [InlineData("env FOO=1 rm x")]
    [InlineData("LD_PRELOAD=x.so ls")]
    [InlineData("sort -o out in")]
    [InlineData("dmesg -c")]
    [InlineData("ip link set eth0 down")]
    [InlineData("cat <<EOF")]
    [InlineData("diff <(ls a) <(ls b)")]
    [InlineData("echo 'unbalanced")]
    [InlineData("sudo cat /etc/shadow")]
    [InlineData("uniq in.txt out.txt")]
    [InlineData("hostname evil")]
    [InlineData("date 010100002030")]
    [InlineData("file -C -m magic")]
    [InlineData("hostnamectl set-hostname x")]
    [InlineData("git reflog expire --all")]
    [InlineData("yq -i '.a = 1' f.yaml")]
    [InlineData("xxd in out")]
    [InlineData("git grep -Osh pattern")]
    [InlineData("git grep --open-files-in-pager=sh x")]
    [InlineData("git grep foo")]
    [InlineData("git -c core.pager=sh log")]
    [InlineData("less +!sh file")]
    public void Commands_that_may_change_things_are_not_read_only(string line)
    {
        // "git" alone prints help, which is harmless; the rest must not pass.
        if (line == "git")
        {
            Assert.True(ShellCommand.Parse(line).IsReadOnly);
            return;
        }
        Assert.False(ShellCommand.Parse(line).IsReadOnly, line);
    }

    [Fact]
    public void Redacts_secrets_from_the_record()
    {
        Assert.Equal("mysql -u *** -p*** db", TGK.Core.Agents.AgentActivity.Redact("mysql -u root -phunter2 db")); // the username is hidden too, which is fine for a log
        Assert.Equal("psql -h db -p 5432", TGK.Core.Agents.AgentActivity.Redact("psql -h db -p 5432")); // -p with a space is a port, not a password
        Assert.Equal("curl --token ***", TGK.Core.Agents.AgentActivity.Redact("curl --token abc123"));
        Assert.Equal("git clone https://user:***@example.com/r.git", TGK.Core.Agents.AgentActivity.Redact("git clone https://user:pw@example.com/r.git"));
        Assert.Equal("ls -la /srv", TGK.Core.Agents.AgentActivity.Redact("ls -la /srv"));
    }

    [Fact]
    public void Splits_chained_commands_and_respects_quotes()
    {
        ShellCommand c = ShellCommand.Parse("echo 'a; b' && ls -l \"my dir\" | wc -l");
        Assert.Equal(3, c.Commands.Count);
        Assert.Equal(["echo", "a; b"], c.Commands[0]);
        Assert.Equal(["ls", "-l", "my dir"], c.Commands[1]);
        Assert.False(c.IsComplex);
    }

    [Fact]
    public void Allowed_prefixes_match_whole_words_and_wildcards()
    {
        string[] allowed = ["systemctl restart myapp*", "make test"];
        Assert.True(ShellCommand.Parse("systemctl restart myapp-worker").IsAllowed(allowed));
        Assert.True(ShellCommand.Parse("make test && git status").IsAllowed(allowed));
        Assert.True(ShellCommand.Parse("make   test VERBOSE=1").IsAllowed(allowed));
        Assert.False(ShellCommand.Parse("make testing").IsAllowed(allowed));
        Assert.False(ShellCommand.Parse("make test; rm -rf /").IsAllowed(allowed));
        Assert.False(ShellCommand.Parse("make test > out.txt").IsAllowed(allowed));
    }
}

public class AgentPolicyTests
{
    private const string Home = "/home/dev";

    private static AgentRules Rules(AgentAccess access, string[]? commands = null, string[]? paths = null) =>
        new(access, commands ?? [], paths ?? []);

    [Fact]
    public void Off_denies_everything()
    {
        AgentRules off = Rules(AgentAccess.Off);
        Assert.Equal(AgentVerdict.Deny, AgentPolicy.Read(off, "/etc/hosts", Home).Verdict);
        Assert.Equal(AgentVerdict.Deny, AgentPolicy.Write(off, "/tmp/x", Home).Verdict);
        Assert.Equal(AgentVerdict.Deny, AgentPolicy.Run(off, "ls", Home).Verdict);
    }

    [Fact]
    public void Read_only_reads_and_runs_read_only_commands_but_never_changes()
    {
        AgentRules ro = Rules(AgentAccess.ReadOnly, commands: ["make check"]);
        Assert.Equal(AgentVerdict.Allow, AgentPolicy.Read(ro, "/etc/nginx/nginx.conf", Home).Verdict);
        Assert.Equal(AgentVerdict.Allow, AgentPolicy.Run(ro, "tail -n 100 /var/log/syslog", Home).Verdict);
        Assert.Equal(AgentVerdict.Allow, AgentPolicy.Run(ro, "make check", Home).Verdict);
        Assert.Equal(AgentVerdict.Deny, AgentPolicy.Run(ro, "systemctl restart nginx", Home).Verdict);
        Assert.Equal(AgentVerdict.Deny, AgentPolicy.Write(ro, "/tmp/x", Home).Verdict);
        Assert.Equal(AgentVerdict.Deny, AgentPolicy.Read(ro, "/home/dev/.ssh/id_ed25519", Home).Verdict);
        Assert.Equal(AgentVerdict.Deny, AgentPolicy.Run(ro, "cat ~/.ssh/authorized_keys", Home).Verdict);
    }

    [Fact]
    public void Ask_lets_reads_through_and_asks_for_changes()
    {
        AgentRules ask = Rules(AgentAccess.Ask, commands: ["systemctl reload nginx"]);
        Assert.Equal(AgentVerdict.Allow, AgentPolicy.Read(ask, "/etc/nginx/nginx.conf", Home).Verdict);
        Assert.Equal(AgentVerdict.Ask, AgentPolicy.Run(ask, "nginx -t 2>&1 | head; systemctl reload nginx", Home).Verdict); // nginx -t is not known
        Assert.Equal(AgentVerdict.Allow, AgentPolicy.Run(ask, "systemctl reload nginx", Home).Verdict);
        Assert.Equal(AgentVerdict.Ask, AgentPolicy.Run(ask, "apt-get upgrade -y", Home).Verdict);
        Assert.Equal(AgentVerdict.Ask, AgentPolicy.Write(ask, "/etc/nginx/nginx.conf", Home).Verdict);
        Assert.Equal(AgentVerdict.Ask, AgentPolicy.Read(ask, "/etc/shadow", Home).Verdict);
    }

    [Fact]
    public void Full_needs_approval_only_for_protected_paths()
    {
        AgentRules full = Rules(AgentAccess.Full, paths: ["/srv/secrets/**"]);
        Assert.Equal(AgentVerdict.Allow, AgentPolicy.Run(full, "apt-get upgrade -y", Home).Verdict);
        Assert.Equal(AgentVerdict.Allow, AgentPolicy.Write(full, "/etc/nginx/nginx.conf", Home).Verdict);
        Assert.Equal(AgentVerdict.Ask, AgentPolicy.Write(full, "/srv/secrets/db.txt", Home).Verdict);
        Assert.Equal(AgentVerdict.Ask, AgentPolicy.Read(full, "/srv/secrets", Home).Verdict);
        Assert.Equal(AgentVerdict.Ask, AgentPolicy.Write(full, "/home/dev/.ssh/authorized_keys", Home).Verdict);
        Assert.Equal(AgentVerdict.Ask, AgentPolicy.Read(full, "/opt/app/.env", Home).Verdict);
        Assert.Equal(AgentVerdict.Ask, AgentPolicy.Read(full, "/etc/ssl/private/site.key", Home).Verdict);
        Assert.Equal(AgentVerdict.Ask, AgentPolicy.Run(full, "cp backup.tar /srv/secrets/", Home).Verdict);
        Assert.Equal(AgentVerdict.Allow, AgentPolicy.Read(full, "/srv/secretsauce.txt", Home).Verdict);
    }

    [Theory]
    [InlineData("cat ~/.ss*/id*")]
    [InlineData("cat /home/dev/.ssh/*")]
    [InlineData("head /etc/ssh/*")]
    [InlineData("cat $HOME/.ssh/authorized_keys")]
    [InlineData("cat \"${HOME}/.ssh/config\"")]
    [InlineData("cat $SOME_DIR/file")]
    [InlineData("grep -r BEGIN /home/dev")]
    [InlineData("grep -rn password ~")]
    [InlineData("rg secret /etc")]
    [InlineData("grep -R x")] // the working directory (home) holds ~/.ssh
    [InlineData("cat ssh_host_ed25519_key", "/etc/ssh")]
    [InlineData("cat .ssh/id_ed25519")]
    [InlineData("tail --follow=name /etc/shadow")]
    [InlineData("cat /etc/ssl/private/*")]
    public void Commands_that_may_reach_protected_paths_are_caught(string command, string? cwd = null)
    {
        AgentRules ro = Rules(AgentAccess.ReadOnly);
        Assert.Equal(AgentVerdict.Deny, AgentPolicy.Run(ro, command, Home, cwd).Verdict);
        Assert.Equal(AgentVerdict.Ask, AgentPolicy.Run(Rules(AgentAccess.Full), command, Home, cwd).Verdict);
    }

    [Theory]
    [InlineData("tail -n 50 /var/log/nginx/*.log")]
    [InlineData("ls ~/projects")]
    [InlineData("grep -rn listen /etc/nginx")]
    [InlineData("rg TODO ~/projects/app")]
    [InlineData("find /etc -name '*.conf'")]
    [InlineData("echo $HOME")]
    [InlineData("date -d yesterday +%F")]
    [InlineData("sort data.txt | uniq -c")]
    public void Ordinary_read_only_commands_still_run(string command) =>
        Assert.Equal(AgentVerdict.Allow, AgentPolicy.Run(Rules(AgentAccess.ReadOnly), command, Home).Verdict);

    [Fact]
    public void Cd_in_the_command_line_moves_where_paths_are_resolved()
    {
        Assert.Equal(AgentVerdict.Ask, AgentPolicy.Run(Rules(AgentAccess.Full), "cd /etc/ssh && cat ssh_host_rsa_key", Home).Verdict);
        Assert.Equal(AgentVerdict.Ask, AgentPolicy.Run(Rules(AgentAccess.Full), "cd ~ && cat .ssh/config", Home).Verdict);
        Assert.Equal(AgentVerdict.Ask, AgentPolicy.Run(Rules(AgentAccess.Full), "cd \"$X\" && cat a/b", Home).Verdict);
        Assert.Equal(AgentVerdict.Allow, AgentPolicy.Run(Rules(AgentAccess.Full), "cd /var/www && cat index.html", Home).Verdict);
    }

    [Fact]
    public void Rules_are_inherited_from_group_and_defaults()
    {
        var group = new HostGroup { Name = "Production", Options = { AgentAccess = AgentAccess.ReadOnly, AgentCommands = ["systemctl status *"] } };
        var host = new HostEntry { Name = "web", Host = "web", GroupId = group.Id };
        var open = new HostEntry { Name = "lab", Host = "lab", Options = { AgentAccess = AgentAccess.Full } };
        var plain = new HostEntry { Name = "nas", Host = "nas" };
        var vault = new VaultData { Groups = [group], Hosts = [host, open, plain], Defaults = { AgentProtectedPaths = ["/data/**"] } };

        AgentRules web = AgentRules.For(vault, host);
        Assert.Equal(AgentAccess.ReadOnly, web.Access);
        Assert.Equal(["systemctl status *"], web.AllowedCommands);
        Assert.Equal(["/data/**"], web.ProtectedPaths);
        Assert.Equal(AgentAccess.Full, AgentRules.For(vault, open).Access);
        Assert.Equal(AgentAccess.Off, AgentRules.For(vault, plain).Access);
    }

    [Fact]
    public void Agent_options_validate_clone_and_round_trip()
    {
        var options = new HostOptions { AgentAccess = AgentAccess.Ask, AgentCommands = ["make test"], AgentProtectedPaths = ["~/private/**"] };
        Assert.Null(options.Validate());
        Assert.False(options.IsEmpty);
        HostOptions copy = options.Clone();
        copy.AgentCommands!.Add("x");
        Assert.Single(options.AgentCommands);

        string json = JsonSerializer.Serialize(options, TgkJson.Options);
        Assert.Contains("\"agentAccess\": \"ask\"", json);
        HostOptions back = JsonSerializer.Deserialize<HostOptions>(json, TgkJson.Options)!;
        Assert.Equal(AgentAccess.Ask, back.AgentAccess);
        Assert.Equal(["~/private/**"], back.AgentProtectedPaths);

        // A mode from a newer client is read as Off instead of failing the whole item.
        Assert.Equal(AgentAccess.Off, JsonSerializer.Deserialize<HostOptions>("""{ "agentAccess": "supervised" }""", TgkJson.Options)!.AgentAccess);
        Assert.Equal(AgentAccess.ReadOnly, JsonSerializer.Deserialize<HostOptions>("""{ "agentAccess": "readOnly" }""", TgkJson.Options)!.AgentAccess);
        Assert.Equal(AgentAccess.Off, JsonSerializer.Deserialize<HostOptions>("""{ "agentAccess": 3 }""", TgkJson.Options)!.AgentAccess);
        Assert.Null(JsonSerializer.Deserialize<HostOptions>("""{ "agentAccess": null }""", TgkJson.Options)!.AgentAccess);
        Assert.Contains("\"agentAccess\": null", JsonSerializer.Serialize(new HostOptions(), TgkJson.Options));

        Assert.NotNull(new HostOptions { AgentCommands = [""] }.Validate());
        Assert.NotNull(new HostOptions { AgentProtectedPaths = ["a\nb"] }.Validate());
        Assert.NotNull(new HostOptions { AgentAccess = (AgentAccess)42 }.Validate());
    }
}

public class RemotePathTests
{
    [Theory]
    [InlineData("~", "/home/u")]
    [InlineData("~/a/b", "/home/u/a/b")]
    [InlineData("a/./b/../c", "/home/u/a/c")]
    [InlineData("/etc//nginx/", "/etc/nginx")]
    [InlineData("/../..", "/")]
    public void Resolves_against_home(string path, string expected) => Assert.Equal(expected, RemotePath.Resolve("/home/u", path));

    [Fact]
    public void Rejects_empty_and_control_characters()
    {
        Assert.Throws<ArgumentException>(() => RemotePath.Resolve("/home/u", " "));
        Assert.Throws<ArgumentException>(() => RemotePath.Resolve("/home/u", "a\nb"));
    }

    [Theory]
    [InlineData("~/.ssh/**", "/home/u/.ssh/id_rsa", true)]
    [InlineData("~/.ssh/**", "/home/u/.sshx/id", false)]
    [InlineData("*.pem", "/etc/ssl/a.pem", true)]
    [InlineData("/etc/*.conf", "/etc/a.conf", true)]
    [InlineData("/etc/*.conf", "/etc/x/a.conf", false)]
    [InlineData("/etc/**/*.conf", "/etc/x/y/a.conf", true)]
    [InlineData("/etc/**/*.conf", "/etc/a.conf", true)]
    [InlineData("src/*.cs", "/srv/app/src/a.cs", true)]
    public void Globs_match_paths(string pattern, string path, bool expected) =>
        Assert.Equal(expected, RemotePath.Matches(pattern, path, "/home/u"));

    [Fact]
    public void Quotes_for_the_shell() => Assert.Equal("'it'\\''s'", RemotePath.Quote("it's"));
}

public class TextContentTests
{
    [Fact]
    public void Keeps_bom_and_crlf_through_an_edit()
    {
        byte[] original = [0xEF, 0xBB, 0xBF, .. "a = 1\r\nb = 2\r\n"u8.ToArray()];
        TextContent text = TextContent.Decode(original)!;
        Assert.True(text.HasBom);
        Assert.Equal("\r\n", text.Newline);
        Assert.Equal("a = 1\nb = 2\n", text.Normalized);

        (TextContent edited, int count) = text.Replace("a = 1\nb", "a = 10\nb", all: false);
        Assert.Equal(1, count);
        Assert.Equal([0xEF, 0xBB, 0xBF, .. "a = 10\r\nb = 2\r\n"u8.ToArray()], edited.Encode());
    }

    [Fact]
    public void Edit_requires_a_unique_existing_match()
    {
        TextContent text = TextContent.Decode("x\nx\ny\n"u8.ToArray())!;
        Assert.Contains("2 times", Assert.Throws<RemoteFileException>(() => text.Replace("x", "z", false)).Message);
        Assert.Contains("not found", Assert.Throws<RemoteFileException>(() => text.Replace("q", "z", false)).Message);
        Assert.Throws<RemoteFileException>(() => text.Replace("", "z", false));
        Assert.Equal("z\nz\ny\n", text.Replace("x", "z", true).Result.Text);
    }

    [Fact]
    public void Binary_is_refused_and_latin1_kept()
    {
        Assert.Null(TextContent.Decode([1, 2, 0, 3]));
        TextContent latin = TextContent.Decode([0x63, 0x61, 0x66, 0xE9])!; // "café" in Latin-1
        Assert.True(latin.IsLatin1);
        Assert.Equal("café", latin.Text);
        Assert.Equal(new byte[] { 0x63, 0x61, 0x66, 0xE9 }, latin.Encode());
    }

    [Fact]
    public void Numbers_lines_like_cat_n()
    {
        TextContent text = TextContent.Decode(Encoding.UTF8.GetBytes("one\ntwo\nthree\n"))!;
        (string numbered, int total, int first, int last) = text.Numbered(2, 5, 100);
        Assert.Equal("     2\ttwo\n     3\tthree\n", numbered);
        Assert.Equal((3, 2, 3), (total, first, last));
    }

    [Fact]
    public void Diff_shows_changed_lines_with_context()
    {
        string before = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line {i}")) + "\n";
        string after = before.Replace("line 10\n", "line ten\n");
        string diff = TextDiff.Unified(before, after);
        Assert.Contains("-line 10\n+line ten\n", diff);
        Assert.Contains(" line 9\n", diff);
        Assert.DoesNotContain("line 2\n", diff);
        Assert.StartsWith("@@ -7,7 +7,7 @@", diff);
        Assert.Equal("(no changes)", TextDiff.Unified(before, before));
    }
}
