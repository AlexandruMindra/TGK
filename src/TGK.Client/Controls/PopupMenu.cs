using System;
using System.Collections.Generic;
using Blossom;
using Blossom.Core.Input;
using Blossom.Core.Visual;
using Silk.NET.Input;
using SkiaSharp;
using TGK.Client.Input;

namespace TGK.Client.Controls;

/// <summary>An entry in a <see cref="PopupMenu"/>.</summary>
public sealed class MenuItem
{
    public string Text { get; init; } = "";
    public string? Icon { get; init; }
    /// <summary>Right-aligned hint, typically a shortcut ("Ctrl+T").</summary>
    public string? Hint { get; init; }
    public Action? Action { get; init; }
    public bool IsDanger { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool IsChecked { get; init; }
    public bool IsSeparator { get; private init; }
    /// <summary>Non-interactive caption (e.g. "Signed in as demo").</summary>
    public bool IsHeader { get; init; }

    public static MenuItem Separator { get; } = new() { IsSeparator = true, IsEnabled = false };

    internal bool IsSelectable => IsEnabled && !IsSeparator && !IsHeader;
}

/// <summary>
/// Full-window popup layer for context menus and dropdown lists: a click outside closes it, arrows / Enter / Escape
/// work while it is open, and it restores the previous keyboard focus when closed. One instance per view
/// (<c>TgkView.Menu</c>), reused for every menu.
/// </summary>
public sealed class PopupMenu : VisualElement, IKeyInput
{
    private readonly MenuPanel _panel;
    private VisualElement? _previousFocus;
    private Action? _onClosed;

    public PopupMenu()
    {
        Name = "PopupMenu";
        Visible = false;
        ZIndex = 5000;
        ReceivesKeyboard = true;
        Style = new ElementStyle();
        _panel = new MenuPanel(this);
        AddChild(_panel);
        Events.OnMouseDown += (_, e) =>
        {
            e.Handled = true;
            Close();
        };
    }

    public bool IsOpen => Visible;

    /// <summary>
    /// Opens the menu with its top-left at window point (<paramref name="x"/>, <paramref name="y"/>), flipped or shifted
    /// to stay inside the window. <paramref name="anchorHeight"/> is the height of the control it drops down from,
    /// so the menu can open upwards above it when there is no room below.
    /// </summary>
    public void Show(IReadOnlyList<MenuItem> items, float x, float y, float minWidth = 180, float anchorHeight = 0, Action? onClosed = null)
    {
        if (Visible)
            Close();
        var view = ParentView;
        _panel.SetItems(items);
        float w = Math.Max(minWidth, _panel.PreferredWidth);
        float vw = view.Width, vh = view.Height;
        float h = Math.Min(_panel.PreferredHeight, vh - 16); // taller lists scroll
        if (y + h > vh - 8 && y - anchorHeight - h >= 8)
            y = y - anchorHeight - h - 4;
        x = Math.Clamp(x, 8, Math.Max(8, vw - w - 8));
        y = Math.Clamp(y, 8, Math.Max(8, vh - h - 8));

        _panel.Transform.SetAbsoluteFrame(x, y, w, h);
        _panel.ScrollToChecked();
        _onClosed = onClosed;
        _previousFocus = view.ActiveKeyboardElement;
        Visible = true;
        view.SetActiveKeyboardElement(this);
        InvalidateLayout();
    }

    public void Close()
    {
        if (!Visible)
            return;
        Visible = false;
        var view = ParentView;
        if (view.ActiveKeyboardElement == this || view.ActiveKeyboardElement is null)
            view.SetActiveKeyboardElement(_previousFocus is { IsDisposed: false, EffectiveVisible: true } ? _previousFocus : null);
        _previousFocus = null;
        Action? closed = _onClosed;
        _onClosed = null;
        closed?.Invoke();
    }

    internal void Activate(MenuItem item)
    {
        if (!item.IsSelectable)
            return;
        Close();
        // Run after the current input event so the action may freely rebuild the UI.
        if (item.Action is { } action)
            Shell.Post(action);
    }

    public bool OnKey(KeyStroke k)
    {
        switch (k.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Up:
                _panel.MoveHover(-1);
                break;
            case Key.Down:
            case Key.Tab:
                _panel.MoveHover(k.Key == Key.Tab && k.Shift ? -1 : 1);
                break;
            case Key.Enter or Key.KeypadEnter or Key.Space when !k.IsRepeat:
                if (_panel.HoveredItem is { } item)
                    Activate(item);
                break;
        }
        return true; // modal while open
    }

    public void OnText(string text) { }

    private sealed class MenuPanel : Control
    {
        private const float RowH = 30, SeparatorH = 9, HeaderH = 28, PadY = 4, PadX = 12;
        private readonly PopupMenu _owner;
        private IReadOnlyList<MenuItem> _items = [];
        private int _hover = -1;
        private float _scroll; // content offset of a list taller than the window

        public MenuPanel(PopupMenu owner)
        {
            _owner = owner;
            Style = new ElementStyle
            {
                Shadow = new ShadowStyle(0, 6, 8, 8, SKColors.Black.WithAlpha(110)),
                Border = new BorderStyle { Roundness = Theme.RadiusLg },
            };
            Events.OnMouseDown += (_, e) => e.Handled = true;
            Events.OnMouseMove += (_, e) => SetHover(IndexAt(e.Relative.Y));
            Events.OnScroll += (_, e) =>
            {
                e.Handled = true;
                _hover = -1; // the pointer is over another item now; the next move highlights it
                ScrollTo(_scroll - e.Offset.Y * 3 * RowH);
            };
            Events.OnMouseUp += (_, e) =>
            {
                e.Handled = true;
                int i = IndexAt(e.Relative.Y);
                if (e.Button == 0 && i >= 0)
                    _owner.Activate(_items[i]);
            };
        }

        public MenuItem? HoveredItem => _hover >= 0 && _hover < _items.Count ? _items[_hover] : null;

        public float PreferredWidth
        {
            get
            {
                float w = 0;
                foreach (MenuItem item in _items)
                {
                    float iw = Gfx.Measure(item.Text, Theme.FontBase) + 2 * PadX + 28;
                    if (item.Hint is not null)
                        iw += Gfx.Measure(item.Hint, Theme.FontSm) + 24;
                    w = Math.Max(w, iw);
                }
                return MathF.Ceiling(w);
            }
        }

        public float PreferredHeight
        {
            get
            {
                float h = 2 * PadY;
                foreach (MenuItem item in _items)
                    h += Height(item);
                return h;
            }
        }

        public void SetItems(IReadOnlyList<MenuItem> items)
        {
            _items = items;
            _hover = -1;
            _scroll = 0;
            InvalidatePaint();
        }

        /// <summary>Scrolls the checked item (a dropdown's current choice) into view.</summary>
        public void ScrollToChecked()
        {
            int i = -1;
            for (int n = 0; n < _items.Count && i < 0; n++)
                i = _items[n].IsChecked ? n : -1;
            if (i >= 0)
                EnsureVisible(i);
        }

        private float Top(int index)
        {
            float top = PadY;
            for (int i = 0; i < index; i++)
                top += Height(_items[i]);
            return top;
        }

        private void EnsureVisible(int index)
        {
            float top = Top(index), bottom = top + Height(_items[index]);
            if (top - PadY < _scroll)
                ScrollTo(top - PadY);
            else if (bottom + PadY > _scroll + H)
                ScrollTo(bottom + PadY - H);
        }

        private void ScrollTo(float offset) => SetAndPaint(ref _scroll, Math.Clamp(offset, 0, Math.Max(0, PreferredHeight - H)));

        public void MoveHover(int delta)
        {
            if (_items.Count == 0)
                return;
            int i = _hover;
            for (int n = 0; n < _items.Count; n++)
            {
                i = ((i < 0 && delta < 0 ? 0 : i) + delta + _items.Count) % _items.Count;
                if (_items[i].IsSelectable)
                {
                    SetHover(i);
                    EnsureVisible(i);
                    return;
                }
            }
        }

        private static float Height(MenuItem item) => item.IsSeparator ? SeparatorH : item.IsHeader ? HeaderH : RowH;

        private int IndexAt(float y)
        {
            y += _scroll;
            float top = PadY;
            for (int i = 0; i < _items.Count; i++)
            {
                float h = Height(_items[i]);
                if (y >= top && y < top + h)
                    return _items[i].IsSelectable ? i : -1;
                top += h;
            }
            return -1;
        }

        private void SetHover(int index) => SetAndPaint(ref _hover, index);

        protected override void OnHoverChanged()
        {
            if (!IsHovered)
                _hover = -1;
        }

        protected override void Paint(SKCanvas c)
        {
            var r = new SKRect(0, 0, W, H);
            Gfx.FillRound(c, r, Theme.RadiusLg, Theme.Overlay);
            Gfx.StrokeRound(c, r, Theme.RadiusLg, Theme.BorderStrong);
            int save = c.Save();
            c.ClipRect(SKRect.Inflate(r, -1, -2));
            c.Translate(0, -_scroll);
            float y = PadY;
            for (int i = 0; i < _items.Count; i++)
            {
                MenuItem item = _items[i];
                float h = Height(item);
                if (item.IsSeparator)
                {
                    Gfx.Line(c, 8, MathF.Round(y + h / 2f) + 0.5f, W - 8, MathF.Round(y + h / 2f) + 0.5f, Theme.Border);
                }
                else if (item.IsHeader)
                {
                    Gfx.Text(c, item.Text, PadX, y + h / 2f, Theme.FontSm, Theme.WeightRegular, Theme.TextMuted, TextAlignment.Left, W - 2 * PadX);
                }
                else
                {
                    if (i == _hover)
                        Gfx.FillRound(c, new SKRect(4, y, W - 4, y + h), Theme.RadiusSm, item.IsDanger ? Theme.DangerSoft : Theme.SurfaceHover);
                    SKColor fg = !item.IsEnabled ? Theme.TextDisabled : item.IsDanger ? Theme.Danger : Theme.TextPrimary;
                    float x = PadX;
                    if (item.IsChecked)
                        Icons.Draw(c, "check", x + 8, y + h / 2f, 14, Theme.Accent);
                    else if (item.Icon is not null)
                        Icons.Draw(c, item.Icon, x + 8, y + h / 2f, 15, item.IsEnabled ? (item.IsDanger ? Theme.Danger : Theme.TextSecondary) : Theme.TextDisabled);
                    x += 28;
                    float hintW = item.Hint is null ? 0 : Gfx.Measure(item.Hint, Theme.FontSm) + 16;
                    Gfx.Text(c, item.Text, x, y + h / 2f, Theme.FontBase, Theme.WeightRegular, fg, TextAlignment.Left, W - x - PadX - hintW);
                    if (item.Hint is not null)
                        Gfx.Text(c, item.Hint, W - PadX, y + h / 2f, Theme.FontSm, Theme.WeightRegular, Theme.TextMuted, TextAlignment.Right);
                }
                y += h;
            }
            c.RestoreToCount(save);
        }
    }
}
