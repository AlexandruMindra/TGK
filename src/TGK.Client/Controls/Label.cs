using System;
using System.Collections.Generic;
using SkiaSharp;

namespace TGK.Client.Controls;

/// <summary>Static text. Single line with ellipsis by default; set <see cref="MaxLines"/> above 1 to word-wrap.</summary>
public class Label : Control
{
    private string _text;
    private SKColor _color;
    private float _size;
    private int _weight;
    private TextAlignment _align;
    private int _maxLines = 1;
    private bool _mono;

    public Label(string text = "", float size = Theme.FontBase, SKColor? color = null, int weight = Theme.WeightRegular,
        TextAlignment align = TextAlignment.Left)
    {
        _text = text;
        _size = size;
        _color = color ?? Theme.TextPrimary;
        _weight = weight;
        _align = align;
        IsClickthrough = true;
        Interactive = false;
    }

    public new string Text { get => _text; set => SetAndPaint(ref _text, value ?? ""); }
    public SKColor Color { get => _color; set => SetAndPaint(ref _color, value); }
    public float Size { get => _size; set => SetAndPaint(ref _size, value); }
    public int Weight { get => _weight; set => SetAndPaint(ref _weight, value); }
    public TextAlignment Align { get => _align; set => SetAndPaint(ref _align, value); }
    public int MaxLines { get => _maxLines; set => SetAndPaint(ref _maxLines, Math.Max(1, value)); }

    /// <summary>Use the monospace font (fingerprints, addresses).</summary>
    public bool Mono { get => _mono; set => SetAndPaint(ref _mono, value); }

    public float LineHeight => MathF.Ceiling(_size * 1.45f);

    private SKPaint Font => _mono ? Gfx.Font(_size, _weight >= Theme.WeightSemibold ? Theme.MonoBold : Theme.Mono) : Gfx.Font(_size, _weight);

    /// <summary>Height needed to show the text at <paramref name="width"/> (respects <see cref="MaxLines"/>).</summary>
    public float MeasureHeight(float width) =>
        _maxLines == 1 ? LineHeight : Math.Max(1, Gfx.Wrap(_text, Font, Math.Max(1, width), _maxLines).Count) * LineHeight;

    public float MeasureWidth() => MathF.Ceiling(Gfx.Measure(_text, Font));

    protected override void Paint(SKCanvas c)
    {
        float x = _align switch { TextAlignment.Center => W / 2f, TextAlignment.Right => W, _ => 0 };
        if (_maxLines == 1)
        {
            Gfx.Text(c, _text, x, H / 2f, Font, _color, _align, W);
            return;
        }
        List<string> lines = Gfx.Wrap(_text, Font, W, _maxLines);
        float y = LineHeight / 2f;
        foreach (string line in lines)
        {
            Gfx.Text(c, line, x, y, Font, _color, _align, W);
            y += LineHeight;
        }
    }
}
