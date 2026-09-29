using System;
using System.Linq;
using Blossom;
using Silk.NET.Input;
using SkiaSharp;

namespace TGK.Client.Controls;

/// <summary>The authenticator secret in groups of four; a click copies it (ungrouped) to the clipboard.</summary>
public sealed class SetupKeyBox : Control
{
    private string _secret = "";

    public SetupKeyBox()
    {
        Cursor = StandardCursor.Hand;
        Events.OnClick += (_, e) =>
        {
            e.Handled = true;
            if (_secret.Length == 0)
                return;
            Browser.SetClipboardText(_secret);
            Copied?.Invoke();
        };
    }

    public event Action? Copied;

    public string Secret { get => _secret; set => SetAndPaint(ref _secret, value ?? ""); }

    protected override void Paint(SKCanvas c)
    {
        var r = new SKRect(0, 0, W, H);
        Gfx.FillRound(c, r, Theme.Radius, Theme.Input);
        Gfx.StrokeRound(c, r, Theme.Radius, IsHovered ? Theme.BorderStrong : Theme.BorderInput);
        string grouped = string.Join(' ', _secret.Chunk(4).Select(chunk => new string(chunk)));
        Gfx.Text(c, grouped, 10, H / 2f, Gfx.Font(Theme.FontSm, Theme.Mono), Enabled ? Theme.TextPrimary : Theme.TextMuted, TextAlignment.Left, W - 46);
        var button = SKRect.Create(W - 34, 4, 30, H - 8);
        if (IsHovered)
            Gfx.FillRound(c, button, Theme.RadiusSm, Theme.SurfaceHover);
        Icons.Draw(c, "copy", button.MidX, button.MidY, 16, IsHovered ? Theme.TextPrimary : Theme.TextSecondary);
    }
}
