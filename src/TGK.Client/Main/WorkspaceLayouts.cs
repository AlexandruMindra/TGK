using System;
using System.Collections.Generic;
using TGK.Core.Models;

namespace TGK.Client.Main;

/// <summary>
/// Converts split-view layouts to and from the saved form in the vault (<see cref="WorkspaceSplit"/>, whose panes are
/// tab indices). Panes whose tab is not saved, or no longer exists when restoring, are left out.
/// </summary>
public static class WorkspaceLayouts
{
    /// <summary>
    /// The saved form of <paramref name="layout"/>, with each pane's tab index from <paramref name="indexOf"/> (null
    /// leaves the pane out); null when fewer than two panes remain.
    /// </summary>
    public static WorkspaceSplit? Export<T>(PaneLayout<T> layout, Func<T, int?> indexOf) where T : class
    {
        WorkspacePane? root = Export(layout.Root, indexOf);
        if (root?.Children is null)
            return null;
        int? zoomed = layout.Zoomed is { } z ? indexOf(z) : null;
        return new WorkspaceSplit { Root = root, Zoomed = zoomed };
    }

    private static WorkspacePane? Export<T>(PaneLayout<T>.Node node, Func<T, int?> indexOf) where T : class
    {
        if (node is PaneLayout<T>.Leaf leaf)
            return indexOf(leaf.Item) is { } index ? new WorkspacePane { Tab = index } : null;
        var split = (PaneLayout<T>.Split)node;
        var children = new List<WorkspacePane>();
        var weights = new List<float>();
        for (int i = 0; i < split.Children.Count; i++)
        {
            if (Export(split.Children[i], indexOf) is not { } child)
                continue;
            children.Add(child);
            weights.Add(split.Weights[i]);
        }
        if (children.Count == 0)
            return null;
        if (children.Count == 1)
            return children[0];
        // Stored as ratios, so a layout saved at any window size reopens with the same proportions.
        float total = 0;
        foreach (float w in weights)
            total += w;
        for (int i = 0; i < weights.Count; i++)
            weights[i] = Math.Max(0.0001f, MathF.Round(weights[i] / total, 4)); // never 0: the vault rejects that
        return new WorkspacePane { Stacked = split.Orientation == SplitOrientation.Vertical, Children = children, Weights = weights };
    }

    /// <summary>
    /// Rebuilds a saved split view with the tabs <paramref name="resolve"/> returns for its tab indices (null: the tab
    /// is gone, its pane is left out); null when fewer than two panes remain or the saved form is unusable.
    /// </summary>
    public static PaneLayout<T>? Import<T>(WorkspaceSplit split, Func<int, T?> resolve) where T : class
    {
        PaneLayout<T>? layout;
        try
        {
            layout = PaneLayout<T>.FromTree(Import(split.Root, resolve, 0));
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (layout is null || layout.Count < 2)
            return null;
        if (split.Zoomed is { } zoomed && resolve(zoomed) is { } item && layout.Contains(item))
            layout.Zoomed = item;
        return layout;
    }

    private static PaneLayout<T>.Node? Import<T>(WorkspacePane? pane, Func<int, T?> resolve, int depth) where T : class
    {
        if (pane is null || depth > 16)
            return null;
        if (pane.Tab is { } tab)
            return resolve(tab) is { } item ? new PaneLayout<T>.Leaf(item) : null;
        if (pane.Children is null)
            return null;
        var split = new PaneLayout<T>.Split(pane.Stacked ? SplitOrientation.Vertical : SplitOrientation.Horizontal);
        for (int i = 0; i < pane.Children.Count; i++)
        {
            if (Import(pane.Children[i], resolve, depth + 1) is not { } child)
                continue;
            float weight = pane.Weights is { } w && i < w.Count && w[i] > 0 && !float.IsInfinity(w[i]) ? w[i] : 1;
            split.Children.Add(child);
            split.Weights.Add(weight);
        }
        return split.Children.Count == 0 ? null : split;
    }
}
