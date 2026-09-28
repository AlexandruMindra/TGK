using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TGK.Terminal.Tests;

internal static class TestUtil
{
    public const string Esc = "\u001b";
    public const string Csi = "\u001b[";

    public static TerminalEmulator Term(int cols = 10, int rows = 5, int scrollback = 100) => new(cols, rows, scrollback);

    /// <summary>Text of a line: empty cells as spaces, continuation cells skipped, trailing spaces trimmed.</summary>
    public static string Row(this TerminalEmulator t, int line)
    {
        var sb = new StringBuilder();
        foreach (var cell in t.GetLine(line))
        {
            if (!cell.IsContinuation)
                sb.Append(cell.Rune == 0 ? " " : char.ConvertFromUtf32(cell.Rune));
        }

        return sb.ToString().TrimEnd();
    }

    public static string[] Screen(this TerminalEmulator t) => Enumerable.Range(0, t.Rows).Select(t.Row).ToArray();

    public static (int Col, int Row) Cursor(this TerminalEmulator t) => (t.CursorCol, t.CursorRow);

    public static Cell CellAt(this TerminalEmulator t, int line, int col) => t.GetLine(line)[col];

    /// <summary>Collects everything the emulator sends back to the host, as a string.</summary>
    public static List<string> CaptureOutput(this TerminalEmulator t)
    {
        var replies = new List<string>();
        t.Output += bytes => replies.Add(Encoding.UTF8.GetString(bytes));
        return replies;
    }
}
