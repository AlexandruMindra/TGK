using System;
using System.Collections.Generic;
using System.Linq;
using Silk.NET.Input;
using SkiaSharp;
using TGK.Client.Views;

namespace TGK.Client.Controls;

/// <summary>Select box: shows the chosen option and opens a list in the view's <see cref="PopupMenu"/>.</summary>
public class Dropdown : Control
{
    private IReadOnlyList<string> _options = [];
    private int _selected = -1;
    private bool _open;

    public Dropdown()
    {
        Cursor = StandardCursor.Hand;
        Events.OnClick += (_, e) =>
        {
            e.Handled = true;
            Open();
        };
    }

    /// <summary>Raised when the user picks an option.</summary>
    public event Action<int>? SelectionChanged;

    public IReadOnlyList<string> Options
    {
        get => _options;
        set
        {
            _options = value;
            _selected = Math.Min(_selected, _options.Count - 1);
            InvalidatePaint();
        }
    }

    public int SelectedIndex { get => _selected; set => SetAndPaint(ref _selected, Math.Clamp(value, -1, _options.Count - 1)); }

    public string Placeholder { get; set; } = "Select…";

    private void Open()
    {
        if (ParentView is not TgkView view || _options.Count == 0)
            return;
        List<MenuItem> items = _options.Select((text, i) => new MenuItem
        {
            Text = text,
            IsChecked = i == _selected,
            Action = () => Pick(i),
        }).ToList();
        _open = true;
        InvalidatePaint();
        view.Menu.Show(items, Transform.Computed.X, Transform.Computed.Y + H + 4, W, H, () => { _open = false; InvalidatePaint(); });
    }

    private void Pick(int index)
    {
        if (index == _selected)
            return;
        SelectedIndex = index;
        SelectionChanged?.Invoke(index);
    }

    protected override void Paint(SKCanvas c)
    {
        var r = new SKRect(0, 0, W, H);
        Gfx.FillRound(c, r, Theme.Radius, Theme.Input);
        Gfx.StrokeRound(c, r, Theme.Radius, _open ? Theme.Accent : IsHovered ? Theme.BorderStrong : Theme.BorderInput, _open ? 1.5f : 1f);
        bool has = _selected >= 0 && _selected < _options.Count;
        Gfx.Text(c, has ? _options[_selected] : Placeholder, 10, H / 2f, Theme.FontBase, Theme.WeightRegular,
            has ? Theme.TextPrimary : Theme.TextMuted, TextAlignment.Left, W - 40);
        Icons.Draw(c, "chevron-down", W - 17, H / 2f, 16, Theme.TextMuted);
    }
}
