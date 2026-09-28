using System;
using Blossom.Core.Input;
using Silk.NET.Input;
using SkiaSharp;

namespace TGK.Client.Controls;

/// <summary>A row of mutually exclusive options (e.g. "Password | Private key").</summary>
public class SegmentedControl : Control
{
    private readonly string[] _options;
    private int _selected;
    private int _hover = -1;

    public SegmentedControl(params string[] options)
    {
        _options = options;
        Cursor = StandardCursor.Hand;
        Events.OnMouseMove += (_, e) => SetHover(IndexAt(e.Relative.X));
        Events.OnClick += OnClick;
    }

    /// <summary>Raised when the user picks a different option.</summary>
    public event Action<int>? SelectionChanged;

    public int SelectedIndex
    {
        get => _selected;
        set => SetAndPaint(ref _selected, Math.Clamp(value, 0, _options.Length - 1));
    }

    private float SegmentWidth => (W - 4) / _options.Length;

    private int IndexAt(float x) => x < 2 || x > W - 2 ? -1 : Math.Clamp((int)((x - 2) / SegmentWidth), 0, _options.Length - 1);

    private void SetHover(int index) => SetAndPaint(ref _hover, index);

    protected override void OnHoverChanged()
    {
        if (!IsHovered)
            _hover = -1;
    }

    private void OnClick(object? sender, MouseEventArgs e)
    {
        e.Handled = true;
        int index = IndexAt(e.Relative.X);
        if (index < 0 || index == _selected)
            return;
        SelectedIndex = index;
        SelectionChanged?.Invoke(index);
    }

    protected override void Paint(SKCanvas c)
    {
        var r = new SKRect(0, 0, W, H);
        Gfx.FillRound(c, r, Theme.Radius, Theme.Input);
        Gfx.StrokeRound(c, r, Theme.Radius, Theme.BorderInput);
        float sw = SegmentWidth;
        for (int i = 0; i < _options.Length; i++)
        {
            var seg = new SKRect(2 + i * sw, 2, 2 + (i + 1) * sw, H - 2);
            bool selected = i == _selected;
            if (selected)
            {
                Gfx.FillRound(c, seg, Theme.Radius - 2, Theme.SurfaceHover);
                Gfx.StrokeRound(c, seg, Theme.Radius - 2, Theme.BorderStrong);
            }
            else if (i == _hover)
            {
                Gfx.FillRound(c, seg, Theme.Radius - 2, Theme.SurfaceRaised);
            }
            Gfx.Text(c, _options[i], seg.MidX, H / 2f, Theme.FontBase, selected ? Theme.WeightSemibold : Theme.WeightRegular,
                selected ? Theme.TextPrimary : Theme.TextSecondary, TextAlignment.Center, sw - 8);
        }
    }
}
