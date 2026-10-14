using System;
using System.Collections.Generic;
using SkiaSharp;
using TGK.Client.Controls;

namespace TGK.Client.Main;

/// <summary>
/// A pane of the layout <see cref="PaneDropOverlay"/> previews: <see cref="Key"/> (a tab, or what is dragged) at
/// <see cref="Rect"/>. A pane that was not in the previous preview starts at <see cref="From"/>; the pane being dropped
/// (<see cref="Dropped"/>) is highlighted.
/// </summary>
public readonly record struct PreviewPane(object Key, string Title, SKRect Rect, SKRect From, bool Dropped);

/// <summary>
/// Over the content area while a tab, a pane or a saved host is dragged over it. Draws nothing until the pointer is in
/// a drop zone; then the panes are drawn as they will be after the drop, the dropped one highlighted: panes glide to
/// their new places and a new pane grows from the edge it comes in at. Click-through; content-local coordinates.
/// </summary>
public sealed class PaneDropOverlay : Control
{
    private const float TauMs = 55; // each frame moves 1 - e^(-dt/τ) of the way to the target
    private const float Radius = Theme.RadiusLg;

    private static readonly SKPaint DashPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 2,
        PathEffect = SKPathEffect.CreateDash([7, 5], 0),
    };

    private static readonly SKPaint LayerPaint = new();

    private sealed class Ghost(object key)
    {
        public object Key { get; } = key;
        public string Title = "";
        public SKRect Rect, Target;
        public bool Dropped;
        public float Alpha, TargetAlpha = 1;
    }

    private readonly List<Ghost> _ghosts = [];
    private string? _label;
    private float _preview, _previewTarget;
    private long _lastTick = -1;
    private bool _ticking;

    public PaneDropOverlay()
    {
        IsClickthrough = true;
    }

    /// <summary>
    /// Previews the layout after the drop (no panes: none), with <paramref name="label"/> on the dropped pane. Panes
    /// already shown move to their new places; the others fade out.
    /// </summary>
    public void SetPreview(IReadOnlyList<PreviewPane> panes, string? label)
    {
        foreach (Ghost ghost in _ghosts)
            ghost.TargetAlpha = 0;
        foreach (PreviewPane pane in panes)
        {
            // A pane that had (almost) faded out starts over from where it comes from.
            Ghost? ghost = _ghosts.Find(g => g.Key.Equals(pane.Key));
            if (ghost is not null && ghost.Alpha < 0.05f)
            {
                _ghosts.Remove(ghost);
                ghost = null;
            }
            if (ghost is null)
            {
                ghost = new Ghost(pane.Key) { Rect = pane.From };
                _ghosts.Add(ghost);
            }
            ghost.Title = pane.Title;
            ghost.Target = pane.Rect;
            ghost.Dropped = pane.Dropped;
            ghost.TargetAlpha = 1;
        }
        // The dropped pane is drawn last, over the others.
        if (_ghosts.FindIndex(g => g.Dropped) is >= 0 and int dropped && dropped < _ghosts.Count - 1)
        {
            Ghost ghost = _ghosts[dropped];
            _ghosts.RemoveAt(dropped);
            _ghosts.Add(ghost);
        }
        _label = label;
        _previewTarget = panes.Count > 0 ? 1 : 0;
        Animate();
        InvalidatePaint();
    }

    /// <summary>Clears everything at once (the drag ended); the next drag starts from nothing.</summary>
    public void Reset()
    {
        _ghosts.Clear();
        _label = null;
        _preview = _previewTarget = 0;
        InvalidatePaint();
    }

    private void Animate()
    {
        if (_ticking)
            return;
        _ticking = true;
        _lastTick = UiClock.NowMs;
        UiClock.Tick += OnTick;
    }

    private void OnTick()
    {
        if (IsDisposed || !Visible)
        {
            UiClock.Tick -= OnTick;
            _ticking = false;
            return;
        }
        long now = UiClock.NowMs;
        float k = 1 - MathF.Exp(-Math.Clamp(now - _lastTick, 0, 100) / TauMs);
        _lastTick = now;
        bool moving = Approach(ref _preview, _previewTarget, k);
        for (int i = _ghosts.Count - 1; i >= 0; i--)
        {
            Ghost g = _ghosts[i];
            moving |= Approach(ref g.Alpha, g.TargetAlpha, k);
            moving |= Approach(ref g.Rect, g.Target, k);
            if (g.TargetAlpha == 0 && g.Alpha == 0)
                _ghosts.RemoveAt(i);
        }
        InvalidatePaint();
        if (!moving)
        {
            UiClock.Tick -= OnTick;
            _ticking = false;
        }
    }

    // Moves `value` towards `target` (snapping when close); whether it is still on its way.
    private static bool Approach(ref float value, float target, float k)
    {
        value += (target - value) * k;
        if (Math.Abs(target - value) >= 0.004f)
            return true;
        value = target;
        return false;
    }

    private static bool Approach(ref SKRect r, SKRect target, float k)
    {
        r = new SKRect(r.Left + (target.Left - r.Left) * k, r.Top + (target.Top - r.Top) * k,
            r.Right + (target.Right - r.Right) * k, r.Bottom + (target.Bottom - r.Bottom) * k);
        if (Math.Abs(target.Left - r.Left) + Math.Abs(target.Top - r.Top) + Math.Abs(target.Right - r.Right) + Math.Abs(target.Bottom - r.Bottom) >= 0.5f)
            return true;
        r = target;
        return false;
    }

    protected override void Paint(SKCanvas c)
    {
        // The panes all but hidden while the preview shows the layout after the drop.
        if (_preview > 0)
            Gfx.FillRect(c, new SKRect(0, 0, W, H), Theme.AppBg.WithAlpha((byte)(230 * _preview)));
        foreach (Ghost ghost in _ghosts)
            InLayer(c, ghost.Alpha, () => DrawGhost(c, ghost));
    }

    // Draws with `alpha` applied to everything `draw` paints.
    private static void InLayer(SKCanvas c, float alpha, Action draw)
    {
        if (alpha <= 0.004f)
            return;
        if (alpha >= 0.996f)
        {
            draw();
            return;
        }
        LayerPaint.Color = SKColors.White.WithAlpha((byte)(alpha * 255));
        c.SaveLayer(LayerPaint);
        draw();
        c.Restore();
    }

    private void DrawGhost(SKCanvas c, Ghost g)
    {
        SKRect r = g.Rect;
        if (r.Width < 3 || r.Height < 3)
            return;
        float radius = Math.Min(Radius, Math.Min(r.Width, r.Height) / 2f);
        bool roomy = r.Width > 48 && r.Height > 28;
        float titleY = r.MidY;
        if (g.Dropped)
        {
            Gfx.FillRound(c, r, radius, Theme.Mix(Theme.AppBg, Theme.Accent, 0.16f));
            DashPaint.Color = Theme.Accent;
            c.DrawRoundRect(SKRect.Inflate(r, -1, -1), Math.Max(0, radius - 1), Math.Max(0, radius - 1), DashPaint);
            if (!roomy)
                return;
            Gfx.Text(c, g.Title, r.MidX, titleY, Theme.FontMd, Theme.WeightSemibold, Theme.TextPrimary, TextAlignment.Center, r.Width - 24);
            if (_label is not null)
                DrawLabel(c, _label, r.MidX, titleY + 32);
            return;
        }
        Gfx.FillRound(c, r, radius, Theme.Surface);
        Gfx.StrokeRound(c, r, radius, new SKColor(255, 255, 255, 120), 1.5f);
        if (roomy)
            Gfx.Text(c, g.Title, r.MidX, titleY, Theme.FontBase, Theme.WeightRegular, Theme.TextMuted, TextAlignment.Center, r.Width - 24);
    }

    private static void DrawLabel(SKCanvas c, string label, float cx, float cy)
    {
        float w = Gfx.Measure(label, Theme.FontBase, Theme.WeightSemibold) + 24;
        var pill = new SKRect(cx - w / 2f, cy - 14, cx + w / 2f, cy + 14);
        Gfx.FillRound(c, pill, 14, Theme.Accent);
        Gfx.Text(c, label, cx, cy, Theme.FontBase, Theme.WeightSemibold, Theme.TextOnAccent, TextAlignment.Center);
    }

}

/// <summary>A saved host dragged from the sidebar: its name in a pill that follows the pointer. Click-through.</summary>
public sealed class DragChip : Control
{
    public const float Height = 30;
    private string _text = "";
    private SKColor _dot;

    public DragChip()
    {
        IsClickthrough = true;
    }

    public void Set(string text, SKColor dot)
    {
        if (_text == text && _dot == dot)
            return;
        _text = text;
        _dot = dot;
        InvalidatePaint();
    }

    public float PreferredWidth => Math.Min(260, Gfx.Measure(_text, Theme.FontBase, Theme.WeightSemibold) + 42);

    protected override void Paint(SKCanvas c)
    {
        var r = new SKRect(0, 0, W, H);
        Gfx.Shadow(c, r, H / 2f, 12, 3, new SKColor(0, 0, 0, 140));
        Gfx.FillRound(c, r, H / 2f, Theme.SurfaceRaised);
        Gfx.StrokeRound(c, r, H / 2f, Theme.Accent, 1.5f);
        Gfx.Circle(c, 16, H / 2f, 4, _dot);
        Gfx.Text(c, _text, 28, H / 2f, Theme.FontBase, Theme.WeightSemibold, Theme.TextPrimary, TextAlignment.Left, W - 40);
    }
}
