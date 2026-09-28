using System;
using Blossom.Core.Visual;
using SkiaSharp;

namespace TGK.Client.Controls;

public enum ToastKind
{
    Info,
    Success,
    Error,
}

/// <summary>Bottom-centered notification pill that hides itself after a few seconds. Use <c>TgkView.ShowToast</c>.</summary>
public sealed class Toast : Control
{
    private const int DurationMs = 3200;
    private const float PillHeight = 36;
    private string _message = "";
    private ToastKind _kind;
    private long _hideAt;

    public Toast()
    {
        Name = "Toast";
        Visible = false;
        ZIndex = 4000;
        IsClickthrough = true;
        Interactive = false;
        Style = new ElementStyle { Shadow = new ShadowStyle(0, 4, 6, 6, SKColors.Black.WithAlpha(100)), Border = new BorderStyle { Roundness = PillHeight / 2 } };
    }

    public void Show(string message, ToastKind kind)
    {
        _message = message;
        _kind = kind;
        _hideAt = UiClock.NowMs + DurationMs;
        var view = ParentView;
        float w = MathF.Ceiling(Math.Min(view.Width - 32, Gfx.Measure(message, Theme.FontBase) + 58));
        Transform.SetAbsoluteFrame(MathF.Round((view.Width - w) / 2f), view.Height - Theme.StatusBarHeight - PillHeight - 20, w, PillHeight);
        Transform.Anchor = Anchor.Bottom;
        Transform.FixedWidth = true;
        Transform.FixedHeight = true;
        if (!Visible)
            UiClock.Tick += OnTick;
        Visible = true;
        InvalidatePaint();
    }

    private void OnTick()
    {
        if (UiClock.NowMs < _hideAt)
            return;
        UiClock.Tick -= OnTick;
        Visible = false;
    }

    protected override void Paint(SKCanvas c)
    {
        var r = new SKRect(0, 0, W, H);
        Gfx.FillRound(c, r, H / 2f, Theme.SurfaceHover);
        Gfx.StrokeRound(c, r, H / 2f, Theme.BorderStrong);
        SKColor dot = _kind switch { ToastKind.Success => Theme.Success, ToastKind.Error => Theme.Danger, _ => Theme.Accent };
        Gfx.Circle(c, 20, H / 2f, 4, dot);
        Gfx.Text(c, _message, 34, H / 2f, Theme.FontBase, Theme.WeightRegular, Theme.TextPrimary, TextAlignment.Left, W - 50);
    }
}
