using System;
using System.Collections.Generic;
using System.Linq;
using Blossom.Core.Input;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Core.Sftp;

namespace TGK.Client.Files;

/// <summary>
/// The file tab's transfers, newest first: what is copied where, a progress bar with size, speed and time left, and a
/// button to cancel it (or, for a finished download, to show its folder). The header clears the finished ones.
/// <see cref="Refresh"/> is called on every UI tick while something runs.
/// </summary>
public sealed class TransferPanel : Control
{
    public const float HeaderH = 30, RowH = 44;
    private const int MaxVisibleRows = 4;
    private const float ButtonSize = 24;

    private readonly Dictionary<SftpTransfer, Rate> _rates = [];
    private readonly Dictionary<SftpTransfer, string> _labels = [];
    private IReadOnlyList<SftpTransfer> _transfers = [];
    private float _scroll;
    private int _hoverButton = -2; // row whose button is hovered; -1 the "Clear" button

    public TransferPanel()
    {
        Style = new Blossom.Core.Visual.ElementStyle { BackColor = Theme.SurfaceRaised };
        Events.OnMouseDown += OnDown;
        Events.OnMouseMove += (_, e) => SetAndPaint(ref _hoverButton, ButtonAt(e.Relative.X, e.Relative.Y));
        Events.OnScroll += (_, e) =>
        {
            e.Handled = true;
            SetAndPaint(ref _scroll, Math.Clamp(_scroll - e.Offset.Y * RowH, 0, MaxScroll));
        };
    }

    /// <summary>The cancel button of a transfer that has not finished.</summary>
    public event Action<SftpTransfer>? CancelClicked;

    /// <summary>The folder button of a finished download.</summary>
    public event Action<SftpTransfer>? ShowClicked;

    /// <summary>"Clear finished" in the header.</summary>
    public event Action? ClearClicked;

    /// <summary>The height the panel wants for its rows (0 when there are no transfers).</summary>
    public float PreferredHeight => _transfers.Count == 0 ? 0 : HeaderH + Math.Min(MaxVisibleRows, _transfers.Count) * RowH + 4;

    /// <summary>A transfer is queued or running.</summary>
    public bool IsBusy => _transfers.Any(t => !t.Snapshot().IsFinished);

    private float MaxScroll => Math.Max(0, _transfers.Count * RowH + 4 - (H - HeaderH));

    public void SetTransfers(IReadOnlyList<SftpTransfer> transfers)
    {
        _transfers = transfers.Reverse().ToList();
        foreach (SftpTransfer gone in _rates.Keys.Except(_transfers).ToList())
            _rates.Remove(gone);
        foreach (SftpTransfer gone in _labels.Keys.Except(_transfers).ToList())
            _labels.Remove(gone);
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        InvalidatePaint();
    }

    /// <summary>Shows <paramref name="where"/> instead of the destination (e.g. for the private copy of a file being edited).</summary>
    public void Describe(SftpTransfer transfer, string where) => _labels[transfer] = where;

    /// <summary>Samples the running transfers' speeds and repaints.</summary>
    public void Refresh()
    {
        long now = UiClock.NowMs;
        foreach (SftpTransfer transfer in _transfers)
        {
            TransferProgress p = transfer.Snapshot();
            if (p.State != TransferState.Running)
                continue;
            if (!_rates.TryGetValue(transfer, out Rate? rate))
                _rates[transfer] = rate = new Rate(p.DoneBytes, now);
            rate.Sample(p.DoneBytes, now);
        }
        InvalidatePaint();
    }

    /// <summary>One line for the status bar about the transfers that are not finished, or null when there are none.</summary>
    public string? Summary()
    {
        var active = _transfers.Select(t => t.Snapshot()).Where(p => !p.IsFinished).ToList();
        if (active.Count == 0)
            return null;
        long done = active.Sum(p => p.DoneBytes), total = active.Sum(p => p.TotalBytes);
        string percent = total > 0 ? $" · {Math.Clamp(done * 100 / total, 0, 100)}%" : "";
        return $"{FileFormat.Count(active.Count, "transfer")}{percent}";
    }

    private void OnDown(object? sender, MouseEventArgs e)
    {
        e.Handled = true;
        if (e.Button != 0)
            return;
        int button = ButtonAt(e.Relative.X, e.Relative.Y);
        if (button == -1)
        {
            ClearClicked?.Invoke();
            return;
        }
        if (button < 0)
            return;
        SftpTransfer transfer = _transfers[button];
        TransferProgress p = transfer.Snapshot();
        if (!p.IsFinished)
            CancelClicked?.Invoke(transfer);
        else if (transfer.Direction == TransferDirection.Download && p.State == TransferState.Done)
            ShowClicked?.Invoke(transfer);
    }

    // -1: the header's Clear button; a row index: that row's button; -2: nothing.
    private int ButtonAt(float x, float y)
    {
        if (y < HeaderH)
            return x >= W - ClearWidth() - 16 && _transfers.Any(t => t.Snapshot().IsFinished) ? -1 : -2;
        int index = (int)((y - HeaderH + _scroll) / RowH);
        if (index < 0 || index >= _transfers.Count || x < W - 12 - ButtonSize - 4)
            return -2;
        TransferProgress p = _transfers[index].Snapshot();
        bool hasButton = !p.IsFinished
            || (_transfers[index].Direction == TransferDirection.Download && p.State == TransferState.Done && !_labels.ContainsKey(_transfers[index]));
        return hasButton ? index : -2;
    }

    private static float ClearWidth() => Gfx.Measure("Clear finished", Theme.FontSm);

    protected override void Paint(SKCanvas c)
    {
        Gfx.Line(c, 0, 0.5f, W, 0.5f, Theme.Border);
        Gfx.Text(c, "Transfers", 14, HeaderH / 2f, Theme.FontXs, Theme.WeightSemibold, Theme.TextSecondary);
        if (_transfers.Any(t => t.Snapshot().IsFinished))
            Gfx.Text(c, "Clear finished", W - 14, HeaderH / 2f, Theme.FontSm, Theme.WeightRegular,
                _hoverButton == -1 ? Theme.TextPrimary : Theme.Accent, TextAlignment.Right);

        c.Save();
        c.ClipRect(new SKRect(0, HeaderH, W, H));
        long now = UiClock.NowMs;
        for (int i = 0; i < _transfers.Count; i++)
        {
            float y = HeaderH + i * RowH - _scroll;
            if (y + RowH < HeaderH || y > H)
                continue;
            PaintRow(c, _transfers[i], i, y, now);
        }
        c.Restore();
    }

    private void PaintRow(SKCanvas c, SftpTransfer transfer, int index, float y, long now)
    {
        TransferProgress p = transfer.Snapshot();
        bool upload = transfer.Direction == TransferDirection.Upload;
        float buttonX = W - 12 - ButtonSize;
        float textRight = buttonX - 12;
        float top = y + 14, bottom = y + 32;

        SKColor iconColor = p.State switch
        {
            TransferState.Failed => Theme.Danger,
            TransferState.Done => Theme.Success,
            TransferState.Cancelled => Theme.TextMuted,
            _ => Theme.Accent,
        };
        Icons.Draw(c, upload ? "upload" : "download", 22, y + RowH / 2f, 16, iconColor);

        string where = _labels.TryGetValue(transfer, out string? label) ? label : $"to {FileFormat.Printable(transfer.Destination)}";
        float titleW = Gfx.Measure(FileFormat.Printable(transfer.Title), Theme.FontBase, Theme.WeightSemibold);
        float titleMax = Math.Max(60, (textRight - 40) * 0.55f);
        Gfx.Text(c, FileFormat.Printable(transfer.Title), 40, top, Theme.FontBase, Theme.WeightSemibold, Theme.TextPrimary, TextAlignment.Left, titleMax);
        float afterTitle = 40 + Math.Min(titleW, titleMax) + 8;
        Gfx.Text(c, where, afterTitle, top, Theme.FontSm, Theme.WeightRegular, Theme.TextMuted, TextAlignment.Left, textRight - afterTitle);

        string detail = Detail(transfer, p, now);
        SKColor detailColor = p.State == TransferState.Failed ? Theme.Danger : Theme.TextSecondary;
        float barLeft = Math.Max(40 + 180, textRight - 220);
        bool showBar = p.State is TransferState.Running or TransferState.Preparing;
        Gfx.Text(c, detail, 40, bottom, Theme.FontSm, Theme.WeightRegular, detailColor, TextAlignment.Left, (showBar ? barLeft - 12 : textRight) - 40);
        if (showBar)
        {
            var track = new SKRect(barLeft, bottom - 3, textRight, bottom + 3);
            Gfx.FillRound(c, track, 3, Theme.Border);
            if (p.State == TransferState.Running)
            {
                float fill = (float)(track.Width * p.Fraction);
                if (fill > 0)
                    Gfx.FillRound(c, new SKRect(track.Left, track.Top, track.Left + Math.Max(6, fill), track.Bottom), 3, Theme.Accent);
            }
            else
            {
                // Preparing: a short segment sweeping across.
                float phase = now % 1200 / 1200f;
                float seg = track.Width * 0.25f;
                float left = track.Left + (track.Width + seg) * phase - seg;
                c.Save();
                c.ClipRect(track);
                Gfx.FillRound(c, new SKRect(left, track.Top, left + seg, track.Bottom), 3, Theme.Accent);
                c.Restore();
            }
        }

        bool canShow = !upload && p.State == TransferState.Done && !_labels.ContainsKey(transfer);
        if (!p.IsFinished || canShow)
        {
            var b = new SKRect(buttonX, y + (RowH - ButtonSize) / 2f, buttonX + ButtonSize, y + (RowH + ButtonSize) / 2f);
            if (_hoverButton == index)
                Gfx.FillRound(c, b, Theme.RadiusSm, Theme.SurfaceHover);
            Icons.Draw(c, canShow ? "folder" : "x", b.MidX, b.MidY, 14, _hoverButton == index ? Theme.TextPrimary : Theme.TextSecondary);
        }
        if (index < _transfers.Count - 1)
            Gfx.Line(c, 12, y + RowH - 0.5f, W - 12, y + RowH - 0.5f, Theme.Border.WithAlpha(110));
    }

    private string Detail(SftpTransfer transfer, TransferProgress p, long now)
    {
        string skipped = transfer.Skipped > 0 ? $" · {transfer.Skipped} skipped" : "";
        switch (p.State)
        {
            case TransferState.Queued:
                return "Waiting…";
            case TransferState.Preparing:
                return "Preparing…";
            case TransferState.Done when p.TotalFiles == 0 && transfer.Skipped > 0:
                return $"Nothing copied · {transfer.Skipped} skipped";
            case TransferState.Done:
                return $"Done · {FileFormat.Count(p.TotalFiles, "file")}, {FileFormat.Size(p.TotalBytes)}{skipped}";
            case TransferState.Cancelled:
                return $"Cancelled{(p.DoneFiles > 0 ? $" after {FileFormat.Count(p.DoneFiles, "file")}" : "")}";
            case TransferState.Failed:
                return $"Failed: {p.Error}";
        }
        string text = $"{FileFormat.Size(p.DoneBytes)} of {FileFormat.Size(p.TotalBytes)}";
        if (p.TotalFiles > 1)
            text += $" · file {Math.Min(p.DoneFiles + 1, p.TotalFiles)} of {p.TotalFiles}";
        if (_rates.TryGetValue(transfer, out Rate? rate) && rate.BytesPerSecond > 0 && now - rate.Started > 1000)
        {
            text += $" · {FileFormat.Speed(rate.BytesPerSecond)}";
            long left = p.TotalBytes - p.DoneBytes;
            if (left > 0)
                text += $" · {FileFormat.Duration(TimeSpan.FromSeconds(left / rate.BytesPerSecond))} left";
        }
        return text + skipped;
    }

    /// <summary>A transfer's speed, smoothed over the last seconds.</summary>
    private sealed class Rate(long bytes, long now)
    {
        private long _bytes = bytes, _at = now;

        public long Started { get; } = now;
        public double BytesPerSecond { get; private set; }

        public void Sample(long bytes, long now)
        {
            long elapsed = now - _at;
            if (elapsed < 250)
                return;
            double current = Math.Max(0, bytes - _bytes) * 1000.0 / elapsed;
            BytesPerSecond = BytesPerSecond <= 0 ? current : BytesPerSecond * 0.7 + current * 0.3;
            _bytes = bytes;
            _at = now;
        }
    }
}
