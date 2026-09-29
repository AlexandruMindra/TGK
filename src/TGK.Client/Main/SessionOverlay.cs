using System;
using System.Collections.Generic;
using SkiaSharp;
using TGK.Client.Controls;

namespace TGK.Client.Main;

/// <summary>
/// Connection state over a session's terminal: a centered card while connecting or after a failure, or a banner
/// along the bottom once an established session has ended or is being reconnected automatically (the terminal above
/// stays readable and selectable). The owner positions it: the full tab for the card, the bottom
/// <see cref="BannerHeight"/> px for a banner (<see cref="IsBanner"/>).
/// </summary>
public sealed class SessionOverlay : Control
{
    public const float BannerHeight = 44;
    private const float CardW = 440, Pad = 24, IconSize = 28, ButtonH = 32, TitleH = 24, LineH = 19, MaxMessageLines = 5;

    private readonly Spinner _spinner = new();
    private readonly Button _primary = new("Retry", ButtonVariant.Primary, "refresh");
    private readonly Button _secondary = new("Cancel", ButtonVariant.Secondary);
    private OverlayMode _mode;
    private string _title = "";
    private string _message = "";
    private bool _error;

    public SessionOverlay()
    {
        Visible = false;
        AddChild(_spinner);
        AddChild(_primary);
        AddChild(_secondary);
        _primary.Clicked += () => PrimaryClicked?.Invoke();
        _secondary.Clicked += () => SecondaryClicked?.Invoke();
        Events.OnMouseDown += (_, e) => e.Handled = true; // the card is modal for its tab
    }

    public enum OverlayMode
    {
        Hidden,
        Connecting,
        Failed,
        Ended,

        /// <summary>Banner: the connection was lost and is reconnected automatically.</summary>
        Reconnecting,
    }

    /// <summary>Retry (failed), Reconnect (ended) or Reconnect now (reconnecting).</summary>
    public event Action? PrimaryClicked;

    /// <summary>Cancel (connecting, reconnecting) or Edit host (failed, saved hosts only).</summary>
    public event Action? SecondaryClicked;

    public OverlayMode Mode => _mode;

    public bool IsBanner => _mode is OverlayMode.Ended or OverlayMode.Reconnecting;

    public void ShowConnecting(string title, string detail) => Show(OverlayMode.Connecting, title, detail, false, null, "Cancel");

    /// <param name="error">Red alert styling; false for a neutral "not connected" state (e.g. the user cancelled).</param>
    public void ShowFailed(string title, string message, string? secondary, bool error = true) =>
        Show(OverlayMode.Failed, title, message, error, "Retry", secondary);

    public void ShowEnded(string message, bool error) => Show(OverlayMode.Ended, "", message, error, "Reconnect", null);

    /// <param name="waiting">Counting down to the next attempt ("Reconnect now" is offered); false while an attempt runs.</param>
    public void ShowReconnecting(string message, bool waiting) =>
        Show(OverlayMode.Reconnecting, "", message, false, waiting ? "Reconnect now" : null, "Cancel");

    public void Hide()
    {
        _mode = OverlayMode.Hidden;
        Visible = false;
    }

    private void Show(OverlayMode mode, string title, string message, bool error, string? primary, string? secondary)
    {
        _mode = mode;
        _title = title;
        _message = message;
        _error = error;
        _spinner.Visible = mode == OverlayMode.Connecting || (mode == OverlayMode.Reconnecting && primary is null);
        _primary.Visible = primary is not null;
        _primary.Text = primary ?? "";
        _secondary.Visible = secondary is not null;
        _secondary.Text = secondary ?? "";
        Visible = true;
        Parent?.InvalidateLayout();
        InvalidateLayout();
    }

    private List<string> MessageLines(float width) =>
        Gfx.Wrap(_message, Gfx.Font(Theme.FontBase), width, (int)MaxMessageLines);

    // The card, centered in the element; its height follows the message.
    private SKRect CardRect()
    {
        float w = Math.Min(CardW, W - 32);
        float h = Pad + IconSize + 16 + TitleH + 8 + MessageLines(w - 2 * Pad).Count * LineH + 20 + ButtonH + Pad;
        float x = MathF.Round((W - w) / 2f), y = MathF.Round(Math.Max(16, (H - h) / 2f - H * 0.06f));
        return new SKRect(x, y, x + w, y + h);
    }

    protected override void LayoutChildren()
    {
        if (IsBanner)
        {
            // Right-aligned [primary] [secondary].
            float right = W - 12;
            foreach (Button b in new[] { _secondary, _primary })
            {
                if (!b.Visible)
                    continue;
                float bw = Math.Max(96, b.PreferredWidth);
                right -= bw;
                b.Transform.SetLocalFrame(right, (H - 28) / 2f, bw, 28);
                right -= 8;
            }
            _spinner.Transform.SetLocalFrame(16, (H - 16) / 2f, 16, 16);
            return;
        }
        SKRect card = CardRect();
        _spinner.Transform.SetLocalFrame(card.MidX - IconSize / 2f, card.Top + Pad, IconSize, IconSize);

        // Buttons centered at the bottom of the card: [secondary] [primary].
        var buttons = new List<Button>();
        if (_secondary.Visible)
            buttons.Add(_secondary);
        if (_primary.Visible)
            buttons.Add(_primary);
        float total = 0;
        foreach (Button b in buttons)
            total += Math.Max(96, b.PreferredWidth);
        total += 10 * Math.Max(0, buttons.Count - 1);
        float bx = card.MidX - total / 2f, by = card.Bottom - Pad - ButtonH;
        foreach (Button b in buttons)
        {
            float bw = Math.Max(96, b.PreferredWidth);
            b.Transform.SetLocalFrame(MathF.Round(bx), by, bw, ButtonH);
            bx += bw + 10;
        }
    }

    protected override void Paint(SKCanvas c)
    {
        if (IsBanner)
        {
            PaintBanner(c);
            return;
        }

        Gfx.FillRect(c, new SKRect(0, 0, W, H), Theme.TerminalBg.WithAlpha(215));
        SKRect card = CardRect();
        Gfx.Shadow(c, card, Theme.RadiusLg, 28, 8, SKColors.Black.WithAlpha(120));
        Gfx.FillRound(c, card, Theme.RadiusLg, Theme.Overlay);
        Gfx.StrokeRound(c, card, Theme.RadiusLg, Theme.BorderStrong);

        float y = card.Top + Pad;
        if (_mode == OverlayMode.Failed)
        {
            SKColor tint = _error ? Theme.Danger : Theme.TextMuted;
            Gfx.Circle(c, card.MidX, y + IconSize / 2f, IconSize / 2f + 4, tint.WithAlpha(34));
            Icons.Draw(c, _error ? "alert" : "terminal", card.MidX, y + IconSize / 2f, 18, tint);
        }
        y += IconSize + 16;
        Gfx.Text(c, _title, card.MidX, y + TitleH / 2f, Theme.FontLg, Theme.WeightSemibold, Theme.TextPrimary,
            TextAlignment.Center, card.Width - 2 * Pad);
        y += TitleH + 8;
        foreach (string line in MessageLines(card.Width - 2 * Pad))
        {
            Gfx.Text(c, line, card.MidX, y + LineH / 2f, Theme.FontBase, Theme.WeightRegular, Theme.TextSecondary, TextAlignment.Center);
            y += LineH;
        }
    }

    private void PaintBanner(SKCanvas c)
    {
        var r = new SKRect(0, 0, W, H);
        Gfx.FillRect(c, r, Theme.SurfaceRaised);
        Gfx.Line(c, 0, 0.5f, W, 0.5f, Theme.BorderStrong);
        bool reconnecting = _mode == OverlayMode.Reconnecting;
        SKColor tint = _error ? Theme.Danger : reconnecting ? Theme.Warning : Theme.TextMuted;
        if (!_spinner.Visible)
            Icons.Draw(c, _error ? "alert" : reconnecting ? "refresh" : "terminal", 24, H / 2f, 16, tint);
        Button first = _primary.Visible ? _primary : _secondary;
        float textMax = first.Transform.Computed.X - Transform.Computed.X - 44 - 16;
        Gfx.Text(c, _message, 44, H / 2f, Theme.FontBase, Theme.WeightRegular,
            _error ? Theme.Danger : reconnecting ? Theme.TextPrimary : Theme.TextSecondary, TextAlignment.Left, textMax);
    }

    /// <summary>An indeterminate progress ring; animates only while visible.</summary>
    private sealed class Spinner : Control
    {
        private const int FrameMs = 33;
        private long _nextFrame;

        public Spinner()
        {
            IsClickthrough = true;
            Interactive = false;
            UiClock.Tick += OnTick;
        }

        public override void Dispose()
        {
            UiClock.Tick -= OnTick;
            base.Dispose();
        }

        private void OnTick()
        {
            if (UiClock.NowMs < _nextFrame || !EffectiveVisible)
                return;
            _nextFrame = UiClock.NowMs + FrameMs;
            InvalidatePaint();
        }

        protected override void Paint(SKCanvas c)
        {
            float stroke = 3, r = Math.Min(W, H) / 2f - stroke;
            var oval = new SKRect(W / 2f - r, H / 2f - r, W / 2f + r, H / 2f + r);
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, StrokeCap = SKStrokeCap.Round };
            paint.Color = Theme.Accent.WithAlpha(45);
            c.DrawOval(oval, paint);
            paint.Color = Theme.Accent;
            float start = UiClock.NowMs % 1000 / 1000f * 360f;
            using var arc = new SKPath();
            arc.AddArc(oval, start, 100);
            c.DrawPath(arc, paint);
        }
    }
}
