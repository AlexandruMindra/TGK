using SkiaSharp;
using TGK.Client.Controls;

namespace TGK.Client.Main;

/// <summary>Bottom bar: active session on the left; terminal size and sync status on the right.</summary>
public sealed class StatusBar : Control
{
    private string _left = "";
    private TabStatus _status;
    private string? _size;
    private string _sync = "";

    public void Set(string left, TabStatus status, string? size, string sync)
    {
        if (_left == left && _status == status && _size == size && _sync == sync)
            return;
        _left = left;
        _status = status;
        _size = size;
        _sync = sync;
        InvalidatePaint();
    }

    protected override void Paint(SKCanvas c)
    {
        Gfx.FillRect(c, new SKRect(0, 0, W, H), Theme.Chrome);
        Gfx.Line(c, 0, 0.5f, W, 0.5f, Theme.Border);
        float cy = H / 2f;

        float right = W - 12;
        float syncW = Gfx.Measure(_sync, Theme.FontXs);
        Gfx.Text(c, _sync, right, cy, Theme.FontXs, Theme.WeightRegular, Theme.TextMuted, TextAlignment.Right);
        right -= syncW + 18;
        if (_size is not null)
        {
            Gfx.Text(c, _size, right, cy, Theme.FontXs, Theme.WeightRegular, Theme.TextMuted, TextAlignment.Right);
            right -= Gfx.Measure(_size, Theme.FontXs) + 18;
        }

        float x = 12;
        if (_status != TabStatus.None)
        {
            Gfx.Circle(c, x + 3, cy, 3, TabStrip.StatusColor(_status));
            x += 12;
        }
        Gfx.Text(c, _left, x, cy, Theme.FontXs, Theme.WeightRegular, Theme.TextSecondary, TextAlignment.Left, right - x - 12);
    }
}
