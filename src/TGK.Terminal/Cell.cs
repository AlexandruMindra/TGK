namespace TGK.Terminal;

/// <summary>
/// One character cell of the screen (16 bytes). <c>default(Cell)</c> is an empty, single-width cell in the
/// default style. A wide (2-column) character occupies a cell with <see cref="Width"/> 2 followed by a
/// continuation cell with <see cref="Width"/> 0 and <see cref="Rune"/> 0.
/// </summary>
public struct Cell
{
    private const int RuneMask = 0x1FFFFF;
    private const int WidthShift = 21;

    // Bits 0-20: the rune. Bits 21-22: the width XOR 1, so that default(Cell) reads as Width == 1.
    private int _runeAndWidth;

    public Cell(int rune, byte width, CellStyle style)
    {
        _runeAndWidth = (rune & RuneMask) | ((width ^ 1) << WidthShift);
        Style = style;
    }

    /// <summary>Unicode scalar value, or 0 for an empty cell (rendered as a space).</summary>
    public int Rune
    {
        readonly get => _runeAndWidth & RuneMask;
        set => _runeAndWidth = (_runeAndWidth & ~RuneMask) | (value & RuneMask);
    }

    /// <summary>1 or 2 for the leading cell of a character, 0 for the continuation half of a wide character.</summary>
    public byte Width
    {
        readonly get => (byte)(((_runeAndWidth >> WidthShift) & 3) ^ 1);
        set => _runeAndWidth = (_runeAndWidth & RuneMask) | ((value ^ 1) << WidthShift);
    }

    public CellStyle Style { get; set; }

    /// <summary>True for the second half of a wide character.</summary>
    public readonly bool IsContinuation => (_runeAndWidth >> WidthShift) == 1;

    /// <summary>True when the cell holds no character (it may still carry a background color).</summary>
    public readonly bool IsEmpty => Rune == 0;

    /// <summary>An empty cell carrying only the given background (used by erase operations: BCE).</summary>
    public static Cell Blank(TermColor bg) => new(0, 1, new CellStyle(TermColor.Default, bg, CellFlags.None));

    public override readonly string ToString() =>
        IsContinuation ? "<cont>" : Rune == 0 ? "<empty>" : char.ConvertFromUtf32(Rune);
}
