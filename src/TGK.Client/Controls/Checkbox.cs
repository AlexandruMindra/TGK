using System;
using Silk.NET.Input;
using SkiaSharp;

namespace TGK.Client.Controls;

/// <summary>Check box with a clickable label.</summary>
public class Checkbox : Control
{
    private const float Box = 16;
    private bool _checked;
    private string _label;

    public Checkbox(string label, bool isChecked = false)
    {
        _label = label;
        _checked = isChecked;
        Cursor = StandardCursor.Hand;
        Events.OnClick += (_, e) =>
        {
            e.Handled = true;
            Checked = !Checked;
            CheckedChanged?.Invoke(_checked);
        };
    }

    /// <summary>Raised when the user toggles the box (not for programmatic sets).</summary>
    public event Action<bool>? CheckedChanged;

    public bool Checked { get => _checked; set => SetAndPaint(ref _checked, value); }
    public string Label { get => _label; set => SetAndPaint(ref _label, value); }

    public float PreferredWidth => MathF.Ceiling(Box + 8 + Gfx.Measure(_label, Theme.FontBase));

    protected override void Paint(SKCanvas c)
    {
        float y = MathF.Round((H - Box) / 2f);
        var box = new SKRect(0, y, Box, y + Box);
        if (_checked)
        {
            Gfx.FillRound(c, box, Theme.RadiusSm, IsHovered ? Theme.AccentHover : Theme.Accent);
            Icons.Draw(c, "check", box.MidX, box.MidY, 14, Theme.TextOnAccent);
        }
        else
        {
            Gfx.FillRound(c, box, Theme.RadiusSm, Theme.Input);
            Gfx.StrokeRound(c, box, Theme.RadiusSm, IsHovered ? Theme.TextMuted : Theme.BorderStrong, 1.5f);
        }
        Gfx.Text(c, _label, Box + 8, H / 2f, Theme.FontBase, Theme.WeightRegular,
            IsHovered ? Theme.TextPrimary : Theme.TextSecondary, TextAlignment.Left, W - Box - 8);
    }
}
