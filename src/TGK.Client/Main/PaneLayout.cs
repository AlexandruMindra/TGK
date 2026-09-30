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
        var added = new Leaf(item);
        Preset = null;
        _zoomed = null;
        if (leaf.Parent is { } parent && parent.Orientation == orientation)
        {
            // Same direction as the parent: a new sibling that takes half of the target's share.
            int index = parent.Children.IndexOf(leaf);
            float half = parent.Weights[index] / 2f;
            parent.Weights[index] = half;
            int at = after ? index + 1 : index;
            parent.Children.Insert(at, added);
            parent.Weights.Insert(at, half);
            added.Parent = parent;
            return;
        }
        var split = new Split(orientation);
        ReplaceNode(leaf, split);
        foreach (Node child in after ? new Node[] { leaf, added } : [added, leaf])
        {
            child.Parent = split;
            split.Children.Add(child);
            split.Weights.Add(1);
        }
    }

    /// <summary>Removes the pane of <paramref name="item"/>; false when it has none or is the last pane.</summary>
    public bool Remove(T item)
    {
        Leaf? leaf = FindLeaf(item);
        if (leaf?.Parent is not { } parent)
            return false;
        if (Same(_zoomed, item))
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
        return true;
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
        bool across = split.Orientation == SplitOrientation.Horizontal;
        int n = split.Children.Count;
        float start = across ? r.Left : r.Top, length = across ? r.Width : r.Height;
        float available = Math.Max(0, length - gap * (n - 1));
        split.Available = available;
        float total = split.Weights.Sum();
        float used = 0, pos = start;
        for (int i = 0; i < n; i++)
        {
            used += split.Weights[i];
            // The last child ends exactly at the far edge; the others end on a whole pixel.
            float end = i == n - 1 ? start + length : MathF.Round(start + available * used / total + gap * i);
            SKRect child = across ? new SKRect(pos, r.Top, end, r.Bottom) : new SKRect(r.Left, pos, r.Right, end);
            Place(split.Children[i], child, gap, panes, dividers);
            if (i < n - 1)
            {
                SKRect divider = across ? new SKRect(end, r.Top, end + gap, r.Bottom) : new SKRect(r.Left, end, r.Right, end + gap);
                dividers.Add(new DividerRect(split, i, divider));
            }
            pos = end + gap;
        }
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
