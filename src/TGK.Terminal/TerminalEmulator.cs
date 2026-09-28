using System;
using System.Text;
using System.Threading;

namespace TGK.Terminal;

/// <summary>
/// Headless xterm-compatible terminal emulator. Feed it the bytes the host sends (<see cref="Feed(ReadOnlySpan{byte})"/>),
/// read the screen back through <see cref="GetLine"/> and the cursor/mode properties, and send the
/// bytes raised by <see cref="Output"/> (replies to device queries) back to the host.
/// </summary>
/// <remarks>
/// <para>Line coordinates: 0..<see cref="Rows"/>-1 are the visible screen, -1..-<see cref="ScrollbackCount"/>
/// the scrollback (-1 = the most recently scrolled-off line).</para>
/// <para>Zero-width combining marks never advance the cursor: they are composed into the previous cell
/// when Unicode has a precomposed (NFC) form, and dropped otherwise.</para>
/// <para>The class is not thread-safe. When feeding from a network thread and rendering on the UI thread,
/// hold <see cref="SyncRoot"/> around both. Events are raised synchronously from inside
/// <see cref="Feed(ReadOnlySpan{byte})"/> and must not block.</para>
/// </remarks>
public sealed partial class TerminalEmulator : IVtHandler
{
    private readonly VtParser _parser;
    private readonly Scrollback _scrollback;
    private readonly ScreenBuffer _main;
    private readonly ScreenBuffer _alt;
    private ScreenBuffer _buf;

    private int _cols;
    private int _rows;
    private int _x;
    private int _y;
    private bool _pendingWrap; // a character was printed in the last column; the next one wraps first
    private CellStyle _style;
    private int _top; // scroll region, inclusive
    private int _bottom;
    private bool[] _tabStops;
    private bool[] _dirty;
    private Cell[] _scratch; // GetLine buffer for scrollback lines whose width differs from Cols
    private TerminalModes _modes = TerminalModes.Initial;
    private Charset _g0;
    private Charset _g1;
    private bool _shiftOut; // SO: G1 is invoked into GL
    private int _lastPrinted = -1; // for REP

    public TerminalEmulator(int cols, int rows, int scrollbackLimit = 10000)
    {
        _cols = Math.Max(1, cols);
        _rows = Math.Max(1, rows);
        _scrollback = new Scrollback(scrollbackLimit);
        _main = new ScreenBuffer(_cols, _rows);
        _alt = new ScreenBuffer(_cols, _rows);
        _buf = _main;
        _bottom = _rows - 1;
        _tabStops = CreateTabStops(_cols, []);
        _dirty = new bool[_rows];
        _scratch = new Cell[_cols];
        _parser = new VtParser(this);
        MarkAllDirty();
    }

    /// <summary>Window title set by OSC 0 or OSC 2.</summary>
    public event Action<string>? TitleChanged;

    /// <summary>BEL was received.</summary>
    public event Action? Bell;

    /// <summary>Bytes to send back to the host (DSR/CPR, DA1/DA2, XTVERSION and size reports).</summary>
    public event Action<byte[]>? Output;

    /// <summary>Lock to hold around <see cref="Feed(ReadOnlySpan{byte})"/> and reads when they happen on different threads.</summary>
    public Lock SyncRoot { get; } = new();

    public int Cols => _cols;

    public int Rows => _rows;

    /// <summary>Available scrollback lines. Always 0 while the alternate screen is active.</summary>
    public int ScrollbackCount => _buf == _main ? _scrollback.Count : 0;

    public int ScrollbackLimit => _scrollback.Limit;

    /// <summary>
    /// Total number of lines ever appended to the scrollback (monotonic; eviction does not decrease it).
    /// A view scrolled back by N lines stays on the same content by adding the delta since its last frame
    /// to N (clamped to <see cref="ScrollbackCount"/>).
    /// </summary>
    public long ScrollbackAdded { get; private set; }

    /// <summary>Cursor column (0-based). Stays on the last column while a wrap is pending.</summary>
    public int CursorCol => _x;

    public int CursorRow => _y;

    public bool CursorVisible { get; private set; } = true;

    public CursorShape CursorShape { get; private set; }

    public bool CursorBlink { get; private set; } = true;

    /// <summary>A snapshot of the current modes.</summary>
    public TerminalModes Modes => _modes;

    /// <summary>The attributes new text is written with.</summary>
    public CellStyle CurrentStyle => _style;

    public string Title { get; private set; } = "";

    /// <summary>Incremented by every <see cref="Feed(ReadOnlySpan{byte})"/>, <see cref="Resize"/>, <see cref="ClearScrollback"/> and <see cref="Reset"/>.</summary>
    public long Version { get; private set; }

    /// <summary>Processes output from the host. UTF-8 sequences may be split across calls.</summary>
    public void Feed(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
            return;
        _parser.Feed(utf8);
        Version++;
    }

    /// <summary>Convenience overload that feeds <paramref name="text"/> as UTF-8.</summary>
    public void Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Returns the cells of <paramref name="line"/> (see the class remarks for coordinates); always exactly
    /// <see cref="Cols"/> long. For scrollback lines the span may point into a scratch buffer that is only
    /// valid until the next call.
    /// </summary>
    public ReadOnlySpan<Cell> GetLine(int line)
    {
        var cells = LineAt(line).Cells;
        if (cells.Length == _cols)
            return cells;

        // A scrollback line recorded before a resize keeps its old width: pad or truncate it.
        int keep = Math.Min(cells.Length, _cols);
        cells.AsSpan(0, keep).CopyTo(_scratch);
        _scratch.AsSpan(keep).Clear();
        if (keep == _cols && _scratch[keep - 1].Width == 2)
            _scratch[keep - 1] = default;
        return _scratch;
    }

    /// <summary>True when <paramref name="line"/> was soft-wrapped into the next line by autowrap.</summary>
    public bool IsLineWrapped(int line) => LineAt(line).Wrapped;

    /// <summary>True when screen row <paramref name="row"/> changed since the last <see cref="ClearDirty"/>.</summary>
    public bool IsRowDirty(int row) => (uint)row < (uint)_rows && _dirty[row];

    public void ClearDirty() => Array.Clear(_dirty);

    /// <summary>Discards the scrollback history (like ED 3), keeping the screen.</summary>
    public void ClearScrollback()
    {
        _scrollback.Clear();
        MarkAllDirty();
        Version++;
    }

    /// <summary>Returns the emulator to its initial state (screen, scrollback, modes and title), keeping its size.</summary>
    public void Reset()
    {
        _parser.Reset();
        FullReset();
        SetTitle("");
        Version++;
    }

    /// <summary>
    /// Prepares the terminal for a new session on the same screen: leaves the alternate screen and resets the
    /// modes (mouse reporting, bracketed paste, keypad, focus events, ...), scroll region, character sets,
    /// attributes, cursor style and title, but keeps the main screen and the scrollback. A cursor left mid-line
    /// moves to the start of the next line, so the new session's output starts on a line of its own.
    /// </summary>
    public void ResetModes()
    {
        _parser.Reset();
        if (_buf == _alt)
        {
            SwitchScreen(false);
            RestoreCursor(); // as DECRST 1049 would have
        }

        SoftReset();
        _modes = TerminalModes.Initial;
        _tabStops = CreateTabStops(_cols, []);
        CursorShape = CursorShape.Block;
        CursorBlink = true;
        _lastPrinted = -1;
        if (_x > 0)
            NextLine();
        SetTitle("");
        MarkAllDirty();
        Version++;
    }

    private TerminalLine LineAt(int line)
    {
        if (line >= 0)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(line, _rows);
            return _buf.Lines[line];
        }

        int count = ScrollbackCount;
        ArgumentOutOfRangeException.ThrowIfLessThan(line, -count);
        return _scrollback[count + line];
    }

    private void MarkAllDirty() => _dirty.AsSpan().Fill(true);

    private void MarkDirty(int from, int to)
    {
        for (int y = from; y <= to; y++)
            _dirty[y] = true;
    }

    private void Reply(string s) => Output?.Invoke(Encoding.ASCII.GetBytes(s));

    private void SetTitle(string title)
    {
        if (title == Title)
            return;
        Title = title;
        TitleChanged?.Invoke(title);
    }

    private static bool[] CreateTabStops(int cols, bool[] existing)
    {
        var stops = new bool[cols];
        int keep = Math.Min(cols, existing.Length);
        Array.Copy(existing, stops, keep);
        for (int x = keep; x < cols; x++)
            stops[x] = x % 8 == 0 && x > 0;
        return stops;
    }

    private sealed class ScreenBuffer
    {
        public SavedCursor Saved;

        public ScreenBuffer(int cols, int rows)
        {
            Lines = new TerminalLine[rows];
            for (int i = 0; i < rows; i++)
                Lines[i] = new TerminalLine(cols);
        }

        public TerminalLine[] Lines { get; set; }
    }

    /// <summary>State saved by DECSC / restored by DECRC.</summary>
    private struct SavedCursor
    {
        public int X;
        public int Y;
        public bool PendingWrap;
        public bool Origin;
        public CellStyle Style;
        public Charset G0;
        public Charset G1;
        public bool ShiftOut;
    }
}
