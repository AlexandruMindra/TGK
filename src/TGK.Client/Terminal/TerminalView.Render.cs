using System;
using System.Collections.Generic;
using Blossom.Core;
using SkiaSharp;
using TGK.Client.Controls;
using TGK.Core.Models;
using TGK.Terminal;

namespace TGK.Client.Terminal;

// Rendering: one cached row per screen row (background runs, text blobs, decorations and procedurally drawn box
// characters), rebuilt only when the emulator marks the row dirty or the viewport moves. Paint replays the caches.
public sealed partial class TerminalView
{
    private const int BlinkMs = 530;

    private readonly SKPaint _fill = new() { IsAntialias = false };
    private readonly SKPaint _text = new() { IsAntialias = true };
    private readonly SKPaint _outline = new() { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private readonly SKPaint _thumb = new() { IsAntialias = true };
    private readonly SKTextBlobBuilder _blobBuilder = new();
    private readonly List<ushort> _runGlyphs = [];
    private readonly List<float> _runXs = [];
    private RowCache[] _rows = [];
    private bool _allRowsDirty = true;
    private long _lastScrollbackAdded;
    private long _blinkEpoch;
    private bool _blinkOn = true;

    /// <summary>Lines scrolled back into history (0 = following the live screen).</summary>
    private int _offset;

    protected override void OnAfterStyleDraw(List<DrawCommand> cmds) => cmds.Add(new DrawCallbackCommand(Paint));

    private void InvalidateAllRows() => _allRowsDirty = true;

    private void ResizeRows(int rows)
    {
        DisposeRows();
        _rows = new RowCache[rows];
        for (int r = 0; r < rows; r++)
            _rows[r] = new RowCache();
        _allRowsDirty = true;
    }

    private void DisposeRows()
    {
        foreach (RowCache row in _rows)
            row.Clear();
    }

    private void DisposeRendering()
    {
        DisposeRows();
        _blobBuilder.Dispose();
        foreach (SKPaint paint in new[] { _fill, _text, _outline, _thumb })
            paint.Dispose();
    }

    private void Paint(SKCanvas c)
    {
        _paintedSinceTick = true;
        UpdateRows();
        float cw = _font.CellWidth, ch = _font.CellHeight;
        int save = c.Save();
        c.Translate(PadX, PadY);
        SKRect clip = c.LocalClipBounds;
        int first = Math.Max(0, (int)MathF.Floor(clip.Top / ch));
        int last = Math.Min(Rows - 1, (int)MathF.Ceiling(clip.Bottom / ch));
        float light = Math.Max(1, MathF.Round(_font.Size / 16f));
        (long selStart, int selStartCol, long selEnd, int selEndCol) = OrderedSelection();
        long topAbs = Emulator.ScrollbackAdded - _offset;

        for (int r = first; r <= last; r++)
        {
            RowCache row = _rows[r];
            float y = r * ch;
            foreach (BgRun bg in row.Backgrounds)
            {
                _fill.Color = bg.Color;
                c.DrawRect(new SKRect(bg.X0, y, bg.X1, y + ch), _fill);
            }
            if (HasSelection)
            {
                long abs = topAbs + r;
                if (abs >= selStart && abs <= selEnd)
                {
                    int c0 = abs == selStart ? selStartCol : 0, c1 = abs == selEnd ? selEndCol : Cols;
                    _fill.Color = _palette.Selection;
                    if (c1 > c0)
                        c.DrawRect(new SKRect(c0 * cw, y, c1 * cw, y + ch), _fill);
                }
            }
            foreach (TextRun run in row.Runs)
            {
                _text.Color = run.Color;
                c.DrawText(run.Blob, 0, y + _font.Baseline, _text);
            }
            foreach (BoxCell box in row.Boxes)
                BoxDrawing.Draw(c, box.Rune, box.X, y, box.Width * cw, ch, box.Color, light);
            foreach (Decoration d in row.Decorations)
            {
                _fill.Color = d.Color;
                c.DrawRect(new SKRect(d.X0, y + d.Y, d.X1, y + d.Y + d.Thickness), _fill);
            }
        }

        PaintCursor(c, light);
        PaintScrollbar(c);
        c.RestoreToCount(save);
        if (_bellUntil > 0)
        {
            _fill.Color = SKColors.White.WithAlpha(22);
            c.DrawRect(new SKRect(0, 0, Transform.Computed.Width, Transform.Computed.Height), _fill);
        }
    }

    // Brings every row cache up to date with the emulator and the viewport, then clears the emulator's dirty flags.
    private void UpdateRows()
    {
        if (Emulator.ScrollbackAdded != _lastScrollbackAdded)
        {
            // History moved under the viewport: rows showing scrollback now show different lines.
            _lastScrollbackAdded = Emulator.ScrollbackAdded;
            if (_offset > 0)
                _allRowsDirty = true;
        }
        _offset = Math.Min(_offset, Emulator.ScrollbackCount);
        for (int r = 0; r < _rows.Length; r++)
        {
            int line = r - _offset;
            RowCache row = _rows[r];
            if (_allRowsDirty || row.Line != line || (line >= 0 && Emulator.IsRowDirty(line)))
                BuildRow(row, line);
        }
        _allRowsDirty = false;
        Emulator.ClearDirty();
    }

    private void BuildRow(RowCache row, int line)
    {
        row.Clear();
        row.Line = line;
        ReadOnlySpan<Cell> cells = Emulator.GetLine(line);
        float cw = _font.CellWidth;
        SKColor runColor = default;
        int runFont = -1;

        for (int col = 0; col < cells.Length; col++)
        {
            Cell cell = cells[col];
            if (cell.IsContinuation)
                continue;
            int width = Math.Max(1, (int)cell.Width);
            float x = col * cw, x1 = Math.Min(cells.Length, col + width) * cw;
            CellStyle style = cell.Style;
            (SKColor fg, SKColor bg) = _palette.Resolve(style);

            if (bg != _palette.Background)
            {
                if (row.Backgrounds.Count > 0 && row.Backgrounds[^1] is { } prev && prev.Color == bg && Math.Abs(prev.X1 - x) < 0.01f)
                    row.Backgrounds[^1] = prev with { X1 = x1 };
                else
                    row.Backgrounds.Add(new BgRun(x, x1, bg));
            }

            int rune = cell.Rune;
            bool visible = rune > ' ' && !style.Has(CellFlags.Hidden);
            if (visible && BoxDrawing.Handles(rune))
            {
                row.Boxes.Add(new BoxCell(x, rune, width, fg));
            }
            else if (visible)
            {
                GlyphRef glyph = _font.Lookup(rune, style.Has(CellFlags.Bold), style.Has(CellFlags.Italic), width);
                if (glyph.FontIndex != runFont || fg != runColor)
                {
                    FlushRun(row, runFont, runColor);
                    runFont = glyph.FontIndex;
                    runColor = fg;
                }
                _runGlyphs.Add(glyph.Glyph);
                _runXs.Add(x + glyph.OffsetX);
            }

            if ((style.Flags & (CellFlags.AnyUnderline | CellFlags.Strikethrough | CellFlags.Overline)) != 0 && !style.Has(CellFlags.Hidden))
                AddDecorations(row, style.Flags, x, x1, fg);
        }
        FlushRun(row, runFont, runColor);
    }

    private void FlushRun(RowCache row, int fontIndex, SKColor color)
    {
        if (_runGlyphs.Count == 0)
            return;
        SKHorizontalRunBuffer run = _blobBuilder.AllocateHorizontalRun(_font.Fonts[fontIndex], _runGlyphs.Count, 0);
        Span<ushort> glyphs = run.GetGlyphSpan();
        Span<float> xs = run.GetPositionSpan();
        for (int i = 0; i < _runGlyphs.Count; i++)
        {
            glyphs[i] = _runGlyphs[i];
            xs[i] = _runXs[i];
        }
        _runGlyphs.Clear();
        _runXs.Clear();
        if (_blobBuilder.Build() is { } blob)
            row.Runs.Add(new TextRun(blob, color));
    }

    private void AddDecorations(RowCache row, CellFlags flags, float x0, float x1, SKColor color)
    {
        float t = _font.LineThickness;
        if ((flags & CellFlags.AnyUnderline) != 0)
        {
            row.Decorations.Add(new Decoration(x0, x1, _font.UnderlineY, t, color));
            if ((flags & CellFlags.DoubleUnderline) != 0)
                row.Decorations.Add(new Decoration(x0, x1, _font.UnderlineY + 2 * t, t, color));
        }
        if ((flags & CellFlags.Strikethrough) != 0)
            row.Decorations.Add(new Decoration(x0, x1, _font.StrikeY, t, color));
        if ((flags & CellFlags.Overline) != 0)
            row.Decorations.Add(new Decoration(x0, x1, 0, t, color));
    }

    private void PaintCursor(SKCanvas c, float light)
    {
        int row = Emulator.CursorRow + _offset;
        if (!Emulator.CursorVisible || row >= Rows)
            return;
        bool focused = HasFocus;
        if (focused && CursorBlinks && !_blinkOn)
            return;

        int col = Math.Min(Emulator.CursorCol, Cols - 1);
        Cell cell = Emulator.GetLine(Emulator.CursorRow)[col];
        float cw = _font.CellWidth, ch = _font.CellHeight;
        float x = col * cw, y = row * _font.CellHeight, w = Math.Max(1, (int)cell.Width) * cw;
        if (!focused)
        {
            _outline.Color = _palette.Cursor;
            c.DrawRect(new SKRect(x + 0.5f, y + 0.5f, x + w - 0.5f, y + ch - 0.5f), _outline);
            return;
        }

        _fill.Color = _palette.Cursor;
        switch (CurrentCursorShape)
        {
            case CursorShape.Bar:
                c.DrawRect(new SKRect(x, y, x + Math.Max(2, light * 2), y + ch), _fill);
                return;
            case CursorShape.Underline:
                c.DrawRect(new SKRect(x, y + ch - Math.Max(2, light * 2), x + w, y + ch), _fill);
                return;
        }

        // Block: fill the cell and redraw its character in the background color.
        c.DrawRect(new SKRect(x, y, x + w, y + ch), _fill);
        int rune = cell.Rune;
        if (rune <= ' ' || cell.Style.Has(CellFlags.Hidden))
            return;
        SKColor ink = _palette.Resolve(cell.Style).Bg;
        if (BoxDrawing.Handles(rune))
        {
            BoxDrawing.Draw(c, rune, x, y, w, ch, ink, light);
            return;
        }
        GlyphRef glyph = _font.Lookup(rune, cell.Style.Has(CellFlags.Bold), cell.Style.Has(CellFlags.Italic), (int)cell.Width);
        SKHorizontalRunBuffer run = _blobBuilder.AllocateHorizontalRun(_font.Fonts[glyph.FontIndex], 1, 0);
        run.GetGlyphSpan()[0] = glyph.Glyph;
        run.GetPositionSpan()[0] = x + glyph.OffsetX;
        using SKTextBlob? blob = _blobBuilder.Build();
        if (blob is not null)
        {
            _text.Color = ink;
            c.DrawText(blob, 0, y + _font.Baseline, _text);
        }
    }

    // A thin thumb on the right edge while scrolled back into history.
    private void PaintScrollbar(SKCanvas c)
    {
        int history = Emulator.ScrollbackCount;
        if (_offset <= 0 || history <= 0)
            return;
        float trackH = Rows * _font.CellHeight;
        float thumbH = Math.Max(24, trackH * Rows / (history + Rows));
        float top = (trackH - thumbH) * (history - _offset) / history;
        float x = Cols * _font.CellWidth + PadX - 6;
        _thumb.Color = _palette.Foreground.WithAlpha(90);
        c.DrawRoundRect(new SKRect(x, top, x + 4, top + thumbH), 2, 2, _thumb);
    }

    // The application's DECSCUSR choice wins over the user's default once it differs from the reset state.
    private bool AppSetCursor => Emulator.CursorShape != CursorShape.Block || !Emulator.CursorBlink;

    private CursorShape CurrentCursorShape => AppSetCursor ? Emulator.CursorShape : _settings.CursorShape switch
    {
        TerminalSettings.CursorBar => CursorShape.Bar,
        TerminalSettings.CursorUnderline => CursorShape.Underline,
        _ => CursorShape.Block,
    };

    private bool CursorBlinks => AppSetCursor ? Emulator.CursorBlink : _settings.CursorBlink;

    private void ResetBlink()
    {
        _blinkEpoch = UiClock.NowMs;
        if (!_blinkOn)
        {
            _blinkOn = true;
            InvalidatePaint();
        }
    }

    // Repaints only when the blink phase flips, and only for a focused, visible, blinking cursor.
    private void TickBlink(long now)
    {
        bool on = !CursorBlinks || !HasFocus || (now - _blinkEpoch) / BlinkMs % 2 == 0;
        if (on == _blinkOn)
            return;
        _blinkOn = on;
        if (EffectiveVisible)
            InvalidatePaint();
    }

    private readonly record struct BgRun(float X0, float X1, SKColor Color);

    private readonly record struct TextRun(SKTextBlob Blob, SKColor Color);

    private readonly record struct BoxCell(float X, int Rune, int Width, SKColor Color);

    private readonly record struct Decoration(float X0, float X1, float Y, float Thickness, SKColor Color);

    private sealed class RowCache
    {
        public int Line = int.MinValue;
        public readonly List<BgRun> Backgrounds = [];
        public readonly List<TextRun> Runs = [];
        public readonly List<BoxCell> Boxes = [];
        public readonly List<Decoration> Decorations = [];

        public void Clear()
        {
            foreach (TextRun run in Runs)
                run.Blob.Dispose();
            Runs.Clear();
            Backgrounds.Clear();
            Boxes.Clear();
            Decorations.Clear();
            Line = int.MinValue;
        }
    }
}
