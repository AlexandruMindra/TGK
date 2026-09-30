using System;
using System.Linq;
using SkiaSharp;
using TGK.Client.Main;
using Xunit;

namespace TGK.Client.Tests;

public class PaneLayoutTests
{
    private static readonly SKRect Area = new(0, 0, 1000, 600);
    private const float Gap = 6;

    private static SKRect RectOf(PaneLayout<string> layout, string item, SKRect? area = null) =>
        layout.Arrange(area ?? Area, Gap).Panes.Single(p => p.Item == item).Rect;

    [Theory]
    [InlineData(LayoutPreset.Columns2, 2)]
    [InlineData(LayoutPreset.Rows2, 2)]
    [InlineData(LayoutPreset.Columns3, 3)]
    [InlineData(LayoutPreset.MainLeft, 3)]
    [InlineData(LayoutPreset.MainTop, 3)]
    [InlineData(LayoutPreset.Grid2x2, 4)]
    [InlineData(LayoutPreset.Grid3x2, 6)]
    public void Presets_tile_the_area_without_overlap(LayoutPreset preset, int count)
    {
        string[] items = Enumerable.Range(0, count).Select(i => $"p{i}").ToArray();
        var layout = PaneLayout<string>.FromPreset(preset, items);

        var arranged = layout.Arrange(Area, Gap);

        Assert.Equal(items, layout.Items);
        Assert.Equal(preset, layout.Preset);
        Assert.Equal(count, arranged.Panes.Count);
        float area = 0;
        foreach (var pane in arranged.Panes)
        {
            Assert.True(pane.Rect.Width > 0 && pane.Rect.Height > 0);
            Assert.True(Area.Contains(pane.Rect));
            area += pane.Rect.Width * pane.Rect.Height;
            foreach (var other in arranged.Panes.Where(o => o.Item != pane.Item))
                Assert.False(SKRect.Intersect(pane.Rect, other.Rect) is { Width: > 0, Height: > 0 }, $"{pane.Item} overlaps {other.Item}");
        }
        foreach (var divider in arranged.Dividers)
            area += divider.Rect.Width * divider.Rect.Height;
        // Panes plus gaps cover the area, except where two dividers cross (grids).
        Assert.InRange(area, Area.Width * Area.Height - 4 * Gap * Gap, Area.Width * Area.Height);
    }

    [Fact]
    public void Wrong_item_count_for_a_preset_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => PaneLayout<string>.FromPreset(LayoutPreset.Grid2x2, ["a", "b", "c"]));
        Assert.Throws<ArgumentException>(() => PaneLayout<string>.FromPreset(LayoutPreset.Columns2, ["a", "a"]));
    }

    [Fact]
    public void Columns_share_the_width_on_whole_pixels()
    {
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.Columns3, ["a", "b", "c"]);

        var panes = layout.Arrange(new SKRect(0, 0, 1001, 500), Gap).Panes;

        Assert.Equal(new SKRect(0, 0, 330, 500), panes[0].Rect);
        Assert.Equal(new SKRect(336, 0, 665, 500), panes[1].Rect);
        Assert.Equal(new SKRect(671, 0, 1001, 500), panes[2].Rect);
    }

    [Fact]
    public void Main_left_has_a_full_height_pane_and_a_stack()
    {
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.MainLeft, ["main", "top", "bottom"]);

        Assert.Equal(new SKRect(0, 0, 497, 600), RectOf(layout, "main"));
        Assert.Equal(new SKRect(503, 0, 1000, 297), RectOf(layout, "top"));
        Assert.Equal(new SKRect(503, 303, 1000, 600), RectOf(layout, "bottom"));
    }

    [Fact]
    public void Splitting_in_the_parents_direction_adds_a_sibling_with_half_the_space()
    {
        var layout = new PaneLayout<string>("a");
        layout.SplitAt("a", "b", SplitOrientation.Horizontal);
        layout.SplitAt("b", "c", SplitOrientation.Horizontal);

        Assert.Equal(["a", "b", "c"], layout.Items);
        var root = Assert.IsType<PaneLayout<string>.Split>(layout.Root);
        Assert.Equal(3, root.Children.Count);
        Assert.Equal(new[] { 1f, 0.5f, 0.5f }, root.Weights);
    }

    [Fact]
    public void Splitting_across_nests_a_new_split_in_the_panes_place()
    {
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.Columns2, ["a", "b"]);
        layout.SplitAt("b", "c", SplitOrientation.Vertical);

        Assert.Equal(["a", "b", "c"], layout.Items);
        Assert.Null(layout.Preset);
        Assert.Equal(new SKRect(0, 0, 497, 600), RectOf(layout, "a"));
        Assert.Equal(new SKRect(503, 0, 1000, 297), RectOf(layout, "b"));
        Assert.Equal(new SKRect(503, 303, 1000, 600), RectOf(layout, "c"));
    }

    [Fact]
    public void Splitting_before_puts_the_new_pane_first()
    {
        var layout = new PaneLayout<string>("a");
        layout.SplitAt("a", "b", SplitOrientation.Vertical, after: false);

        Assert.Equal(["b", "a"], layout.Items);
        Assert.Equal(0, RectOf(layout, "b").Top);
    }

    [Fact]
    public void Splitting_rejects_unknown_targets_and_duplicates()
    {
        var layout = new PaneLayout<string>("a");

        Assert.Throws<ArgumentException>(() => layout.SplitAt("x", "b", SplitOrientation.Horizontal));
        Assert.Throws<ArgumentException>(() => layout.SplitAt("a", "a", SplitOrientation.Horizontal));
    }

    [Fact]
    public void Removing_a_pane_collapses_its_split()
    {
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.Columns2, ["a", "b"]);
        layout.SplitAt("b", "c", SplitOrientation.Vertical);

        Assert.True(layout.Remove("c"));

        Assert.Equal(["a", "b"], layout.Items);
        var root = Assert.IsType<PaneLayout<string>.Split>(layout.Root);
        Assert.All(root.Children, child => Assert.IsType<PaneLayout<string>.Leaf>(child));
        Assert.Equal(new SKRect(503, 0, 1000, 600), RectOf(layout, "b"));
    }

    [Fact]
    public void Removing_flattens_a_remaining_split_into_a_parent_of_the_same_direction()
    {
        // a | (b / (c | d)): removing b leaves (c | d) inside a row, which joins it: a | c | d.
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.Columns2, ["a", "b"]);
        layout.SplitAt("b", "c", SplitOrientation.Vertical);
        layout.SplitAt("c", "d", SplitOrientation.Horizontal);

        Assert.True(layout.Remove("b"));

        var root = Assert.IsType<PaneLayout<string>.Split>(layout.Root);
        Assert.Equal(SplitOrientation.Horizontal, root.Orientation);
        Assert.Equal(3, root.Children.Count);
        Assert.Equal(["a", "c", "d"], layout.Items);
        Assert.Equal(1f, root.Weights[0]);
        Assert.Equal(1f, root.Weights[1] + root.Weights[2], 3);
    }

    [Fact]
    public void The_last_pane_cannot_be_removed()
    {
        var layout = new PaneLayout<string>("a");

        Assert.False(layout.Remove("a"));
        Assert.False(layout.Remove("missing"));
        Assert.Equal(["a"], layout.Items);
    }

    [Fact]
    public void Replacing_keeps_the_place_and_the_zoom()
    {
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.Rows2, ["a", "b"]);
        layout.Zoomed = "b";

        layout.Replace("b", "z");

        Assert.Equal(["a", "z"], layout.Items);
        Assert.Equal("z", layout.Zoomed);
        Assert.Equal(LayoutPreset.Rows2, layout.Preset);
    }

    [Fact]
    public void A_zoomed_pane_gets_the_whole_area()
    {
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.Grid2x2, ["a", "b", "c", "d"]);
        layout.Zoomed = "c";

        var arranged = layout.Arrange(Area, Gap);

        Assert.Equal([new PaneLayout<string>.PaneRect("c", Area)], arranged.Panes);
        Assert.Empty(arranged.Dividers);
        Assert.Throws<ArgumentException>(() => layout.Zoomed = "missing");
    }

    [Fact]
    public void Removing_the_zoomed_pane_unzooms()
    {
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.Columns3, ["a", "b", "c"]);
        layout.Zoomed = "b";

        layout.Remove("b");

        Assert.Null(layout.Zoomed);
    }

    [Fact]
    public void Moving_a_divider_resizes_its_neighbours_within_limits()
    {
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.Columns2, ["a", "b"]);
        var divider = layout.Arrange(Area, Gap).Dividers.Single();

        Assert.True(layout.MoveDivider(divider.Split, divider.Index, 100, 120));
        Assert.Equal(597, RectOf(layout, "a").Right);

        Assert.True(layout.MoveDivider(divider.Split, divider.Index, 10_000, 120));
        Assert.Equal(120, RectOf(layout, "b").Width, 1);

        Assert.False(layout.MoveDivider(divider.Split, divider.Index, 50, 120)); // already at the limit
        PaneLayout<string>.Equalize(divider.Split);
        Assert.Equal(497, RectOf(layout, "a").Right);
    }

    [Theory]
    [InlineData("a", PaneDirection.Right, "b")]
    [InlineData("b", PaneDirection.Left, "a")]
    [InlineData("a", PaneDirection.Down, "c")]
    [InlineData("d", PaneDirection.Up, "b")]
    [InlineData("d", PaneDirection.Left, "c")]
    [InlineData("a", PaneDirection.Left, null)]
    [InlineData("a", PaneDirection.Up, null)]
    public void Neighbors_follow_the_grid(string from, PaneDirection direction, string? expected)
    {
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.Grid2x2, ["a", "b", "c", "d"]);

        Assert.Equal(expected, PaneLayout<string>.Neighbor(layout.Arrange(Area, Gap).Panes, from, direction));
    }

    [Fact]
    public void The_neighbor_with_the_largest_overlap_wins()
    {
        // main | (top / bottom) with the stack's divider moved down: from main, Right goes to the taller top pane.
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.MainLeft, ["main", "top", "bottom"]);
        var stack = layout.Arrange(Area, Gap).Dividers.Single(d => d.Split.Orientation == SplitOrientation.Vertical);
        layout.MoveDivider(stack.Split, stack.Index, 150, 50);

        var panes = layout.Arrange(Area, Gap).Panes;

        Assert.Equal("top", PaneLayout<string>.Neighbor(panes, "main", PaneDirection.Right));
        Assert.Equal("main", PaneLayout<string>.Neighbor(panes, "bottom", PaneDirection.Left));
    }

    [Fact]
    public void Swap_exchanges_places_and_keeps_sizes()
    {
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.MainLeft, ["a", "b", "c"]);
        SKRect big = RectOf(layout, "a"), small = RectOf(layout, "c");

        layout.Swap("a", "c");

        Assert.Equal(["c", "b", "a"], layout.Items);
        Assert.Equal(big, RectOf(layout, "c"));
        Assert.Equal(small, RectOf(layout, "a"));
        Assert.Equal(LayoutPreset.MainLeft, layout.Preset);
        Assert.Throws<ArgumentException>(() => layout.Swap("a", "x"));
    }
}
