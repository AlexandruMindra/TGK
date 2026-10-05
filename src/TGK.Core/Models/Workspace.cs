using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace TGK.Core.Models;

/// <summary>
/// "Reopen my tabs": whether the client reopens the tabs and split views at start (a user preference, off by
/// default) and the tabs and split views the user's last client saved. Synced as the vault's single "workspace" item,
/// so work continues on whichever device the user signs in next. Never holds secrets: a tab is a saved host's id or
/// the address of an unsaved (quick-connect) host.
/// </summary>
public sealed class Workspace
{
    public const int MaxTabs = 200;
    public const int MaxAddressLength = 255;

    /// <summary>Longest remote folder a files tab may save.</summary>
    public const int MaxPathLength = 4096;

    internal const int MaxDepth = 16;

    /// <summary>The preference: reopen the saved tabs when the client starts. While off, no tabs are kept.</summary>
    public bool RestoreTabs { get; set; }

    /// <summary>The saved tabs, in tab-strip order.</summary>
    public List<WorkspaceTab> Tabs { get; set; } = [];

    /// <summary>Index in <see cref="Tabs"/> of the tab that was active, or -1.</summary>
    public int ActiveTab { get; set; } = -1;

    /// <summary>The split views among <see cref="Tabs"/>.</summary>
    public List<WorkspaceSplit> Splits { get; set; } = [];

    /// <summary>When and on which device the tabs were saved (shown when they are reopened elsewhere).</summary>
    public DateTimeOffset? SavedAt { get; set; }

    public string? SavedOn { get; set; }

    /// <summary>Nothing to store: the preference is off and no tabs are kept (a vault without a workspace item).</summary>
    [JsonIgnore]
    public bool IsEmpty => !RestoreTabs && Tabs.Count == 0;

    public Workspace Clone() => new()
    {
        RestoreTabs = RestoreTabs,
        Tabs = Tabs.Select(t => t.Clone()).ToList(),
        ActiveTab = ActiveTab,
        Splits = Splits.Select(s => s.Clone()).ToList(),
        SavedAt = SavedAt,
        SavedOn = SavedOn,
    };

    /// <summary>A problem that makes this workspace unfit to store (a message for the log), or null.</summary>
    public string? Validate()
    {
        if (Tabs is null || Splits is null)
            return "Tabs and splits are required.";
        if (Tabs.Count > MaxTabs)
            return $"At most {MaxTabs} tabs can be saved.";
        if (ActiveTab < -1 || ActiveTab >= Tabs.Count)
            return "The active tab is not one of the tabs.";
        foreach (WorkspaceTab? tab in Tabs)
        {
            if (tab is null)
                return "Empty tab.";
            if (tab.HostId is not null && tab.Host is not null)
                return "A tab is either a saved host or an address.";
            if (tab.Host is { } host && (host.Length is 0 or > MaxAddressLength || (tab.Username?.Length ?? 0) > MaxAddressLength || tab.Port is < 1 or > 65535))
                return "Invalid tab address.";
            if (tab.Path is { } path && (path.Length is 0 or > MaxPathLength || path[0] != '/' || path.AsSpan().IndexOfAny('\0', '\n', '\r') >= 0))
                return "Invalid folder of a files tab.";
        }
        if ((SavedOn?.Length ?? 0) > MaxAddressLength)
            return "Device name too long.";
        var used = new HashSet<int>();
        foreach (WorkspaceSplit? split in Splits)
        {
            if (split?.Root is null)
                return "Empty split view.";
            var panes = new List<int>();
            if (!split.Root.Collect(panes, 0))
                return "Malformed split view.";
            if (panes.Count < 2 || panes.Any(p => p < 0 || p >= Tabs.Count || !used.Add(p)))
                return "A split view needs two or more tabs of its own.";
            if (split.Zoomed is { } zoomed && !panes.Contains(zoomed))
                return "The maximized pane is not part of its split view.";
        }
        return null;
    }
}

/// <summary>
/// A saved tab: a saved host (<see cref="HostId"/>), an unsaved host by address, or (neither) a new-tab page. A host's
/// tab is a terminal, or with <see cref="Kind"/> <see cref="FilesKind"/> its files (at <see cref="Path"/>).
/// </summary>
public sealed class WorkspaceTab
{
    /// <summary>The <see cref="Kind"/> of a file browser tab.</summary>
    public const string FilesKind = "files";

    /// <summary>The <see cref="Kind"/> of a file browser tab for this computer's files (no host).</summary>
    public const string LocalFilesKind = "local-files";

    public Guid? HostId { get; set; }

    /// <summary>Host name or address of an unsaved (quick-connect) host.</summary>
    public string? Host { get; set; }

    public int Port { get; set; } = 22;

    public string? Username { get; set; }

    /// <summary>
    /// Null for a terminal; <see cref="FilesKind"/> for a file browser. An unknown kind (from a newer version) opens a
    /// terminal, as versions before 0.3.0 do with every kind.
    /// </summary>
    public string? Kind { get; set; }

    /// <summary>The remote folder a file browser tab shows (absolute); null for its home folder.</summary>
    public string? Path { get; set; }

    [JsonIgnore]
    public bool IsNewTabPage => HostId is null && Host is null && Kind != LocalFilesKind;

    [JsonIgnore]
    public bool IsFiles => Kind == FilesKind;

    public WorkspaceTab Clone() => (WorkspaceTab)MemberwiseClone();
}

/// <summary>A saved split view: its layout (whose leaves are tab indices) and the maximized pane, if any.</summary>
public sealed class WorkspaceSplit
{
    public WorkspacePane Root { get; set; } = new();

    /// <summary>Tab index of the maximized pane, or null.</summary>
    public int? Zoomed { get; set; }

    public WorkspaceSplit Clone() => new() { Root = Root.Clone(), Zoomed = Zoomed };
}

/// <summary>
/// A node of a saved split-view layout: a pane (<see cref="Tab"/>) or a split of <see cref="Children"/> side by side
/// or, with <see cref="Stacked"/>, top to bottom, sized by <see cref="Weights"/>.
/// </summary>
public sealed class WorkspacePane
{
    public int? Tab { get; set; }

    public bool Stacked { get; set; }

    public List<WorkspacePane>? Children { get; set; }

    public List<float>? Weights { get; set; }

    public WorkspacePane Clone() => new()
    {
        Tab = Tab,
        Stacked = Stacked,
        Children = Children?.Select(c => c.Clone()).ToList(),
        Weights = Weights is null ? null : [.. Weights],
    };

    // Adds the tab indices of the panes below this node; false for a malformed node (both or neither of a pane and
    // children, fewer than two children, weights that don't match, or too deep).
    internal bool Collect(List<int> panes, int depth)
    {
        if (depth > Workspace.MaxDepth)
            return false;
        if (Tab is { } tab)
        {
            if (Children is not null)
                return false;
            panes.Add(tab);
            return true;
        }
        if (Children is not { Count: >= 2 } || (Weights is not null && (Weights.Count != Children.Count || Weights.Any(w => !(w > 0) || float.IsInfinity(w)))))
            return false;
        foreach (WorkspacePane? child in Children)
        {
            if (child is null || !child.Collect(panes, depth + 1))
                return false;
        }
        return true;
    }
}
