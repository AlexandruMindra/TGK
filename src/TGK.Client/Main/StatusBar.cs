using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Core.Ssh;

namespace TGK.Client.Main;

/// <summary>
/// Bottom bar: active session on the left; its tunnels, terminal size and sync status on the right, and a quiet chip
/// when a newer TGK version is available.
/// </summary>
public sealed class StatusBar : Control
{
    private string _left = "";
    private TabStatus _status;
    private string? _size;
    private string _sync = "";
    private IReadOnlyList<TunnelStatus> _tunnels = [];
    private string? _update;
    private SKRect _chip = SKRect.Empty, _updateChip = SKRect.Empty;
    private bool _chipHover, _updateHover;

    public StatusBar()
    {
        Events.OnMouseMove += (_, e) =>
        {
            SetAndPaint(ref _chipHover, _chip.Contains(e.Relative.X, e.Relative.Y));
            SetAndPaint(ref _updateHover, _updateChip.Contains(e.Relative.X, e.Relative.Y));
        };
        Events.OnClick += (_, e) =>
        {
            SKRect chip = _chip.Contains(e.Relative.X, e.Relative.Y) ? _chip : _updateChip.Contains(e.Relative.X, e.Relative.Y) ? _updateChip : SKRect.Empty;
            if (chip.IsEmpty)
                return;
            e.Handled = true;
            var at = Transform.Computed;
            var window = new SKRect(at.X + chip.Left, at.Y + chip.Top, at.X + chip.Right, at.Y + chip.Bottom);
            if (chip == _chip)
                TunnelsClicked?.Invoke(window);
            else
                UpdateClicked?.Invoke(window);
        };
    }

    /// <summary>The tunnel chip was clicked; the argument is its window rect (to anchor a popup).</summary>
    public event Action<SKRect>? TunnelsClicked;

    /// <summary>The update chip was clicked; the argument is its window rect.</summary>
    public event Action<SKRect>? UpdateClicked;

    /// <summary>Shows the update chip with <paramref name="text"/> (e.g. "TGK 0.3.0 available"), or hides it (null).</summary>
    public void SetUpdate(string? text)
    {
        if (SetAndPaint(ref _update, text) && text is null)
            _updateHover = false;
    }

    public void Set(string left, TabStatus status, string? size, string sync, IReadOnlyList<TunnelStatus> tunnels)
    {
        if (_left == left && _status == status && _size == size && _sync == sync && ReferenceEquals(_tunnels, tunnels))
            return;
        _left = left;
        _status = status;
        _size = size;
        _sync = sync;
        _tunnels = tunnels;
        InvalidatePaint();
    }

    protected override void OnHoverChanged()
    {
        if (!IsHovered)
            _chipHover = _updateHover = false;
    }

    protected override void Paint(SKCanvas c)
    {
        Gfx.FillRect(c, new SKRect(0, 0, W, H), Theme.Chrome);
        Gfx.Line(c, 0, 0.5f, W, 0.5f, Theme.Border);
        float cy = H / 2f;

        float right = PaintUpdateChip(c, W - 12, cy);
        float syncW = Gfx.Measure(_sync, Theme.FontXs);
        Gfx.Text(c, _sync, right, cy, Theme.FontXs, Theme.WeightRegular, Theme.TextMuted, TextAlignment.Right);
        right -= syncW + 18;
        if (_size is not null)
        {
            Gfx.Text(c, _size, right, cy, Theme.FontXs, Theme.WeightRegular, Theme.TextMuted, TextAlignment.Right);
            right -= Gfx.Measure(_size, Theme.FontXs) + 18;
        }
        right = PaintTunnelChip(c, right, cy);

        float x = 12;
        if (_status != TabStatus.None)
        {
            Gfx.Circle(c, x + 3, cy, 3, TabStrip.StatusColor(_status));
            x += 12;
        }
        Gfx.Text(c, _left, x, cy, Theme.FontXs, Theme.WeightRegular, Theme.TextSecondary, TextAlignment.Left, right - x - 12);
    }

    // "↓ TGK 0.3.0 available" at the right end, in the accent color but without a fill until hovered.
    private float PaintUpdateChip(SKCanvas c, float right, float cy)
    {
        _updateChip = SKRect.Empty;
        if (_update is null)
            return right;
        float w = Gfx.Measure(_update, Theme.FontXs) + 30;
        _updateChip = new SKRect(right - w + 6, 3, right + 6, H - 3);
        if (_updateHover)
            Gfx.FillRound(c, _updateChip, Theme.RadiusSm, Theme.AccentSoft);
        SKColor color = _updateHover ? Theme.AccentHover : Theme.Accent;
        Icons.Draw(c, "download", _updateChip.Left + 12, cy, 12, color);
        Gfx.Text(c, _update, _updateChip.Left + 22, cy, Theme.FontXs, Theme.WeightRegular, color);
        return _updateChip.Left - 12;
    }

    // "⇄ 3 tunnels" (amber with the failed count when any failed); returns the new right edge for the text beside it.
    private float PaintTunnelChip(SKCanvas c, float right, float cy)
    {
        _chip = SKRect.Empty;
        if (_tunnels.Count == 0)
            return right;
        int failed = _tunnels.Count(t => t.State == TunnelState.Failed);
        string text = $"{_tunnels.Count} tunnel{(_tunnels.Count == 1 ? "" : "s")}" + (failed > 0 ? $" · {failed} failed" : "");
        SKColor color = failed > 0 ? Theme.Warning
            : _tunnels.All(t => t.State == TunnelState.Active) ? Theme.TextSecondary
            : Theme.TextMuted;
        float w = Gfx.Measure(text, Theme.FontXs) + 30;
        _chip = new SKRect(right - w, 3, right, H - 3);
        Gfx.FillRound(c, _chip, Theme.RadiusSm, failed > 0 ? Theme.Warning.WithAlpha(_chipHover ? (byte)48 : (byte)28)
            : _chipHover ? Theme.SurfaceHover : Theme.SurfaceRaised);
        Icons.Draw(c, "tunnel", _chip.Left + 12, cy, 12, color);
        Gfx.Text(c, text, _chip.Left + 22, cy, Theme.FontXs, Theme.WeightRegular, color);
        return _chip.Left - 18;
    }
}
