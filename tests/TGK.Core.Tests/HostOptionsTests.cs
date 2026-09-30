using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TGK.Core.Models;
using TGK.Core.Services;
using TGK.Core.Ssh;
using Xunit;

namespace TGK.Core.Tests;

public class HostOptionsTests
{
    private static HostOptions Full(Guid jump) => new()
    {
        JumpHostId = jump, KeepAliveSeconds = 5, ConnectTimeoutSeconds = 7, AutoReconnect = true,
        StartupCommand = "tmux attach", Environment = [new EnvVar { Name = "LANG", Value = "C.UTF-8" }], TerminalType = "vt220",
        FontSize = 16, ColorScheme = "Solarized", LegacyAlgorithms = true, FontFamily = "Hack",
        ScrollbackLines = 500, CursorShape = TerminalSettings.CursorBar, CursorBlink = false, CopyOnSelect = true,
    };

    [Fact]
    public void Options_AndTunnels_RoundTripThroughJson()
    {
        var host = new HostEntry
        {
            Host = "web", Options = Full(Guid.NewGuid()),
            Tunnels = [new PortForward { Kind = ForwardKind.Remote, BindAddress = "0.0.0.0", BindPort = 9000, DestinationHost = "localhost", DestinationPort = 3000, Enabled = false, Description = "dev" }],
        };
        var vault = new VaultData { Hosts = [host], Groups = [new HostGroup { Name = "g", Options = new HostOptions { KeepAliveSeconds = 0 } }], Defaults = new HostOptions { TerminalType = "xterm" } };

        string json = JsonSerializer.Serialize(vault, TgkJson.Options);
        VaultData copy = JsonSerializer.Deserialize<VaultData>(json, TgkJson.Options)!;

        Assert.Equal(json, JsonSerializer.Serialize(copy, TgkJson.Options));
        Assert.Contains("\"kind\": \"remote\"", json);
        Assert.Equivalent(host.Options, copy.Hosts[0].Options);
        Assert.Equivalent(host.Tunnels, copy.Hosts[0].Tunnels);
        Assert.Equal(0, copy.Groups[0].Options.KeepAliveSeconds);
        Assert.Equal("xterm", copy.Defaults.TerminalType);
    }

    [Fact]
    public void OldJson_WithoutOrWithNullOptions_GetsEmptyOptions()
    {
        const string json = """
            { "hosts": [ { "host": "a" }, { "host": "b", "options": null, "tunnels": null } ],
              "groups": [ { "name": "g" } ], "defaults": null }
            """;
        VaultData vault = JsonSerializer.Deserialize<VaultData>(json, TgkJson.Options)!;

        Assert.All(vault.Hosts, h => Assert.True(h.Options.IsEmpty));
        Assert.All(vault.Hosts, h => Assert.Empty(h.Tunnels));
        Assert.True(vault.Groups[0].Options.IsEmpty);
        Assert.True(vault.Defaults.IsEmpty);
    }

    [Fact]
    public void Clone_IsDeep()
    {
        var host = new HostEntry { Host = "a", Options = Full(Guid.NewGuid()), Tunnels = [new PortForward { BindPort = 1 }] };
        HostEntry copy = host.Clone();
        copy.Options.Environment![0].Value = "changed";
        copy.Options.KeepAliveSeconds = 99;
        copy.Tunnels[0].BindPort = 2;

        Assert.Equal("C.UTF-8", host.Options.Environment![0].Value);
        Assert.Equal(5, host.Options.KeepAliveSeconds);
        Assert.Equal(1, host.Tunnels[0].BindPort);

        var vault = new VaultData { Defaults = Full(Guid.NewGuid()) };
        vault.Clone().Defaults.Environment!.Clear();
        Assert.Single(vault.Defaults.Environment!);
    }

    public static TheoryData<HostOptions> InvalidOptions() =>
    [
        new HostOptions { KeepAliveSeconds = -1 },
        new HostOptions { KeepAliveSeconds = HostOptions.MaxKeepAliveSeconds + 1 },
        new HostOptions { ConnectTimeoutSeconds = 0 },
        new HostOptions { Environment = [new EnvVar { Name = "1BAD" }] },
        new HostOptions { Environment = [new EnvVar { Name = "A=B" }] },
        new HostOptions { TerminalType = "" },
        new HostOptions { TerminalType = "xterm 256" },
        new HostOptions { FontSize = 2 },
        new HostOptions { ColorScheme = " " },
        new HostOptions { FontFamily = "" },
        new HostOptions { FontFamily = "Bad\nName" },
        new HostOptions { ScrollbackLines = -1 },
        new HostOptions { ScrollbackLines = HostOptions.MaxScrollbackLines + 1 },
        new HostOptions { CursorShape = "circle" },
    ];

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void Validate_ReportsProblem(HostOptions options)
    {
        Assert.NotNull(options.Validate());
        Assert.Throws<ArgumentException>(() => VaultEdits.SaveDefaults(options));
        Assert.Throws<ArgumentException>(() => VaultEdits.SaveGroup(new HostGroup { Name = "g", Options = options }));
        Assert.Throws<ArgumentException>(() => VaultEdits.SaveHost(new HostEntry { Host = "h", Options = options }));
    }

    [Fact]
    public void LocalTerminalSettings_MoveIntoEmptyDefaultsOnce()
    {
        var local = new TerminalSettings { FontSize = 16, ScrollbackLines = 50_000, CursorShape = TerminalSettings.CursorBar, CursorBlink = false, CopyOnSelect = true };
        var defaults = new HostOptions { KeepAliveSeconds = 10 };

        HostOptions moved = HostOptions.WithLocalTerminal(defaults, local)!;

        Assert.Equal(16, moved.FontSize);
        Assert.Equal(50_000, moved.ScrollbackLines);
        Assert.Equal(TerminalSettings.CursorBar, moved.CursorShape);
        Assert.False(moved.CursorBlink);
        Assert.True(moved.CopyOnSelect);
        Assert.Equal(10, moved.KeepAliveSeconds);
        Assert.Null(defaults.FontSize); // not modified
        // Built-in values: nothing to move. Terminal values already in the vault (another device's): left alone.
        Assert.Null(HostOptions.WithLocalTerminal(defaults, new TerminalSettings()));
        Assert.Null(HostOptions.WithLocalTerminal(new HostOptions { CursorBlink = true }, local));
        // Only what differs from the built-in values is set.
        HostOptions sizeOnly = HostOptions.WithLocalTerminal(new HostOptions(), new TerminalSettings { FontSize = 12 })!;
        Assert.Equal(12, sizeOnly.FontSize);
        Assert.Null(sizeOnly.ScrollbackLines);
        Assert.Null(sizeOnly.CursorShape);
    }

    [Fact]
    public void Validate_AcceptsEmptyAndFull()
    {
        Assert.Null(new HostOptions().Validate());
        Assert.Null(Full(Guid.NewGuid()).Validate());
        Assert.Null(new HostOptions { KeepAliveSeconds = 0, StartupCommand = "" }.Validate());
    }

    [Fact]
    public void SaveHost_RejectsSelfJumpAndInvalidTunnels()
    {
        var host = new HostEntry { Host = "h" };
        host.Options.JumpHostId = host.Id;
        Assert.Throws<ArgumentException>(() => VaultEdits.SaveHost(host));

        var tunnel = new HostEntry { Host = "h", Tunnels = [new PortForward { Kind = ForwardKind.Local, BindPort = 8080 }] };
        Assert.Contains("destination", Assert.Throws<ArgumentException>(() => VaultEdits.SaveHost(tunnel)).Message);
        tunnel.Tunnels[0].Kind = ForwardKind.Dynamic;
        VaultEdits.SaveHost(tunnel); // a SOCKS proxy needs no destination
    }

    [Theory]
    [InlineData(ForwardKind.Local, "L 127.0.0.1:8080 → db:5432")]
    [InlineData(ForwardKind.Remote, "R 127.0.0.1:8080 → db:5432")]
    [InlineData(ForwardKind.Dynamic, "D 127.0.0.1:8080")]
    public void PortForward_ToString_IsOpenSshLike(ForwardKind kind, string expected)
    {
        var forward = new PortForward { Kind = kind, BindPort = 8080, DestinationHost = "db", DestinationPort = 5432 };
        Assert.Equal(expected, forward.ToString());
        Assert.Null(forward.Validate());
    }

    [Theory]
    [InlineData(ForwardKind.Local, "127.0.0.1", false)]
    [InlineData(ForwardKind.Local, "127.1.2.3", false)]
    [InlineData(ForwardKind.Dynamic, "localhost", false)]
    [InlineData(ForwardKind.Dynamic, "::1", false)]
    [InlineData(ForwardKind.Local, "[::1]", false)]
    [InlineData(ForwardKind.Local, "0.0.0.0", true)]
    [InlineData(ForwardKind.Dynamic, "192.168.1.5", true)]
    [InlineData(ForwardKind.Dynamic, "::", true)]
    [InlineData(ForwardKind.Remote, "0.0.0.0", false)] // listens on the server, not on this computer
    public void PortForward_IsOpenToNetwork_WhenListeningBeyondLoopbackHere(ForwardKind kind, string bind, bool expected) =>
        Assert.Equal(expected, new PortForward { Kind = kind, BindAddress = bind, BindPort = 1080 }.IsOpenToNetwork);

    [Fact]
    public void UnknownMembers_FromANewerClient_SurviveCloneAndSave()
    {
        const string hostJson = """
            { "host": "a", "futureHost": 1,
              "options": { "keepAliveSeconds": 5, "futureOption": { "x": [1, 2] },
                           "environment": [ { "name": "A", "value": "1", "futureEnv": true } ] },
              "tunnels": [ { "kind": "dynamic", "bindPort": 1080, "futureTunnel": "y" } ] }
            """;
        HostEntry host = JsonSerializer.Deserialize<HostEntry>(hostJson, TgkJson.Options)!;
        HostEntry copy = host.Clone();
        copy.Name = "renamed";
        copy.Options.KeepAliveSeconds = 6;

        using JsonDocument saved = JsonDocument.Parse(JsonSerializer.Serialize(copy, TgkJson.Options));
        JsonElement root = saved.RootElement;
        Assert.Equal(1, root.GetProperty("futureHost").GetInt32());
        Assert.Equal(6, root.GetProperty("options").GetProperty("keepAliveSeconds").GetInt32());
        Assert.Equal(2, root.GetProperty("options").GetProperty("futureOption").GetProperty("x")[1].GetInt32());
        Assert.True(root.GetProperty("options").GetProperty("environment")[0].GetProperty("futureEnv").GetBoolean());
        Assert.Equal("y", root.GetProperty("tunnels")[0].GetProperty("futureTunnel").GetString());
        Assert.NotSame(host.Unknown, copy.Unknown);

        HostGroup group = JsonSerializer.Deserialize<HostGroup>("""{ "name": "g", "futureGroup": "z" }""", TgkJson.Options)!;
        Assert.Contains("\"futureGroup\": \"z\"", JsonSerializer.Serialize(group.Clone(), TgkJson.Options));
    }
}

public class EffectiveOptionsTests
{
    private static readonly Guid JumpA = Guid.NewGuid(), JumpB = Guid.NewGuid(), JumpC = Guid.NewGuid();

    [Fact]
    public void NothingSet_UsesBuiltInDefaults()
    {
        EffectiveHostOptions options = EffectiveOptions.Resolve(new VaultData(), new HostEntry { Host = "h" }, localFontSize: 13);

        Assert.Equal(new Resolved<Guid?>(null, OptionSource.Default), options.JumpHostId);
        Assert.Equal(new Resolved<int>(30, OptionSource.Default), options.KeepAliveSeconds);
        Assert.Equal(new Resolved<int>(15, OptionSource.Default), options.ConnectTimeoutSeconds);
        Assert.Equal(new Resolved<bool>(false, OptionSource.Default), options.AutoReconnect);
        Assert.Equal(new Resolved<string>("", OptionSource.Default), options.StartupCommand);
        Assert.Empty(options.Environment.Value);
        Assert.Equal(OptionSource.Default, options.Environment.Source);
        Assert.Equal(new Resolved<string>("xterm-256color", OptionSource.Default), options.TerminalType);
        Assert.Equal(new Resolved<float>(13, OptionSource.Default), options.FontSize);
        Assert.Equal(new Resolved<string>("TGK Dark", OptionSource.Default), options.ColorScheme);
        Assert.Equal(new Resolved<bool>(false, OptionSource.Default), options.LegacyAlgorithms);
        Assert.Equal(new Resolved<string>("DejaVu Sans Mono", OptionSource.Default), options.FontFamily);
        Assert.Equal(new Resolved<int>(10_000, OptionSource.Default), options.ScrollbackLines);
        Assert.Equal(new Resolved<string>(TerminalSettings.CursorBlock, OptionSource.Default), options.CursorShape);
        Assert.Equal(new Resolved<bool>(true, OptionSource.Default), options.CursorBlink);
        Assert.Equal(new Resolved<bool>(false, OptionSource.Default), options.CopyOnSelect);
        Assert.Null(options.GroupName);
        Assert.Equal(14, EffectiveOptions.Resolve(new VaultData(), new HostEntry()).FontSize.Value); // no local setting given
    }

    // Each level sets a distinct value for every field; the nearest level must win for each one.
    private static HostOptions Level(int n) => new()
    {
        JumpHostId = n switch { 1 => JumpA, 2 => JumpB, _ => JumpC },
        KeepAliveSeconds = n, ConnectTimeoutSeconds = 10 + n, AutoReconnect = n % 2 == 1,
        StartupCommand = $"cmd{n}", Environment = [new EnvVar { Name = $"V{n}", Value = "x" }], TerminalType = $"term{n}",
        FontSize = 10 + n, ColorScheme = $"scheme{n}", LegacyAlgorithms = n % 2 == 1, FontFamily = $"font{n}",
        ScrollbackLines = 100 * n, CursorShape = n switch { 1 => "bar", 2 => "underline", _ => "block" }, CursorBlink = n % 2 == 1, CopyOnSelect = n % 2 == 0,
    };

    public static TheoryData<int, OptionSource> Levels() => new()
    {
        { 1, OptionSource.Host },
        { 2, OptionSource.Group },
        { 3, OptionSource.Global },
    };

    [Theory]
    [MemberData(nameof(Levels))]
    public void NearestSetLevel_WinsForEveryField(int level, OptionSource expected)
    {
        var group = new HostGroup { Name = "Production", Options = level <= 2 ? Level(2) : new HostOptions() };
        var host = new HostEntry { Host = "h", GroupId = group.Id, Options = level == 1 ? Level(1) : new HostOptions() };
        var vault = new VaultData { Groups = [group], Hosts = [host], Defaults = Level(3) };

        EffectiveHostOptions options = EffectiveOptions.Resolve(vault, host);

        Assert.Equal(new Resolved<Guid?>(level switch { 1 => JumpA, 2 => JumpB, _ => JumpC }, expected), options.JumpHostId);
        Assert.Equal(new Resolved<int>(level, expected), options.KeepAliveSeconds);
        Assert.Equal(new Resolved<int>(10 + level, expected), options.ConnectTimeoutSeconds);
        Assert.Equal(new Resolved<bool>(level % 2 == 1, expected), options.AutoReconnect);
        Assert.Equal(new Resolved<string>($"cmd{level}", expected), options.StartupCommand);
        Assert.Equal(($"V{level}", expected), (Assert.Single(options.Environment.Value).Name, options.Environment.Source));
        Assert.Equal(new Resolved<string>($"term{level}", expected), options.TerminalType);
        Assert.Equal(new Resolved<float>(10 + level, expected), options.FontSize);
        Assert.Equal(new Resolved<string>($"scheme{level}", expected), options.ColorScheme);
        Assert.Equal(new Resolved<bool>(level % 2 == 1, expected), options.LegacyAlgorithms);
        Assert.Equal(new Resolved<string>($"font{level}", expected), options.FontFamily);
        Assert.Equal(new Resolved<int>(100 * level, expected), options.ScrollbackLines);
        Assert.Equal(new Resolved<string>(level switch { 1 => "bar", 2 => "underline", _ => "block" }, expected), options.CursorShape);
        Assert.Equal(new Resolved<bool>(level % 2 == 1, expected), options.CursorBlink);
        Assert.Equal(new Resolved<bool>(level % 2 == 0, expected), options.CopyOnSelect);
        TerminalSettings terminal = EffectiveOptions.Terminal(options);
        Assert.Equal(($"font{level}", 100 * level, 10f + level), (terminal.FontFamily, terminal.ScrollbackLines, terminal.FontSize));
        Assert.Equal("Production", options.GroupName);
    }

    [Fact]
    public void Fields_ResolveIndependently_AndFalsyValuesStillOverride()
    {
        var group = new HostGroup { Name = "g", Options = new HostOptions { KeepAliveSeconds = 0, StartupCommand = "group-cmd" } };
        var host = new HostEntry { Host = "h", GroupId = group.Id, Options = new HostOptions { StartupCommand = "", LegacyAlgorithms = false } };
        var vault = new VaultData { Groups = [group], Hosts = [host], Defaults = new HostOptions { KeepAliveSeconds = 60, LegacyAlgorithms = true, Environment = [] } };

        EffectiveHostOptions options = EffectiveOptions.Resolve(vault, host);

        Assert.Equal(new Resolved<int>(0, OptionSource.Group), options.KeepAliveSeconds);
        Assert.Equal(new Resolved<string>("", OptionSource.Host), options.StartupCommand);
        Assert.Equal(new Resolved<bool>(false, OptionSource.Host), options.LegacyAlgorithms);
        Assert.Equal(OptionSource.Global, options.Environment.Source); // an empty list is a value, not "inherit"
        Assert.Equal(OptionSource.Default, options.ConnectTimeoutSeconds.Source);
    }

    [Fact]
    public void MissingGroup_IsSkipped_AndExplicitLevelsResolveToo()
    {
        var host = new HostEntry { Host = "h", GroupId = Guid.NewGuid() };
        var vault = new VaultData { Hosts = [host], Defaults = new HostOptions { TerminalType = "vt100" } };

        Assert.Equal(new Resolved<string>("vt100", OptionSource.Global), EffectiveOptions.Resolve(vault, host).TerminalType);
        Assert.Null(EffectiveOptions.Resolve(vault, host).GroupName);
        // What a group inherits (e.g. for its editor's placeholders).
        Assert.Equal(new Resolved<string>("vt100", OptionSource.Global), EffectiveOptions.Resolve(null, null, vault.Defaults).TerminalType);
    }

    [Fact]
    public void InheritedJumpToItself_IsSkipped()
    {
        var bastion = new HostEntry { Name = "bastion", Host = "10.0.0.1" };
        var gateway = new HostEntry { Name = "gateway", Host = "10.0.0.2" };
        var group = new HostGroup { Name = "Production", Options = new HostOptions { JumpHostId = bastion.Id } };
        bastion.GroupId = group.Id;
        var web = new HostEntry { Name = "web", Host = "10.0.1.5", GroupId = group.Id };
        var vault = new VaultData { Groups = [group], Hosts = [bastion, gateway, web] };

        Assert.Null(EffectiveOptions.Resolve(vault, bastion).JumpHostId.Value);
        Assert.Equal(bastion.Id, EffectiveOptions.Resolve(vault, web).JumpHostId.Value);
        Assert.Equal([bastion], EffectiveOptions.ResolveJumpChain(vault, web, out string? error));
        Assert.Null(error);

        // With a global jump host the bastion is reached through that one instead.
        vault.Defaults = new HostOptions { JumpHostId = gateway.Id };
        Assert.Equal(new Resolved<Guid?>(gateway.Id, OptionSource.Global), EffectiveOptions.Resolve(vault, bastion).JumpHostId);
        Assert.Equal([gateway, bastion], EffectiveOptions.ResolveJumpChain(vault, web, out _));
        Assert.Empty(EffectiveOptions.ResolveJumpChain(vault, gateway, out error));
        Assert.Null(error);
    }

    [Fact]
    public void NoJumpHost_OverridesAnInheritedJumpHost()
    {
        var bastion = new HostEntry { Name = "bastion", Host = "10.0.0.1" };
        var group = new HostGroup { Name = "Production", Options = new HostOptions { JumpHostId = bastion.Id } };
        var direct = new HostEntry { Name = "direct", Host = "10.0.1.6", GroupId = group.Id, Options = new HostOptions { JumpHostId = HostOptions.NoJumpHost } };
        var vault = new VaultData { Groups = [group], Hosts = [bastion, direct] };

        Assert.Null(direct.Options.Validate());
        Assert.Equal(new Resolved<Guid?>(null, OptionSource.Host), EffectiveOptions.Resolve(vault, direct).JumpHostId);
        Assert.Empty(EffectiveOptions.ResolveJumpChain(vault, direct, out string? error));
        Assert.Null(error);
    }

    private static (VaultData Vault, List<HostEntry> Hosts) Chain(int length)
    {
        // hosts[0] jumps through hosts[1], which jumps through hosts[2], ...
        List<HostEntry> hosts = Enumerable.Range(0, length).Select(i => new HostEntry { Name = $"h{i}", Host = $"10.0.0.{i}" }).ToList();
        for (int i = 0; i < length - 1; i++)
            hosts[i].Options.JumpHostId = hosts[i + 1].Id;
        return (new VaultData { Hosts = hosts }, hosts);
    }

    [Fact]
    public void JumpChain_IsOutermostFirst_UpToFourHops()
    {
        var (vault, hosts) = Chain(5);

        IReadOnlyList<HostEntry> chain = EffectiveOptions.ResolveJumpChain(vault, hosts[0], out string? error);

        Assert.Null(error);
        Assert.Equal(["h4", "h3", "h2", "h1"], chain.Select(h => h.Name));
        Assert.Empty(EffectiveOptions.ResolveJumpChain(vault, hosts[4], out error));
        Assert.Null(error);
    }

    [Fact]
    public void JumpChain_RejectsTooManyHops()
    {
        var (vault, hosts) = Chain(6);

        Assert.Empty(EffectiveOptions.ResolveJumpChain(vault, hosts[0], out string? error));
        Assert.Equal("h0 goes through more than 4 jump hosts.", error);
    }

    [Fact]
    public void JumpChain_RejectsLoops()
    {
        var (vault, hosts) = Chain(3);
        hosts[2].Options.JumpHostId = hosts[1].Id;

        Assert.Empty(EffectiveOptions.ResolveJumpChain(vault, hosts[0], out string? error));
        Assert.Equal("The jump hosts form a loop: h0 → h1 → h2 → h1.", error);
    }

    [Fact]
    public void NewJumpChainProblem_FindsALoopAGroupEditCreates()
    {
        var staging = new HostGroup { Name = "Staging" };
        var web = new HostEntry { Name = "staging-web", Host = "10.0.0.1", GroupId = staging.Id };
        var vps = new HostEntry { Name = "vps", Host = "10.0.0.2" };
        vps.Options.JumpHostId = web.Id;
        var before = new VaultData { Hosts = [web, vps], Groups = [staging] };

        // The group's hosts now go through vps, which goes through staging-web (a host of the group).
        VaultData after = before.ShallowCopy();
        HostGroup edited = staging.Clone();
        edited.Options.JumpHostId = vps.Id;
        after.Groups[0] = edited;

        Assert.Equal("The jump hosts form a loop: staging-web → vps → staging-web.", EffectiveOptions.NewJumpChainProblem(before, after));
        Assert.Null(EffectiveOptions.NewJumpChainProblem(before, before));
    }

    [Fact]
    public void NewJumpChainProblem_FindsATooLongChainTheDefaultsCreate()
    {
        var (before, _) = Chain(5); // h0 goes through four jump hosts
        var bastion = new HostEntry { Name = "bastion", Host = "10.0.1.1" };
        before.Hosts.Add(bastion);
        VaultData after = before.ShallowCopy();
        after.Defaults = new HostOptions { JumpHostId = bastion.Id }; // h4 (and so h0) now also goes through bastion

        Assert.Equal("h0 goes through more than 4 jump hosts.", EffectiveOptions.NewJumpChainProblem(before, after));
    }

    [Fact]
    public void NewJumpChainProblem_IgnoresOlderProblemsAndDeletedJumpHosts()
    {
        var (before, hosts) = Chain(3);
        hosts[2].Options.JumpHostId = hosts[1].Id; // a loop saved earlier
        var other = new HostEntry { Name = "other", Host = "10.0.2.1" };
        before.Hosts.Add(other);

        VaultData after = before.ShallowCopy();
        HostEntry edited = other.Clone();
        edited.Options.JumpHostId = Guid.NewGuid(); // a host that does not exist (it was deleted)
        after.Hosts[^1] = edited;

        Assert.Null(EffectiveOptions.NewJumpChainProblem(before, after));
    }

    [Fact]
    public void JumpChain_RejectsDeletedJumpHost()
    {
        var (vault, hosts) = Chain(3);
        vault.Hosts.Remove(hosts[2]);

        Assert.Empty(EffectiveOptions.ResolveJumpChain(vault, hosts[0], out string? error));
        Assert.StartsWith("The jump host of h1 no longer exists.", error);
    }

    [Fact]
    public void ConnectRequest_ForVaultHost_CarriesChainOptionsAndTunnels()
    {
        var bastionKey = new Identity { Name = "k", Username = "jump", AuthKind = AuthKind.PrivateKey, PrivateKey = "KEY" };
        var webLogin = new Identity { Name = "p", Username = "deploy", Password = "pw" };
        var bastion = new HostEntry { Name = "bastion", Host = "10.0.0.1", Port = 2200, IdentityId = bastionKey.Id, Options = new HostOptions { LegacyAlgorithms = true, KeepAliveSeconds = 0 } };
        var web = new HostEntry
        {
            Name = "web", Host = " web.internal ", IdentityId = webLogin.Id,
            Options = new HostOptions { JumpHostId = bastion.Id, StartupCommand = "uptime", Environment = [new EnvVar { Name = "A", Value = "1" }] },
            Tunnels =
            [
                new PortForward { Kind = ForwardKind.Local, BindPort = 8080, DestinationHost = "db", DestinationPort = 5432 },
                new PortForward { Kind = ForwardKind.Dynamic, BindPort = 1080, Enabled = false },
            ],
        };
        var vault = new VaultData
        {
            Hosts = [bastion, web], Identities = [bastionKey, webLogin],
            Defaults = new HostOptions { ConnectTimeoutSeconds = 20, TerminalType = "screen-256color" },
        };

        SshConnectRequest request = SshConnectRequest.ForHost(vault, web, 100, 30);

        Assert.Null(request.Validate());
        Assert.Equal(("web.internal", 22, "deploy", "pw", 100, 30), (request.Host, request.Port, request.Username, request.Password, request.Cols, request.Rows));
        Assert.Equal(("uptime", "screen-256color", TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30), false),
            (request.StartupCommand, request.TermType, request.ConnectTimeout, request.KeepAlive, request.LegacyAlgorithms));
        Assert.Equal("A", Assert.Single(request.Environment).Name);
        Assert.Equal(8080, Assert.Single(request.Tunnels).BindPort);

        SshConnectRequest hop = Assert.Single(request.JumpChain);
        Assert.Equal(("10.0.0.1", 2200, "jump", "KEY", (string?)null), (hop.Host, hop.Port, hop.Username, hop.PrivateKey, hop.Password));
        Assert.Equal((true, TimeSpan.Zero, TimeSpan.FromSeconds(20)), (hop.LegacyAlgorithms, hop.KeepAlive, hop.ConnectTimeout));
        Assert.Equal("bastion (10.0.0.1)", hop.Label);
    }

    [Fact]
    public void ConnectRequest_ForVaultHost_ReportsJumpProblems()
    {
        var (vault, hosts) = Chain(2);
        hosts[1].Options.JumpHostId = hosts[0].Id;

        var ex = Assert.Throws<SshSessionException>(() => SshConnectRequest.ForHost(vault, hosts[0]));

        Assert.Equal(SshErrorKind.InvalidRequest, ex.Kind);
        Assert.Contains("loop", ex.Message);
    }
}
