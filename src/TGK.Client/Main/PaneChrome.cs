using System;
using System.Collections.Generic;
using Blossom.Core.Input;
using Silk.NET.Input;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Views;

namespace TGK.Client.Main;

/// <summary>
/// The title bar of a pane in a split view: status dot, title, maximize/restore and close. The pane with the keyboard
/// is highlighted. A click focuses the pane, a double-click maximizes or restores it, a right-click opens the pane menu
/// and a middle-click closes it.
/// </summary>
public sealed class PaneHeader : Control
{
    public const float Height = 26;
    private const float ButtonSize = 20, ButtonGap = 2, PadRight = 4, DoubleClickMs = 400;

    private enum Part
    {
        None,
        Zoom,
        Close,
    }

    private readonly MainView _main;
    private TabContent? _tab;
    private bool _focused, _zoomed;
    private Part _hover, _pressed;
    private bool _middlePressed;
    private long _lastClickMs = long.MinValue / 2;

    public PaneHeader(MainView main)
    {
        _main = main;
        Events.OnMouseMove += (_, e) => SetAndPaint(ref _hover, PartAt(e.Relative.X, e.Relative.Y));
        Events.OnMouseDown += OnDown;
        Events.OnMouseUp += OnUp;
    }

    public TabContent? Tab => _tab;

    /// <summary>Shows <paramref name="tab"/>; <paramref name="focused"/>: it is the active tab; <paramref name="zoomed"/>: it is maximized.</summary>
    public void Set(TabContent? tab, bool focused, bool zoomed)
    {
        bool changed = _tab != tab || _focused != focused || _zoomed != zoomed;
        _tab = tab;
        _focused = focused;
        _zoomed = zoomed;
        if (changed)
            InvalidatePaint();
    }

    private SKRect CloseRect => new(W - PadRight - ButtonSize, (H - ButtonSize) / 2f, W - PadRight, (H + ButtonSize) / 2f);

    private SKRect ZoomRect
    {
        get
        {
            SKRect close = CloseRect;
            return new SKRect(close.Left - ButtonGap - ButtonSize, close.Top, close.Left - ButtonGap, close.Bottom);
        }
    }

    private Part PartAt(float x, float y) => CloseRect.Contains(x, y) ? Part.Close : ZoomRect.Contains(x, y) ? Part.Zoom : Part.None;

    protected override void OnHoverChanged()
    {
        if (!IsHovered)
            _hover = Part.None;
    }

    private void OnDown(object? sender, MouseEventArgs e)
    {
        e.Handled = true;
        if (_tab is not { } tab)
            return;
        Part part = PartAt(e.Relative.X, e.Relative.Y);
        switch (e.Button)
        {
            case 0:
                _pressed = part;
                if (part != Part.None)
                    break;
                _main.ActivateTab(tab);
                long now = UiClock.NowMs;
                if (now - _lastClickMs <= DoubleClickMs)
                {
                    _lastClickMs = long.MinValue / 2;
                    _main.ToggleZoom(tab);
                }
                else
                {
                    _lastClickMs = now;
                }
                break;
            case 1:
                _main.ActivateTab(tab);
                _main.ShowTabMenu(tab, e.Global.X, e.Global.Y);
                break;
            case 2:
                _middlePressed = true;
                break;
        }
    }

    private void OnUp(object? sender, MouseEventArgs e)
    {
        e.Handled = true;
        Part part = PartAt(e.Relative.X, e.Relative.Y);
        Part pressed = _pressed;
        bool middle = _middlePressed;
        _pressed = Part.None;
        _middlePressed = false;
        if (_tab is not { } tab)
            return;
        bool inside = e.Relative.X >= 0 && e.Relative.Y >= 0 && e.Relative.X < W && e.Relative.Y < H;
        if (e.Button == 2 && middle && inside)
            _main.CloseTab(tab);
        else if (e.Button == 0 && pressed != Part.None && pressed == part)
        {
            if (part == Part.Close)
                _main.CloseTab(tab);
            else
                _main.ToggleZoom(tab);
        }
    }

    protected override void Paint(SKCanvas c)
    {
        if (_tab is not { } tab)
            return;
        var r = new SKRect(0, 0, W, H);
        Gfx.FillRect(c, r, _focused ? Theme.SurfaceRaised : Theme.AppBg);
        if (_focused)
            Gfx.FillRect(c, new SKRect(0, 0, W, 2), Theme.Accent);
        float x = 10, cy = H / 2f + (_focused ? 1 : 0);
        if (tab.Status != TabStatus.None)
        {
            Gfx.Circle(c, x + 3.5f, cy, 3.5f, TabStrip.StatusColor(tab.Status));
            x += 14;
        }
        SKColor title = _focused ? Theme.TextPrimary : tab.NeedsAttention ? Theme.Warning : Theme.TextSecondary;
        Gfx.Text(c, tab.Title, x, cy, Theme.FontSm, _focused ? Theme.WeightSemibold : Theme.WeightRegular, title,
            TextAlignment.Left, ZoomRect.Left - 8 - x);
        DrawButton(c, ZoomRect, _zoomed ? "minimize" : "maximize", Part.Zoom, cy);
        DrawButton(c, CloseRect, "x", Part.Close, cy);
    }

    private void DrawButton(SKCanvas c, SKRect r, string icon, Part part, float cy)
    {
        bool hover = _hover == part;
        r.Offset(0, cy - r.MidY);
        if (hover)
            Gfx.FillRound(c, r, Theme.RadiusSm, _pressed == part ? Theme.SurfacePressed : Theme.SurfaceHover);
        Icons.Draw(c, icon, r.MidX, r.MidY, part == Part.Close ? 13 : 12, hover ? Theme.TextPrimary : Theme.TextMuted);
    }
}

/// <summary>
/// The gap between two panes of a split view: dragging it resizes them, a double-click makes the panes of that split
/// equal again. Highlighted while hovered or dragged.
/// </summary>
public sealed class PaneDivider : Control
{
    private const float MinPanePx = 100, DoubleClickMs = 400;
    private readonly MainView _main;
    private PaneLayout<TabContent>? _layout;
    private PaneLayout<TabContent>.Split? _split;
    private int _index;
    private bool _dragging;
    private float _dragStart;
    private List<float>? _startWeights;
    private long _lastDownMs = long.MinValue / 2;

    public PaneDivider(MainView main)
    {
        _main = main;
        Events.OnMouseDown += OnDown;
        Events.OnMouseMove += OnMove;
        Events.OnMouseUp += OnUp;
    }

    private bool Across => _split?.Orientation == SplitOrientation.Horizontal;

    public void Set(PaneLayout<TabContent> layout, PaneLayout<TabContent>.DividerRect divider)
    {
        if (_dragging)
            return; // keeps the split it is resizing
        _layout = layout;
        _split = divider.Split;
        _index = divider.Index;
        Cursor = Across ? StandardCursor.HResize : StandardCursor.VResize;
    }

    public void Clear()
    {
        _dragging = false;
        _layout = null;
        _split = null;
        _startWeights = null;
    }

    private float Along(MouseEventArgs e) => Across ? e.Global.X : e.Global.Y;

    private void OnDown(object? sender, MouseEventArgs e)
    {
        e.Handled = true;
        if (e.Button != 0 || _split is null)
            return;
        long now = UiClock.NowMs;
        if (now - _lastDownMs <= DoubleClickMs)
        {
            _lastDownMs = long.MinValue / 2;
            PaneLayout<TabContent>.Equalize(_split);
            _main.RelayoutPanes();
            return;
        }
        _lastDownMs = now;
        _dragging = true;
        _dragStart = Along(e);
        _startWeights = [.. _split.Weights];
        CapturePointer();
        InvalidatePaint();
    }

    private void OnMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging || _layout is null || _split is null || _startWeights is null)
            return;
        e.Handled = true;
        // Always from the weights at the start of the drag, so a pane held at its minimum follows the pointer back.
        for (int i = 0; i < _startWeights.Count && i < _split.Weights.Count; i++)
            _split.Weights[i] = _startWeights[i];
        _layout.MoveDivider(_split, _index, Along(e) - _dragStart, MinPanePx);
        _main.RelayoutPanes();
    }

    private void OnUp(object? sender, MouseEventArgs e)
    {
        e.Handled = true;
        if (!_dragging)
            return;
        _dragging = false;
        _startWeights = null;
        InvalidatePaint();
    }

    protected override void Paint(SKCanvas c)
    {
        if (!_dragging && !IsHovered)
            return;
        SKColor color = _dragging ? Theme.Accent : Theme.BorderStrong;
        if (Across)
            Gfx.FillRect(c, new SKRect(W / 2f - 1, 0, W / 2f + 1, H), color);
        else
            Gfx.FillRect(c, new SKRect(0, H / 2f - 1, W, H / 2f + 1), color);
    }
}
