using System;
using Blossom.Core.Visual;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Main;

namespace TGK.Client.Views;

/// <summary>Where a pane (or a tab) dragged over the content area would go.</summary>
public enum PaneDropKind
{
    None,
    /// <summary>Changes places with the target pane (both in the same split view).</summary>
    Swap,
    Left,
    Right,
    Top,
    Bottom,
    /// <summary>Out of its split view into a tab of its own (a pane's title bar dragged onto the tab strip).</summary>
    OwnTab,
}

/// <param name="Zone">Content-local rectangle highlighted for the drop.</param>
public readonly record struct PaneDrop(TabContent? Target, PaneDropKind Kind, SKRect Zone)
{
    public static readonly PaneDrop None = new(null, PaneDropKind.None, SKRect.Empty);
}

// Drag and drop of panes: a pane's title bar (or a tab from the tab strip) dragged over the content area. Near an edge
// of a pane on screen it goes beside that pane on that side; over the middle of a pane of its own split view the two
// swap places; a title bar dragged onto the tab strip makes the pane a tab of its own. Without a split view on screen
// the active tab counts as the one pane, so dropping a tab at its edge starts a split view.
public sealed partial class MainView
{
    private const float SwapFrom = 0.3f, SwapTo = 0.7f;
    private PaneDropOverlay? _dropOverlay;
    private PaneDrop _paneDrop = PaneDrop.None;

    /// <summary>The drop for <paramref name="dragged"/> at window point (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public PaneDrop PaneDropAt(TabContent dragged, float x, float y)
    {
        if (_torndown || !_tabs.Contains(dragged) || ActiveTab is not { } active)
            return PaneDrop.None;
        var origin = _content.Transform.Computed;
        float cx = x - origin.X, cy = y - origin.Y;
        bool splitOnScreen = active.Split is { Zoomed: null } && _shownPanes.Count > 0;

        if (cy < 0)
        {
            // Above the content: onto the tab strip, out of the split view.
            if (dragged.Split is null || cy < -Theme.TabStripHeight)
                return PaneDrop.None;
            SKRect own = splitOnScreen && _shownPanes.Find(p => p.Item == dragged) is { Item: not null } pane ? pane.Rect : new SKRect(0, 0, origin.Width, origin.Height);
            return new PaneDrop(dragged, PaneDropKind.OwnTab, own);
        }

        TabContent? target = null;
        SKRect r = SKRect.Empty;
        if (splitOnScreen)
        {
            foreach (PaneLayout<TabContent>.PaneRect pane in _shownPanes)
            {
                if (pane.Rect.Contains(cx, cy))
                {
                    (target, r) = (pane.Item, pane.Rect);
                    break;
                }
            }
        }
        else if (cx >= 0 && cx < origin.Width && cy < origin.Height)
        {
            (target, r) = (active, new SKRect(PanePad, PanePad, origin.Width - PanePad, origin.Height - PanePad));
        }
        if (target is null || target == dragged || r.Width <= 0 || r.Height <= 0)
            return PaneDrop.None;

        float fx = (cx - r.Left) / r.Width, fy = (cy - r.Top) / r.Height;
        if (dragged.Split is not null && dragged.Split == target.Split && fx > SwapFrom && fx < SwapTo && fy > SwapFrom && fy < SwapTo)
            return new PaneDrop(target, PaneDropKind.Swap, SKRect.Inflate(r, -6, -6));
        float left = fx, right = 1 - fx, top = fy, bottom = 1 - fy;
        float nearest = Math.Min(Math.Min(left, right), Math.Min(top, bottom));
        PaneDropKind kind = nearest == left ? PaneDropKind.Left : nearest == right ? PaneDropKind.Right : nearest == top ? PaneDropKind.Top : PaneDropKind.Bottom;
        SKRect zone = kind switch
        {
            PaneDropKind.Left => new SKRect(r.Left, r.Top, r.MidX, r.Bottom),
            PaneDropKind.Right => new SKRect(r.MidX, r.Top, r.Right, r.Bottom),
            PaneDropKind.Top => new SKRect(r.Left, r.Top, r.Right, r.MidY),
            _ => new SKRect(r.Left, r.MidY, r.Right, r.Bottom),
        };
        return new PaneDrop(target, kind, zone);
    }

    /// <summary>A drag moved: highlights where <paramref name="dragged"/> would go (nothing when <paramref name="x"/> is null).</summary>
    public void UpdatePaneDrag(TabContent dragged, float? x, float y)
    {
        _paneDrop = x is { } px ? PaneDropAt(dragged, px, y) : PaneDrop.None;
        if (_dropOverlay is null)
        {
            _dropOverlay = new PaneDropOverlay { IsClickthrough = true, ZIndex = 100, Visible = false };
            _content.AddChild(_dropOverlay);
        }
        bool show = _paneDrop.Kind != PaneDropKind.None;
        _dropOverlay.Visible = show;
        if (!show)
            return;
        _dropOverlay.Label = _paneDrop.Kind switch
        {
            PaneDropKind.Swap => "Swap places",
            PaneDropKind.OwnTab => "Move to its own tab",
            _ => "",
        };
        SKRect z = _paneDrop.Zone;
        _dropOverlay.Transform.SetLocalFrame(z.Left, z.Top, z.Width, z.Height);
        _dropOverlay.InvalidatePaint();
    }

    /// <summary>The drag ended: moves <paramref name="dragged"/> to the highlighted place (unless <paramref name="drop"/> is false).</summary>
    /// <returns>Whether it was dropped somewhere.</returns>
    public bool EndPaneDrag(TabContent dragged, bool drop = true)
    {
        PaneDrop target = _paneDrop;
        UpdatePaneDrag(dragged, null, 0);
        if (!drop || target.Kind == PaneDropKind.None || !_tabs.Contains(dragged))
            return false;
        switch (target.Kind)
        {
            case PaneDropKind.OwnTab:
                MoveToOwnTab(dragged);
                ActivateTab(dragged);
                return true;
            case PaneDropKind.Swap when target.Target is { Split: { } layout } other && layout == dragged.Split:
                layout.Swap(dragged, other);
                GatherPanes(layout);
                break;
            case PaneDropKind.Left or PaneDropKind.Right or PaneDropKind.Top or PaneDropKind.Bottom when target.Target is { } other && _tabs.Contains(other):
                bool across = target.Kind is PaneDropKind.Left or PaneDropKind.Right;
                PlaceBeside(dragged, other, across ? SplitOrientation.Horizontal : SplitOrientation.Vertical,
                    after: target.Kind is PaneDropKind.Right or PaneDropKind.Bottom);
                break;
            default:
                return false;
        }
        ActivateTab(dragged);
        UpdatePanes(); // ActivateTab returns early when `dragged` was already active
        SyncChrome();
        return true;
    }

    /// <summary>The highlighted drop zone: an accent-tinted rectangle, with a label for a swap or a move out.</summary>
    private sealed class PaneDropOverlay : Control
    {
        public string Label { get; set; } = "";

        protected override void Paint(SKCanvas c)
        {
            var r = new SKRect(1, 1, W - 1, H - 1);
            Gfx.FillRound(c, r, Theme.Radius, Theme.Accent.WithAlpha(46));
            Gfx.StrokeRound(c, r, Theme.Radius, Theme.Accent, 2);
            if (Label.Length == 0)
                return;
            float w = Gfx.Measure(Label, Theme.FontBase, Theme.WeightSemibold) + 24;
            var pill = new SKRect(W / 2f - w / 2f, H / 2f - 15, W / 2f + w / 2f, H / 2f + 15);
            Gfx.FillRound(c, pill, 15, Theme.Accent);
            Gfx.Text(c, Label, W / 2f, H / 2f, Theme.FontBase, Theme.WeightSemibold, Theme.TextOnAccent, TextAlignment.Center);
        }
    }
}
