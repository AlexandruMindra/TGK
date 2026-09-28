using System;

namespace TGK.Terminal;

/// <summary>A row of cells plus its soft-wrap flag.</summary>
internal sealed class TerminalLine
{
    public TerminalLine(int cols) => Cells = new Cell[cols];

    /// <summary>
    /// Exactly <see cref="TerminalEmulator.Cols"/> long on screen. Scrollback lines keep the width they had
    /// when they scrolled off.
    /// </summary>
    public Cell[] Cells { get; private set; }

    /// <summary>True when the text continues on the next line because of autowrap.</summary>
    public bool Wrapped { get; set; }

    public void Clear(Cell blank)
    {
        Cells.AsSpan().Fill(blank);
        Wrapped = false;
    }

    /// <summary>Truncates or pads (with default cells) to <paramref name="cols"/> cells.</summary>
    public void SetWidth(int cols)
    {
        if (Cells.Length == cols)
            return;

        var cells = new Cell[cols];
        int keep = Math.Min(cols, Cells.Length);
        Array.Copy(Cells, cells, keep);
        if (keep == cols && cells[cols - 1].Width == 2)
            cells[cols - 1] = Cell.Blank(cells[cols - 1].Style.Bg); // wide character cut in half
        Cells = cells;
    }
}
