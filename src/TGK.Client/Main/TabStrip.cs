using System;
using System.Collections.Generic;
using System.Linq;
using Blossom.Core.Input;
using Silk.NET.Input;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Views;

namespace TGK.Client.Main;

/// <summary>
/// The top bar: sidebar toggle (+ brand while the sidebar is open), the tabs with a "+" button, the split-view button,
/// the sync status chip and the account button. Tabs start where the content column starts so the active tab merges into it.
/// </summary>
public sealed class TabStrip : Control
{
    private const float ToggleSize = 28;
    private readonly IconButton _toggle;
    private readonly TabsArea _tabs;
    private readonly IconButton _layout;
    private readonly SyncChip _sync;
    private readonly AvatarButton _avatar;
    private float _contentLeft;

    public TabStrip(MainView main)
    {
        _toggle = new IconButton("sidebar", 18);
        _toggle.Clicked += main.ToggleSidebar;
        _tabs = new TabsArea(main);
        _layout = new IconButton("layout-columns", 18);
        _layout.Clicked += () =>
        {
            var r = _layout.Transform.Computed;
            main.ShowLayoutMenu(r.X + r.Width - 260, r.Y, r.Height);
        };
        _sync = new SyncChip(main);
        _avatar = new AvatarButton(main);
        AddChild(_toggle);
        AddChild(_tabs);
        AddChild(_layout);
        AddChild(_sync);
        AddChild(_avatar);
    }

    /// <summary>X where the content column (and so the first tab) starts; 0 when the sidebar is hidden.</summary>
    public float ContentLeft
    {
        get => _contentLeft;
        set
        {
            if (Math.Abs(_contentLeft - value) < 0.5f)
                return;
            _contentLeft = value;
            InvalidateLayout();
        }
    }

    public AvatarButton Avatar => _avatar;

    public void SetTabs(IReadOnlyList<TabContent> tabs, int active)
    {
        _tabs.SetTabs(tabs, active);
        _layout.Active = active >= 0 && active < tabs.Count && tabs[active].Split is not null;
    }

    public void RefreshSync() => _sync.Refresh();

    protected override void LayoutChildren()
    {
        float y = (H - ToggleSize) / 2f;
        _toggle.Transform.SetLocalFrame(8, y, ToggleSize, ToggleSize);
        _avatar.Transform.SetLocalFrame(W - 8 - 30, (H - 30) / 2f, 30, 30);
        float chipW = _sync.PreferredWidth;
        float chipX = W - 8 - 30 - 8 - chipW;
        _sync.Transform.SetLocalFrame(chipX, (H - 26) / 2f, chipW, 26);
        float layoutX = chipX - 8 - ToggleSize;
        _layout.Transform.SetLocalFrame(layoutX, y, ToggleSize, ToggleSize);
        float tabsX = Math.Max(_contentLeft, 8 + ToggleSize + 8);
        _tabs.Transform.SetLocalFrame(tabsX, 0, Math.Max(0, layoutX - 12 - tabsX), H);
        _tabs.OnResized(); // the strip may have become narrower
    }

    protected override void Paint(SKCanvas c)
    {
        Gfx.FillRect(c, new SKRect(0, 0, W, H), Theme.Chrome);
        Gfx.Line(c, 0, H - 0.5f, W, H - 0.5f, Theme.Border);
        if (_contentLeft > 0)
        {
            Gfx.Line(c, _contentLeft - 0.5f, 0, _contentLeft - 0.5f, H, Theme.Border);
            float bx = 8 + ToggleSize + 10;
            BrandMark.Draw(c, bx, H / 2f, 20);
            Gfx.Text(c, "TGK", bx + 28, H / 2f, Theme.FontMd, Theme.WeightSemibold, Theme.TextPrimary, TextAlignment.Left, _contentLeft - bx - 36);
        }
    }

    /// <summary>
    /// The tabs themselves plus the "+" button, custom-drawn in one element. Tabs shrink down to a minimum width; when
    /// more tabs are open than fit, the strip scrolls sideways: mouse wheel (vertical or horizontal), the ‹ › arrows at
    /// its ends (hold to keep scrolling) and a list button with every tab. The active tab is scrolled into view when it
    /// changes, never while the user is browsing the strip. The panes of a split view are marked with a bar over their
    /// tabs. The "+" button has room of its own at the end, so it never covers a tab.
    /// Tabs can be dragged: between two tabs moves the tab there (into a split view when both neighbours are its panes,
    /// out of its split view otherwise); onto the middle of another tab shows it beside that one (see
    /// <see cref="MainView.DropTab"/>); down into the content area, next to a pane on screen (see
    /// <see cref="MainView.PaneDropAt"/>).
    /// </summary>
    private sealed class TabsArea : Control
    {
        private const float TabTop = 6, MinTabW = 104, MaxTabW = 220, PlusSize = 28, PlusGap = 6, CloseSize = 18, FadeW = 28;
        private const float ArrowW = 22;
        private const int RepeatDelayMs = 350, RepeatIntervalMs = 90;
        private const float BelowStrip = 10, DragThreshold = 6, OntoFrom = 0.28f, OntoTo = 0.72f, DragScrollZone = 24, DragScrollStep = 10;

        private enum Part
        {
            None,
            Tab,
            Close,
            Plus,
            Left,
            Right,
            List,
        }

        private readonly MainView _main;
        private IReadOnlyList<TabContent> _items = [];
        private int _active;
        private TabContent? _revealed; // the active tab last scrolled into view
        private float _revealedWidth;
        private int _hoverTab = -1;
        private Part _hover, _pressed;
        private int _closePressed = -1, _middlePressed = -1;
        private long _nextRepeat = -1;
        private float _scroll;
        private (float X, float Y) _pointer = (-1, -1);

        // Dragging a tab: the tab under the button (a drag once it moved DragThreshold px), where it was grabbed, and
        // the drop target: an insertion slot or a tab to show it beside (-1 when none).
        private TabContent? _dragTab;
        private bool _dragging;
        private float _dragStartX, _grabOffset, _dragX;
        private int _dropSlot = -1, _dropOnto = -1;
        private bool _overContent; // the dragged tab is below the strip, over the panes

        public TabsArea(MainView main)
        {
            _main = main;
            IsClipping = true;
            Events.OnMouseMove += OnMove;
            Events.OnMouseDown += OnDown;
            Events.OnMouseUp += OnUp;
            Events.OnScroll += OnWheel;
            UiClock.Tick += OnTick;
        }

        public void SetTabs(IReadOnlyList<TabContent> tabs, int active)
        {
            int count = _items.Count;
            _items = tabs;
            _active = active;
            _hoverTab = Math.Min(_hoverTab, tabs.Count - 1);
            // Title and status changes also come through here: only a newly active tab (or a new or closed tab)
            // scrolls, so the strip stays where the user scrolled it.
            TabContent? current = active >= 0 && active < tabs.Count ? tabs[active] : null;
            if (current != _revealed || tabs.Count != count)
                RevealActive();
            else
                ClampScroll();
            InvalidatePaint();
        }

        /// <summary>The strip was laid out: when its width changed, the active tab is brought back into view.</summary>
        public void OnResized()
        {
            if (Math.Abs(W - _revealedWidth) >= 0.5f)
                RevealActive();
        }

        /// <summary>Scrolls the strip so the active tab is fully visible.</summary>
        private void RevealActive()
        {
            float scroll = Scroll;
            _revealed = _active >= 0 && _active < _items.Count ? _items[_active] : null;
            _revealedWidth = W;
            if (_revealed is not null)
            {
                float left = _active * TabWidth, right = left + TabWidth;
                if (left < scroll)
                    scroll = left;
                else if (right > scroll + ViewWidth)
                    scroll = right - ViewWidth;
            }
            ScrollTo(scroll);
        }

        private void ClampScroll() => ScrollTo(_scroll);

        private void ScrollTo(float scroll)
        {
            if (SetAndPaint(ref _scroll, Math.Clamp(scroll, 0, MaxScroll)))
                UpdateHover(_pointer.X, _pointer.Y);
        }

        // Room left for the tabs by the "+" button (and, while scrolling, the arrows and the list button).
        private float Room => Math.Max(0, W - PlusSize - 2 * PlusGap);

        private bool Overflowing => _items.Count * MinTabW > Room + 0.5f;

        private float ViewLeft => Overflowing ? ArrowW : 0;

        private float ViewRight => Math.Max(ViewLeft, Overflowing ? Room - 2 * ArrowW : Room);

        private float ViewWidth => ViewRight - ViewLeft;

        private float TabWidth => _items.Count == 0 ? 0 : Math.Clamp(ViewWidth / _items.Count, MinTabW, MaxTabW);

        private float MaxScroll => Math.Max(0, _items.Count * TabWidth - ViewWidth);

        private float Scroll => Math.Clamp(_scroll, 0, MaxScroll);

        private SKRect TabRect(int i) => new(ViewLeft + i * TabWidth - Scroll, TabTop, ViewLeft + (i + 1) * TabWidth - Scroll, H);

        private SKRect CloseRect(int i)
        {
            SKRect r = TabRect(i);
            float cy = (r.Top + r.Bottom) / 2f;
            return new SKRect(r.Right - 10 - CloseSize, cy - CloseSize / 2f, r.Right - 10, cy + CloseSize / 2f);
        }

        private SKRect ButtonRect(float x, float w)
        {
            float cy = (TabTop + H) / 2f;
            return new SKRect(x, cy - PlusSize / 2f, x + w, cy + PlusSize / 2f);
        }

        private SKRect LeftRect => ButtonRect(0, ArrowW);

        private SKRect RightRect => ButtonRect(ViewRight, ArrowW);

        private SKRect ListRect => ButtonRect(ViewRight + ArrowW, ArrowW);

        private SKRect PlusRect => ButtonRect(Overflowing ? ViewRight + 2 * ArrowW + PlusGap
            : ViewLeft + Math.Min(_items.Count * TabWidth - Scroll, ViewWidth) + PlusGap, PlusSize);

        private int TabAt(float x, float y)
        {
            if (y < TabTop || TabWidth <= 0 || x < ViewLeft || x >= ViewRight)
                return -1;
            int i = (int)((x - ViewLeft + Scroll) / TabWidth);
            return i < _items.Count ? i : -1;
        }

        private Part PartAt(float x, float y, out int tab)
        {
            tab = TabAt(x, y);
            if (tab >= 0)
                return CloseRect(tab).Contains(x, y) ? Part.Close : Part.Tab; // a hovered tab always shows its close button
            if (PlusRect.Contains(x, y))
                return Part.Plus;
            if (!Overflowing)
                return Part.None;
            return LeftRect.Contains(x, y) ? Part.Left : RightRect.Contains(x, y) ? Part.Right : ListRect.Contains(x, y) ? Part.List : Part.None;
        }

        private void OnWheel(object? sender, MouseScrollEventArgs e)
        {
            e.Handled = true;
            // A wheel scrolls vertically; touchpads and tilt wheels also scroll horizontally (positive X = towards the left).
            float delta = e.Offset.Y + e.Offset.X;
            ScrollTo(Scroll - delta * TabWidth / 2f);
        }

        // One arrow step: the next tab that is cut off (or hidden) on that side comes fully into view.
        private void Step(int direction)
        {
            float w = TabWidth;
            if (w <= 0)
                return;
            float scroll = Scroll;
            if (direction < 0)
                ScrollTo((MathF.Ceiling(scroll / w - 0.01f) - 1) * w);
            else
                ScrollTo((MathF.Floor((scroll + ViewWidth) / w + 0.01f) + 1) * w - ViewWidth);
        }

        private void OnMove(object? sender, MouseEventArgs e)
        {
            float x = e.Relative.X;
            if (_dragTab is not null && !_dragging && Math.Abs(x - _dragStartX) >= DragThreshold && _items.Contains(_dragTab))
            {
                _dragging = true;
                _hoverTab = -1;
                _hover = Part.None;
            }
            if (!_dragging)
            {
                UpdateHover(x, e.Relative.Y);
                return;
            }
            e.Handled = true;
            _dragX = x;
            bool overContent = e.Relative.Y > H + BelowStrip;
            if (overContent || _overContent)
                _main.UpdatePaneDrag(_dragTab!, overContent ? e.Global.X : null, e.Global.Y);
            _overContent = overContent;
            if (overContent)
            {
                (_dropSlot, _dropOnto) = (-1, -1);
                InvalidatePaint();
                return;
            }
            UpdateDropTarget();
        }

        private void UpdateDropTarget()
        {
            (_dropSlot, _dropOnto) = DropTarget(_dragX);
            InvalidatePaint();
        }

        // Middle of another tab: beside it; nearer a tab's edge: the slot there; past the last tab: the end.
        private (int Slot, int Onto) DropTarget(float x)
        {
            int n = _items.Count;
            float w = TabWidth;
            if (n == 0 || w <= 0)
                return (-1, -1);
            float p = (Math.Clamp(x, ViewLeft, ViewRight - 0.01f) - ViewLeft + Scroll) / w;
            int i = (int)MathF.Floor(p);
            if (i >= n)
                return (n, -1);
            if (i < 0)
                return (0, -1);
            float frac = p - i;
            if (frac > OntoFrom && frac < OntoTo)
                return _items[i] == _dragTab ? (-1, -1) : (-1, i);
            return (frac <= OntoFrom ? i : i + 1, -1);
        }

        private void EndDrag()
        {
            if (_overContent && _dragTab is { } tab)
                _main.EndPaneDrag(tab, drop: false);
            _overContent = false;
            _dragTab = null;
            _dragging = false;
            _dropSlot = _dropOnto = -1;
            InvalidatePaint();
        }

        private void OnTick()
        {
            if (IsDisposed)
            {
                UiClock.Tick -= OnTick;
                return;
            }
            // A tab dragged to either end of an overflowing strip scrolls it.
            if (_dragging && !_overContent && Overflowing)
            {
                float before = Scroll;
                if (_dragX < ViewLeft + DragScrollZone)
                    ScrollTo(Scroll - DragScrollStep);
                else if (_dragX > ViewRight - DragScrollZone)
                    ScrollTo(Scroll + DragScrollStep);
                if (Math.Abs(Scroll - before) > 0.01f)
                    UpdateDropTarget();
            }
            if (_nextRepeat < 0 || UiClock.NowMs < _nextRepeat)
                return;
            if (_pressed is Part.Left or Part.Right && _hover == _pressed)
                Step(_pressed == Part.Left ? -1 : 1);
            _nextRepeat = _pressed is Part.Left or Part.Right ? UiClock.NowMs + RepeatIntervalMs : -1;
        }

        private void UpdateHover(float x, float y)
        {
            _pointer = (x, y);
            Part part = PartAt(x, y, out int tab);
            if (tab == _hoverTab && part == _hover)
                return;
            _hoverTab = tab;
            _hover = part;
            InvalidatePaint();
        }

        protected override void OnHoverChanged()
        {
            if (IsHovered)
                return;
            _hoverTab = -1;
            _hover = Part.None;
            _pointer = (-1, -1);
        }

        private void OnDown(object? sender, MouseEventArgs e)
        {
            e.Handled = true;
            float x = e.Relative.X, y = e.Relative.Y;
            UpdateHover(x, y);
            Part part = PartAt(x, y, out int tab);
            switch (e.Button)
            {
                case 2:
                    _middlePressed = tab;
                    return;
                case 1:
                    if (tab >= 0)
                        _main.ShowTabMenu(_items[tab], e.Global.X, e.Global.Y);
                    return;
                case not 0:
                    return;
            }
            _pressed = part;
            switch (part)
            {
                case Part.Close:
                    _closePressed = tab;
                    break;
                case Part.Tab:
                    _dragTab = _items[tab];
                    _dragStartX = _dragX = x;
                    _grabOffset = x - TabRect(tab).Left;
                    CapturePointer();
                    // Activated on release unless dragged: activating starts a session, whose password prompt would
                    // end the drag.
                    break;
                case Part.Left or Part.Right:
                    Step(part == Part.Left ? -1 : 1);
                    _nextRepeat = UiClock.NowMs + RepeatDelayMs;
                    break;
                case Part.List:
                    var r = Transform.Computed;
                    SKRect list = ListRect;
                    _main.ShowTabList(r.X + list.Right - 300, r.Y + list.Top, list.Height);
                    break;
            }
            InvalidatePaint();
        }

        private void OnUp(object? sender, MouseEventArgs e)
        {
            e.Handled = true;
            float x = e.Relative.X, y = e.Relative.Y;
            Part part = PartAt(x, y, out int tab);
            if (e.Button == 2)
            {
                if (tab >= 0 && tab == _middlePressed)
                    _main.CloseTab(tab);
                _middlePressed = -1;
                return;
            }
            if (e.Button == 0 && _dragging && _overContent && _dragTab is { } toPane)
            {
                _overContent = false;
                EndDrag();
                _pressed = Part.None;
                if (!_main.EndPaneDrag(toPane))
                    _main.ActivateTab(toPane);
                UpdateHover(x, y);
                return;
            }
            if (e.Button == 0 && _dragging && _dragTab is { } dragged)
            {
                (int slot, int onto) = (_dropSlot, _dropOnto);
                TabContent? target = onto >= 0 && onto < _items.Count ? _items[onto] : null;
                EndDrag();
                _pressed = Part.None;
                if (slot >= 0 || target is not null)
                    _main.DropTab(dragged, slot, target);
                _main.ActivateTab(dragged); // also when dropped where it was
                UpdateHover(x, y);
                return;
            }
            if (e.Button == 0 && _dragTab is { } clicked)
            {
                _dragTab = null;
                if (_pressed == Part.Tab && _items.Contains(clicked))
                    _main.ActivateTab(clicked);
            }
            if (_pressed == Part.Plus && part == Part.Plus)
                _main.NewTab();
            else if (_closePressed >= 0 && _closePressed == tab && part == Part.Close)
                _main.CloseTab(tab);
            _pressed = Part.None;
            _closePressed = -1;
            _nextRepeat = -1;
            InvalidatePaint();
        }

        protected override void Paint(SKCanvas c)
        {
            float scroll = Scroll;
            PaneLayout<TabContent>? shownSplit = _active >= 0 && _active < _items.Count ? _items[_active].Split : null;
            c.Save();
            c.ClipRect(new SKRect(ViewLeft, 0, ViewRight, H));
            int first = Math.Max(0, (int)(scroll / Math.Max(1, TabWidth)));
            for (int i = first; i < _items.Count; i++)
            {
                SKRect r = TabRect(i);
                if (r.Left >= ViewRight)
                    break;
                TabContent tab = _items[i];
                bool active = i == _active, hover = i == _hoverTab;
                if (active)
                {
                    Gfx.FillTopRound(c, r, 8, Theme.Surface);
                    using var outline = new SKPathBuilder();
                    outline.MoveTo(r.Left + 0.5f, r.Bottom);
                    outline.LineTo(r.Left + 0.5f, r.Top + 8);
                    outline.ArcTo(new SKRect(r.Left + 0.5f, r.Top + 0.5f, r.Left + 16.5f, r.Top + 16.5f), 180, 90, false);
                    outline.LineTo(r.Right - 8.5f, r.Top + 0.5f);
                    outline.ArcTo(new SKRect(r.Right - 16.5f, r.Top + 0.5f, r.Right - 0.5f, r.Top + 16.5f), 270, 90, false);
                    outline.LineTo(r.Right - 0.5f, r.Bottom);
                    using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = Theme.Border };
                    using SKPath outlinePath = outline.Detach();
                    c.DrawPath(outlinePath, stroke);
                }
                else if (hover)
                {
                    Gfx.FillTopRound(c, new SKRect(r.Left + 2, r.Top + 2, r.Right - 2, r.Bottom - 4), 6, Theme.SurfaceRaised);
                }
                else if (tab.IsShown)
                {
                    // Another pane of the split view on screen.
                    Gfx.FillTopRound(c, new SKRect(r.Left + 2, r.Top + 2, r.Right - 2, r.Bottom - 4), 6, Theme.SurfaceRaised.WithAlpha(150));
                }
                else if (i + 1 < _items.Count && i + 1 != _active && i + 1 != _hoverTab && !_items[i + 1].IsShown)
                {
                    Gfx.Line(c, r.Right - 0.5f, r.Top + 9, r.Right - 0.5f, r.Bottom - 9, Theme.Border);
                }

                // The panes of a split view share a bar over their tabs (accent while that split view is on screen).
                if (tab.Split is { } split)
                {
                    bool firstPane = i == 0 || _items[i - 1].Split != split, lastPane = i + 1 >= _items.Count || _items[i + 1].Split != split;
                    var bar = new SKRect(r.Left + (firstPane ? 8 : 0), 1.5f, r.Right - (lastPane ? 8 : 0), 4);
                    Gfx.FillRound(c, bar, 1.25f, split == shownSplit ? Theme.Accent : Theme.TextDisabled);
                }

                float cy = (r.Top + r.Bottom) / 2f;
                float x = r.Left + 14;
                if (tab.Status != TabStatus.None)
                {
                    Gfx.Circle(c, x + 3.5f, cy, 3.5f, StatusColor(tab.Status));
                    x += 15;
                }
                bool showClose = active || hover;
                float textMax = r.Right - x - (showClose ? CloseSize + 16 : 12);
                SKColor titleColor = active ? Theme.TextPrimary : tab.NeedsAttention ? Theme.Warning : hover || tab.IsShown ? Theme.TextSecondary : Theme.TextMuted;
                Gfx.Text(c, tab.Title, x, cy, Theme.FontBase, Theme.WeightRegular, titleColor, TextAlignment.Left, textMax);
                if (showClose)
                {
                    SKRect cr = CloseRect(i);
                    bool closeHover = hover && _hover == Part.Close;
                    if (closeHover)
                        Gfx.FillRound(c, cr, Theme.RadiusSm, Theme.SurfacePressed);
                    Icons.Draw(c, "x", cr.MidX, cr.MidY, 13, closeHover ? Theme.TextPrimary : Theme.TextMuted);
                }
            }

            if (_dragging && !_overContent)
                PaintDrag(c);

            // Fade the edges where more tabs are scrolled out of view.
            if (scroll > 0.5f)
                EdgeFade(c, ViewLeft, ViewLeft + FadeW);
            if (scroll < MaxScroll - 0.5f)
                EdgeFade(c, ViewRight, ViewRight - FadeW);
            c.Restore();

            if (Overflowing)
            {
                DrawButton(c, LeftRect, "chevron-left", Part.Left, enabled: scroll > 0.5f, 15);
                DrawButton(c, RightRect, "chevron-right", Part.Right, enabled: scroll < MaxScroll - 0.5f, 15);
                DrawButton(c, ListRect, "chevron-down", Part.List, enabled: true, 15);
                // A background tab that wants attention while scrolled out of view tints the arrow on its side.
                for (int i = 0; i < _items.Count; i++)
                {
                    if (!_items[i].NeedsAttention)
                        continue;
                    SKRect r = TabRect(i);
                    if (r.Right <= ViewLeft + 1)
                        Gfx.Circle(c, LeftRect.Right - 4, LeftRect.Top + 6, 2.5f, Theme.Warning);
                    else if (r.Left >= ViewRight - 1)
                        Gfx.Circle(c, RightRect.Right - 4, RightRect.Top + 6, 2.5f, Theme.Warning);
                }
            }
            DrawButton(c, PlusRect, "plus", Part.Plus, enabled: true, 16);
        }

        // The drop target (an accent bar between tabs, or an outlined tab to show the dragged one beside) and the
        // dragged tab following the pointer.
        private void PaintDrag(SKCanvas c)
        {
            if (_dropOnto >= 0 && _dropOnto < _items.Count)
            {
                SKRect r = TabRect(_dropOnto);
                var box = new SKRect(r.Left + 2, r.Top + 2, r.Right - 2, r.Bottom - 3);
                Gfx.FillRound(c, box, 6, Theme.AccentSoft);
                Gfx.StrokeRound(c, box, 6, Theme.Accent);
                Icons.Draw(c, "layout-columns", box.Right - 14, box.MidY, 13, Theme.Accent);
            }
            else if (_dropSlot >= 0)
            {
                float x = ViewLeft + _dropSlot * TabWidth - Scroll;
                Gfx.FillRound(c, new SKRect(x - 1.5f, TabTop + 3, x + 1.5f, H - 3), 1.5f, Theme.Accent);
            }
            if (_dragTab is not { } tab)
                return;
            float w = TabWidth;
            float left = Math.Clamp(_dragX - _grabOffset, ViewLeft - w / 2f, ViewRight - w / 2f);
            var ghost = new SKRect(left, TabTop + 1, left + w, H - 2);
            Gfx.FillTopRound(c, ghost, 8, Theme.SurfaceRaised.WithAlpha(170));
            Gfx.StrokeRound(c, ghost, 8, Theme.BorderStrong);
            float cy = ghost.MidY, tx = ghost.Left + 14;
            if (tab.Status != TabStatus.None)
            {
                Gfx.Circle(c, tx + 3.5f, cy, 3.5f, StatusColor(tab.Status));
                tx += 15;
            }
            Gfx.Text(c, tab.Title, tx, cy, Theme.FontBase, Theme.WeightRegular, Theme.TextPrimary, TextAlignment.Left, ghost.Right - tx - 12);
        }

        private void DrawButton(SKCanvas c, SKRect r, string icon, Part part, bool enabled, float size)
        {
            bool hover = enabled && _hover == part;
            if (hover)
                Gfx.FillRound(c, r, Theme.Radius, _pressed == part ? Theme.SurfacePressed : Theme.SurfaceHover);
            Icons.Draw(c, icon, r.MidX, r.MidY, size, !enabled ? Theme.TextDisabled : hover ? Theme.TextPrimary : Theme.TextSecondary);
        }

        // A band from the strip's background color at x = edge to transparent at x = inner.
        private void EdgeFade(SKCanvas c, float edge, float inner)
        {
            using var paint = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(new SKPoint(edge, 0), new SKPoint(inner, 0),
                    [Theme.Chrome, Theme.Chrome.WithAlpha(0)], SKShaderTileMode.Clamp),
            };
            c.DrawRect(new SKRect(Math.Min(edge, inner), 0, Math.Max(edge, inner), H), paint);
        }
    }

    public static SKColor StatusColor(TabStatus status) => status switch
    {
        TabStatus.Connecting => Theme.Warning,
        TabStatus.Connected => Theme.Success,
        TabStatus.Failed => Theme.Danger,
        _ => Theme.Idle,
    };

    /// <summary>"Synced · 2m" chip (amber when offline, red on errors); a click shows the details and "Sync now".</summary>
    private sealed class SyncChip : Control
    {
        private readonly MainView _main;
        private string _text = "";
        private (Core.Services.SyncState, bool) _state;
        private long _nextRefresh;

        public SyncChip(MainView main)
        {
            _main = main;
            Cursor = StandardCursor.Hand;
            Events.OnClick += (_, e) =>
            {
                e.Handled = true;
                var r = Transform.Computed;
                _main.ShowSyncMenu(r.X, r.Y, r.Height);
            };
            UiClock.Tick += OnTick;
            Refresh();
        }

        public float PreferredWidth => MathF.Ceiling(Gfx.Measure(_text, Theme.FontSm) + 34);

        public void Refresh()
        {
            _nextRefresh = UiClock.NowMs + 15_000;
            var vault = _main.Services.Vault;
            string text = HostFormat.Sync(vault, DateTimeOffset.UtcNow);
            var state = (vault.Status, vault.LastError is not null);
            if (text == _text && state == _state)
                return;
            _state = state;
            bool resize = text.Length != _text.Length;
            _text = text;
            InvalidatePaint();
            if (resize)
                Parent?.InvalidateLayout();
        }

        private void OnTick()
        {
            if (IsDisposed)
            {
                UiClock.Tick -= OnTick;
                return;
            }
            if (UiClock.NowMs >= _nextRefresh)
                Refresh();
        }

        protected override void Paint(SKCanvas c)
        {
            var r = new SKRect(0, 0, W, H);
            if (IsHovered)
                Gfx.FillRound(c, r, H / 2f, Theme.SurfaceHover);
            // Offline with an error = signed in but the server is unreachable (amber); Offline alone = signed out.
            var vault = _main.Services.Vault;
            SKColor? alert = vault.Status switch
            {
                Core.Services.SyncState.Error => Theme.Danger,
                Core.Services.SyncState.Offline when vault.LastError is not null => Theme.Warning,
                _ => null,
            };
            SKColor dot = alert ?? vault.Status switch
            {
                _ when vault.Mode == Core.Services.VaultMode.Local => Theme.Idle,
                Core.Services.SyncState.Syncing => Theme.Accent,
                Core.Services.SyncState.Offline => Theme.Idle,
                _ => Theme.Success,
            };
            if (alert is { } a)
                Gfx.FillRound(c, r, H / 2f, a.WithAlpha(IsHovered ? (byte)48 : (byte)30));
            Gfx.Circle(c, 14, H / 2f, 3.5f, dot);
            Gfx.Text(c, _text, 24, H / 2f, Theme.FontSm, Theme.WeightRegular, alert ?? (IsHovered ? Theme.TextPrimary : Theme.TextSecondary));
        }
    }
}

/// <summary>Round account button showing the user's initial (a computer for the local vault); opens the account menu.</summary>
public sealed class AvatarButton : Control
{
    private readonly MainView _main;

    public AvatarButton(MainView main)
    {
        _main = main;
        Cursor = StandardCursor.Hand;
        Events.OnClick += (_, e) =>
        {
            e.Handled = true;
            _main.ShowAccountMenu();
        };
    }

    protected override void Paint(SKCanvas c)
    {
        float r = Math.Min(W, H) / 2f - 2;
        if (IsHovered)
            Gfx.Circle(c, W / 2f, H / 2f, r + 2, Theme.AccentSoft);
        Gfx.Circle(c, W / 2f, H / 2f, r, IsPressed ? Theme.AccentPressed : Theme.Accent);
        if (_main.Services.Vault.Mode == Core.Services.VaultMode.Local)
        {
            Icons.Draw(c, "monitor", W / 2f, H / 2f, 16, Theme.TextOnAccent);
            return;
        }
        string user = _main.Services.Vault.CurrentUser ?? "?";
        string initial = user.Length > 0 ? user[..1].ToUpperInvariant() : "?";
        Gfx.Text(c, initial, W / 2f, H / 2f, Theme.FontBase, Theme.WeightSemibold, Theme.TextOnAccent, TextAlignment.Center);
    }
}

/// <summary>The TGK mark: a rounded accent square with a ">_" prompt, drawn in code at any size.</summary>
public static class BrandMark
{
    public static void Draw(SKCanvas c, float x, float centerY, float size)
    {
        var r = SKRect.Create(x, centerY - size / 2f, size, size);
        using (var bg = new SKPaint { IsAntialias = true })
        {
            bg.Shader = SKShader.CreateLinearGradient(new SKPoint(r.Left, r.Top), new SKPoint(r.Right, r.Bottom),
                [new SKColor(0x6E, 0xA8, 0xFF), new SKColor(0x3B, 0x6F, 0xE0)], SKShaderTileMode.Clamp);
            c.DrawRoundRect(r, size * 0.22f, size * 0.22f, bg);
        }
        float s = size / 256f;
        using var fg = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.5f, 24 * s),
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            Color = SKColors.White,
        };
        using var chevron = new SKPathBuilder();
        chevron.MoveTo(r.Left + 70 * s, r.Top + 84 * s);
        chevron.LineTo(r.Left + 118 * s, r.Top + 128 * s);
        chevron.LineTo(r.Left + 70 * s, r.Top + 172 * s);
        using SKPath chevronPath = chevron.Detach();
        c.DrawPath(chevronPath, fg);
        c.DrawLine(r.Left + 140 * s, r.Top + 172 * s, r.Left + 188 * s, r.Top + 172 * s, fg);
    }
}
