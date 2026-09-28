using System;

namespace TGK.Terminal;

// Screen operations: cursor movement, scrolling, erasing, insertion/deletion, tabs, cursor save/restore,
// screen switching and resets.
public sealed partial class TerminalEmulator
{
    // Erased cells take the current background color (BCE).
    private Cell Blank => Cell.Blank(_style.Bg);

    private void CursorUp(int n)
    {
        int min = _y >= _top ? _top : 0;
        _y = Math.Max(min, _y - n);
        _pendingWrap = false;
    }

    private void CursorDown(int n)
    {
        int max = _y <= _bottom ? _bottom : _rows - 1;
        _y = Math.Min(max, _y + n);
        _pendingWrap = false;
    }

    private void CursorForward(int n)
    {
        _x = Math.Min(_cols - 1, _x + n);
        _pendingWrap = false;
    }

    private void CursorBackward(int n)
    {
        _x = Math.Max(0, _x - n);
        _pendingWrap = false;
    }

    private void SetCol(int col)
    {
        _x = Math.Clamp(col, 0, _cols - 1);
        _pendingWrap = false;
    }

    // 0-based; relative to (and confined to) the scroll region in origin mode.
    private void SetRow(int row)
    {
        _y = _modes.Origin ? Math.Clamp(_top + row, _top, _bottom) : Math.Clamp(row, 0, _rows - 1);
        _pendingWrap = false;
    }

    // 1-based, as in CUP.
    private void CursorPosition(int row, int col)
    {
        SetRow(row - 1);
        SetCol(col - 1);
    }

    private void Index()
    {
        _pendingWrap = false;
        if (_y == _bottom)
            ScrollUp(_top, _bottom, 1, allowScrollback: true);
        else if (_y < _rows - 1)
            _y++;
    }

    private void ReverseIndex()
    {
        _pendingWrap = false;
        if (_y == _top)
            ScrollDown(_top, _bottom, 1);
        else if (_y > 0)
            _y--;
    }

    private void NextLine()
    {
        Index();
        _x = 0;
    }

    /// <summary>
    /// Scrolls rows <paramref name="top"/>..<paramref name="bottom"/> up by <paramref name="n"/>. Lines leaving
    /// the top of the main screen go to the scrollback when the region starts at the first row, as in xterm: this
    /// keeps the output of programs that reserve the bottom rows for a status line (e.g. apt's progress bar).
    /// </summary>
    private void ScrollUp(int top, int bottom, int n, bool allowScrollback)
    {
        int height = bottom - top + 1;
        n = Math.Min(n, height);
        if (n <= 0)
            return;

        bool save = allowScrollback && _buf == _main && top == 0 && _scrollback.Limit > 0;
        var region = _buf.Lines.AsSpan(top, height);
        var blank = Blank;
        for (int i = 0; i < n; i++)
        {
            if (save)
            {
                // The line itself moves into the scrollback; the line evicted from there (once it is full)
                // is recycled for the screen, so steady-state scrolling allocates nothing.
                region[i] = PushScrollback(region[i]) ?? new TerminalLine(_cols);
            }

            region[i].Clear(blank); // becomes a new bottom line
        }

        RotateLeft(region, n);
        MarkDirty(top, bottom);
    }

    private void ScrollDown(int top, int bottom, int n)
    {
        int height = bottom - top + 1;
        n = Math.Min(n, height);
        if (n <= 0)
            return;

        var region = _buf.Lines.AsSpan(top, height);
        var blank = Blank;
        for (int i = height - n; i < height; i++)
            region[i].Clear(blank);
        RotateLeft(region, height - n);
        MarkDirty(top, bottom);
    }

    /// <summary>Moves <paramref name="line"/> into the scrollback and returns the evicted line, resized to Cols.</summary>
    private TerminalLine? PushScrollback(TerminalLine line)
    {
        ScrollbackAdded++;
        var evicted = _scrollback.Push(line);
        evicted?.SetWidth(_cols);
        return evicted;
    }

    private static void RotateLeft(Span<TerminalLine> lines, int n)
    {
        if (n == 0 || n == lines.Length)
            return;
        if (n == 1)
        {
            // The common single-line scroll: one block move instead of three reversals.
            var first = lines[0];
            lines[1..].CopyTo(lines);
            lines[^1] = first;
            return;
        }

        lines[..n].Reverse();
        lines[n..].Reverse();
        lines.Reverse();
    }

    private void SetScrollRegion(int top, int bottom)
    {
        bottom = Math.Min(bottom, _rows);
        if (top >= bottom)
            return;
        _top = top - 1;
        _bottom = bottom - 1;
        CursorPosition(1, 1);
    }

    /// <summary>Erases columns [<paramref name="from"/>, <paramref name="to"/>) of row <paramref name="y"/>.</summary>
    private void EraseCells(int y, int from, int to)
    {
        from = Math.Max(0, from);
        to = Math.Min(_cols, to);
        if (from >= to)
            return;

        var cells = _buf.Lines[y].Cells;
        SplitWide(cells, from);
        SplitWide(cells, to);
        cells.AsSpan(from, to - from).Fill(Blank);
        _dirty[y] = true;
    }

    private void ClearLine(int y)
    {
        _buf.Lines[y].Clear(Blank);
        _dirty[y] = true;
    }

    /// <summary>
    /// Ensures no wide character straddles the boundary in front of <paramref name="col"/>, blanking both
    /// halves of one that does. Called before an operation splits a row at that column.
    /// </summary>
    private static void SplitWide(Cell[] cells, int col)
    {
        if (col <= 0 || col >= cells.Length || !cells[col].IsContinuation)
            return;
        cells[col - 1] = Cell.Blank(cells[col - 1].Style.Bg);
        cells[col] = Cell.Blank(cells[col].Style.Bg);
    }

    private void EraseInLine(int mode)
    {
        _pendingWrap = false;
        switch (mode)
        {
            case 0:
                EraseCells(_y, _x, _cols);
                _buf.Lines[_y].Wrapped = false;
                break;
            case 1:
                EraseCells(_y, 0, _x + 1);
                break;
            case 2:
                ClearLine(_y);
                break;
        }
    }

    private void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (int y = _y + 1; y < _rows; y++)
                    ClearLine(y);
                break;
            case 1:
                for (int y = 0; y < _y; y++)
                    ClearLine(y);
                EraseInLine(1);
                break;
            case 2:
                _pendingWrap = false;
                for (int y = 0; y < _rows; y++)
                    ClearLine(y);
                break;
            case 3: // xterm: erase saved lines
                _scrollback.Clear();
                MarkAllDirty();
                break;
        }
    }

    private void EraseChars(int n)
    {
        _pendingWrap = false;
        EraseCells(_y, _x, _x + n);
    }

    private void InsertCells(int n)
    {
        _pendingWrap = false;
        var cells = _buf.Lines[_y].Cells;
        n = Math.Min(n, _cols - _x);
        SplitWide(cells, _x);
        SplitWide(cells, _cols - n); // a wide character pushed partly off the edge
        cells.AsSpan(_x, _cols - _x - n).CopyTo(cells.AsSpan(_x + n));
        cells.AsSpan(_x, n).Fill(Blank);
        _dirty[_y] = true;
    }

    private void DeleteCells(int n)
    {
        _pendingWrap = false;
        var cells = _buf.Lines[_y].Cells;
        n = Math.Min(n, _cols - _x);
        SplitWide(cells, _x);
        SplitWide(cells, _x + n);
        cells.AsSpan(_x + n).CopyTo(cells.AsSpan(_x));
        cells.AsSpan(_cols - n).Fill(Blank);
        _dirty[_y] = true;
    }

    private void InsertLines(int n)
    {
        if (_y < _top || _y > _bottom)
            return;
        ScrollDown(_y, _bottom, n);
        _x = 0;
        _pendingWrap = false;
    }

    private void DeleteLines(int n)
    {
        if (_y < _top || _y > _bottom)
            return;
        ScrollUp(_y, _bottom, n, allowScrollback: false);
        _x = 0;
        _pendingWrap = false;
    }

    // REP: repeat the last printed character. Programs use it for runs within one line (ncurses), so the repeat is
    // capped at one line's worth of cells: a few bytes of input must not turn into a screenful of work each.
    private void Repeat(int n)
    {
        if (_lastPrinted < 0)
            return;
        int width = UnicodeWidth.GetWidth(_lastPrinted);
        n = Math.Min(n, Math.Max(1, _cols / Math.Max(1, width)));
        if (width != 1 || _modes.Insert)
        {
            for (int i = 0; i < n; i++)
                PrintRune(_lastPrinted);
            return;
        }

        // Same effect as PrintRune n times, a row segment at a time.
        var cell = new Cell(_lastPrinted, 1, _style);
        while (n > 0)
        {
            if (_pendingWrap)
                WrapLine();
            var cells = _buf.Lines[_y].Cells;
            int count = Math.Min(n, _cols - _x);
            SplitWide(cells, _x);
            SplitWide(cells, _x + count);
            cells.AsSpan(_x, count).Fill(cell);
            _dirty[_y] = true;
            _x += count;
            n -= count;
            if (_x < _cols)
                break;
            _x = _cols - 1;
            if (!_modes.AutoWrap)
                break; // the rest would overwrite the last column with the same character
            _pendingWrap = true;
        }
    }

    private void TabForward(int n)
    {
        for (; n > 0 && _x < _cols - 1; n--)
        {
            do
                _x++;
            while (_x < _cols - 1 && !_tabStops[_x]);
        }
    }

    private void TabBackward(int n)
    {
        for (; n > 0 && _x > 0; n--)
        {
            do
                _x--;
            while (_x > 0 && !_tabStops[_x]);
        }

        _pendingWrap = false;
    }

    private void ClearTabStops(int mode)
    {
        if (mode == 0)
            _tabStops[_x] = false;
        else if (mode == 3)
            Array.Clear(_tabStops);
    }

    private void SaveCursor() => _buf.Saved = new SavedCursor
    {
        X = _x,
        Y = _y,
        PendingWrap = _pendingWrap,
        Origin = _modes.Origin,
        Style = _style,
        G0 = _g0,
        G1 = _g1,
        ShiftOut = _shiftOut,
    };

    private void RestoreCursor()
    {
        ref readonly var s = ref _buf.Saved;
        _x = Math.Clamp(s.X, 0, _cols - 1);
        _y = Math.Clamp(s.Y, 0, _rows - 1);
        _pendingWrap = s.PendingWrap && _x == _cols - 1;
        _modes.Origin = s.Origin;
        _style = s.Style;
        _g0 = s.G0;
        _g1 = s.G1;
        _shiftOut = s.ShiftOut;
    }

    private void SwitchScreen(bool alternate)
    {
        var target = alternate ? _alt : _main;
        if (target == _buf)
            return;
        _buf = target;
        _modes.AlternateScreen = alternate;
        _pendingWrap = false;
        MarkAllDirty();
    }

    private void ClearBuffer(ScreenBuffer buffer)
    {
        var blank = Blank;
        foreach (var line in buffer.Lines)
            line.Clear(blank);
        if (buffer == _buf)
            MarkAllDirty();
    }

    // DECALN: fill the screen with 'E'.
    private void ScreenAlignmentTest()
    {
        var e = new Cell('E', 1, default);
        foreach (var line in _buf.Lines)
        {
            line.Cells.AsSpan().Fill(e);
            line.Wrapped = false;
        }

        _top = 0;
        _bottom = _rows - 1;
        _modes.Origin = false;
        CursorPosition(1, 1);
        MarkAllDirty();
    }

    // DECSTR
    private void SoftReset()
    {
        CursorVisible = true;
        _modes.Insert = false;
        _modes.Origin = false;
        _modes.AutoWrap = true;
        _modes.ApplicationCursorKeys = false;
        _modes.ApplicationKeypad = false;
        _top = 0;
        _bottom = _rows - 1;
        _style = default;
        _g0 = _g1 = Charset.Ascii;
        _shiftOut = false;
        _pendingWrap = false;
        _buf.Saved = default;
    }

    // RIS
    private void FullReset()
    {
        SoftReset();
        _modes = TerminalModes.Initial;
        _buf = _main;
        ClearBuffer(_main);
        ClearBuffer(_alt);
        _main.Saved = default;
        _alt.Saved = default;
        _scrollback.Clear();
        _x = 0;
        _y = 0;
        _tabStops = CreateTabStops(_cols, []);
        CursorShape = CursorShape.Block;
        CursorBlink = true;
        _lastPrinted = -1;
        MarkAllDirty();
    }
}
