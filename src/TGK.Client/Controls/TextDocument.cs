using System;
using System.Collections.Generic;
using System.Text;

namespace TGK.Client.Controls;

/// <summary>A place in a <see cref="TextDocument"/>: a line (0-based) and a character index in it.</summary>
public readonly record struct TextPos(int Line, int Col) : IComparable<TextPos>
{
    public int CompareTo(TextPos other) => Line != other.Line ? Line.CompareTo(other.Line) : Col.CompareTo(other.Col);

    public static bool operator <(TextPos a, TextPos b) => a.CompareTo(b) < 0;
    public static bool operator >(TextPos a, TextPos b) => a.CompareTo(b) > 0;
    public static bool operator <=(TextPos a, TextPos b) => a.CompareTo(b) <= 0;
    public static bool operator >=(TextPos a, TextPos b) => a.CompareTo(b) >= 0;

    public static TextPos Min(TextPos a, TextPos b) => a <= b ? a : b;
    public static TextPos Max(TextPos a, TextPos b) => a >= b ? a : b;
}

/// <summary>
/// The text of the editor (<see cref="TextEditor"/>): lines (without their line ends), a caret and a selection,
/// edits with undo and redo, and search. Knows nothing about drawing; moving up and down by lines is the view's job
/// (it knows the columns on screen).
/// </summary>
public sealed class TextDocument
{
    private const int MaxUndo = 2000;

    private readonly List<string> _lines = [""];
    private readonly List<Edit> _undo = [];
    private readonly List<Edit> _redo = [];
    private TextPos _caret, _anchor;
    private int _version;
    private long _lastEditId, _savedEditId; // the undo step the saved text is at (0: none)
    private bool _mergeTyping; // the next typed character joins the last undo step

    /// <summary>One change: <see cref="Removed"/> at <see cref="Start"/> replaced by <see cref="Inserted"/>.</summary>
    private sealed record Edit(long Id, TextPos Start, string Removed, string Inserted, TextPos CaretBefore, TextPos AnchorBefore, TextPos CaretAfter)
    {
        public TextPos InsertedEnd => EndOf(Start, Inserted);
    }

    /// <summary>The text changed (an edit, undo, redo or a new text).</summary>
    public event Action? Changed;

    /// <summary>The caret or the selection moved (also after every change).</summary>
    public event Action? CaretMoved;

    public int LineCount => _lines.Count;

    public string this[int line] => _lines[line];

    /// <summary>No edits from the keyboard (edits through code still work, e.g. a log that grows).</summary>
    public bool ReadOnly { get; set; }

    /// <summary>What Tab inserts: a tab, or the spaces the text indents with (see <see cref="SetText"/>).</summary>
    public string IndentUnit { get; set; } = "    ";

    public TextPos Caret => _caret;
    public TextPos Anchor => _anchor;
    public bool HasSelection => _caret != _anchor;
    public TextPos SelectionStart => TextPos.Min(_caret, _anchor);
    public TextPos SelectionEnd => TextPos.Max(_caret, _anchor);

    /// <summary>Counts every change of the text.</summary>
    public int Version => _version;

    /// <summary>The text differs from the one last saved (<see cref="MarkSaved"/>); undoing back to it clears this.</summary>
    public bool IsModified => TopEdit != _savedEditId;

    private long TopEdit => _undo.Count > 0 ? _undo[^1].Id : 0;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public TextPos End => new(_lines.Count - 1, _lines[^1].Length);

    /// <summary>The whole text, lines joined with <c>\n</c>.</summary>
    public string Text => string.Join('\n', _lines);

    public string SelectedText => GetText(SelectionStart, SelectionEnd);

    /// <summary>Replaces everything (a file opened or reloaded): no undo, caret at the start, not modified.</summary>
    public void SetText(string text)
    {
        _lines.Clear();
        _lines.AddRange(Normalize(text).Split('\n'));
        _undo.Clear();
        _redo.Clear();
        _caret = _anchor = default;
        _version++;
        _savedEditId = 0;
        _mergeTyping = false;
        IndentUnit = DetectIndent();
        Changed?.Invoke();
        CaretMoved?.Invoke();
    }

    /// <summary>Adds text at the end (a log that grew): no undo; the caret stays where it is.</summary>
    public void Append(string text)
    {
        if (text.Length == 0)
            return;
        string[] parts = Normalize(text).Split('\n');
        _lines[^1] += parts[0];
        for (int i = 1; i < parts.Length; i++)
            _lines.Add(parts[i]);
        _version++;
        Changed?.Invoke();
    }

    /// <summary>Drops the first <paramref name="count"/> lines (a followed log kept to a size); no undo.</summary>
    public void RemoveFirstLines(int count)
    {
        count = Math.Min(count, _lines.Count - 1);
        if (count <= 0)
            return;
        _lines.RemoveRange(0, count);
        _caret = Clamp(_caret with { Line = _caret.Line - count });
        _anchor = Clamp(_anchor with { Line = _anchor.Line - count });
        bool modified = IsModified;
        _undo.Clear();
        _redo.Clear();
        _savedEditId = modified ? -1 : 0;
        _version++;
        Changed?.Invoke();
        CaretMoved?.Invoke();
    }

    public void MarkSaved()
    {
        _savedEditId = TopEdit;
        _mergeTyping = false; // what is typed next is a new step, after the saved one
    }

    public TextPos Clamp(TextPos p)
    {
        int line = Math.Clamp(p.Line, 0, _lines.Count - 1);
        return new TextPos(line, Math.Clamp(p.Col, 0, _lines[line].Length));
    }

    public string GetText(TextPos start, TextPos end)
    {
        start = Clamp(start);
        end = Clamp(end);
        if (start >= end)
            return "";
        if (start.Line == end.Line)
            return _lines[start.Line][start.Col..end.Col];
        var sb = new StringBuilder(_lines[start.Line], start.Col, _lines[start.Line].Length - start.Col, 256);
        for (int line = start.Line + 1; line < end.Line; line++)
            sb.Append('\n').Append(_lines[line]);
        return sb.Append('\n').Append(_lines[end.Line], 0, end.Col).ToString();
    }

    // ---- caret and selection ----

    /// <summary>Moves the caret; with <paramref name="extend"/> the selection grows from where it started.</summary>
    public void MoveTo(TextPos p, bool extend = false)
    {
        _caret = Clamp(p);
        if (!extend)
            _anchor = _caret;
        _mergeTyping = false;
        CaretMoved?.Invoke();
    }

    public void Select(TextPos anchor, TextPos caret)
    {
        _anchor = Clamp(anchor);
        _caret = Clamp(caret);
        _mergeTyping = false;
        CaretMoved?.Invoke();
    }

    public void SelectAll() => Select(default, End);

    public void SelectWordAt(TextPos p)
    {
        p = Clamp(p);
        string line = _lines[p.Line];
        if (line.Length == 0)
        {
            Select(p, p);
            return;
        }
        int at = Math.Min(p.Col, line.Length - 1);
        int cls = CharClass(line[at]);
        int start = at, end = at + 1;
        while (start > 0 && CharClass(line[start - 1]) == cls)
            start--;
        while (end < line.Length && CharClass(line[end]) == cls)
            end++;
        Select(new TextPos(p.Line, start), new TextPos(p.Line, end));
    }

    /// <summary>Selects a whole line, its line end included (so that it can be cut or replaced as a line).</summary>
    public void SelectLine(int line)
    {
        line = Math.Clamp(line, 0, _lines.Count - 1);
        Select(new TextPos(line, 0), line + 1 < _lines.Count ? new TextPos(line + 1, 0) : new TextPos(line, _lines[line].Length));
    }

    public TextPos Left(TextPos p, bool word)
    {
        p = Clamp(p);
        if (p.Col == 0)
            return p.Line > 0 ? new TextPos(p.Line - 1, _lines[p.Line - 1].Length) : p;
        string line = _lines[p.Line];
        if (!word)
            return p with { Col = p.Col >= 2 && char.IsSurrogatePair(line[p.Col - 2], line[p.Col - 1]) ? p.Col - 2 : p.Col - 1 };
        int i = p.Col;
        while (i > 0 && CharClass(line[i - 1]) == 0)
            i--;
        if (i > 0)
        {
            int cls = CharClass(line[i - 1]);
            while (i > 0 && CharClass(line[i - 1]) == cls)
                i--;
        }
        return p with { Col = i };
    }

    public TextPos Right(TextPos p, bool word)
    {
        p = Clamp(p);
        string line = _lines[p.Line];
        if (p.Col >= line.Length)
            return p.Line + 1 < _lines.Count ? new TextPos(p.Line + 1, 0) : p;
        if (!word)
            return p with { Col = p.Col + 1 < line.Length && char.IsSurrogatePair(line[p.Col], line[p.Col + 1]) ? p.Col + 2 : p.Col + 1 };
        int i = p.Col;
        while (i < line.Length && CharClass(line[i]) == 0)
            i++;
        if (i < line.Length)
        {
            int cls = CharClass(line[i]);
            while (i < line.Length && CharClass(line[i]) == cls)
                i++;
        }
        return p with { Col = i };
    }

    /// <summary>Home: the first non-blank character of the line, or its very start when already there.</summary>
    public TextPos Home(TextPos p)
    {
        p = Clamp(p);
        int indent = LeadingWhitespace(_lines[p.Line]);
        return p with { Col = p.Col == indent ? 0 : indent };
    }

    // ---- editing ----

    /// <summary>Types or pastes <paramref name="text"/> over the selection. Typing one character at a time is undone a word at a time.</summary>
    public void Insert(string text)
    {
        if (ReadOnly)
            return;
        text = Normalize(text);
        if (text.Length == 0 && !HasSelection)
            return;
        bool typing = text.Length == 1 && text[0] != '\n' && !HasSelection;
        bool merge = typing && _mergeTyping && _undo.Count > 0 && _undo[^1].InsertedEnd == _caret
            && !(char.IsWhiteSpace(text[0]) && !char.IsWhiteSpace(_undo[^1].Inserted[^1]));
        if (merge)
        {
            Edit last = _undo[^1];
            Apply(_caret, _caret, text);
            _undo[^1] = last with { Inserted = last.Inserted + text, CaretAfter = _caret };
        }
        else
        {
            Replace(SelectionStart, SelectionEnd, text);
        }
        _mergeTyping = typing;
    }

    /// <summary>Enter: a new line indented like the current one.</summary>
    public void NewLine()
    {
        if (ReadOnly)
            return;
        string line = _lines[SelectionStart.Line];
        int indent = Math.Min(LeadingWhitespace(line), SelectionStart.Col);
        Insert("\n" + line[..indent]);
    }

    public void Backspace(bool word)
    {
        if (ReadOnly)
            return;
        if (!HasSelection)
        {
            TextPos from = Left(_caret, word);
            // In leading blanks a backspace takes back one indent step.
            string line = _lines[_caret.Line];
            if (!word && _caret.Col > 0 && IndentUnit != "\t" && _caret.Col <= LeadingWhitespace(line) && line[.._caret.Col].Trim(' ').Length == 0)
                from = _caret with { Col = (_caret.Col - 1) / IndentUnit.Length * IndentUnit.Length };
            _anchor = from;
        }
        Replace(SelectionStart, SelectionEnd, "");
    }

    public void Delete(bool word)
    {
        if (ReadOnly)
            return;
        if (!HasSelection)
            _anchor = Right(_caret, word);
        Replace(SelectionStart, SelectionEnd, "");
    }

    /// <summary>Tab: an indent at the caret, or every selected line indented (Shift+Tab: outdented).</summary>
    public void Indent(bool outdent)
    {
        if (ReadOnly)
            return;
        bool lines = HasSelection && SelectionStart.Line != SelectionEnd.Line;
        if (!lines && !outdent)
        {
            Insert(IndentUnit == "\t" ? "\t" : new string(' ', IndentUnit.Length - _caret.Col % IndentUnit.Length));
            return;
        }
        int first = SelectionStart.Line;
        int last = lines && SelectionEnd.Col == 0 ? SelectionEnd.Line - 1 : SelectionEnd.Line;
        var changed = new string[last - first + 1];
        bool any = false;
        for (int n = first; n <= last; n++)
        {
            string line = _lines[n];
            string next;
            if (!outdent)
                next = line.Length > 0 ? IndentUnit + line : line;
            else if (line.StartsWith('\t'))
                next = line[1..];
            else
                next = line[Math.Min(IndentUnit.Length, line.Length - line.TrimStart(' ').Length)..];
            any |= next != line;
            changed[n - first] = next;
        }
        if (!any)
            return;
        bool backwards = _caret < _anchor;
        int shift = _caret.Line >= first && _caret.Line <= last ? changed[_caret.Line - first].Length - _lines[_caret.Line].Length : 0;
        TextPos caret = _caret;
        Replace(new TextPos(first, 0), new TextPos(last, _lines[last].Length), string.Join('\n', changed));
        if (lines)
        {
            // The same lines stay selected, whole, in the same direction.
            var start = new TextPos(first, 0);
            var end = new TextPos(last, _lines[last].Length);
            Select(backwards ? end : start, backwards ? start : end);
        }
        else
        {
            MoveTo(caret with { Col = Math.Max(0, caret.Col + shift) });
        }
    }

    /// <summary>Replaces the text between <paramref name="start"/> and <paramref name="end"/> as one undo step.</summary>
    public void Replace(TextPos start, TextPos end, string text)
    {
        start = Clamp(start);
        end = Clamp(end);
        if (end < start)
            (start, end) = (end, start);
        string removed = GetText(start, end);
        text = Normalize(text);
        if (removed.Length == 0 && text.Length == 0)
        {
            MoveTo(start);
            return;
        }
        TextPos caretBefore = _caret, anchorBefore = _anchor;
        Apply(start, end, text);
        Push(new Edit(++_lastEditId, start, removed, text, caretBefore, anchorBefore, _caret));
        _mergeTyping = false;
    }

    public void Undo()
    {
        if (_undo.Count == 0)
            return;
        Edit edit = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Apply(edit.Start, edit.InsertedEnd, edit.Removed);
        _caret = edit.CaretBefore;
        _anchor = edit.AnchorBefore;
        _redo.Add(edit);
        _mergeTyping = false;
        CaretMoved?.Invoke();
    }

    public void Redo()
    {
        if (_redo.Count == 0)
            return;
        Edit edit = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Apply(edit.Start, EndOf(edit.Start, edit.Removed), edit.Inserted);
        _caret = _anchor = edit.CaretAfter;
        _undo.Add(edit);
        _mergeTyping = false;
        CaretMoved?.Invoke();
    }

    // ---- search ----

    /// <summary>
    /// Finds <paramref name="query"/> (one line of text) from <paramref name="from"/> on (or before it, backwards),
    /// wrapping around the end; null when it is nowhere.
    /// </summary>
    public (TextPos Start, TextPos End)? Find(string query, TextPos from, bool forward, bool matchCase)
    {
        if (query.Length == 0 || query.Contains('\n'))
            return null;
        StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        from = Clamp(from);
        int count = _lines.Count;
        (TextPos, TextPos) At(int line, int index) => (new TextPos(line, index), new TextPos(line, index + query.Length));
        if (forward)
        {
            for (int step = 0; step < count; step++)
            {
                int n = (from.Line + step) % count;
                int index = _lines[n].IndexOf(query, step == 0 ? from.Col : 0, comparison);
                if (index >= 0)
                    return At(n, index);
            }
            int wrapped = _lines[from.Line].IndexOf(query, comparison); // before the start, on its own line
            return wrapped >= 0 && wrapped < from.Col ? At(from.Line, wrapped) : null;
        }
        for (int step = 0; step < count; step++)
        {
            int n = ((from.Line - step) % count + count) % count;
            string line = _lines[n];
            // A match ending at or before the start index: searched backwards from its last possible character.
            int last = step == 0 ? Math.Min(line.Length - 1, from.Col + query.Length - 2) : line.Length - 1;
            int index = last >= 0 ? line.LastIndexOf(query, last, comparison) : -1;
            if (index >= 0 && (step > 0 || index < from.Col))
                return At(n, index);
        }
        string own = _lines[from.Line];
        int after = own.Length > 0 ? own.LastIndexOf(query, own.Length - 1, comparison) : -1; // after the start, on its own line
        return after >= from.Col ? At(from.Line, after) : null;
    }

    /// <summary>How many times <paramref name="query"/> occurs.</summary>
    public int CountMatches(string query, bool matchCase)
    {
        if (query.Length == 0 || query.Contains('\n'))
            return 0;
        StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int total = 0;
        foreach (string line in _lines)
        {
            for (int i = line.IndexOf(query, comparison); i >= 0; i = line.IndexOf(query, i + query.Length, comparison))
                total++;
        }
        return total;
    }

    /// <summary>Replaces every occurrence as one undo step; returns how many.</summary>
    public int ReplaceAll(string query, string replacement, bool matchCase)
    {
        if (ReadOnly)
            return 0;
        int count = CountMatches(query, matchCase);
        if (count == 0)
            return 0;
        StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        string text = Text.Replace(query, Normalize(replacement), comparison);
        TextPos caret = _caret;
        Replace(default, End, text);
        MoveTo(Clamp(caret));
        return count;
    }

    // ---- internals ----

    // Replaces the text and puts the caret after what was inserted; records nothing.
    private void Apply(TextPos start, TextPos end, string text)
    {
        string prefix = _lines[start.Line][..start.Col];
        string suffix = _lines[end.Line][end.Col..];
        string[] parts = text.Split('\n');
        var replacement = new string[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            replacement[i] = parts[i];
        replacement[0] = prefix + replacement[0];
        int lastCol = replacement[^1].Length;
        replacement[^1] += suffix;
        _lines.RemoveRange(start.Line, end.Line - start.Line + 1);
        _lines.InsertRange(start.Line, replacement);
        _caret = _anchor = new TextPos(start.Line + parts.Length - 1, lastCol);
        _version++;
        Changed?.Invoke();
        CaretMoved?.Invoke();
    }

    private void Push(Edit edit)
    {
        _undo.Add(edit);
        if (_undo.Count > MaxUndo)
            _undo.RemoveAt(0);
        _redo.Clear();
    }

    private static TextPos EndOf(TextPos start, string text)
    {
        int newline = text.LastIndexOf('\n');
        if (newline < 0)
            return start with { Col = start.Col + text.Length };
        int lines = 0;
        foreach (char ch in text)
        {
            if (ch == '\n')
                lines++;
        }
        return new TextPos(start.Line + lines, text.Length - newline - 1);
    }

    private static string Normalize(string text) =>
        text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;

    private static int LeadingWhitespace(string line)
    {
        int i = 0;
        while (i < line.Length && line[i] is ' ' or '\t')
            i++;
        return i;
    }

    // Tabs when lines start with them, else the smallest indent of space-indented lines (2 to 8), else 4 spaces.
    private string DetectIndent()
    {
        int tabs = 0, spaced = 0, smallest = int.MaxValue;
        int checkedLines = Math.Min(_lines.Count, 5000);
        for (int n = 0; n < checkedLines; n++)
        {
            string line = _lines[n];
            if (line.StartsWith('\t'))
            {
                tabs++;
            }
            else if (line.StartsWith(' ') && line.TrimStart(' ').Length > 0 && !line.TrimStart(' ').StartsWith('*'))
            {
                int indent = line.Length - line.TrimStart(' ').Length;
                if (indent >= 2)
                {
                    spaced++;
                    smallest = Math.Min(smallest, indent);
                }
            }
        }
        if (tabs > spaced)
            return "\t";
        return spaced > 0 ? new string(' ', Math.Clamp(smallest, 2, 8)) : "    ";
    }

    private static int CharClass(char ch) => char.IsWhiteSpace(ch) ? 0 : char.IsLetterOrDigit(ch) || ch == '_' ? 1 : 2;
}
