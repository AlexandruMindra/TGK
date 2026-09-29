using System;
using System.Collections.Generic;
using Blossom.Core.Input;
using Silk.NET.Input;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Client.Views;
using TGK.Core.Models;

namespace TGK.Client.Main;

/// <summary>Responsive grid of host cards (new-tab page). Click connects, right-click opens the host menu.</summary>
public sealed class HostCardGrid : Control
{
    public const float CardH = 64, Gap = 12, MinCardW = 210;
    private readonly MainView _main;
    private readonly bool _showLastUsed;
    private IReadOnlyList<HostEntry> _hosts = [];
    private int _hover = -1;

    /// <param name="showLastUsed">Show "3h ago" instead of the group name (the Recent row).</param>
    public HostCardGrid(MainView main, bool showLastUsed)
    {
        _main = main;
        _showLastUsed = showLastUsed;
        Cursor = StandardCursor.Hand;
        Events.OnMouseMove += (_, e) => SetAndPaint(ref _hover, IndexAt(e.Relative.X, e.Relative.Y));
        Events.OnMouseDown += OnDown;
        Events.OnClick += (_, e) =>
        {
            e.Handled = true;
            int i = IndexAt(e.Relative.X, e.Relative.Y);
            if (i >= 0)
                HostClicked?.Invoke(_hosts[i]);
        };
    }

    public event Action<HostEntry>? HostClicked;

    public IReadOnlyList<HostEntry> Hosts
    {
        get => _hosts;
        set
        {
            _hosts = value;
            _hover = -1;
            InvalidatePaint();
        }
    }

    public static int ColumnsFor(float width) => Math.Max(1, (int)((width + Gap) / (MinCardW + Gap)));

    /// <summary>Height needed for <paramref name="count"/> cards at <paramref name="width"/>.</summary>
    public static float HeightFor(int count, float width)
    {
        if (count == 0)
            return 0;
        int rows = (count + ColumnsFor(width) - 1) / ColumnsFor(width);
        return rows * CardH + (rows - 1) * Gap;
    }

    private SKRect CardRect(int i)
    {
        int cols = ColumnsFor(W);
        float cw = (W - (cols - 1) * Gap) / cols;
        int row = i / cols, col = i % cols;
        float x = MathF.Round(col * (cw + Gap));
        float y = row * (CardH + Gap);
        return new SKRect(x, y, MathF.Round(x + cw), y + CardH);
    }

    private int IndexAt(float x, float y)
    {
        for (int i = 0; i < _hosts.Count; i++)
        {
            if (CardRect(i).Contains(x, y))
                return i;
        }
        return -1;
    }

    protected override void OnHoverChanged()
    {
        if (!IsHovered)
            _hover = -1;
    }

    private void OnDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != 1)
            return;
        e.Handled = true;
        int i = IndexAt(e.Relative.X, e.Relative.Y);
        if (i >= 0)
            _main.ShowHostMenu(_hosts[i], e.Global.X, e.Global.Y);
    }

    protected override void Paint(SKCanvas c)
    {
        VaultData vault = _main.Services.Vault.Current;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int i = 0; i < _hosts.Count; i++)
        {
            HostEntry host = _hosts[i];
            SKRect r = CardRect(i);
            bool hover = i == _hover;
            Gfx.FillRound(c, r, Theme.RadiusLg, hover ? Theme.SurfaceHover : Theme.SurfaceRaised);
            Gfx.StrokeRound(c, r, Theme.RadiusLg, hover ? Theme.BorderStrong : Theme.Border);

            SKColor tag = Theme.ParseHex(host.TagColor, Theme.Idle);
            var tile = SKRect.Create(r.Left + 14, r.MidY - 18, 36, 36);
            Gfx.FillRound(c, tile, Theme.Radius + 2, tag.WithAlpha(38));
            Icons.Draw(c, "server", tile.MidX, tile.MidY, 18, tag);

            float tx = tile.Right + 12, textW = r.Right - tx - 14;
            // The name wins: the group / last-used label only shows when both fit.
            string meta = _showLastUsed ? HostFormat.Ago(host.LastConnected, now) : vault.FindGroup(host.GroupId)?.Name ?? "";
            float metaW = meta.Length == 0 ? 0 : Gfx.Measure(meta, Theme.FontXs) + 10;
            if (Gfx.Measure(host.DisplayName, Theme.FontMd, Theme.WeightSemibold) + metaW > textW)
                metaW = 0;
            Gfx.Text(c, host.DisplayName, tx, r.MidY - 9, Theme.FontMd, Theme.WeightSemibold, Theme.TextPrimary, TextAlignment.Left, textW - metaW);
            if (metaW > 0)
                Gfx.Text(c, meta, r.Right - 14, r.MidY - 9, Theme.FontXs, Theme.WeightRegular, Theme.TextMuted, TextAlignment.Right);
            float addressRight = HostHints.Draw(c, host, vault, r.Right - 14, r.MidY + 10, Theme.TextMuted);
            Gfx.Text(c, HostFormat.Address(host, vault), tx, r.MidY + 10, Theme.FontSm, Theme.WeightRegular, Theme.TextMuted, TextAlignment.Left, addressRight - tx - 4);
        }
    }
}
