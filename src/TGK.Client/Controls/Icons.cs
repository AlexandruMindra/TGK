using System;
using System.Collections.Generic;
using System.IO;
using Blossom;
using SkiaSharp;

namespace TGK.Client.Controls;

/// <summary>
/// Loads the stroke icons in <c>assets/icons/*.svg</c> (24×24, white strokes) once and draws them tinted.
/// </summary>
public static class Icons
{
    private static readonly Dictionary<string, SKPicture?> Pictures = new(StringComparer.Ordinal);
    private static readonly Dictionary<SKColor, SKPaint> TintPaints = new();
    private const float ViewBox = 24f;

    /// <summary>Draws icon <paramref name="name"/> centered in a <paramref name="size"/>-px square at (<paramref name="cx"/>, <paramref name="cy"/>).</summary>
    public static void Draw(SKCanvas c, string name, float cx, float cy, float size, SKColor color)
    {
        SKPicture? picture = Get(name);
        if (picture is null)
            return;
        float scale = size / ViewBox;
        var matrix = SKMatrix.CreateScale(scale, scale);
        matrix.TransX = MathF.Round(cx - size / 2f);
        matrix.TransY = MathF.Round(cy - size / 2f);
        c.DrawPicture(picture, ref matrix, Tint(color));
    }

    private static SKPicture? Get(string name)
    {
        if (Pictures.TryGetValue(name, out SKPicture? cached))
            return cached;

        SKPicture? picture = null;
        string? path = Find(name);
        if (path is not null)
        {
            try
            {
                var svg = new SkiaSharp.Extended.Svg.SKSvg();
                picture = svg.Load(path);
            }
            catch (Exception ex)
            {
                Log.Warning($"Icon '{name}' could not be loaded: {ex.Message}");
            }
        }
        else
        {
            Log.Warning($"Icon '{name}' not found in assets/icons.");
        }
        Pictures[name] = picture;
        return picture;
    }

    private static string? Find(string name)
    {
        foreach (string dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            string path = Path.Combine(dir, "assets", "icons", name + ".svg");
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    // The icons are white; SrcIn keeps their coverage and replaces the color.
    private static SKPaint Tint(SKColor color)
    {
        if (!TintPaints.TryGetValue(color, out SKPaint? paint))
        {
            paint = new SKPaint { IsAntialias = true, ColorFilter = SKColorFilter.CreateBlendMode(color, SKBlendMode.SrcIn) };
            TintPaints[color] = paint;
        }
        return paint;
    }
}
