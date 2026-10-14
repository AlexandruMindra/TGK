using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace TGK.Client.Main;

/// <summary>How a split arranges its children: side by side (columns) or stacked (rows).</summary>
public enum SplitOrientation
{
    /// <summary>Children side by side, left to right.</summary>
    Horizontal,
    /// <summary>Children stacked, top to bottom.</summary>
    Vertical,
}

public enum PaneDirection
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>Ready-made arrangements offered by the split-view menu.</summary>
public enum LayoutPreset
{
    Columns2,
    Rows2,
    Columns3,
    /// <summary>One large pane on the left, two stacked on the right.</summary>
    MainLeft,
    /// <summary>One large pane on top, two side by side below.</summary>
    MainTop,
    Grid2x2,
    /// <summary>Three columns, two rows.</summary>
    Grid3x2,
}

/// <summary>
/// The area over which a drop is picked up: <see cref="Rect"/>. Where zones overlap, the one with the higher
/// <see cref="Priority"/> wins, then the one whose anchor (the segment <see cref="From"/>–<see cref="To"/>: an edge, a
/// divider or a single point) is nearer.
/// </summary>
public readonly record struct PaneDropZone(SKRect Rect, int Priority, SKPoint From, SKPoint To)
{
    /// <summary>The middle of a pane (swap, take its place): below everything else.</summary>
    public const int PanePriority = 0;
    /// <summary>Along a pane's outer edge.</summary>
    public const int EdgePriority = 1;
    /// <summary>Along a divider, between the two children it separates.</summary>
    public const int GapPriority = 2;
    /// <summary>The end of a divider, beside its whole split.</summary>
    public const int EndPriority = 3;

    /// <summary>Distance from <paramref name="p"/> to the anchor.</summary>
    public float Distance(SKPoint p)
    {
        SKPoint d = To - From;
        float length = d.X * d.X + d.Y * d.Y;
        float t = length <= 0 ? 0 : Math.Clamp(((p.X - From.X) * d.X + (p.Y - From.Y) * d.Y) / length, 0, 1);
        return SKPoint.Distance(p, new SKPoint(From.X + d.X * t, From.Y + d.Y * t));
    }

    /// <summary>The index of the zone among <paramref name="zones"/> that picks up point <paramref name="p"/>, or -1.</summary>
    public static int Pick(IReadOnlyList<PaneDropZone> zones, SKPoint p)
    {
        int best = -1;
        for (int i = 0; i < zones.Count; i++)
        {
            PaneDropZone zone = zones[i];
            if (!zone.Rect.Contains(p.X, p.Y))
                continue;
            if (best < 0 || zone.Priority > zones[best].Priority
                || (zone.Priority == zones[best].Priority && zone.Distance(p) < zones[best].Distance(p)))
                best = i;
        }
        return best;
    }
}

/// <summary>
/// The panes of a split view: a tree whose leaves are the items shown (tabs, in the app) and whose inner nodes split
/// their area among their children, side by side or stacked, by weight. Pure layout logic, no UI: <see cref="Arrange"/>
/// turns it into pane and divider rectangles for an area.
/// </summary>
public sealed class PaneLayout<T> where T : class
{
    public abstract class Node
    {
        public Split? Parent { get; internal set; }
    }

    public sealed class Leaf(T item) : Node
    {
        public T Item { get; internal set; } = item;
    }

    public sealed class Split(SplitOrientation orientation) : Node
    {
        public SplitOrientation Orientation { get; } = orientation;
        public List<Node> Children { get; } = [];
        /// <summary>Relative sizes of <see cref="Children"/> (same count; only their ratios matter).</summary>
        public List<float> Weights { get; } = [];
        /// <summary>Length shared by the children in the last <see cref="Arrange"/> (the area minus the gaps).</summary>
        public float Available { get; internal set; }
    }

    public readonly record struct PaneRect(T Item, SKRect Rect);

    /// <summary>The gap between children <see cref="Index"/> and <see cref="Index"/> + 1 of <see cref="Split"/>.</summary>
    public readonly record struct DividerRect(Split Split, int Index, SKRect Rect);

    public readonly record struct Arrangement(IReadOnlyList<PaneRect> Panes, IReadOnlyList<DividerRect> Dividers);

    /// <summary>
    /// Where <see cref="Insert"/> puts a pane: on side <see cref="Side"/> of <see cref="Node"/> (a pane, or a whole
    /// split), taking half of its space; or, with <see cref="Gap"/> set, between children <see cref="Gap"/> and
    /// <see cref="Gap"/> + 1 of the split <see cref="Node"/>, taking a third of their space.
    /// </summary>
    public readonly record struct Placement(Node Node, PaneDirection Side, int Gap = -1)
    {
        public bool IsBetween => Gap >= 0;

        public static Placement Beside(Node node, PaneDirection side) => new(node, side);

        public static Placement Between(Split split, int gap) => new(split, PaneDirection.Right, gap);
    }

    /// <summary>A place offered for a dropped pane, and the area of the screen that picks it up.</summary>
    public readonly record struct DropSpot(Placement Placement, PaneDropZone Zone);

    private T? _zoomed;

    public PaneLayout(T first) => Root = new Leaf(first);

    private PaneLayout(Node root) => Root = root;

    public Node Root { get; private set; }

    /// <summary>The preset this layout was built from; cleared once panes are split off or removed.</summary>
    public LayoutPreset? Preset { get; private set; }

    /// <summary>The pane shown alone over the whole area (a maximized pane), or null.</summary>
    public T? Zoomed
    {
        get => _zoomed;
        set
        {
            if (value is not null && FindLeaf(value) is null)
                throw new ArgumentException("The item is not a pane of this layout.", nameof(value));
            _zoomed = value;
        }
    }

    public int Count => Items.Count;

    /// <summary>The panes in reading order (left to right, top to bottom within each split).</summary>
    public IReadOnlyList<T> Items
    {
        get
        {
            var items = new List<T>();
            Collect(Root, items);
            return items;
        }
    }

    public bool Contains(T item) => FindLeaf(item) is not null;

    public static int PaneCount(LayoutPreset preset) => preset switch
    {
        LayoutPreset.Columns2 or LayoutPreset.Rows2 => 2,
        LayoutPreset.Columns3 or LayoutPreset.MainLeft or LayoutPreset.MainTop => 3,
        LayoutPreset.Grid2x2 => 4,
        LayoutPreset.Grid3x2 => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };

    /// <summary>Builds <paramref name="preset"/> with <paramref name="items"/> in reading order (exactly its pane count).</summary>
    public static PaneLayout<T> FromPreset(LayoutPreset preset, IReadOnlyList<T> items)
    {
        if (items.Count != PaneCount(preset))
            throw new ArgumentException($"{preset} needs {PaneCount(preset)} items, got {items.Count}.", nameof(items));
        if (items.Distinct().Count() != items.Count)
            throw new ArgumentException("Every pane needs a different item.", nameof(items));
        Node L(int i) => new Leaf(items[i]);
        Node root = preset switch
        {
            LayoutPreset.Columns2 => Make(SplitOrientation.Horizontal, L(0), L(1)),
            LayoutPreset.Rows2 => Make(SplitOrientation.Vertical, L(0), L(1)),
            LayoutPreset.Columns3 => Make(SplitOrientation.Horizontal, L(0), L(1), L(2)),
            LayoutPreset.MainLeft => Make(SplitOrientation.Horizontal, L(0), Make(SplitOrientation.Vertical, L(1), L(2))),
            LayoutPreset.MainTop => Make(SplitOrientation.Vertical, L(0), Make(SplitOrientation.Horizontal, L(1), L(2))),
            LayoutPreset.Grid2x2 => Make(SplitOrientation.Vertical,
                Make(SplitOrientation.Horizontal, L(0), L(1)), Make(SplitOrientation.Horizontal, L(2), L(3))),
            LayoutPreset.Grid3x2 => Make(SplitOrientation.Vertical,
                Make(SplitOrientation.Horizontal, L(0), L(1), L(2)), Make(SplitOrientation.Horizontal, L(3), L(4), L(5))),
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };
        return new PaneLayout<T>(root) { Preset = preset };
    }

    /// <summary>
    /// A layout from a tree the caller built (<see cref="Leaf"/>s in <see cref="Split"/>s with their weights; parents
    /// are set here). Splits with a single child are replaced by it; null when the tree has no panes.
    /// </summary>
    /// <exception cref="ArgumentException">An item appears twice, or a split's weights don't match its children.</exception>
    public static PaneLayout<T>? FromTree(Node? root)
    {
        Node? normalized = Normalize(root);
        if (normalized is null)
            return null;
        normalized.Parent = null;
        var layout = new PaneLayout<T>(normalized);
        IReadOnlyList<T> items = layout.Items;
        if (items.Distinct().Count() != items.Count)
            throw new ArgumentException("Every pane needs a different item.", nameof(root));
        return layout;
    }

    private static Node? Normalize(Node? node)
    {
        if (node is null or Leaf)
            return node;
        var split = (Split)node;
        if (split.Weights.Count != split.Children.Count || split.Weights.Any(w => !(w > 0) || float.IsInfinity(w)))
            throw new ArgumentException("A split needs one positive weight per child.", nameof(node));
        var children = new List<(Node Child, float Weight)>();
        for (int i = 0; i < split.Children.Count; i++)
        {
            if (Normalize(split.Children[i]) is { } child)
                children.Add((child, split.Weights[i]));
        }
        if (children.Count == 0)
            return null;
        if (children.Count == 1)
            return children[0].Child;
        split.Children.Clear();
        split.Weights.Clear();
        foreach ((Node child, float weight) in children)
        {
            child.Parent = split;
            split.Children.Add(child);
            split.Weights.Add(weight);
        }
        return split;
    }

    private static Split Make(SplitOrientation orientation, params Node[] children)
    {
        var split = new Split(orientation);
        foreach (Node child in children)
        {
            child.Parent = split;
            split.Children.Add(child);
            split.Weights.Add(1);
        }
        return split;
    }

    /// <summary>
    /// Splits the pane of <paramref name="target"/> in two: <paramref name="item"/> gets the half after it (right of it or
    /// below it) or, with <paramref name="after"/> false, the half before it.
    /// </summary>
    public void SplitAt(T target, T item, SplitOrientation orientation, bool after = true)
    {
        Leaf leaf = FindLeaf(target) ?? throw new ArgumentException("The target is not a pane of this layout.", nameof(target));
        if (FindLeaf(item) is not null)
            throw new ArgumentException("The item already has a pane.", nameof(item));
        Preset = null;
        _zoomed = null;
        PaneDirection side = orientation == SplitOrientation.Horizontal
            ? after ? PaneDirection.Right : PaneDirection.Left
            : after ? PaneDirection.Down : PaneDirection.Up;
        Put(new Leaf(item), Placement.Beside(leaf, side));
    }

    /// <summary>
    /// Puts <paramref name="item"/> in a new pane at <paramref name="at"/> (see <see cref="DropSpots"/>); when it is
    /// already a pane of this layout, that pane moves there. Ends the maximized pane and the preset.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="at"/> is not in this layout, or is beside the item's own pane.</exception>
    public void Insert(T item, Placement at)
    {
        Validate(at);
        Leaf? old = FindLeaf(item);
        if (old is not null && at.Node == old)
            throw new ArgumentException("A pane cannot go beside itself.", nameof(at));
        Preset = null;
        _zoomed = null;
        // The new pane first, then the old one out: `at` stays valid although removing a pane can restructure the tree.
        Put(new Leaf(item), at);
        if (old is not null)
            RemoveLeaf(old);
    }

    /// <summary>
    /// How the panes would be arranged in <paramref name="area"/> after <see cref="Insert"/> of <paramref name="item"/>
    /// (null: an item that is not open yet) at <paramref name="at"/>, without changing this layout: the other panes,
    /// and the rectangle of the inserted one.
    /// </summary>
    public (IReadOnlyList<PaneRect> Others, SKRect Inserted) ArrangeInserted(T? item, Placement at, SKRect area, float gap)
    {
        Validate(at);
        var copies = new Dictionary<Node, Node>();
        var preview = new PaneLayout<T>(Copy(Root, copies));
        Leaf? old = item is null ? null : preview.FindLeaf(item);
        Node target = copies[at.Node];
        if (old is not null && target == old)
            throw new ArgumentException("A pane cannot go beside itself.", nameof(at));
        // The item may be null here (a pane not open yet): this leaf only lives in the preview.
        var inserted = new Leaf(item!);
        preview.Put(inserted, at with { Node = target });
        if (old is not null)
            preview.RemoveLeaf(old);
        var panes = new List<PaneRect>();
        Place(preview.Root, area, gap, panes, []);
        SKRect rect = SKRect.Empty;
        var others = new List<PaneRect>();
        foreach (PaneRect pane in panes)
        {
            if (Same(pane.Item, item))
                rect = pane.Rect;
            else
                others.Add(pane);
        }
        return (others, rect);
    }

    /// <summary>
    /// The places offered for dropping <paramref name="dragged"/> (null, or not a pane of this layout: a new pane) into
    /// the layout as arranged in <paramref name="area"/>, each with the zone that picks it up (see
    /// <see cref="PaneDropZone"/>), <paramref name="band"/> px deep at most, and never more than
    /// <see cref="MaxBandShare"/> of a pane:
    /// <list type="bullet">
    /// <item>along each edge of a pane that does not face another child of its own split: beside that pane;</item>
    /// <item>along every divider, on both sides (<see cref="NearShare"/> of the band into a neighbour that is a split):
    /// between the two children it separates;</item>
    /// <item>at the ends of every divider (those that do not face another child of the split's own parent), the part
    /// of the divider's zone next to the outer edge: beside the whole split, on that side (a pane across all of it).</item>
    /// </list>
    /// So an edge shared by two children of a split is offered once, by the divider between them. A maximized pane
    /// offers its four edges. Nothing is offered beside the dragged pane itself or next to it.
    /// </summary>
    public IReadOnlyList<DropSpot> DropSpots(SKRect area, float gap, float band, T? dragged)
    {
        var spots = new List<DropSpot>();
        Leaf? moving = dragged is null ? null : FindLeaf(dragged);
        if (_zoomed is { } zoomed)
        {
            Leaf leaf = FindLeaf(zoomed)!;
            if (leaf != moving)
            {
                foreach (PaneDirection side in Sides)
                    spots.Add(new DropSpot(Placement.Beside(leaf, side), EdgeZone(area, side, band)));
            }
            return spots;
        }
        CollectSpots(Root, area, gap, band, moving, spots);
        return spots;
    }

    /// <summary>The largest share of a pane's width (or height) a drop zone along its edge takes.</summary>
    public const float MaxBandShare = 0.3f;

    /// <summary>
    /// How far, as a share of the band, a divider's zone reaches into a neighbour that is itself split: the panes there
    /// have zones of their own along that divider, from there on in.
    /// </summary>
    public const float NearShare = 0.4f;

    private static readonly PaneDirection[] Sides = [PaneDirection.Up, PaneDirection.Left, PaneDirection.Right, PaneDirection.Down];

    private static void CollectSpots(Node node, SKRect r, float gap, float band, Leaf? moving, List<DropSpot> spots)
    {
        if (node is Leaf leaf)
        {
            if (leaf == moving)
                return;
            foreach (PaneDirection side in Sides)
            {
                if (!FacesSibling(leaf, side))
                    spots.Add(new DropSpot(Placement.Beside(leaf, side), EdgeZone(r, side, band)));
            }
            return;
        }
        var split = (Split)node;
        (SKRect[] children, SKRect[] gaps, _) = Divide(split, r, gap);
        for (int i = 0; i < children.Length; i++)
            CollectSpots(split.Children[i], children[i], gap, band, moving, spots);
        bool across = split.Orientation == SplitOrientation.Horizontal;
        PaneDirection start = across ? PaneDirection.Up : PaneDirection.Left, end = across ? PaneDirection.Down : PaneDirection.Right;
        for (int i = 0; i < gaps.Length; i++)
        {
            // The divider's zone reaches into both neighbours (less into a split, whose panes have zones along this
            // divider of their own); its ends are the parts next to the outer edges.
            SKRect g = gaps[i];
            float before = Math.Min(split.Children[i] is Leaf ? band : band * NearShare, MaxBandShare * Extent(children[i], across));
            float after = Math.Min(split.Children[i + 1] is Leaf ? band : band * NearShare, MaxBandShare * Extent(children[i + 1], across));
            SKRect zone = across ? new SKRect(g.Left - before, g.Top, g.Right + after, g.Bottom) : new SKRect(g.Left, g.Top - before, g.Right, g.Bottom + after);
            float depth = Math.Min(band, MaxBandShare * Extent(g, !across));
            if (split.Children[i] != moving && split.Children[i + 1] != moving)
            {
                SKPoint from = across ? new SKPoint(g.MidX, g.Top) : new SKPoint(g.Left, g.MidY);
                SKPoint to = across ? new SKPoint(g.MidX, g.Bottom) : new SKPoint(g.Right, g.MidY);
                spots.Add(new DropSpot(Placement.Between(split, i), new PaneDropZone(zone, PaneDropZone.GapPriority, from, to)));
            }
            if (!FacesSibling(split, start))
            {
                SKRect at = across ? zone with { Bottom = zone.Top + depth } : zone with { Right = zone.Left + depth };
                SKPoint point = across ? new SKPoint(g.MidX, g.Top) : new SKPoint(g.Left, g.MidY);
                spots.Add(new DropSpot(Placement.Beside(split, start), new PaneDropZone(at, PaneDropZone.EndPriority, point, point)));
            }
            if (!FacesSibling(split, end))
            {
                SKRect at = across ? zone with { Top = zone.Bottom - depth } : zone with { Left = zone.Right - depth };
                SKPoint point = across ? new SKPoint(g.MidX, g.Bottom) : new SKPoint(g.Right, g.MidY);
                spots.Add(new DropSpot(Placement.Beside(split, end), new PaneDropZone(at, PaneDropZone.EndPriority, point, point)));
            }
        }
    }

    // Width of `r` when `across`, else its height.
    private static float Extent(SKRect r, bool across) => across ? r.Width : r.Height;

    // Whether `side` of `node` faces another child of its own split (the divider between them is offered instead).
    private static bool FacesSibling(Node node, PaneDirection side)
    {
        if (node.Parent is not { } parent || parent.Orientation != Axis(side))
            return false;
        int index = parent.Children.IndexOf(node);
        return side is PaneDirection.Left or PaneDirection.Up ? index > 0 : index < parent.Children.Count - 1;
    }

    // The band along `side` of pane `r`.
    private static PaneDropZone EdgeZone(SKRect r, PaneDirection side, float band)
    {
        float depth = Math.Min(band, MaxBandShare * Extent(r, side is PaneDirection.Left or PaneDirection.Right));
        (SKRect zone, SKPoint from, SKPoint to) = side switch
        {
            PaneDirection.Left => (r with { Right = r.Left + depth }, new SKPoint(r.Left, r.Top), new SKPoint(r.Left, r.Bottom)),
            PaneDirection.Right => (r with { Left = r.Right - depth }, new SKPoint(r.Right, r.Top), new SKPoint(r.Right, r.Bottom)),
            PaneDirection.Up => (r with { Bottom = r.Top + depth }, new SKPoint(r.Left, r.Top), new SKPoint(r.Right, r.Top)),
            _ => (r with { Top = r.Bottom - depth }, new SKPoint(r.Left, r.Bottom), new SKPoint(r.Right, r.Bottom)),
        };
        return new PaneDropZone(zone, PaneDropZone.EdgePriority, from, to);
    }

    private static SplitOrientation Axis(PaneDirection side) =>
        side is PaneDirection.Left or PaneDirection.Right ? SplitOrientation.Horizontal : SplitOrientation.Vertical;

    private void Validate(Placement at)
    {
        Node node = at.Node;
        while (node.Parent is { } parent)
            node = parent;
        if (node != Root)
            throw new ArgumentException("The placement is not in this layout.", nameof(at));
        if (at.IsBetween && (at.Node is not Split split || at.Gap >= split.Children.Count - 1))
            throw new ArgumentException("There is no such gap.", nameof(at));
    }

    // Puts `added` (a new leaf) at `at`.
    private void Put(Leaf added, Placement at)
    {
        if (at.IsBetween)
        {
            // A third of the two neighbours' space, taken from both in proportion.
            var between = (Split)at.Node;
            int gap = at.Gap;
            float a = between.Weights[gap], b = between.Weights[gap + 1];
            between.Weights[gap] = a * 2f / 3f;
            between.Weights[gap + 1] = b * 2f / 3f;
            AddChild(between, gap + 1, added, (a + b) / 3f);
            return;
        }
        Node node = at.Node;
        SplitOrientation orientation = Axis(at.Side);
        bool after = at.Side is PaneDirection.Right or PaneDirection.Down;
        if (node is Split own && own.Orientation == orientation)
        {
            // Beside a split in the same direction: one more child at that end, as large as all the others together.
            AddChild(own, after ? own.Children.Count : 0, added, own.Weights.Sum());
            return;
        }
        if (node.Parent is { } parent && parent.Orientation == orientation)
        {
            // Same direction as the parent: a new sibling that takes half of the node's share.
            int index = parent.Children.IndexOf(node);
            float half = parent.Weights[index] / 2f;
            parent.Weights[index] = half;
            AddChild(parent, after ? index + 1 : index, added, half);
            return;
        }
        var split = new Split(orientation);
        ReplaceNode(node, split);
        AddChild(split, 0, after ? node : added, 1);
        AddChild(split, 1, after ? added : node, 1);
    }

    private static void AddChild(Split split, int index, Node child, float weight)
    {
        child.Parent = split;
        split.Children.Insert(index, child);
        split.Weights.Insert(index, weight);
    }

    private static Node Copy(Node node, Dictionary<Node, Node> copies)
    {
        Node copy;
        if (node is Leaf leaf)
        {
            copy = new Leaf(leaf.Item);
        }
        else
        {
            var split = (Split)node;
            var copied = new Split(split.Orientation);
            for (int i = 0; i < split.Children.Count; i++)
                AddChild(copied, i, Copy(split.Children[i], copies), split.Weights[i]);
            copy = copied;
        }
        copies[node] = copy;
        return copy;
    }

    /// <summary>Removes the pane of <paramref name="item"/>; false when it has none or is the last pane.</summary>
    public bool Remove(T item)
    {
        Leaf? leaf = FindLeaf(item);
        if (leaf?.Parent is null)
            return false;
        RemoveLeaf(leaf);
        return true;
    }

    // Takes `leaf` (not the root) out of the tree; a split left with one child is replaced by it.
    private void RemoveLeaf(Leaf leaf)
    {
        Split parent = leaf.Parent!;
        if (Same(_zoomed, leaf.Item))
            _zoomed = null;
        Preset = null;
        int index = parent.Children.IndexOf(leaf);
        parent.Children.RemoveAt(index);
        parent.Weights.RemoveAt(index);
        leaf.Parent = null;
        if (parent.Children.Count == 1)
        {
            // A split with one child is just that child.
            Node only = parent.Children[0];
            ReplaceNode(parent, only);
            if (only is Split inner && inner.Parent is { } outer && outer.Orientation == inner.Orientation)
                Flatten(inner, outer);
        }
    }

    /// <summary>Exchanges the panes of <paramref name="a"/> and <paramref name="b"/> (places and sizes stay).</summary>
    public void Swap(T a, T b)
    {
        Leaf first = FindLeaf(a) ?? throw new ArgumentException("The item is not a pane of this layout.", nameof(a));
        Leaf second = FindLeaf(b) ?? throw new ArgumentException("The item is not a pane of this layout.", nameof(b));
        (first.Item, second.Item) = (second.Item, first.Item);
    }

    /// <summary>Puts <paramref name="replacement"/> in the pane of <paramref name="old"/> (same place and size).</summary>
    public void Replace(T old, T replacement)
    {
        Leaf leaf = FindLeaf(old) ?? throw new ArgumentException("The item is not a pane of this layout.", nameof(old));
        if (!Same(old, replacement) && FindLeaf(replacement) is not null)
            throw new ArgumentException("The replacement already has a pane.", nameof(replacement));
        leaf.Item = replacement;
        if (Same(_zoomed, old))
            _zoomed = replacement;
    }

    /// <summary>
    /// Moves divider <paramref name="index"/> of <paramref name="split"/> by <paramref name="deltaPx"/> (as last arranged),
    /// keeping both neighbours at least <paramref name="minPx"/> long. Returns whether anything changed.
    /// </summary>
    public bool MoveDivider(Split split, int index, float deltaPx, float minPx)
    {
        if (index < 0 || index + 1 >= split.Children.Count || split.Available <= 0)
            return false;
        float total = split.Weights.Sum();
        float scale = split.Available / total;
        float a = split.Weights[index] * scale, b = split.Weights[index + 1] * scale;
        float min = Math.Min(minPx, (a + b) / 2f);
        float newA = Math.Clamp(a + deltaPx, min, a + b - min);
        if (Math.Abs(newA - a) < 0.01f)
            return false;
        split.Weights[index] = newA / scale;
        split.Weights[index + 1] = (a + b - newA) / scale;
        return true;
    }

    /// <summary>Gives every child of <paramref name="split"/> the same size.</summary>
    public static void Equalize(Split split)
    {
        for (int i = 0; i < split.Weights.Count; i++)
            split.Weights[i] = 1;
    }

    /// <summary>
    /// Pane and divider rectangles for <paramref name="area"/>, with <paramref name="gap"/> px between neighbours.
    /// Edges are on whole pixels. A zoomed pane gets the whole area and there are no dividers.
    /// </summary>
    public Arrangement Arrange(SKRect area, float gap)
    {
        var panes = new List<PaneRect>();
        var dividers = new List<DividerRect>();
        if (_zoomed is { } zoomed)
            panes.Add(new PaneRect(zoomed, area));
        else
            Place(Root, area, gap, panes, dividers);
        return new Arrangement(panes, dividers);
    }

    private static void Place(Node node, SKRect r, float gap, List<PaneRect> panes, List<DividerRect> dividers)
    {
        if (node is Leaf leaf)
        {
            panes.Add(new PaneRect(leaf.Item, r));
            return;
        }
        var split = (Split)node;
        (SKRect[] children, SKRect[] gaps, float available) = Divide(split, r, gap);
        split.Available = available;
        for (int i = 0; i < children.Length; i++)
        {
            Place(split.Children[i], children[i], gap, panes, dividers);
            if (i < gaps.Length)
                dividers.Add(new DividerRect(split, i, gaps[i]));
        }
    }

    // The rectangles of the children of `split` in `r` and of the gaps between them, and the length the children share.
    private static (SKRect[] Children, SKRect[] Gaps, float Available) Divide(Split split, SKRect r, float gap)
    {
        bool across = split.Orientation == SplitOrientation.Horizontal;
        int n = split.Children.Count;
        float start = across ? r.Left : r.Top, length = across ? r.Width : r.Height;
        float available = Math.Max(0, length - gap * (n - 1));
        float total = split.Weights.Sum();
        var children = new SKRect[n];
        var gaps = new SKRect[n - 1];
        float used = 0, pos = start;
        for (int i = 0; i < n; i++)
        {
            used += split.Weights[i];
            // The last child ends exactly at the far edge; the others end on a whole pixel.
            float end = i == n - 1 ? start + length : MathF.Round(start + available * used / total + gap * i);
            children[i] = across ? new SKRect(pos, r.Top, end, r.Bottom) : new SKRect(r.Left, pos, r.Right, end);
            if (i < n - 1)
                gaps[i] = across ? new SKRect(end, r.Top, end + gap, r.Bottom) : new SKRect(r.Left, end, r.Right, end + gap);
            pos = end + gap;
        }
        return (children, gaps, available);
    }

    /// <summary>
    /// The pane next to <paramref name="from"/> in <paramref name="direction"/> among <paramref name="panes"/> (as arranged):
    /// the nearest one on that side that overlaps it, preferring the largest overlap. Null at the edge.
    /// </summary>
    public static T? Neighbor(IReadOnlyList<PaneRect> panes, T from, PaneDirection direction)
    {
        SKRect? origin = null;
        foreach (PaneRect pane in panes)
        {
            if (Same(pane.Item, from))
                origin = pane.Rect;
        }
        if (origin is not { } o)
            return null;
        T? best = null;
        (float Distance, float Overlap) bestScore = (float.MaxValue, 0);
        foreach (PaneRect pane in panes)
        {
            if (Same(pane.Item, from))
                continue;
            SKRect r = pane.Rect;
            (float distance, float overlap) = direction switch
            {
                PaneDirection.Left => (o.Left - r.Right, Overlap(o.Top, o.Bottom, r.Top, r.Bottom)),
                PaneDirection.Right => (r.Left - o.Right, Overlap(o.Top, o.Bottom, r.Top, r.Bottom)),
                PaneDirection.Up => (o.Top - r.Bottom, Overlap(o.Left, o.Right, r.Left, r.Right)),
                _ => (r.Top - o.Bottom, Overlap(o.Left, o.Right, r.Left, r.Right)),
            };
            if (distance < -0.5f || overlap <= 0)
                continue;
            if (distance < bestScore.Distance - 0.5f || (Math.Abs(distance - bestScore.Distance) <= 0.5f && overlap > bestScore.Overlap))
            {
                best = pane.Item;
                bestScore = (distance, overlap);
            }
        }
        return best;
    }

    private static bool Same(T? a, T? b) => EqualityComparer<T?>.Default.Equals(a, b);

    private static float Overlap(float a0, float a1, float b0, float b1) => Math.Min(a1, b1) - Math.Max(a0, b0);

    private Leaf? FindLeaf(T item) => FindLeaf(Root, item);

    private static Leaf? FindLeaf(Node node, T item)
    {
        if (node is Leaf leaf)
            return Same(leaf.Item, item) ? leaf : null;
        foreach (Node child in ((Split)node).Children)
        {
            if (FindLeaf(child, item) is { } found)
                return found;
        }
        return null;
    }

    private static void Collect(Node node, List<T> items)
    {
        if (node is Leaf leaf)
            items.Add(leaf.Item);
        else
        {
            foreach (Node child in ((Split)node).Children)
                Collect(child, items);
        }
    }

    // Puts `replacement` where `node` is in the tree (keeping the node's weight).
    private void ReplaceNode(Node node, Node replacement)
    {
        Split? parent = node.Parent;
        replacement.Parent = parent;
        if (parent is null)
            Root = replacement;
        else
            parent.Children[parent.Children.IndexOf(node)] = replacement;
        node.Parent = null;
    }

    // `inner` sits in `outer` and splits the same way: its children join `outer` in its place, sharing its weight.
    private static void Flatten(Split inner, Split outer)
    {
        int index = outer.Children.IndexOf(inner);
        float weight = outer.Weights[index];
        float total = inner.Weights.Sum();
        outer.Children.RemoveAt(index);
        outer.Weights.RemoveAt(index);
        for (int i = 0; i < inner.Children.Count; i++)
        {
            Node child = inner.Children[i];
            child.Parent = outer;
            outer.Children.Insert(index + i, child);
            outer.Weights.Insert(index + i, weight * inner.Weights[i] / total);
        }
        inner.Children.Clear();
        inner.Weights.Clear();
    }
}
