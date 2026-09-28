using System;
using Silk.NET.Input;
using SkiaSharp;

namespace TGK.Client.Controls;

public enum ButtonVariant
{
    /// <summary>Accent fill: the main action of a view or dialog.</summary>
    Primary,
    /// <summary>Raised neutral fill with a border.</summary>
    Secondary,
    /// <summary>Transparent until hovered.</summary>
    Ghost,
    /// <summary>Destructive action.</summary>
    Danger,
}

/// <summary>Text button with an optional leading icon.</summary>
public class Button : Control
{
    private string _text;
    private string? _icon;
    private ButtonVariant _variant;

    public Button(string text, ButtonVariant variant = ButtonVariant.Secondary, string? icon = null)
    {
        _text = text;
        _variant = variant;
        _icon = icon;
        Cursor = StandardCursor.Hand;
        Events.OnClick += (_, e) =>
        {
            e.Handled = true;
            if (Enabled)
                Clicked?.Invoke();
        };
    }

    public event Action? Clicked;

    public new string Text { get => _text; set => SetAndPaint(ref _text, value); }
    public string? Icon { get => _icon; set => SetAndPaint(ref _icon, value); }
    public ButtonVariant Variant { get => _variant; set => SetAndPaint(ref _variant, value); }
    public float FontSize { get; set; } = Theme.FontBase;

    /// <summary>Width that fits the label and icon with standard padding.</summary>
    public float PreferredWidth
    {
        get
        {
            float w = Gfx.Measure(_text, FontSize, Theme.WeightSemibold) + 2 * 14;
            if (_icon is not null)
                w += 16 + (_text.Length > 0 ? 6 : 0);
            return MathF.Ceiling(Math.Max(w, 64));
        }
    }

    /// <summary>Invokes the click handlers as if the button was clicked (keyboard activation).</summary>
    public void PerformClick()
    {
        if (Enabled && EffectiveVisible)
            Clicked?.Invoke();
    }

    protected override void Paint(SKCanvas c)
    {
        var r = new SKRect(0, 0, W, H);
        (SKColor bg, SKColor fg, SKColor border) = Colors();
        Gfx.FillRound(c, r, Theme.Radius, bg);
        Gfx.StrokeRound(c, r, Theme.Radius, border);

        float textW = Gfx.Measure(_text, FontSize, Theme.WeightSemibold);
        float iconW = _icon is null ? 0 : 16 + (_text.Length > 0 ? 6 : 0);
        float x = MathF.Round((W - textW - iconW) / 2f);
        if (_icon is not null)
        {
            Icons.Draw(c, _icon, x + 8, H / 2f, 16, fg);
            x += iconW;
        }
        Gfx.Text(c, _text, x, H / 2f, FontSize, Theme.WeightSemibold, fg, TextAlignment.Left, W - 8);
    }

    private (SKColor Bg, SKColor Fg, SKColor Border) Colors()
    {
        if (!Enabled)
        {
            return _variant switch
            {
                ButtonVariant.Primary => (Theme.Accent.WithAlpha(90), Theme.TextOnAccent.WithAlpha(150), SKColors.Transparent),
                ButtonVariant.Danger => (Theme.Danger.WithAlpha(80), Theme.TextOnAccent.WithAlpha(150), SKColors.Transparent),
                ButtonVariant.Ghost => (SKColors.Transparent, Theme.TextDisabled, SKColors.Transparent),
                _ => (Theme.SurfaceRaised, Theme.TextDisabled, Theme.Border),
            };
        }
        return _variant switch
        {
            ButtonVariant.Primary => (IsPressed ? Theme.AccentPressed : IsHovered ? Theme.AccentHover : Theme.Accent, Theme.TextOnAccent, SKColors.Transparent),
            ButtonVariant.Danger => (IsPressed ? Theme.Mix(Theme.Danger, SKColors.Black, 0.15f) : IsHovered ? Theme.DangerHover : Theme.Danger, Theme.TextOnAccent, SKColors.Transparent),
            ButtonVariant.Ghost => (IsPressed ? Theme.SurfacePressed : IsHovered ? Theme.SurfaceHover : SKColors.Transparent, IsHovered ? Theme.TextPrimary : Theme.TextSecondary, SKColors.Transparent),
            _ => (IsPressed ? Theme.SurfacePressed : IsHovered ? Theme.SurfaceHover : Theme.SurfaceRaised, Theme.TextPrimary, IsHovered ? Theme.BorderStrong : Theme.Border),
        };
    }
}

/// <summary>Square icon-only button (toolbar, tab strip, row actions).</summary>
public class IconButton : Control
{
    private string _icon;
    private bool _active;

    public IconButton(string icon, float iconSize = 16)
    {
        _icon = icon;
        IconSize = iconSize;
        Cursor = StandardCursor.Hand;
        Events.OnClick += (_, e) =>
        {
            e.Handled = true;
            if (Enabled)
                Clicked?.Invoke();
        };
    }

    public event Action? Clicked;

    public string Icon { get => _icon; set => SetAndPaint(ref _icon, value); }
    public float IconSize { get; set; }

    /// <summary>Toggled/selected look (accent icon on a soft accent background).</summary>
    public bool Active { get => _active; set => SetAndPaint(ref _active, value); }

    public SKColor? IconColor { get; set; }

    protected override void Paint(SKCanvas c)
    {
        var r = new SKRect(0, 0, W, H);
        SKColor bg = _active ? Theme.AccentSoft : IsPressed ? Theme.SurfacePressed : IsHovered ? Theme.SurfaceHover : SKColors.Transparent;
        Gfx.FillRound(c, r, Theme.Radius, bg);
        SKColor fg = !Enabled ? Theme.TextDisabled
            : _active ? Theme.Accent
            : IconColor ?? (IsHovered ? Theme.TextPrimary : Theme.TextSecondary);
        Icons.Draw(c, _icon, W / 2f, H / 2f, IconSize, fg);
    }
}
