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

    // ---- drop spots and Insert ----

    private const float Band = 80;

    private static PaneLayout<string>.Leaf LeafOf(PaneLayout<string> layout, string item)
    {
        PaneLayout<string>.Leaf? Find(PaneLayout<string>.Node node) => node switch
        {
            PaneLayout<string>.Leaf leaf => leaf.Item == item ? leaf : null,
            PaneLayout<string>.Split split => split.Children.Select(Find).FirstOrDefault(found => found is not null),
            _ => null,
        };
        return Find(layout.Root) ?? throw new ArgumentException(item);
    }

    private static string Describe(PaneLayout<string>.Placement p)
    {
        string node = p.Node is PaneLayout<string>.Leaf leaf ? leaf.Item : "split";
        return p.IsBetween ? $"between {p.Gap}" : $"{node} {p.Side}";
    }

    private static PaneLayout<string>.DropSpot SpotFor(PaneLayout<string> layout, string description, string? dragged = null) =>
        layout.DropSpots(Area, Gap, Band, dragged).Single(s => Describe(s.Placement) == description);

    // The spot whose zone picks up point (x, y), if any.
    private static PaneLayout<string>.DropSpot? SpotAt(PaneLayout<string> layout, float x, float y, string? dragged = null)
    {
        var spots = layout.DropSpots(Area, Gap, Band, dragged);
        int index = PaneDropZone.Pick(spots.Select(s => s.Zone).ToList(), new SKPoint(x, y));
        return index < 0 ? null : spots[index];
    }

    private static string? DescribeAt(PaneLayout<string> layout, float x, float y, string? dragged = null) =>
        SpotAt(layout, x, y, dragged) is { } spot ? Describe(spot.Placement) : null;

    [Fact]
    public void Stacked_panes_offer_their_outer_edges_and_the_dividers_ends_and_middle()
    {
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.Rows2, ["a", "b"]);

        var spots = layout.DropSpots(Area, Gap, Band, null);

        Assert.Equal(["a Up", "a Left", "a Right", "b Left", "b Right", "b Down", "between 0", "split Left", "split Right"],
            spots.Select(s => Describe(s.Placement)));
        Assert.Equal(new SKRect(0, 0, 1000, 80), spots[0].Zone.Rect);
        Assert.Equal(new SKRect(920, 0, 1000, 297), spots[2].Zone.Rect);
        Assert.Equal(new SKRect(0, 217, 1000, 383), spots[6].Zone.Rect); // the divider is 297..303
        Assert.Equal(new SKRect(920, 217, 1000, 383), spots[8].Zone.Rect);
    }

    [Theory]
    [InlineData(990, 100, "a Right")]
    [InlineData(990, 290, "split Right")] // the end of the divider wins over the edge and the divider
    [InlineData(500, 290, "between 0")]
    [InlineData(500, 590, "b Down")]
    [InlineData(20, 10, "a Up")] // a corner goes to the nearer edge
    [InlineData(10, 40, "a Left")]
    [InlineData(500, 150, null)] // the middle of a pane is left to the caller (swap, take its place)
    public void Zones_pick_the_spot_under_the_pointer(float x, float y, string? expected)
    {
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.Rows2, ["a", "b"]);

        Assert.Equal(expected, DescribeAt(layout, x, y));
    }

    [Fact]
    public void In_a_grid_a_shared_edge_is_offered_once_by_the_divider_between()
    {
        // (a | b) / (c | d): the rows' dividers end at the outer edges only; their inner ends would meet the divider
        // between the rows, which is offered instead.
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.Grid2x2, ["a", "b", "c", "d"]);
        var root = (PaneLayout<string>.Split)layout.Root;

        var spots = layout.DropSpots(Area, Gap, Band, null).Select(s => Describe(s.Placement)).ToList();

        Assert.Equal(19, spots.Count); // 3 edges per pane, a + and both ends for the rows' divider, a + and one end for each row's
        Assert.Single(spots, s => s == "split Up");
        Assert.Single(spots, s => s == "split Down");
        Assert.DoesNotContain("a Right", spots); // a faces b: the row's divider instead
        Assert.Same(root, SpotAt(layout, 500, 300)?.Placement.Node);
        Assert.Same(root.Children[0], SpotAt(layout, 500, 150)?.Placement.Node);
        Assert.Equal("split Up", DescribeAt(layout, 500, 20));
    }

    [Fact]
    public void A_panes_edge_along_a_divider_of_another_split_keeps_a_zone_beyond_the_dividers()
    {
        // (a | b) / (c | d): a's lower edge faces the divider between the rows, which reaches only a little into the
        // rows; beyond it, a's own edge splits just a.
        var grid = PaneLayout<string>.FromPreset(LayoutPreset.Grid2x2, ["a", "b", "c", "d"]);
        Assert.Equal("between 0", DescribeAt(grid, 250, 285));
        Assert.Equal("a Down", DescribeAt(grid, 250, 240));

        // a | (b / c): the same for b's left edge along the divider between a and the stack.
        var main = PaneLayout<string>.FromPreset(LayoutPreset.MainLeft, ["a", "b", "c"]);
        Assert.Equal("between 0", DescribeAt(main, 520, 150));
        Assert.Equal("b Left", DescribeAt(main, 560, 150));
        Assert.Equal("between 0", DescribeAt(main, 440, 150)); // a faces the stack: the divider takes a's whole band
    }

    [Fact]
    public void A_dividers_end_puts_the_pane_across_its_whole_split()
    {
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.Rows2, ["a", "b"]);

        layout.Insert("c", SpotFor(layout, "split Right").Placement);

        Assert.Equal(["a", "b", "c"], layout.Items);
        Assert.Null(layout.Preset);
        Assert.Equal(new SKRect(0, 0, 497, 297), RectOf(layout, "a"));
        Assert.Equal(new SKRect(0, 303, 497, 600), RectOf(layout, "b"));
        Assert.Equal(new SKRect(503, 0, 1000, 600), RectOf(layout, "c"));
    }

    [Fact]
    public void A_panes_edge_splits_only_that_pane()
    {
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.Rows2, ["a", "b"]);

        layout.Insert("c", SpotFor(layout, "a Right").Placement);

        Assert.Equal(new SKRect(0, 0, 497, 297), RectOf(layout, "a"));
        Assert.Equal(new SKRect(503, 0, 1000, 297), RectOf(layout, "c"));
        Assert.Equal(new SKRect(0, 303, 1000, 600), RectOf(layout, "b"));
    }

    [Fact]
    public void A_dividers_middle_puts_the_pane_between_its_neighbours_with_a_third_of_their_space()
    {
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.Columns2, ["a", "b"]);

        layout.Insert("c", SpotFor(layout, "between 0").Placement);

        Assert.Equal(["a", "c", "b"], layout.Items);
        float[] widths = new[] { "a", "c", "b" }.Select(item => RectOf(layout, item).Width).ToArray();
        Assert.InRange(widths.Max() - widths.Min(), 0, 1);
    }

    [Fact]
    public void A_pane_beside_a_split_in_the_same_direction_joins_it_with_half_the_space()
    {
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.Columns2, ["a", "b"]);

        layout.Insert("c", PaneLayout<string>.Placement.Beside(layout.Root, PaneDirection.Right));

        var root = Assert.IsType<PaneLayout<string>.Split>(layout.Root);
        Assert.Equal(3, root.Children.Count);
        Assert.Equal(new SKRect(506, 0, 1000, 600), RectOf(layout, "c")); // 494 of the 988 px the three share
    }

    [Fact]
    public void Inserting_a_pane_of_the_layout_moves_it()
    {
        // a / b / c (b and c share half), then c beside a: (a | c) / b, the row keeping a's share.
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.Rows2, ["a", "b"]);
        layout.SplitAt("b", "c", SplitOrientation.Vertical);

        layout.Insert("c", SpotFor(layout, "a Right", dragged: "c").Placement);

        Assert.Equal(["a", "c", "b"], layout.Items);
        Assert.Equal(new SKRect(503, 0, 1000, 396), RectOf(layout, "c"));
        Assert.Equal(new SKRect(0, 402, 1000, 600), RectOf(layout, "b"));
    }

    [Fact]
    public void Moving_a_pane_beside_its_own_split_collapses_that_split()
    {
        // a | (b / c), b to the right end of the stack's divider: a | c | b.
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.MainLeft, ["a", "b", "c"]);

        layout.Insert("b", SpotFor(layout, "split Right", dragged: "b").Placement);

        Assert.Equal(["a", "c", "b"], layout.Items);
        var root = Assert.IsType<PaneLayout<string>.Split>(layout.Root);
        Assert.All(root.Children, child => Assert.IsType<PaneLayout<string>.Leaf>(child));
        Assert.Equal(600, RectOf(layout, "b").Height);
        Assert.Equal(1000, RectOf(layout, "b").Right);
    }

    [Fact]
    public void Nothing_is_offered_beside_the_dragged_pane()
    {
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.Columns3, ["a", "b", "c"]);
        var b = LeafOf(layout, "b");

        var spots = layout.DropSpots(Area, Gap, Band, "b");

        Assert.DoesNotContain(spots, s => s.Placement.Node == b);
        Assert.DoesNotContain(spots, s => s.Placement.IsBetween); // both gaps are next to b
        Assert.DoesNotContain(spots, s => Describe(s.Placement) == "a Right"); // a faces b: not offered at all
        Assert.Equal(10, spots.Count); // a and c: 3 edges each; both dividers: 2 ends each
    }

    [Fact]
    public void The_preview_matches_the_insert_and_changes_nothing()
    {
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.MainLeft, ["a", "b", "c"]);
        var before = layout.Arrange(Area, Gap).Panes;
        var at = SpotFor(layout, "split Right", dragged: "b").Placement;

        var (others, inserted) = layout.ArrangeInserted("b", at, Area, Gap);

        Assert.Equal(before, layout.Arrange(Area, Gap).Panes);
        layout.Insert("b", at);
        Assert.Equal(RectOf(layout, "b"), inserted);
        Assert.Equal(layout.Arrange(Area, Gap).Panes.Where(p => p.Item != "b"), others);
    }

    [Fact]
    public void The_preview_of_a_pane_not_open_yet_leaves_it_out_of_the_others()
    {
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.Rows2, ["a", "b"]);

        var (others, inserted) = layout.ArrangeInserted(null, SpotFor(layout, "split Right").Placement, Area, Gap);

        Assert.Equal(new SKRect(503, 0, 1000, 600), inserted);
        Assert.Equal(["a", "b"], others.Select(p => p.Item));
        Assert.Equal(["a", "b"], layout.Items);
    }

    [Fact]
    public void A_maximized_pane_offers_its_four_edges_and_a_drop_restores_the_layout()
    {
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.Columns3, ["a", "b", "c"]);
        layout.Zoomed = "b";

        var spots = layout.DropSpots(Area, Gap, Band, null);

        Assert.Equal(["b Up", "b Left", "b Right", "b Down"], spots.Select(s => Describe(s.Placement)));
        Assert.Equal(new SKRect(920, 0, 1000, 600), spots[2].Zone.Rect);
        layout.Insert("d", spots[2].Placement);
        Assert.Null(layout.Zoomed);
        Assert.Equal(["a", "b", "d", "c"], layout.Items);
    }

    [Fact]
    public void Insert_rejects_foreign_placements_and_a_pane_beside_itself()
    {
        var layout = PaneLayout<string>.FromPreset(LayoutPreset.Columns2, ["a", "b"]);
        var other = PaneLayout<string>.FromPreset(LayoutPreset.Columns2, ["x", "y"]);

        Assert.Throws<ArgumentException>(() => layout.Insert("c", PaneLayout<string>.Placement.Beside(other.Root, PaneDirection.Up)));
        Assert.Throws<ArgumentException>(() => layout.Insert("a", PaneLayout<string>.Placement.Beside(LeafOf(layout, "a"), PaneDirection.Up)));
        Assert.Throws<ArgumentException>(() => layout.Insert("c", PaneLayout<string>.Placement.Between((PaneLayout<string>.Split)layout.Root, 1)));
        Assert.Equal(["a", "b"], layout.Items);
    }
}
