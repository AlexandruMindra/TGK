using System;
using QRCoder;
using SkiaSharp;

namespace TGK.Client.Controls;

/// <summary>
/// A QR code drawn as whole-pixel module rectangles on a white card (QRCoder only supplies the module matrix,
/// which already includes the 4-module quiet zone the spec requires).
/// </summary>
public sealed class QrCodeView : Control
{
    private string _content = "";
    private bool[,] _modules = new bool[0, 0];

    public string Content
    {
        get => _content;
        set
        {
            if (!SetAndPaint(ref _content, value ?? "") || _content.Length == 0)
                return;
            using QRCodeData data = QRCodeGenerator.GenerateQrCode(_content, QRCodeGenerator.ECCLevel.M);
            int n = data.ModuleMatrix.Count;
            _modules = new bool[n, n];
            for (int row = 0; row < n; row++)
            {
                for (int col = 0; col < n; col++)
                    _modules[row, col] = data.ModuleMatrix[row][col];
            }
        }
    }

    /// <summary>Modules per side, quiet zone included.</summary>
    public int ModuleCount => _modules.GetLength(0);

    protected override void Paint(SKCanvas c)
    {
        Gfx.FillRound(c, new SKRect(0, 0, W, H), Theme.Radius, SKColors.White);
        int n = ModuleCount;
        if (n == 0)
            return;
        // Whole-pixel modules keep every edge sharp; the leftover margin only widens the quiet zone.
        float module = Math.Max(1, MathF.Floor(Math.Min(W, H) / n));
        float x0 = MathF.Round((W - module * n) / 2f), y0 = MathF.Round((H - module * n) / 2f);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = false };
        for (int row = 0; row < n; row++)
        {
            for (int col = 0; col < n; col++)
            {
                if (_modules[row, col])
                    c.DrawRect(x0 + col * module, y0 + row * module, module, module, paint);
            }
        }
    }
}
