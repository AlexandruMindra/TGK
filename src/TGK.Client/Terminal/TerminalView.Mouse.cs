using System;
using System.Collections.Generic;
using Blossom;
using Blossom.Core.Input;
using TGK.Client.Controls;
using TGK.Client.Input;
using TGK.Client.Views;
using TGK.Terminal;
using TermMouseButton = TGK.Terminal.MouseButton;

namespace TGK.Client.Terminal;

// Mouse: selection (drag, double-click word, triple-click line, Shift+click extends), wheel scrollback, the context
// menu, and mouse reporting to applications that enabled it (Shift forces local selection instead).
public sealed partial class TerminalView
{
    private const int MultiClickMs = 400;
    private const int AutoScrollMs = 50;
    private const int WheelLines = 3;

    private enum SelectionUnit
    {
        Char,
        Word,
        Line,
    }

    // Selection endpoints use absolute line numbers (ScrollbackAdded + line), which stay put while output scrolls.
    // Columns are cell boundaries: the selection covers [start, end).
    private (long Line, int Col) _anchorStart, _anchorEnd, _selStart, _selEnd;
    private bool _selecting;
    private SelectionUnit _unit;
    private int _clickCount;
    private long _lastClickMs;
    private (long Line, int Col) _lastClickCell;
    private float _wheelRemainder;
    private (float X, float Y) _pointer;
    private long _nextAutoScroll;

    // Mouse reporting state: the button held down (for motion reports) and the last reported cell.
    private TermMouseButton _reportButton = TermMouseButton.None;
    private (int Col, int Row) _lastReportCell = (-1, -1);

    private bool HasSelection => _selStart != _selEnd;

    private void InitMouse()
    {
        Events.OnMouseDown += OnMouseDown;
        Events.OnMouseMove += OnMouseMove;
        Events.OnMouseUp += OnMouseUp;
        Events.OnScroll += OnScroll;
    }

    public void SelectAll()
    {
        long top = Emulator.ScrollbackAdded - Emulator.ScrollbackCount;
        _selStart = (top, 0);
        _selEnd = (Emulator.ScrollbackAdded + Rows - 1, Cols);
        InvalidatePaint();
    }

    public void ClearSelection()
    {
        if (!HasSelection && !_selecting)
            return;
        _selecting = false;
        _selStart = _selEnd = default;
        InvalidatePaint();
    }

    public string SelectedText
    {
        get
        {
            if (!HasSelection)
                return "";
            long sa = Emulator.ScrollbackAdded;
            return Emulator.GetText((int)(_selStart.Line - sa), _selStart.Col, (int)(_selEnd.Line - sa), _selEnd.Col);
        }
    }

    /// <summary>Removes the scrollback history (the visible screen stays).</summary>
    public void ClearScrollback()
    {
        Emulator.ClearScrollback();
        _offset = 0;
        ClearSelection();
        InvalidateAllRows();
        InvalidatePaint();
    }

    private (long, int, long, int) OrderedSelection() => (_selStart.Line, _selStart.Col, _selEnd.Line, _selEnd.Col);

    private void ResetMouseReporting()
    {
        _reportButton = TermMouseButton.None;
        _lastReportCell = (-1, -1);
    }

    private bool ReportsMouse => InputEnabled && Emulator.Modes.MouseTracking != MouseTrackingMode.None && _offset == 0
        && (KeyboardHub.CurrentModifiers & KeyModifiers.Shift) == 0;

    private void OnMouseDown(object sender, MouseEventArgs e)
    {
        e.Handled = true;
        _pointer = (e.Relative.X, e.Relative.Y);
        if (ReportsMouse && ToReportButton(e.Button) is { } button)
        {
            _reportButton = button;
            Report(button, MouseEventKind.Press);
            CapturePointer();
            return;
        }

        switch (e.Button)
        {
            case 0:
                BeginSelection();
                CapturePointer();
                break;
            case 1:
                ShowContextMenu(e.Global.X, e.Global.Y);
                break;
            case 2:
                Paste();
                break;
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        _pointer = (e.Relative.X, e.Relative.Y);
        if (_reportButton != TermMouseButton.None || (ReportsMouse && Emulator.Modes.MouseTracking == MouseTrackingMode.AnyEvent))
        {
            Report(_reportButton, MouseEventKind.Motion);
            return;
        }
        if (_selecting)
            ExtendSelection();
    }

    private void OnMouseUp(object sender, MouseEventArgs e)
    {
        _pointer = (e.Relative.X, e.Relative.Y);
        if (_reportButton != TermMouseButton.None)
        {
            Report(_reportButton, MouseEventKind.Release);
            _reportButton = TermMouseButton.None;
            return;
        }
        if (e.Button == 0 && _selecting)
        {
            _selecting = false;
            if (HasSelection && _settings.CopyOnSelect)
                Copy();
        }
    }

    private void OnScroll(object sender, MouseScrollEventArgs e)
    {
        e.Handled = true;
        _wheelRemainder += e.Offset.Y; // notches, positive = up; touchpads send fractions
        int notches = (int)_wheelRemainder;
        if (notches == 0)
            return;
        _wheelRemainder -= notches;

        TerminalModes modes = Emulator.Modes;
        if (ReportsMouse)
        {
            for (int i = 0; i < Math.Abs(notches); i++)
                Report(notches > 0 ? TermMouseButton.WheelUp : TermMouseButton.WheelDown, MouseEventKind.Press);
        }
        else if (modes.AlternateScreen && InputEnabled)
        {
            // Full-screen apps without mouse support (less, man): scroll them with arrow keys.
            byte[] key = TerminalInput.EncodeKey(notches > 0 ? TermKey.Up : TermKey.Down, KeyMods.None, modes);
            for (int i = 0; i < Math.Abs(notches) * WheelLines; i++)
                Send(key);
        }
        else
        {
            ScrollViewport(notches * WheelLines);
        }
    }

    /// <summary>Scrolls the viewport <paramref name="lines"/> into history (negative: towards the live screen).</summary>
    private void ScrollViewport(int lines)
    {
        int offset = Math.Clamp(_offset + lines, 0, Emulator.ScrollbackCount);
        if (offset == _offset)
            return;
        _offset = offset;
        InvalidatePaint();
    }

    private void ScrollToBottom() => ScrollViewport(-_offset);

    // While scrolled back, new output must not move what the user is reading: follow it further into history.
    private void KeepViewportAnchored(long linesAdded)
    {
        if (_offset > 0)
            _offset = (int)Math.Min(_offset + linesAdded, Emulator.ScrollbackCount);
    }

    // ---- selection ----

    private (long Line, int Col) CellAt(float x, float y, bool boundary)
    {
        float fx = (x - PadX) / _font.CellWidth;
        int col = boundary ? (int)MathF.Round(fx) : (int)MathF.Floor(fx);
        col = Math.Clamp(col, 0, boundary ? Cols : Cols - 1);
        int row = Math.Clamp((int)MathF.Floor((y - PadY) / _font.CellHeight), 0, Rows - 1);
        return (Emulator.ScrollbackAdded - _offset + row, col);
    }

    private void BeginSelection()
    {
        (long line, int col) cell = CellAt(_pointer.X, _pointer.Y, boundary: false);
        long now = UiClock.NowMs;
        bool repeat = now - _lastClickMs <= MultiClickMs && cell == _lastClickCell;
        _clickCount = repeat ? _clickCount % 3 + 1 : 1;
        _lastClickMs = now;
        _lastClickCell = cell;

        if (_clickCount == 1 && (KeyboardHub.CurrentModifiers & KeyModifiers.Shift) != 0 && HasSelection)
        {
            // Shift+click extends the existing selection: its start stays, the end moves to the click.
            _anchorStart = _anchorEnd = _selStart;
            _unit = SelectionUnit.Char;
            _selecting = true;
            ExtendSelection();
            return;
        }

        _unit = (SelectionUnit)(_clickCount - 1);
        (long line, int col) = cell;
        switch (_unit)
        {
            case SelectionUnit.Word:
                (int s, int e) = WordAt(line, col);
                _anchorStart = (line, s);
                _anchorEnd = (line, e);
                break;
            case SelectionUnit.Line:
                _anchorStart = (line, 0);
                _anchorEnd = (line, Cols);
                break;
            default:
                _anchorStart = _anchorEnd = CellAt(_pointer.X, _pointer.Y, boundary: true);
                break;
        }
        _selStart = _anchorStart;
        _selEnd = _anchorEnd;
        _selecting = true;
        InvalidatePaint();
    }

    private void ExtendSelection()
    {
        (long line, int col) = CellAt(_pointer.X, _pointer.Y, boundary: _unit == SelectionUnit.Char);
        (long Line, int Col) from, to;
        switch (_unit)
        {
            case SelectionUnit.Word:
                (int s, int e) = WordAt(line, col);
                from = (line, s);
                to = (line, e);
                break;
            case SelectionUnit.Line:
                from = (line, 0);
                to = (line, Cols);
                break;
            default:
                from = to = (line, col);
                break;
        }

        if (Compare(from, _anchorStart) < 0)
            (_selStart, _selEnd) = (from, _anchorEnd);
        else
            (_selStart, _selEnd) = (_anchorStart, Compare(to, _anchorEnd) > 0 ? to : _anchorEnd);
        InvalidatePaint();
    }

    private (int Start, int End) WordAt(long absLine, int col)
    {
        int line = (int)(absLine - Emulator.ScrollbackAdded);
        return line < -Emulator.ScrollbackCount || line >= Rows ? (col, col + 1) : Emulator.GetWordBounds(line, col);
    }

    private static int Compare((long Line, int Col) a, (long Line, int Col) b) =>
        a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Col.CompareTo(b.Col);

    // Dragging a selection past the top or bottom edge scrolls through history.
    private void TickAutoScroll(long now)
    {
        if (!_selecting || now < _nextAutoScroll)
            return;
        if (ParentView?.Events.IsMouseButtonDown(0) != true)
        {
            _selecting = false; // the button was released where we could not see it (pointer capture lost)
            return;
        }
        float top = PadY, bottom = PadY + Rows * _font.CellHeight;
        int lines = _pointer.Y < top ? 1 : _pointer.Y > bottom ? -1 : 0;
        if (lines == 0)
            return;
        _nextAutoScroll = now + AutoScrollMs;
        ScrollViewport(lines);
        ExtendSelection();
    }

    // ---- clipboard & context menu ----

    public void Copy()
    {
        string text = SelectedText;
        if (text.Length > 0)
            Browser.SetClipboardText(text);
    }

    public void Paste()
    {
        if (!InputEnabled)
            return;
        string text = Browser.GetClipboardText();
        if (text.Length == 0)
            return;
        ScrollToBottom();
        Send(TerminalInput.EncodePaste(text, Emulator.Modes));
    }

    /// <summary>Extra entries appended to the context menu (e.g. the owning tab's actions), built when it opens.</summary>
    public Func<IEnumerable<MenuItem>>? ExtraMenuItems { get; set; }

    private void ShowContextMenu(float x, float y)
    {
        if (ParentView is not TgkView view)
            return;
        var items = new List<MenuItem>
        {
            new() { Text = "Copy", Icon = "copy", Hint = "Ctrl+Shift+C", IsEnabled = HasSelection, Action = Copy },
            new() { Text = "Paste", Icon = "clipboard", Hint = "Ctrl+Shift+V", IsEnabled = InputEnabled, Action = Paste },
            MenuItem.Separator,
            new() { Text = "Select all", Action = SelectAll },
            new() { Text = "Clear scrollback", Icon = "trash", IsEnabled = Emulator.ScrollbackCount > 0, Action = ClearScrollback },
        };
        if (ExtraMenuItems?.Invoke() is { } extra)
        {
            items.Add(MenuItem.Separator);
            items.AddRange(extra);
        }
        view.Menu.Show(items, x, y);
    }

    // ---- mouse reporting ----

    private static TermMouseButton? ToReportButton(int blossomButton) => blossomButton switch
    {
        0 => TermMouseButton.Left,
        1 => TermMouseButton.Right,
        2 => TermMouseButton.Middle,
        _ => null,
    };

    private void Report(TermMouseButton button, MouseEventKind kind)
    {
        int col = Math.Clamp((int)MathF.Floor((_pointer.X - PadX) / _font.CellWidth), 0, Cols - 1);
        int row = Math.Clamp((int)MathF.Floor((_pointer.Y - PadY) / _font.CellHeight), 0, Rows - 1);
        if (kind == MouseEventKind.Motion && (col, row) == _lastReportCell)
            return;
        _lastReportCell = (col, row);
        Send(TerminalInput.EncodeMouse(button, col, row, kind, ToKeyMods(KeyboardHub.CurrentModifiers), Emulator.Modes));
    }

    private static KeyMods ToKeyMods(KeyModifiers m) =>
        ((m & KeyModifiers.Shift) != 0 ? KeyMods.Shift : 0)
        | ((m & KeyModifiers.Alt) != 0 ? KeyMods.Alt : 0)
        | ((m & KeyModifiers.Ctrl) != 0 ? KeyMods.Ctrl : 0);
}
