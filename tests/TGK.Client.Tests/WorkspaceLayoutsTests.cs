using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using TGK.Client.Main;
using TGK.Core.Models;
using Xunit;

namespace TGK.Client.Tests;

public class WorkspaceLayoutsTests
{
    private static readonly SKRect Area = new(0, 0, 1000, 600);
    private static readonly string[] Tabs = ["a", "b", "c", "d"];

    private static int? IndexOf(string item) => Tabs.ToList().IndexOf(item) is >= 0 and var i ? i : null;

    private static string? Resolve(int index) => index >= 0 && index < Tabs.Length ? Tabs[index] : null;

    private static List<PaneLayout<string>.PaneRect> Panes(PaneLayout<string> layout) => [.. layout.Arrange(Area, 6).Panes];

    [Fact]
    public void A_layout_survives_the_round_trip_with_its_sizes_and_zoom()
    {
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.MainLeft, ["a", "b", "c"]);
        var divider = layout.Arrange(Area, 6).Dividers.First(d => d.Split.Orientation == SplitOrientation.Horizontal);
        layout.MoveDivider(divider.Split, divider.Index, 150, 100);
        layout.Zoomed = "c";

        WorkspaceSplit saved = WorkspaceLayouts.Export(layout, IndexOf)!;
        PaneLayout<string> restored = WorkspaceLayouts.Import<string>(saved, Resolve)!;

        Assert.Equal(["a", "b", "c"], restored.Items);
        Assert.Equal("c", restored.Zoomed);
        Assert.False(saved.Root.Stacked);
        Assert.True(saved.Root.Children![1].Stacked);
        layout.Zoomed = restored.Zoomed = null;
        Assert.Equal(Panes(layout), Panes(restored));
    }

    [Fact]
    public void Unsaved_panes_are_left_out_and_their_splits_collapse()
    {
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.MainLeft, ["a", "b", "x"]); // "x" is not saved

        WorkspaceSplit saved = WorkspaceLayouts.Export(layout, IndexOf)!;

        Assert.Equal(0, saved.Root.Children![0].Tab);
        Assert.Equal(1, saved.Root.Children![1].Tab);
        Assert.Null(WorkspaceLayouts.Export(PaneLayout<string>.FromPreset(LayoutPreset.Columns2, ["a", "x"]), IndexOf));
    }

    [Fact]
    public void Tabs_that_are_gone_are_left_out_when_restoring()
    {
        WorkspaceSplit saved = WorkspaceLayouts.Export(PaneLayout<string>.FromPreset(LayoutPreset.Grid2x2, ["a", "b", "c", "d"]), IndexOf)!;

        PaneLayout<string>? restored = WorkspaceLayouts.Import<string>(saved, i => i == 1 ? null : Resolve(i));
        Assert.Equal(["a", "c", "d"], restored!.Items);
        Assert.Equal(3, Panes(restored).Count);

        Assert.Null(WorkspaceLayouts.Import<string>(saved, i => i == 0 ? "a" : null)); // one pane is no split view
    }

    [Fact]
    public void Unusable_saved_layouts_restore_nothing()
    {
        var twice = new WorkspaceSplit { Root = new WorkspacePane { Children = [new() { Tab = 0 }, new() { Tab = 0 }] } };
        var empty = new WorkspaceSplit { Root = new WorkspacePane() };

        Assert.Null(WorkspaceLayouts.Import<string>(twice, Resolve));
        Assert.Null(WorkspaceLayouts.Import<string>(empty, Resolve));
    }

    [Fact]
    public void FromTree_sets_parents_and_collapses_single_child_splits()
    {
        var inner = new PaneLayout<string>.Split(SplitOrientation.Vertical);
        inner.Children.Add(new PaneLayout<string>.Leaf("b"));
        inner.Weights.Add(1);
        var root = new PaneLayout<string>.Split(SplitOrientation.Horizontal);
        root.Children.Add(new PaneLayout<string>.Leaf("a"));
        root.Children.Add(inner);
        root.Weights.AddRange([1, 1]);

        PaneLayout<string> layout = PaneLayout<string>.FromTree(root)!;

        Assert.Equal(["a", "b"], layout.Items);
        var top = Assert.IsType<PaneLayout<string>.Split>(layout.Root);
        Assert.All(top.Children, child => Assert.Same(top, child.Parent));
        Assert.IsType<PaneLayout<string>.Leaf>(top.Children[1]);
        layout.SplitAt("b", "c", SplitOrientation.Vertical); // still a working layout
        Assert.Equal(["a", "b", "c"], layout.Items);
    }
}
