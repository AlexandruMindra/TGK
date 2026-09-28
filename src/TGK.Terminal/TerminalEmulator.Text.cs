using System;
using System.Text;

namespace TGK.Terminal;

// Text extraction for selection and copy.
public sealed partial class TerminalEmulator
{
    private const string WordSeparators = "\"'`()[]{}<>|;,";

    /// <summary>
    /// Returns the text from (<paramref name="startLine"/>, <paramref name="startCol"/>) inclusive to
    /// (<paramref name="endLine"/>, <paramref name="endCol"/>) exclusive, in <see cref="GetLine"/> coordinates.
    /// The positions may come in either order and are clamped to the available lines. Lines are joined with
    /// '\n' except where a line soft-wraps into the next; trailing blanks at each line end are dropped; a range
    /// starting on the right half of a wide character includes the whole character.
    /// </summary>
    public string GetText(int startLine, int startCol, int endLine, int endCol)
    {
        if (endLine < startLine || (endLine == startLine && endCol < startCol))
            (startLine, startCol, endLine, endCol) = (endLine, endCol, startLine, startCol);

        int first = -ScrollbackCount;
        if (startLine < first)
            (startLine, startCol) = (first, 0);
        if (endLine >= _rows)
            (endLine, endCol) = (_rows - 1, _cols);

        var sb = new StringBuilder();
        for (int line = startLine; line <= endLine; line++)
        {
            var l = LineAt(line);
            var cells = l.Cells;
            int from = Math.Max(0, line == startLine ? startCol : 0);
            int to = Math.Min(Math.Min(cells.Length, _cols), line == endLine ? endCol : _cols);
            if (from > 0 && from < to && cells[from].IsContinuation)
                from--;

            // A soft-wrapped line continues without a newline; only its empty padding cells (e.g. left
            // where a wide character did not fit) are dropped. A hard line end drops trailing spaces too.
            bool joinsNext = l.Wrapped && line < endLine;
            int keep = sb.Length;
            for (int c = from; c < to; c++)
            {
                int rune = cells[c].Rune;
                if (cells[c].IsContinuation)
                    continue;
                if (rune == 0)
                    sb.Append(' ');
                else
                    sb.Append(char.ConvertFromUtf32(rune));
                if (rune != 0 && (joinsNext || rune != ' '))
                    keep = sb.Length;
            }

            sb.Length = keep;
            if (line < endLine && !l.Wrapped)
                sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Returns the columns [Start, End) of the word at (<paramref name="line"/>, <paramref name="col"/>) for
    /// double-click selection. Words are runs of anything but blanks and the separators <c>"'`()[]{}&lt;&gt;|;,</c>;
    /// a click on blanks returns the run of blanks, a click on a separator just that character.
    /// </summary>
    public (int Start, int End) GetWordBounds(int line, int col)
    {
        var cells = LineAt(line).Cells;
        int limit = Math.Min(cells.Length, _cols);
        col = Math.Clamp(col, 0, _cols - 1);
        if (col >= limit)
        {
            // Past the end of a narrower scrollback line: the missing cells count as blanks.
            int start = limit;
            while (start > 0 && Classify(cells, start - 1) == CharClass.Blank)
                start--;
            return (start, _cols);
        }

        if (cells[col].IsContinuation && col > 0)
            col--;
        var cls = Classify(cells, col);
        int width = Math.Max(1, (int)cells[col].Width);
        if (cls == CharClass.Separator)
            return (col, Math.Min(col + width, _cols));

        int s = col;
        while (s > 0 && Classify(cells, s - 1) == cls)
            s--;
        int e = col + width;
        while (e < limit && Classify(cells, e) == cls)
            e++;
        if (e == limit && cls == CharClass.Blank)
            e = _cols;
        return (s, Math.Min(e, _cols));
    }

    private enum CharClass
    {
        Blank,
        Word,
        Separator,
    }

    // The right half of a wide character belongs to the class of its left half.
    private static CharClass Classify(Cell[] cells, int col)
    {
        if (cells[col].IsContinuation && col > 0)
            col--;
        int rune = cells[col].Rune;
        if (rune == 0 || (rune < 0x10000 && char.IsWhiteSpace((char)rune)))
            return CharClass.Blank;
        return rune < 0x80 && WordSeparators.Contains((char)rune) ? CharClass.Separator : CharClass.Word;
    }
}
