using System;
using System.Collections.Generic;
using TGK.Core.Models;
using TGK.Core.Services;
using TGK.Protocol;
using Xunit;

namespace TGK.Core.Tests;

public sealed class WorkspaceTests
{
    private static readonly Guid SavedHost = Guid.NewGuid();

    /// <summary>A saved host, a quick-connect address and a new-tab page; tabs 1 and 2 side by side, 2 maximized.</summary>
    internal static Workspace Sample() => new()
    {
        RestoreTabs = true,
        Tabs =
        [
            new WorkspaceTab { HostId = SavedHost },
            new WorkspaceTab { Host = "10.0.0.5", Port = 2222, Username = "root" },
            new WorkspaceTab(),
        ],
        ActiveTab = 2,
        Splits =
        [
            new WorkspaceSplit
            {
                Root = new WorkspacePane { Children = [new WorkspacePane { Tab = 1 }, new WorkspacePane { Tab = 2 }], Weights = [2, 1] },
                Zoomed = 2,
            },
        ],
        SavedAt = DateTimeOffset.UnixEpoch,
        SavedOn = "laptop · Linux",
    };

    private static WorkspacePane Pane(int tab) => new() { Tab = tab };

    private static WorkspacePane Split(params WorkspacePane[] children) => new() { Children = [.. children] };

    [Fact]
    public void Sample_IsValid_AndClonesDeeply()
    {
        Workspace workspace = Sample();
        Assert.Null(workspace.Validate());
        Assert.False(workspace.IsEmpty);
        Assert.True(new Workspace().IsEmpty);

        Workspace copy = workspace.Clone();
        copy.Tabs[1].Host = "changed";
        copy.Splits[0].Root.Children![0].Tab = 0;
        copy.Splits[0].Root.Weights![0] = 5;
        Assert.Equal("10.0.0.5", workspace.Tabs[1].Host);
        Assert.Equal(1, workspace.Splits[0].Root.Children![0].Tab);
        Assert.Equal(2, workspace.Splits[0].Root.Weights![0]);
        Assert.Equivalent(workspace, workspace.Clone());
    }

    public static IEnumerable<TheoryDataRow<string, Action<Workspace>>> Invalid() =>
    [
        new("active out of range", w => w.ActiveTab = 3),
        new("host and address", w => w.Tabs[0].Host = "x"),
        new("bad port", w => w.Tabs[1].Port = 0),
        new("long address", w => w.Tabs[1].Host = new string('a', Workspace.MaxAddressLength + 1)),
        new("too many tabs", w => { while (w.Tabs.Count <= Workspace.MaxTabs) w.Tabs.Add(new WorkspaceTab()); }),
        new("pane out of range", w => w.Splits[0].Root.Children![1].Tab = 7),
        new("one-pane split", w => w.Splits[0].Root = Split(Pane(1))),
        new("pane and children", w => w.Splits[0].Root.Tab = 0),
        new("weights mismatch", w => w.Splits[0].Root.Weights = [1]),
        new("negative weight", w => w.Splits[0].Root.Weights = [1, -1]),
        new("tab in two panes", w => w.Splits[0].Root = Split(Pane(1), Pane(1))),
        new("tab in two splits", w => w.Splits.Add(new WorkspaceSplit { Root = Split(Pane(0), Pane(1)) })),
        new("zoomed elsewhere", w => w.Splits[0].Zoomed = 0),
        new("null split", w => w.Splits.Add(null!)),
    ];

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Validate_RejectsMalformedWorkspaces(string name, Action<Workspace> damage)
    {
        Workspace workspace = Sample();
        damage(workspace);
        Assert.True(workspace.Validate() is not null, name);
        Assert.Throws<ArgumentException>(() => VaultEdits.SaveWorkspace(workspace));
    }

    [Fact]
    public void Validate_RejectsLayoutsNestedTooDeep()
    {
        var workspace = new Workspace { Tabs = [new WorkspaceTab(), new WorkspaceTab()] };
        WorkspacePane root = Split(Pane(0), Pane(1));
        for (int i = 0; i < 40; i++)
            root = Split(root, Pane(0));
        workspace.Splits.Add(new WorkspaceSplit { Root = root });
        Assert.NotNull(workspace.Validate());
    }

    [Fact]
    public void WorkspaceItem_RoundTripsWithAKeyDerivedId()
    {
        byte[] key = VaultCrypto.NewVaultKey();
        var items = new VaultItems(key);
        Workspace workspace = Sample();
        string id = items.IdOf(workspace);
        Assert.Equal(items.WorkspaceId, id);
        Assert.NotEqual(items.SettingsId, id);
        Assert.NotEqual(new VaultItems(VaultCrypto.NewVaultKey()).WorkspaceId, id);

        var vault = new VaultData();
        items.Apply(vault, id, items.Encrypt(id, workspace));
        Assert.Equivalent(workspace, vault.Workspace);
        Assert.Equal(ItemKinds.Workspace, VaultCrypto.DecryptItem(key, id, items.Encrypt(id, workspace)).Kind);

        items.Apply(vault, id, null);
        Assert.True(vault.Workspace.IsEmpty);
    }

    [Fact]
    public void Diff_ReportsAReplacedWorkspace()
    {
        var items = new VaultItems(VaultCrypto.NewVaultKey());
        var before = new VaultData();
        VaultData after = before.ShallowCopy();
        Assert.Empty(items.Diff(before, after));

        VaultEdits.SaveWorkspace(Sample())(after);
        ItemChange change = Assert.Single(items.Diff(before, after));
        Assert.Equal(items.WorkspaceId, change.Id);
        Assert.Same(after.Workspace, change.Entity);
    }

    [Fact]
    public void Merge_KeepsTheTargetsWorkspace()
    {
        var target = new VaultData();
        var incoming = new VaultData { Workspace = Sample() };
        VaultMerge.Merge(target, incoming);
        Assert.True(target.Workspace.IsEmpty);
    }
}
