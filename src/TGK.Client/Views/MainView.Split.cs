using System;
using System.Collections.Generic;
using System.Linq;
using Blossom.Core.Input;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Main;

namespace TGK.Client.Views;

// Split views: several tabs shown side by side in one layout. Every pane is still an ordinary tab (it keeps its place in
// the tab strip, its shortcuts and its status); the panes of a split view share one PaneLayout (TabContent.Split) and
// sit next to each other in the tab strip. Activating any of them shows the whole layout, with that pane focused.
public sealed partial class MainView
{
    private const float PaneGap = 6, PanePad = 4;

    private readonly List<PaneHeader> _paneHeaders = [];
    private readonly List<PaneDivider> _paneDividers = [];
    private readonly List<PaneLayout<TabContent>.PaneRect> _shownPanes = []; // content-local, from the last layout
    private bool _overlayWasOpen; // a menu or dialog was open at the last frame (see OnViewMouseDown)

    private static readonly (LayoutPreset Preset, string Text, string Icon)[] Presets =
    [
        (LayoutPreset.Columns2, "2 side by side", "layout-columns"),
        (LayoutPreset.Rows2, "2 stacked", "layout-rows"),
        (LayoutPreset.Columns3, "3 side by side", "layout-columns3"),
        (LayoutPreset.MainLeft, "1 large + 2 stacked", "layout-main-left"),
        (LayoutPreset.MainTop, "1 large + 2 below", "layout-main-top"),
        (LayoutPreset.Grid2x2, "Grid of 4", "layout-grid"),
        (LayoutPreset.Grid3x2, "Grid of 6", "layout-grid6"),
    ];

    /// <summary>True when the active tab is a pane of a split view.</summary>
    public bool IsSplit => ActiveTab?.Split is not null;

    // ---- layout ----

    /// <summary>
    /// Sizes every tab: a tab of its own fills the content area; the panes of a split view get their rectangles (below a
    /// title bar), also while the split view is in the background, so its terminals keep their size. Title bars and
    /// dividers belong to the split view on screen.
    /// </summary>
    private void LayoutContent(float w, float h)
    {
        TabContent? active = ActiveTab;
        PaneLayout<TabContent>? shown = active?.Split;
        var area = new SKRect(PanePad, PanePad, Math.Max(PanePad, w - PanePad), Math.Max(PanePad, h - PanePad));
        var arranged = new HashSet<PaneLayout<TabContent>>();
        int headers = 0, dividers = 0;
        SKRect? focus = null;
        _shownPanes.Clear();
        foreach (TabContent tab in _tabs)
        {
            if (tab.Split is not { } layout)
            {
                tab.Transform.SetLocalFrame(0, 0, w, h);
                continue;
            }
            if (!arranged.Add(layout))
                continue;
            PaneLayout<TabContent>.Arrangement arrangement = layout.Arrange(area, PaneGap);
            bool onScreen = layout == shown;
            foreach (PaneLayout<TabContent>.PaneRect pane in arrangement.Panes)
            {
                SKRect r = pane.Rect;
                pane.Item.Transform.SetLocalFrame(r.Left, r.Top + PaneHeader.Height, r.Width, Math.Max(0, r.Height - PaneHeader.Height));
                if (!onScreen || headers >= _paneHeaders.Count)
                    continue;
                _shownPanes.Add(pane);
                PaneHeader header = _paneHeaders[headers++];
                header.Visible = true;
                header.Set(pane.Item, pane.Item == active, layout.Zoomed is not null);
                header.Transform.SetLocalFrame(r.Left, r.Top, r.Width, PaneHeader.Height);
                if (pane.Item == active)
                    focus = r;
            }
            if (!onScreen)
                continue;
            foreach (PaneLayout<TabContent>.DividerRect divider in arrangement.Dividers)
            {
                if (dividers >= _paneDividers.Count)
                    break;
                PaneDivider control = _paneDividers[dividers++];
                control.Visible = true;
                control.Set(layout, divider);
                control.Transform.SetLocalFrame(divider.Rect.Left, divider.Rect.Top, divider.Rect.Width, divider.Rect.Height);
            }
        }
        for (int i = headers; i < _paneHeaders.Count; i++)
        {
            _paneHeaders[i].Visible = false;
            _paneHeaders[i].Set(null, false, false);
        }
        for (int i = dividers; i < _paneDividers.Count; i++)
        {
            _paneDividers[i].Visible = false;
            _paneDividers[i].Clear();
        }
        _content.SetFocusRect(focus);
    }

    /// <summary>
    /// Shows the active tab, or all panes of its split view (just the maximized one while a pane is maximized), hides
    /// every other tab and lays them out. Tabs that just came on screen get <see cref="TabContent.OnShown"/>.
    /// </summary>
    private void UpdatePanes()
    {
        TabContent? active = ActiveTab;
        var shown = new HashSet<TabContent>();
        if (active?.Split is { } layout)
        {
            if (layout.Zoomed is { } zoomed)
                shown.Add(zoomed);
            else
                shown.UnionWith(layout.Items);
        }
        else if (active is not null)
        {
            shown.Add(active);
        }

        // Title bars and dividers for the split view on screen (a tree of n panes has n - 1 gaps).
        int panes = active?.Split is null ? 0 : shown.Count;
        while (_paneHeaders.Count < panes)
        {
            var header = new PaneHeader(this) { Visible = false };
            _paneHeaders.Add(header);
            _content.AddChild(header);
        }
        while (_paneDividers.Count < panes - 1)
        {
            var divider = new PaneDivider(this) { Visible = false };
            _paneDividers.Add(divider);
            _content.AddChild(divider);
        }

        var appeared = new List<TabContent>();
        foreach (TabContent tab in _tabs)
        {
            bool visible = shown.Contains(tab);
            if (visible && !tab.Visible)
                appeared.Add(tab);
            tab.Visible = visible;
        }
        // A focused element in a pane that just went away gives the keyboard back.
        if (ActiveKeyboardElement is { } focused && _tabs.Any(t => !t.Visible && t.ContainsElement(focused)))
            SetActiveKeyboardElement(null);
        _content.InvalidateLayout();
        _content.ForceLayoutSubtree();
        WorkspaceChanged(); // the active tab or a split view changed
        foreach (TabContent tab in appeared)
        {
            tab.ClearAttention();
            tab.OnShown();
        }
    }

    /// <summary>Lays the panes out again after a divider moved.</summary>
    internal void RelayoutPanes()
    {
        _content.InvalidateLayout();
        _content.ForceLayoutSubtree();
        WorkspaceChanged(); // the pane sizes are saved too
    }

    // First and last index in _tabs of the block of tabs around `index` that share its split view (just `index` for a
    // tab of its own). The panes of a split view are always next to each other in the tab strip.
    private (int First, int Last) Block(int index)
    {
        PaneLayout<TabContent>? layout = _tabs[index].Split;
        int first = index, last = index;
        if (layout is null)
            return (first, last);
        while (first > 0 && _tabs[first - 1].Split == layout)
            first--;
        while (last + 1 < _tabs.Count && _tabs[last + 1].Split == layout)
            last++;
        return (first, last);
    }

    // Moves `tab` to position `index` of the tab list (as counted before the move), keeping the active tab.
    private void MoveTab(TabContent tab, int index)
    {
        TabContent? active = ActiveTab;
        int from = _tabs.IndexOf(tab);
        if (from < 0)
            return;
        _tabs.RemoveAt(from);
        _tabs.Insert(index > from ? index - 1 : index, tab);
        _active = active is null ? -1 : _tabs.IndexOf(active);
    }

    // Takes `tab` out of its split view (without moving it); a split view left with one pane becomes a plain tab.
    private void LeaveSplit(TabContent tab)
    {
        if (tab.Split is not { } layout)
            return;
        layout.Remove(tab);
        tab.Split = null;
        if (layout.Count == 1)
            layout.Items[0].Split = null;
    }

    // Puts the panes of `layout` next to each other in the tab strip, in reading order, where the first of them is.
    private void GatherPanes(PaneLayout<TabContent> layout)
    {
        IReadOnlyList<TabContent> panes = layout.Items;
        TabContent? active = ActiveTab;
        int at = panes.Min(p => _tabs.IndexOf(p));
        foreach (TabContent pane in panes)
            _tabs.Remove(pane);
        _tabs.InsertRange(Math.Min(at, _tabs.Count), panes);
        _active = active is null ? -1 : _tabs.IndexOf(active);
    }

    // ---- split view actions ----

    /// <summary>
    /// Arranges the active tab and the tabs after it (then before it) in <paramref name="preset"/>. Tabs already in the
    /// active tab's split view come first; missing panes open a new-tab page.
    /// </summary>
    public void ApplyLayout(LayoutPreset preset)
    {
        if (ActiveTab is not { } active)
            return;
        int count = PaneLayout<TabContent>.PaneCount(preset);
        PaneLayout<TabContent>? old = active.Split;
        var panes = new List<TabContent>(old?.Items ?? [active]);
        if (panes.Count > count)
        {
            // Keep the active tab; the others that don't fit become tabs of their own again.
            panes.Remove(active);
            panes = [active, .. panes.Take(count - 1)];
            panes = old!.Items.Where(panes.Contains).ToList();
        }
        (int first, int last) = Block(_active);
        foreach (TabContent tab in _tabs.Skip(last + 1).Concat(_tabs.Take(first).Reverse()).ToList())
        {
            if (panes.Count >= count)
                break;
            if (tab.Split is null)
                panes.Add(tab);
        }
        foreach (TabContent tab in old?.Items ?? [])
            tab.Split = null;
        while (panes.Count < count)
        {
            var home = new HomeTabContent(this);
            Attach(home, Math.Min(Block(_tabs.IndexOf(panes[^1])).Last + 1, _tabs.Count));
            panes.Add(home);
        }
        var layout = PaneLayout<TabContent>.FromPreset(preset, panes);
        foreach (TabContent pane in panes)
            pane.Split = layout;
        GatherPanes(layout);
        UpdatePanes();
        SyncChrome();
    }

    /// <summary>Ends the active tab's split view: its panes become tabs of their own again (the active one stays on screen).</summary>
    public void Unsplit()
    {
        if (ActiveTab?.Split is not { } layout)
            return;
        foreach (TabContent tab in layout.Items)
            tab.Split = null;
        UpdatePanes();
        SyncChrome();
    }

    /// <summary>
    /// Splits the pane of <paramref name="tab"/> (default: the active tab) and opens a new-tab page beside it
    /// (<see cref="SplitOrientation.Horizontal"/>) or below it; the new page gets the keyboard.
    /// </summary>
    public void SplitPane(SplitOrientation orientation, TabContent? tab = null)
    {
        tab ??= ActiveTab;
        if (tab is null || !_tabs.Contains(tab))
            return;
        var home = new HomeTabContent(this);
        PaneLayout<TabContent> layout = tab.Split ?? new PaneLayout<TabContent>(tab);
        tab.Split = layout;
        layout.SplitAt(tab, home, orientation);
        Attach(home, _tabs.IndexOf(tab) + 1);
        home.Split = layout;
        GatherPanes(layout);
        ActivateTab(home);
        home.FocusQuickConnect();
    }

    /// <summary>Shows <paramref name="tab"/> beside the active tab, in the active tab's split view (started if needed).</summary>
    public void ShowBeside(TabContent tab)
    {
        if (ActiveTab is not { } active || tab == active || !_tabs.Contains(tab) || (tab.Split is not null && tab.Split == active.Split))
            return;
        if (tab.Split is not null)
        {
            // Out of its old split view's block first, so that block stays together in the strip.
            MoveTab(tab, Block(_tabs.IndexOf(tab)).Last + 1);
            LeaveSplit(tab);
        }
        PaneLayout<TabContent> layout = active.Split ?? new PaneLayout<TabContent>(active);
        active.Split = layout;
        layout.SplitAt(active, tab, SplitOrientation.Horizontal);
        tab.Split = layout;
        GatherPanes(layout);
        ActivateTab(tab);
        UpdatePanes(); // ActivateTab returns early when `tab` was already active
        SyncChrome();
    }

    /// <summary>Takes <paramref name="tab"/> out of its split view into a tab of its own, placed after the split view.</summary>
    public void MoveToOwnTab(TabContent tab)
    {
        if (tab.Split is null)
            return;
        int last = Block(_tabs.IndexOf(tab)).Last;
        LeaveSplit(tab);
        MoveTab(tab, last + 1);
        UpdatePanes();
        SyncChrome();
    }

    /// <summary>Maximizes the pane of <paramref name="tab"/> (default: the active tab) in its split view, or restores it.</summary>
    public void ToggleZoom(TabContent? tab = null)
    {
        tab ??= ActiveTab;
        if (tab?.Split is not { } layout)
            return;
        layout.Zoomed = layout.Zoomed == tab ? null : tab;
        if (tab != ActiveTab)
            ActivateTab(tab);
        UpdatePanes();
        SyncChrome();
    }

    /// <summary>Moves the keyboard to the pane next to the active one in <paramref name="direction"/>.</summary>
    public void FocusPane(PaneDirection direction)
    {
        if (ActiveTab is not { Split: not null } active)
            return;
        if (PaneLayout<TabContent>.Neighbor(_shownPanes, active, direction) is { } next)
            ActivateTab(next);
    }

    // Every frame: remembers whether a menu or dialog is open, and keeps the active tab on the pane with the keyboard.
    private void OnUiTick()
    {
        _overlayWasOpen = HasModal || Menu.IsOpen;
        if (!_overlayWasOpen && IsActive && !_torndown)
            FollowFocus();
        TickWorkspace();
        TickUpdates();
    }

    // A click anywhere in a pane of the split view on screen makes it the active tab. Runs after the click itself has
    // been delivered (and has focused whatever it hit, or opened its context menu), so ActivateTab keeps that focus.
    // Clicks meant for a menu or a dialog (including the one that closes a menu) are not for the panes; the menu may
    // already be closed by now, so its state at the last frame counts.
    private void OnViewMouseDown(object? sender, MouseEventArgs e)
    {
        if (_torndown || !IsActive || HasModal || _overlayWasOpen || ActiveTab?.Split is null)
            return;
        var origin = _content.Transform.Computed;
        float x = e.Global.X - origin.X, y = e.Global.Y - origin.Y;
        foreach (PaneLayout<TabContent>.PaneRect pane in _shownPanes)
        {
            if (!pane.Rect.Contains(x, y))
                continue;
            // The title bar handles its own clicks (its close button must not focus the pane it closes).
            if (y < pane.Rect.Top + PaneHeader.Height)
                return;
            if (pane.Item != ActiveTab && _tabs.Contains(pane.Item))
                ActivateTab(pane.Item);
            return;
        }
    }

    // The keyboard can reach another pane without a click in it (Tab / Shift+Tab, a closed menu or dialog giving the
    // focus back): that pane becomes the active tab.
    private void FollowFocus()
    {
        if (ActiveKeyboardElement is not { } focused || ActiveTab is not { Split: { } layout } active)
            return;
        for (Blossom.Core.Visual.VisualElement? el = focused; el is not null; el = el.Parent)
        {
            if (el is not TabContent pane)
                continue;
            if (pane != active && pane.Split == layout && pane.IsShown)
                ActivateTab(pane);
            return;
        }
    }

    // ---- menus ----

    /// <summary>The split-view menu (the layout button in the tab strip), dropping down from window rect (<paramref name="x"/>, <paramref name="y"/>, <paramref name="h"/>).</summary>
    public void ShowLayoutMenu(float x, float y, float h)
    {
        PaneLayout<TabContent>? layout = ActiveTab?.Split;
        var items = new List<MenuItem>
        {
            new() { Text = "Split view · the current tab and the ones after it", IsHeader = true },
            new() { Text = "Single tab", Icon = "layout-single", IsChecked = layout is null, Action = Unsplit },
        };
        foreach ((LayoutPreset preset, string text, string icon) in Presets)
        {
            items.Add(new MenuItem { Text = text, Icon = icon, IsChecked = layout?.Preset == preset, Action = () => ApplyLayout(preset) });
        }
        items.Add(MenuItem.Separator);
        items.AddRange(PaneMenuItems(ActiveTab));
        Menu.Show(items, x, y + h + 6, 260, h);
    }

    /// <summary>Context menu of <paramref name="tab"/> (a tab in the strip or a pane's title bar) at window point (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public void ShowTabMenu(TabContent tab, float x, float y)
    {
        var items = new List<MenuItem>();
        TabContent? active = ActiveTab;
        if (active is not null && tab != active && (tab.Split is null || tab.Split != active.Split))
        {
            items.Add(new MenuItem { Text = "Show beside the current tab", Icon = "layout-columns", Action = () => ShowBeside(tab) });
            items.Add(MenuItem.Separator);
        }
        items.AddRange(PaneMenuItems(tab));
        items.Add(MenuItem.Separator);
        items.Add(new MenuItem { Text = "New tab", Icon = "plus", Hint = "Ctrl+Shift+T", Action = NewTab });
        items.Add(new MenuItem { Text = "Close tab", Icon = "x", Hint = tab == active ? "Ctrl+Shift+W" : null, Action = () => CloseTab(tab) });
        Menu.Show(items, x, y);
    }

    /// <summary>Split, maximize and "own tab" entries for <paramref name="tab"/> (also in the terminal's context menu).</summary>
    public IEnumerable<MenuItem> PaneMenuItems(TabContent? tab)
    {
        // The shortcuts act on the active pane; a right-clicked pane on screen becomes it.
        bool active = tab is { IsShown: true };
        yield return new MenuItem
        {
            Text = "Split right", Icon = "layout-columns", Hint = active ? "Ctrl+Shift+E" : null, IsEnabled = tab is not null,
            Action = () => SplitPane(SplitOrientation.Horizontal, tab),
        };
        yield return new MenuItem
        {
            Text = "Split down", Icon = "layout-rows", Hint = active ? "Ctrl+Shift+O" : null, IsEnabled = tab is not null,
            Action = () => SplitPane(SplitOrientation.Vertical, tab),
        };
        if (tab?.Split is not { } layout)
            yield break;
        bool zoomed = layout.Zoomed == tab;
        yield return new MenuItem
        {
            Text = zoomed ? "Restore pane" : "Maximize pane", Icon = zoomed ? "minimize" : "maximize", Hint = active ? "Ctrl+Shift+X" : null,
            Action = () => ToggleZoom(tab),
        };
        yield return new MenuItem { Text = "Move to its own tab", Icon = "tab-out", Action = () => MoveToOwnTab(tab) };
    }

    /// <summary>All tabs, to jump to one (the tab strip's list button, shown when not all tabs fit).</summary>
    public void ShowTabList(float x, float y, float h)
    {
        var items = new List<MenuItem> { new() { Text = $"{_tabs.Count} open tabs", IsHeader = true } };
        for (int i = 0; i < _tabs.Count; i++)
        {
            TabContent tab = _tabs[i];
            int index = i;
            items.Add(new MenuItem
            {
                Text = tab.Split is null ? tab.Title : $"{tab.Title}  ·  split view",
                Icon = tab is HomeTabContent ? "plus" : "terminal",
                Hint = tab.Status switch
                {
                    TabStatus.Connected => "Connected",
                    TabStatus.Connecting => "Connecting…",
                    TabStatus.Failed => "Failed",
                    TabStatus.Closed => "Not connected",
                    _ => null,
                },
                IsChecked = i == _active,
                IsDanger = tab.NeedsAttention,
                Action = () => ActivateTab(index),
            });
        }
        Menu.Show(items, x, y + h + 6, 300, h);
    }
}
