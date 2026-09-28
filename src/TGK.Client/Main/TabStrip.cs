using System;
using System.Collections.Generic;
using Blossom.Core.Input;
using Silk.NET.Input;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Views;

namespace TGK.Client.Main;

/// <summary>
/// The top bar: sidebar toggle (+ brand while the sidebar is open), the tabs with a "+" button, the sync status chip
/// and the account button. Tabs start where the content column starts so the active tab merges into it.
/// </summary>
public sealed class TabStrip : Control
{
    private const float ToggleSize = 28;
    private readonly IconButton _toggle;
    private readonly TabsArea _tabs;
    private readonly SyncChip _sync;
    private readonly AvatarButton _avatar;
    private float _contentLeft;

    public TabStrip(MainView main)
    {
        _toggle = new IconButton("sidebar", 18);
        _toggle.Clicked += main.ToggleSidebar;
        _tabs = new TabsArea(main);
        _sync = new SyncChip(main);
        _avatar = new AvatarButton(main);
        AddChild(_toggle);
        AddChild(_tabs);
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

    public void SetTabs(IReadOnlyList<TabContent> tabs, int active) => _tabs.SetTabs(tabs, active);

    public void RefreshSync() => _sync.Refresh();

    protected override void LayoutChildren()
    {
        float y = (H - ToggleSize) / 2f;
        _toggle.Transform.SetLocalFrame(8, y, ToggleSize, ToggleSize);
        _avatar.Transform.SetLocalFrame(W - 8 - 30, (H - 30) / 2f, 30, 30);
        float chipW = _sync.PreferredWidth;
        float chipX = W - 8 - 30 - 8 - chipW;
        _sync.Transform.SetLocalFrame(chipX, (H - 26) / 2f, chipW, 26);
        float tabsX = Math.Max(_contentLeft, 8 + ToggleSize + 8);
        _tabs.Transform.SetLocalFrame(tabsX, 0, Math.Max(0, chipX - 12 - tabsX), H);
        _tabs.RevealActive(); // the strip may have become narrower
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
    /// The tabs themselves plus the "+" button, custom-drawn in one element. Tabs shrink down to a minimum width;
    /// more tabs than fit scroll sideways (mouse wheel), always keeping the active tab in view. The "+" button has
    /// room of its own at the end, so it never covers a tab.
    /// </summary>
    private sealed class TabsArea : Control
    {
        private const float TabTop = 6, MinTabW = 92, MaxTabW = 220, PlusSize = 28, PlusGap = 6, CloseSize = 18, FadeW = 28;
        private readonly MainView _main;
        private IReadOnlyList<TabContent> _items = [];
        private int _active;
        private int _hoverTab = -1;
        private bool _hoverClose, _hoverPlus;
        private int _closePressed = -1, _middlePressed = -1;
        private bool _plusPressed;
        private float _scroll;
        private (float X, float Y) _pointer = (-1, -1);

        public TabsArea(MainView main)
        {
            _main = main;
            IsClipping = true;
            Events.OnMouseMove += (_, e) => UpdateHover(e.Relative.X, e.Relative.Y);
            Events.OnMouseDown += OnDown;
            Events.OnMouseUp += OnUp;
            Events.OnScroll += OnWheel;
        }

        public void SetTabs(IReadOnlyList<TabContent> tabs, int active)
        {
            _items = tabs;
            _active = active;
            _hoverTab = Math.Min(_hoverTab, tabs.Count - 1);
            RevealActive();
            InvalidatePaint();
        }

        /// <summary>Scrolls the strip so the active tab is fully visible.</summary>
        public void RevealActive()
        {
            float scroll = Scroll;
            if (_active >= 0 && _active < _items.Count)
            {
                float left = _active * TabWidth, right = left + TabWidth;
                if (left < scroll)
                    scroll = left;
                else if (right > scroll + TabsWidth)
                    scroll = right - TabsWidth;
            }
            if (SetAndPaint(ref _scroll, scroll))
                UpdateHover(_pointer.X, _pointer.Y);
        }

        // Width available to the tabs: everything but the "+" button and its margins.
        private float TabsWidth => Math.Max(0, W - PlusSize - 2 * PlusGap);

        private float TabWidth => _items.Count == 0 ? 0 : Math.Clamp(TabsWidth / _items.Count, MinTabW, MaxTabW);

        private float MaxScroll => Math.Max(0, _items.Count * TabWidth - TabsWidth);

        private float Scroll => Math.Clamp(_scroll, 0, MaxScroll);

        private SKRect TabRect(int i) => new(i * TabWidth - Scroll, TabTop, (i + 1) * TabWidth - Scroll, H);

        private SKRect CloseRect(int i)
        {
            SKRect r = TabRect(i);
            float cy = (r.Top + r.Bottom) / 2f;
            return new SKRect(r.Right - 10 - CloseSize, cy - CloseSize / 2f, r.Right - 10, cy + CloseSize / 2f);
        }

        private SKRect PlusRect
        {
            get
            {
                float x = Math.Min(_items.Count * TabWidth - Scroll, TabsWidth) + PlusGap;
                float cy = (TabTop + H) / 2f;
                return new SKRect(x, cy - PlusSize / 2f, x + PlusSize, cy + PlusSize / 2f);
            }
        }

        private int TabAt(float x, float y)
        {
            if (y < TabTop || TabWidth <= 0 || x < 0 || x >= TabsWidth)
                return -1;
            int i = (int)((x + Scroll) / TabWidth);
            return i < _items.Count ? i : -1;
        }

        private void OnWheel(object? sender, MouseScrollEventArgs e)
        {
            e.Handled = true;
            if (SetAndPaint(ref _scroll, Math.Clamp(Scroll - e.Offset.Y * TabWidth / 2f, 0, MaxScroll)))
                UpdateHover(_pointer.X, _pointer.Y);
        }

        private void UpdateHover(float x, float y)
        {
            _pointer = (x, y);
            int tab = TabAt(x, y);
            bool close = tab >= 0 && CloseRect(tab).Contains(x, y); // a hovered tab always shows its close button
            bool plus = PlusRect.Contains(x, y);
            if (tab == _hoverTab && close == _hoverClose && plus == _hoverPlus)
                return;
            _hoverTab = tab;
            _hoverClose = close;
            _hoverPlus = plus;
            InvalidatePaint();
        }

        protected override void OnHoverChanged()
        {
            if (IsHovered)
                return;
            _hoverTab = -1;
            _hoverClose = _hoverPlus = false;
            _pointer = (-1, -1);
        }

        private void OnDown(object? sender, MouseEventArgs e)
        {
            e.Handled = true;
            float x = e.Relative.X, y = e.Relative.Y;
            UpdateHover(x, y);
            int tab = TabAt(x, y);
            if (e.Button == 2)
            {
                _middlePressed = tab;
                return;
            }
            if (e.Button != 0)
                return;
            if (_hoverPlus)
                _plusPressed = true;
            else if (tab >= 0 && _hoverClose)
                _closePressed = tab;
            else if (tab >= 0)
                _main.ActivateTab(tab);
        }

        private void OnUp(object? sender, MouseEventArgs e)
        {
            e.Handled = true;
            float x = e.Relative.X, y = e.Relative.Y;
            int tab = TabAt(x, y);
            if (e.Button == 2)
            {
                if (tab >= 0 && tab == _middlePressed)
                    _main.CloseTab(tab);
                _middlePressed = -1;
                return;
            }
            if (_plusPressed && PlusRect.Contains(x, y))
                _main.NewTab();
            else if (_closePressed >= 0 && _closePressed == tab && CloseRect(tab).Contains(x, y))
                _main.CloseTab(tab);
            _plusPressed = false;
            _closePressed = -1;
        }

        protected override void Paint(SKCanvas c)
        {
            float scroll = Scroll;
            c.Save();
            c.ClipRect(new SKRect(0, 0, TabsWidth, H));
            int first = (int)(scroll / Math.Max(1, TabWidth));
            for (int i = first; i < _items.Count; i++)
            {
                SKRect r = TabRect(i);
                if (r.Left >= TabsWidth)
                    break;
                bool active = i == _active, hover = i == _hoverTab;
                if (active)
                {
                    Gfx.FillTopRound(c, r, 8, Theme.Surface);
                    using var outline = new SKPath();
                    outline.MoveTo(r.Left + 0.5f, r.Bottom);
                    outline.LineTo(r.Left + 0.5f, r.Top + 8);
                    outline.ArcTo(new SKRect(r.Left + 0.5f, r.Top + 0.5f, r.Left + 16.5f, r.Top + 16.5f), 180, 90, false);
                    outline.LineTo(r.Right - 8.5f, r.Top + 0.5f);
                    outline.ArcTo(new SKRect(r.Right - 16.5f, r.Top + 0.5f, r.Right - 0.5f, r.Top + 16.5f), 270, 90, false);
                    outline.LineTo(r.Right - 0.5f, r.Bottom);
                    using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = Theme.Border };
                    c.DrawPath(outline, stroke);
                }
                else if (hover)
                {
                    Gfx.FillTopRound(c, new SKRect(r.Left + 2, r.Top + 2, r.Right - 2, r.Bottom - 4), 6, Theme.SurfaceRaised);
                }
                else if (i + 1 < _items.Count && i + 1 != _active && i + 1 != _hoverTab)
                {
                    Gfx.Line(c, r.Right - 0.5f, r.Top + 9, r.Right - 0.5f, r.Bottom - 9, Theme.Border);
                }

                TabContent tab = _items[i];
                float cy = (r.Top + r.Bottom) / 2f;
                float x = r.Left + 14;
                if (tab.Status != TabStatus.None)
                {
                    Gfx.Circle(c, x + 3.5f, cy, 3.5f, StatusColor(tab.Status));
                    x += 15;
                }
                bool showClose = active || hover;
                float textMax = r.Right - x - (showClose ? CloseSize + 16 : 12);
                SKColor titleColor = active ? Theme.TextPrimary : tab.NeedsAttention ? Theme.Warning : hover ? Theme.TextSecondary : Theme.TextMuted;
                Gfx.Text(c, tab.Title, x, cy, Theme.FontBase, Theme.WeightRegular, titleColor, TextAlignment.Left, textMax);
                if (showClose)
                {
                    SKRect cr = CloseRect(i);
                    if (hover && _hoverClose)
                        Gfx.FillRound(c, cr, Theme.RadiusSm, Theme.SurfacePressed);
                    Icons.Draw(c, "x", cr.MidX, cr.MidY, 13, hover && _hoverClose ? Theme.TextPrimary : Theme.TextMuted);
                }
            }

            // Fade the edges where more tabs are scrolled out of view.
            if (scroll > 0.5f)
                EdgeFade(c, 0, FadeW);
            if (scroll < MaxScroll - 0.5f)
                EdgeFade(c, TabsWidth, TabsWidth - FadeW);
            c.Restore();

            SKRect plus = PlusRect;
            if (_hoverPlus)
                Gfx.FillRound(c, plus, Theme.Radius, Theme.SurfaceHover);
            Icons.Draw(c, "plus", plus.MidX, plus.MidY, 16, _hoverPlus ? Theme.TextPrimary : Theme.TextSecondary);
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

    /// <summary>"Synced · 2m" chip; click to sync now.</summary>
    private sealed class SyncChip : Control
    {
        private readonly MainView _main;
        private string _text = "";
        private long _nextRefresh;

        public SyncChip(MainView main)
        {
            _main = main;
            Cursor = StandardCursor.Hand;
            Events.OnClick += (_, e) =>
            {
                e.Handled = true;
                _main.SyncNow();
            };
            UiClock.Tick += OnTick;
            Refresh();
        }

        public float PreferredWidth => MathF.Ceiling(Gfx.Measure(_text, Theme.FontSm) + 34);

        public void Refresh()
        {
            _nextRefresh = UiClock.NowMs + 15_000;
            string text = HostFormat.Sync(_main.Services.Vault, DateTimeOffset.UtcNow);
            if (text == _text)
                return;
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
            SKColor dot = _main.Services.Vault.Status switch
            {
                Core.Services.SyncState.Syncing => Theme.Warning,
                Core.Services.SyncState.Error => Theme.Danger,
                Core.Services.SyncState.Offline => Theme.Idle,
                _ => Theme.Success,
            };
            Gfx.Circle(c, 14, H / 2f, 3.5f, dot);
            Gfx.Text(c, _text, 24, H / 2f, Theme.FontSm, Theme.WeightRegular, IsHovered ? Theme.TextPrimary : Theme.TextSecondary);
        }
    }
}

/// <summary>Round account button showing the user's initial; opens the account menu.</summary>
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
        using var chevron = new SKPath();
        chevron.MoveTo(r.Left + 70 * s, r.Top + 84 * s);
        chevron.LineTo(r.Left + 118 * s, r.Top + 128 * s);
        chevron.LineTo(r.Left + 70 * s, r.Top + 172 * s);
        c.DrawPath(chevron, fg);
        c.DrawLine(r.Left + 140 * s, r.Top + 172 * s, r.Left + 188 * s, r.Top + 172 * s, fg);
    }
}
