using System;

namespace TGK.Terminal;

public sealed partial class TerminalEmulator
{
    /// <summary>
    /// Changes the screen size without reflowing text, like xterm: lines are truncated or padded; when rows
    /// shrink, top lines move into the scrollback as needed to keep the cursor line visible, and when rows
    /// grow, lines come back from the scrollback. The alternate screen never uses the scrollback. The
    /// scroll region is reset and sizes below 1 are clamped to 1.
    /// </summary>
    public void Resize(int cols, int rows)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        if (cols == _cols && rows == _rows)
            return;

        foreach (var line in _main.Lines)
            line.SetWidth(cols);
        foreach (var line in _alt.Lines)
            line.SetWidth(cols);
        _cols = cols;

        // Each screen is anchored at its cursor: the live one, or the one saved when switching screens.
        bool altActive = _buf == _alt;
        int mainShift = ResizeRows(_main, rows, altActive ? _main.Saved.Y : _y, useScrollback: true);
        int altShift = ResizeRows(_alt, rows, altActive ? _y : _alt.Saved.Y, useScrollback: false);
        _y -= altActive ? altShift : mainShift;
        _main.Saved.Y -= mainShift;
        _alt.Saved.Y -= altShift;
        _rows = rows;

        _x = Math.Min(_x, cols - 1);
        _y = Math.Clamp(_y, 0, rows - 1);
        _pendingWrap = false;
        _top = 0;
        _bottom = rows - 1;
        _tabStops = CreateTabStops(cols, _tabStops);
        _dirty = new bool[rows];
        _scratch = new Cell[cols];
        MarkAllDirty();
        Version++;
    }

    /// <summary>
    /// Sets the row count of <paramref name="buffer"/> (whose lines already have the new width) and returns
    /// how many lines the content moved up (negative when lines were pulled back from the scrollback).
    /// </summary>
    private int ResizeRows(ScreenBuffer buffer, int rows, int anchorY, bool useScrollback)
    {
        var old = buffer.Lines;
        if (rows == old.Length)
            return 0;

        var lines = new TerminalLine[rows];
        if (rows < old.Length)
        {
            // Drop lines above the cursor only as far as needed to keep it on screen; the rest go from the bottom.
            int shift = Math.Clamp(anchorY + 1 - rows, 0, old.Length - rows);
            for (int i = 0; i < shift && useScrollback && _scrollback.Limit > 0; i++)
                PushScrollback(old[i]);

            Array.Copy(old, shift, lines, 0, rows);
            buffer.Lines = lines;
            return shift;
        }

        int pulled = useScrollback ? Math.Min(rows - old.Length, _scrollback.Count) : 0;
        for (int i = pulled - 1; i >= 0; i--)
        {
            var line = _scrollback.PopNewest();
            line.SetWidth(_cols);
            lines[i] = line;
        }

        Array.Copy(old, 0, lines, pulled, old.Length);
        for (int i = pulled + old.Length; i < rows; i++)
            lines[i] = new TerminalLine(_cols);
        buffer.Lines = lines;
        return -pulled;
    }
}
