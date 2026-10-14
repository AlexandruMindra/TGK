using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using TGK.Client.Main;
using TGK.Core.Models;
using TabLayout = TGK.Client.Main.PaneLayout<TGK.Client.Main.TabContent>;

namespace TGK.Client.Views;

/// <summary>What a drop over the content area does.</summary>
public enum PaneDropKind
{
    None,
    /// <summary>Into a new pane of the split view on screen, at <see cref="PaneDrop.Placement"/> (started if needed).</summary>
    Insert,
    /// <summary>Changes places with the target pane (both in the same split view).</summary>
    Swap,
    /// <summary>Takes the place of the target pane, a new-tab page, which closes.</summary>
    Replace,
    /// <summary>Out of its split view into a tab of its own (a pane's title bar dragged onto the tab strip).</summary>
    OwnTab,
    /// <summary>A saved host dragged onto the tab strip: connects in a new tab.</summary>
    NewTab,
}

/// <param name="Center">Content-local center of the drop's button (none for <see cref="PaneDropKind.OwnTab"/> and <see cref="PaneDropKind.NewTab"/>).</param>
public readonly record struct PaneDrop(PaneDropKind Kind, TabContent? Target, TabLayout.Placement? Placement, SKPoint Center)
{
    public static readonly PaneDrop None = new(PaneDropKind.None, null, null, SKPoint.Empty);
}

// Drag and drop onto the content area: a pane's title bar, a tab from the tab strip or a saved host from the sidebar.
// While dragging, the panes on screen show buttons where it can go (see PaneLayout.DropSpots): at a pane's outer edges
// beside that pane, at the ends of a divider beside that whole split, in the middle of a divider between its two
// panes; in the middle of a pane of its own split view to swap the two, in the middle of a new-tab page to take its
// place. The button under the pointer previews the layout after the drop. Without a split view on screen the active
// tab counts as the one pane, so a drop at its edge starts a split view. A title bar dragged onto the tab strip makes
// the pane a tab of its own; a host dragged there opens in a new tab.
public sealed partial class MainView
{
    private const float ButtonInset = 26, HitRadius = 28, KeepRadius = 38, ChipOffset = 14;

    private PaneDropOverlay? _dropOverlay;
    private DragChip? _dragChip;
    private object? _dragged; // the TabContent or HostEntry being dragged over the content area
    private TabLayout? _dropLayout; // the layout the drops are in: the split view on screen, or a one-pane layout of the active tab
    private readonly List<PaneDrop> _drops = [];
    private PaneDrop _drop = PaneDrop.None; // the one under the pointer

    /// <summary>
    /// A tab or a pane is dragged: shows where it can be dropped and highlights the place at window point
    /// (<paramref name="x"/>, <paramref name="y"/>); nothing while <paramref name="x"/> is null (back over the tab strip).
    /// </summary>
    public void UpdatePaneDrag(TabContent dragged, float? x, float y)
    {
        if (x is { } px && _tabs.Contains(dragged))
            UpdateDrag(dragged, px, y);
        else
            HideDrop();
    }

    /// <summary>The drag ended: moves <paramref name="dragged"/> to the highlighted place (unless <paramref name="drop"/> is false).</summary>
    /// <returns>Whether it was dropped somewhere.</returns>
    public bool EndPaneDrag(TabContent dragged, bool drop = true) => EndDrag(dragged, drop);

    /// <summary>A saved host is dragged from the sidebar, the pointer at window point (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public void UpdateHostDrag(HostEntry host, float x, float y)
    {
        if (_torndown)
            return;
        if (_dragChip is null)
        {
            _dragChip = new DragChip { ZIndex = 300 };
            _root.AddChild(_dragChip);
        }
        _dragChip.Set(host.DisplayName, Theme.ParseHex(host.TagColor, Theme.Idle));
        _dragChip.Visible = true;
        _dragChip.Transform.SetLocalFrame(x + ChipOffset, y + ChipOffset, _dragChip.PreferredWidth, DragChip.Height);
        UpdateDrag(host, x, y);
    }

    /// <summary>The drag of a saved host ended: opens it at the highlighted place (unless <paramref name="drop"/> is false).</summary>
    /// <returns>Whether it was dropped somewhere.</returns>
    public bool EndHostDrag(HostEntry host, bool drop = true)
    {
        if (_dragChip is not null)
            _dragChip.Visible = false;
        return EndDrag(host, drop);
    }

    private void UpdateDrag(object dragged, float x, float y)
    {
        if (_torndown || ActiveTab is null)
        {
            HideDrop();
            return;
        }
        if (_dropOverlay is null)
        {
            _dropOverlay = new PaneDropOverlay { ZIndex = 100, Visible = false };
            _content.AddChild(_dropOverlay);
        }
        if (_dragged != dragged)
        {
            _dropOverlay.Reset();
            _dropLayout = null;
            _drop = PaneDrop.None;
        }
        _dragged = dragged;
        var origin = _content.Transform.Computed;
        float w = origin.Width, h = origin.Height, cx = x - origin.X, cy = y - origin.Y;
        BuildDrops(dragged, w, h);
        _drop = cx < 0 || cx >= w || cy >= h ? PaneDrop.None
            : cy < 0 ? StripDrop(dragged, cy)
            : NearestDrop(cx, cy);

        bool show = _drops.Count > 0 || _drop.Kind != PaneDropKind.None;
        _dropOverlay.Visible = show;
        if (!show)
            return;
        _dropOverlay.Transform.SetLocalFrame(0, 0, w, h);
        _dropOverlay.SetButtons(_drops.Select(d => new DropButton(d.Center, IconOf(d))).ToList(), _drops.IndexOf(_drop));
        string? label = _drop.Kind switch
        {
            PaneDropKind.OwnTab => "Move to its own tab",
            PaneDropKind.NewTab => "Open in a new tab",
            _ => null,
        };
        _dropOverlay.SetPreview(Preview(_drop, dragged, PaneArea(w, h)), label);
    }

    private void HideDrop()
    {
        _dragged = null;
        _dropLayout = null;
        _drops.Clear();
        _drop = PaneDrop.None;
        if (_dropOverlay is null)
            return;
        _dropOverlay.Reset();
        _dropOverlay.Visible = false;
    }

    // The drops for `dragged` over the content area (`w` × `h`): the spots of the split view on screen (or of the
    // active tab alone), and the middle of the panes it can swap with or take the place of.
    private void BuildDrops(object dragged, float w, float h)
    {
        _drops.Clear();
        TabContent active = ActiveTab!;
        var tab = dragged as TabContent;
        // A one-pane layout is kept for the whole drag, so a drop is the same one from move to move.
        TabLayout layout = active.Split
            ?? (_dropLayout is { Count: 1 } solo && solo.Items[0] == active ? solo : new TabLayout(active));
        _dropLayout = layout;
        SKRect area = PaneArea(w, h);
        foreach (TabLayout.DropSpot spot in layout.DropSpots(area, PaneGap, ButtonInset, tab))
            _drops.Add(new PaneDrop(PaneDropKind.Insert, null, spot.Placement, spot.Center));
        foreach (TabLayout.PaneRect pane in PanesOnScreen(area))
        {
            if (pane.Item == tab)
                continue;
            PaneDropKind kind = tab?.Split is { } own && own == pane.Item.Split ? PaneDropKind.Swap
                : pane.Item is HomeTabContent && (tab is null || pane.Item.Split is not null) ? PaneDropKind.Replace
                : PaneDropKind.None;
            if (kind != PaneDropKind.None)
                _drops.Add(new PaneDrop(kind, pane.Item, null, new SKPoint(pane.Rect.MidX, pane.Rect.MidY)));
        }
    }

    // Above the content, over the tab strip: a pane leaves its split view, a host opens in a new tab.
    private static PaneDrop StripDrop(object dragged, float cy)
    {
        if (cy < -Theme.TabStripHeight)
            return PaneDrop.None;
        return dragged switch
        {
            TabContent { Split: not null } tab => new PaneDrop(PaneDropKind.OwnTab, tab, null, SKPoint.Empty),
            HostEntry => new PaneDrop(PaneDropKind.NewTab, null, null, SKPoint.Empty),
            _ => PaneDrop.None,
        };
    }

    // The drop whose button is under content point (`cx`, `cy`). The one already chosen stays chosen a little farther
    // out, so the choice does not flicker between neighbours.
    private PaneDrop NearestDrop(float cx, float cy)
    {
        PaneDrop best = PaneDrop.None;
        float bestDistance = float.MaxValue;
        foreach (PaneDrop drop in _drops)
        {
            float distance = SKPoint.Distance(drop.Center, new SKPoint(cx, cy));
            if (distance <= (drop == _drop ? KeepRadius : HitRadius) && distance < bestDistance)
                (best, bestDistance) = (drop, distance);
        }
        return best;
    }

    private static string IconOf(PaneDrop drop) => drop.Kind switch
    {
        PaneDropKind.Swap => "swap",
        PaneDropKind.Replace => "drop-in",
        _ when drop.Placement is { IsBetween: true } => "plus",
        _ => drop.Placement?.Side switch
        {
            PaneDirection.Left => "chevron-left",
            PaneDirection.Right => "chevron-right",
            PaneDirection.Up => "chevron-up",
            _ => "chevron-down",
        },
    };

    // The rectangle the panes of a split view share in a content area of `w` × `h`.
    private static SKRect PaneArea(float w, float h) =>
        new(PanePad, PanePad, Math.Max(PanePad, w - PanePad), Math.Max(PanePad, h - PanePad));

    // The panes on screen (content-local): those of the split view on screen, or the active tab alone in `area`.
    private IReadOnlyList<TabLayout.PaneRect> PanesOnScreen(SKRect area)
    {
        if (ActiveTab is not { } active)
            return [];
        return active.Split is not null ? _shownPanes : [new TabLayout.PaneRect(active, area)];
    }

    // The panes as they will be after `drop` (none when nothing is chosen), for the overlay's preview.
    private List<PreviewPane> Preview(PaneDrop drop, object dragged, SKRect area)
    {
        var panes = new List<PreviewPane>();
        var tab = dragged as TabContent;
        string title = tab?.Title ?? ((HostEntry)dragged).DisplayName;
        IReadOnlyList<TabLayout.PaneRect> shown = PanesOnScreen(area);
        SKRect RectOf(TabContent? t)
        {
            foreach (TabLayout.PaneRect pane in shown)
            {
                if (pane.Item == t)
                    return pane.Rect;
            }
            return area;
        }

        switch (drop.Kind)
        {
            case PaneDropKind.Insert when _dropLayout is { } layout && drop.Placement is { } at:
                (IReadOnlyList<TabLayout.PaneRect> others, SKRect inserted) = layout.ArrangeInserted(tab, at, area, PaneGap);
                foreach (TabLayout.PaneRect pane in others)
                    panes.Add(new PreviewPane(pane.Item, pane.Item.Title, pane.Rect, RectOf(pane.Item), false));
                panes.Add(new PreviewPane(dragged, title, inserted, GrowFrom(inserted, at), true));
                break;
            case PaneDropKind.Swap when drop.Target is { } other:
                foreach (TabLayout.PaneRect pane in shown)
                {
                    if (pane.Item == other)
                        panes.Add(new PreviewPane(other, other.Title, RectOf(tab), pane.Rect, false));
                    else if (pane.Item != tab)
                        panes.Add(new PreviewPane(pane.Item, pane.Item.Title, pane.Rect, pane.Rect, false));
                }
                panes.Add(new PreviewPane(dragged, title, RectOf(other), RectOf(tab), true));
                break;
            case PaneDropKind.Replace when drop.Target is { } home:
                foreach (TabLayout.PaneRect pane in shown)
                {
                    if (pane.Item != home && pane.Item != tab)
                        panes.Add(new PreviewPane(pane.Item, pane.Item.Title, pane.Rect, pane.Rect, false));
                }
                SKRect place = RectOf(home);
                panes.Add(new PreviewPane(dragged, title, place, Shrunk(place), true));
                break;
            case PaneDropKind.OwnTab:
                panes.Add(new PreviewPane(dragged, title, area, RectOf(tab), true));
                break;
            case PaneDropKind.NewTab:
                panes.Add(new PreviewPane(dragged, title, area, Shrunk(area), true));
                break;
        }
        return panes;
    }

    // Where the preview of a new pane grows from: the edge it shares with what it goes beside, or the middle of the gap
    // it opens.
    private static SKRect GrowFrom(SKRect r, TabLayout.Placement at)
    {
        if (at.IsBetween)
        {
            return at.Node is TabLayout.Split { Orientation: SplitOrientation.Horizontal }
                ? new SKRect(r.MidX, r.Top, r.MidX, r.Bottom)
                : new SKRect(r.Left, r.MidY, r.Right, r.MidY);
        }
        return at.Side switch
        {
            PaneDirection.Left => new SKRect(r.Right, r.Top, r.Right, r.Bottom),
            PaneDirection.Right => new SKRect(r.Left, r.Top, r.Left, r.Bottom),
            PaneDirection.Up => new SKRect(r.Left, r.Bottom, r.Right, r.Bottom),
            _ => new SKRect(r.Left, r.Top, r.Right, r.Top),
        };
    }

    private static SKRect Shrunk(SKRect r) => SKRect.Inflate(r, -r.Width * 0.08f, -r.Height * 0.08f);

    private bool EndDrag(object dragged, bool drop)
    {
        PaneDrop target = _drop;
        TabLayout? layout = _dropLayout;
        HideDrop();
        if (!drop || _torndown || target.Kind == PaneDropKind.None)
            return false;
        var tab = dragged as TabContent;
        if (tab is not null && !_tabs.Contains(tab))
            return false;
        switch (target.Kind)
        {
            case PaneDropKind.OwnTab when tab is not null:
                MoveToOwnTab(tab);
                ActivateTab(tab);
                return true;
            case PaneDropKind.NewTab when dragged is HostEntry host:
                ConnectInNewTab(host);
                return true;
            case PaneDropKind.Swap when tab is not null && target.Target is { Split: { } own } other && own == tab.Split:
                own.Swap(tab, other);
                GatherPanes(own);
                break;
            case PaneDropKind.Replace when target.Target is HomeTabContent home && _tabs.Contains(home):
                tab = tab is null ? OpenIn(home, App.SessionTabFactory((HostEntry)dragged)) : MoveInto(home, tab);
                if (tab is null)
                    return false;
                break;
            case PaneDropKind.Insert when layout is not null && target.Placement is { } at:
                bool open = tab is null;
                tab ??= App.SessionTabFactory((HostEntry)dragged);
                if (!InsertPane(tab, open, layout, at))
                    return false;
                break;
            default:
                return false;
        }
        ActivateTab(tab);
        UpdatePanes(); // ActivateTab returns early when `tab` was already active
        SyncChrome();
        return true;
    }

    // Puts `tab` at `at` in `layout`: the split view on screen, or the one-pane layout of the active tab, which becomes
    // a split view. `open`: a new tab, added to the tab list here. An open tab leaves its own split view first (or moves
    // within this one). False when the layout is no longer on screen.
    private bool InsertPane(TabContent tab, bool open, TabLayout layout, TabLayout.Placement at)
    {
        IReadOnlyList<TabContent> panes = layout.Items;
        bool onScreen = panes.All(_tabs.Contains) && (panes.Count == 1 ? panes[0].Split is null : panes[0].Split == layout);
        if (!onScreen || panes.Contains(tab) && at.Node is TabLayout.Leaf { } leaf && leaf.Item == tab)
            return false;
        if (!open && tab.Split is { } old && old != layout)
        {
            // Out of its old split view's block first, so that block stays together in the strip.
            MoveTab(tab, Block(_tabs.IndexOf(tab)).Last + 1);
            LeaveSplit(tab);
        }
        layout.Insert(tab, at);
        foreach (TabContent pane in panes)
            pane.Split = layout;
        if (open)
            Attach(tab, Block(_tabs.IndexOf(panes[0])).Last + 1);
        tab.Split = layout;
        GatherPanes(layout);
        return true;
    }

    // A new session takes the place of `home` (a new-tab page, in a split view or not).
    private TabContent OpenIn(HomeTabContent home, TabContent session)
    {
        ReplaceTab(home, session);
        return session;
    }

    // An open tab (not of home's split view) takes the place of `home`, a new-tab page in a split view, which closes.
    private TabContent? MoveInto(HomeTabContent home, TabContent tab)
    {
        if (home.Split is not { } layout || tab.Split == layout)
            return null;
        if (tab.Split is not null)
        {
            MoveTab(tab, Block(_tabs.IndexOf(tab)).Last + 1);
            LeaveSplit(tab);
        }
        MoveTab(tab, _tabs.IndexOf(home));
        layout.Replace(home, tab);
        home.Split = null;
        tab.Split = layout;
        GatherPanes(layout);
        CloseTab(home);
        return tab;
    }
}
