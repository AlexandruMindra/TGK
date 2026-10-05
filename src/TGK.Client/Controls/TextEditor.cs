using System;
using System.Text;
using Blossom;
using Blossom.Core.Input;
using Silk.NET.Input;
using SkiaSharp;
using TGK.Client.Input;

namespace TGK.Client.Controls;

/// <summary>
/// A plain-text editor for configuration files and logs: line numbers, a monospaced font, caret and selection with the
/// keyboard and the mouse, clipboard, undo and redo, indent with Tab, scrollbars both ways and highlighted search
/// matches. The text lives in <see cref="Document"/>. Keys it does not use (Ctrl+S, Ctrl+F, Escape…) bubble to the
/// parent; <see cref="MenuRequested"/> asks for a context menu.
/// </summary>
public sealed class TextEditor : Control, IKeyInput, IFocusable
{
    private const float PadX = 8, ThumbW = 7;
    private const int TabSize = 4, BlinkMs = 530, MultiClickMs = 400;

    private readonly StringBuilder _run = new();
    private float _scrollX, _scrollY;
    private float _cellW = 8, _rowH = 20;
    private float _metricsFor = -1;
    private int _preferredCol = -1; // the screen column Up/Down keep to
    private bool _focused, _caretShown;
    private long _blinkEpoch, _lastDownMs;
    private int _clickCount;
    private DragMode _drag;
    private float _thumbGrab;
    private TextPos _dragOrigin;
    private float _pointerX, _pointerY;
    private int _widestFor = -1, _widest;
    private string? _highlight;
    private bool _highlightCase;

    private enum DragMode { None, Text, Words, Lines, VerticalThumb, HorizontalThumb }

    public TextEditor()
    {
        Document = new TextDocument();
        ReceivesKeyboard = true;
        Cursor = StandardCursor.IBeam;
        OnFocused += _ => SetFocused(true);
        OnFocusLost += _ => SetFocused(false);
        Events.OnMouseDown += OnMouseDown;
        Events.OnMouseMove += OnMouseMove;
        Events.OnMouseUp += (_, _) => EndDrag();
        Events.OnScroll += OnWheel;
        Document.CaretMoved += OnCaretMoved;
        Document.Changed += InvalidatePaint;
    }

    public TextDocument Document { get; }

    /// <summary>A right click at window point (x, y), after the caret moved there (unless it was in the selection).</summary>
    public event Action<float, float>? MenuRequested;

    /// <summary>The view scrolled (by the user or to follow the caret).</summary>
    public event Action? Scrolled;

    public float FontSize { get; set; } = Theme.FontBase;

    public bool IsTabStop => true;

    public bool IsFocused => _focused;

    /// <summary>Text whose occurrences are highlighted (the search), or null.</summary>
    public void SetHighlight(string? text, bool matchCase)
    {
        _highlight = string.IsNullOrEmpty(text) || text.Contains('\n') ? null : text;
        _highlightCase = matchCase;
        InvalidatePaint();
    }

    /// <summary>The view shows the last line (a followed log keeps it so).</summary>
    public bool IsAtEnd => _scrollY >= MaxScrollY - _rowH / 2;

    /// <summary>The first line on screen and how many fit.</summary>
    public (int First, int Count) VisibleLines => ((int)(_scrollY / Math.Max(1, _rowH)), (int)(TextH / Math.Max(1, _rowH)));

    public void Focus() => ParentView?.SetActiveKeyboardElement(this);

    public void OnTabFocus() { }

    public void ScrollToEnd()
    {
        Metrics();
        SetScroll(_scrollX, MaxScrollY);
    }

    /// <summary>Scrolls so that <paramref name="line"/> is on screen, about a third from the top when it was not.</summary>
    public void RevealLine(int line)
    {
        Metrics();
        float top = line * _rowH;
        if (top < _scrollY || top + _rowH > _scrollY + TextH)
            SetScroll(_scrollX, top - TextH / 3);
    }

    // ---- geometry ----

    private SKFont Font => Gfx.Font(FontSize, Theme.Mono);

    private void Metrics()
    {
        if (_metricsFor == FontSize)
            return;
        _metricsFor = FontSize;
        _cellW = Font.MeasureText("0000000000") / 10f;
        _rowH = MathF.Round(FontSize * 1.6f);
    }

    private float GutterW => (Math.Max(3, Document.LineCount.ToString().Length) + 2) * _cellW;
    private float TextLeft => GutterW + PadX;
    private float TextW => Math.Max(0, W - TextLeft - ThumbW - 2);
    private float TextH => Math.Max(0, H - (MaxScrollX > 0 ? ThumbW + 2 : 0));
    private float MaxScrollY => Math.Max(0, Document.LineCount * _rowH + _rowH - TextH);
    private float MaxScrollX => Math.Max(0, (Widest() + 2) * _cellW - (W - GutterW - PadX - ThumbW - 2));

    // The widest line in screen columns (tabs expanded), measured again after each change.
    private int Widest()
    {
        if (_widestFor == Document.Version)
            return _widest;
        _widestFor = Document.Version;
        int widest = 0;
        for (int n = 0; n < Document.LineCount; n++)
        {
            string line = Document[n];
            if (line.Length <= widest / TabSize)
                continue;
            widest = Math.Max(widest, line.AsSpan().IndexOf('\t') < 0 && IsNarrow(line) ? line.Length : VisualCol(line, line.Length));
        }
        return _widest = widest;
    }

    private static bool IsNarrow(string line)
    {
        foreach (char ch in line)
        {
            if (ch >= 0x1100)
                return false;
        }
        return true;
    }

    /// <summary>Screen columns a character takes (tabs aside): 2 for wide ones (CJK, emoji), 0 for the second half of a pair.</summary>
    private static int Width(char ch) => ch switch
    {
        < 'ᄀ' => 1,
        >= '\uDC00' and <= '\uDFFF' => 0,
        >= '\uD800' and <= '\uDBFF' => 2,
        <= 'ᅟ' or (>= '⺀' and <= '꓏') or (>= '가' and <= '힣') or (>= '豈' and <= '﫿')
            or (>= '︰' and <= '﹏') or (>= '＀' and <= '｠') or (>= '￠' and <= '￦') => 2,
        _ => 1,
    };

    private static int VisualCol(string line, int col)
    {
        int v = 0;
        for (int i = 0; i < col && i < line.Length; i++)
            v = line[i] == '\t' ? v + TabSize - v % TabSize : v + Width(line[i]);
        return v;
    }

    // The character index nearest to screen column `visual` (a click lands before the character it is in the left half of).
    private static int ColAt(string line, float visual)
    {
        int v = 0;
        for (int i = 0; i < line.Length; i++)
        {
            int next = line[i] == '\t' ? v + TabSize - v % TabSize : v + Width(line[i]);
            if (next == v)
                continue;
            if (visual < (v + next) / 2f)
                return i;
            v = next;
        }
        return line.Length;
    }

    private TextPos PosAt(float x, float y)
    {
        int line = (int)Math.Floor((y + _scrollY) / _rowH);
        if (line < 0)
            return new TextPos(0, 0);
        if (line >= Document.LineCount)
            return Document.End;
        return new TextPos(line, ColAt(Document[line], (x - TextLeft + _scrollX) / _cellW));
    }

    // ---- scrolling ----

    private void SetScroll(float x, float y)
    {
        x = Math.Clamp(x, 0, MaxScrollX);
        y = Math.Clamp(y, 0, MaxScrollY);
        if (x == _scrollX && y == _scrollY)
            return;
        _scrollX = x;
        _scrollY = y;
        InvalidatePaint();
        Scrolled?.Invoke();
    }

    private void OnWheel(object? sender, MouseScrollEventArgs e)
    {
        e.Handled = true;
        Metrics();
        bool shift = (KeyboardHub.CurrentModifiers & KeyModifiers.Shift) != 0;
        float dy = shift ? 0 : e.Offset.Y, dx = shift ? e.Offset.Y : -e.Offset.X;
        SetScroll(_scrollX - dx * _cellW * 6, _scrollY - dy * _rowH * 3);
    }

    private void EnsureCaretVisible()
    {
        if (W <= 0 || H <= 0)
            return;
        Metrics();
        TextPos caret = Document.Caret;
        float top = caret.Line * _rowH, y = _scrollY;
        if (top < y)
            y = top;
        else if (top + _rowH > y + TextH)
            y = top + _rowH - TextH;
        float cx = VisualCol(Document[caret.Line], caret.Col) * _cellW, x = _scrollX;
        float margin = Math.Min(TextW / 4, _cellW * 8);
        if (cx < x)
            x = Math.Max(0, cx - margin);
        else if (cx > x + TextW - _cellW)
            x = cx - TextW + _cellW + margin;
        SetScroll(x, y);
    }

    protected override void LayoutChildren() => SetScroll(_scrollX, _scrollY); // a resize may lower the maximum

    // ---- keyboard ----

    public bool OnKey(KeyStroke k)
    {
        TextDocument d = Document;
        bool shift = k.Shift, ctrl = k.Ctrl;
        bool keepColumn = false;
        bool handled = true;
        switch (k.Key)
        {
            case Key.Left when !k.Alt && !k.Super:
                d.MoveTo(d.HasSelection && !shift ? d.SelectionStart : d.Left(d.Caret, ctrl), shift);
                break;
            case Key.Right when !k.Alt && !k.Super:
                d.MoveTo(d.HasSelection && !shift ? d.SelectionEnd : d.Right(d.Caret, ctrl), shift);
                break;
            case Key.Up when !k.Alt && ctrl && !shift:
                SetScroll(_scrollX, _scrollY - _rowH);
                break;
            case Key.Down when !k.Alt && ctrl && !shift:
                SetScroll(_scrollX, _scrollY + _rowH);
                break;
            case Key.Up when !k.Alt:
                MoveLines(-1, shift);
                keepColumn = true;
                break;
            case Key.Down when !k.Alt:
                MoveLines(1, shift);
                keepColumn = true;
                break;
            case Key.PageUp when !ctrl:
                SetScroll(_scrollX, _scrollY - PageLines() * _rowH);
                MoveLines(-PageLines(), shift);
                keepColumn = true;
                break;
            case Key.PageDown when !ctrl:
                SetScroll(_scrollX, _scrollY + PageLines() * _rowH);
                MoveLines(PageLines(), shift);
                keepColumn = true;
                break;
            case Key.Home:
                d.MoveTo(ctrl ? default : d.Home(d.Caret), shift);
                break;
            case Key.End:
                d.MoveTo(ctrl ? d.End : d.Caret with { Col = d[d.Caret.Line].Length }, shift);
                break;
            case Key.Backspace when !d.ReadOnly:
                d.Backspace(ctrl);
                break;
            case Key.Delete when shift && !ctrl:
                Cut();
                break;
            case Key.Delete when !d.ReadOnly:
                d.Delete(ctrl);
                break;
            case Key.Enter or Key.KeypadEnter when !d.ReadOnly && !ctrl && !k.Alt:
                d.NewLine();
                break;
            case Key.Tab when !d.ReadOnly && !ctrl && !k.Alt:
                d.Indent(outdent: shift);
                break;
            case Key.Insert when ctrl && !shift:
                Copy();
                break;
            case Key.Insert when shift && !ctrl:
                Paste();
                break;
            case Key.A when k.Modifiers == KeyModifiers.Ctrl:
                d.SelectAll();
                break;
            case Key.C when k.Modifiers == KeyModifiers.Ctrl:
                Copy();
                break;
            case Key.X when k.Modifiers == KeyModifiers.Ctrl:
                Cut();
                break;
            case Key.V when k.Modifiers == KeyModifiers.Ctrl:
                Paste();
                break;
            case Key.Z when k.Modifiers == KeyModifiers.Ctrl:
                d.Undo();
                break;
            case Key.Y when k.Modifiers == KeyModifiers.Ctrl:
            case Key.Z when k.Modifiers == (KeyModifiers.Ctrl | KeyModifiers.Shift):
                d.Redo();
                break;
            default:
                handled = false;
                break;
        }
        if (handled && !keepColumn)
            _preferredCol = -1;
        return handled;
    }

    public void OnText(string text)
    {
        if (Document.ReadOnly || text.Length == 0 || char.IsControl(text[0]) && text[0] != '\t')
            return;
        Document.Insert(text);
        _preferredCol = -1;
    }

    private int PageLines() => Math.Max(1, (int)(TextH / _rowH) - 1);

    private void MoveLines(int delta, bool extend)
    {
        TextDocument d = Document;
        TextPos caret = d.Caret;
        if (_preferredCol < 0)
            _preferredCol = VisualCol(d[caret.Line], caret.Col);
        int line = caret.Line + delta;
        TextPos target = line < 0 ? new TextPos(0, 0)
            : line >= d.LineCount ? d.End
            : new TextPos(line, ColAt(d[line], _preferredCol));
        d.MoveTo(target, extend);
    }

    private void Copy()
    {
        TextDocument d = Document;
        if (d.HasSelection)
            Shell.SetClipboardText(d.SelectedText);
        else
            Shell.SetClipboardText(d[d.Caret.Line] + "\n"); // no selection: the whole line
    }

    private void Cut()
    {
        TextDocument d = Document;
        if (d.ReadOnly)
        {
            Copy();
            return;
        }
        if (!d.HasSelection)
            d.SelectLine(d.Caret.Line);
        Shell.SetClipboardText(d.SelectedText);
        d.Insert("");
    }

    private void Paste()
    {
        if (Document.ReadOnly)
            return;
        string clip = Shell.GetClipboardText();
        if (clip.Length > 0)
            Document.Insert(clip);
    }

    /// <summary>The edit commands of the context menu.</summary>
    public void RunCommand(string command)
    {
        switch (command)
        {
            case "cut":
                Cut();
                break;
            case "copy":
                Copy();
                break;
            case "paste":
                Paste();
                break;
            case "select-all":
                Document.SelectAll();
                break;
            case "undo":
                Document.Undo();
                break;
            case "redo":
                Document.Redo();
                break;
        }
    }

    private void OnCaretMoved()
    {
        _blinkEpoch = UiClock.NowMs;
        _caretShown = true;
        if (_drag is DragMode.None or DragMode.Text or DragMode.Words or DragMode.Lines)
            EnsureCaretVisible();
        InvalidatePaint();
    }

    // ---- mouse ----

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        e.Handled = true;
        Metrics();
        Focus();
        float x = e.Relative.X, y = e.Relative.Y;
        TextDocument d = Document;
        if (e.Button == 0 && MaxScrollY > 0 && x >= W - ThumbW - 3 && y < TextH)
        {
            (float top, float height) = VerticalThumb();
            if (y < top || y > top + height)
                SetScroll(_scrollX, (y - height / 2) / Math.Max(1, TextH - height) * MaxScrollY);
            _thumbGrab = y - VerticalThumb().Top;
            StartDrag(DragMode.VerticalThumb);
            return;
        }
        if (e.Button == 0 && MaxScrollX > 0 && y >= H - ThumbW - 3)
        {
            (float left, float width) = HorizontalThumb();
            float local = x - GutterW;
            if (local < left || local > left + width)
                SetScroll((local - width / 2) / Math.Max(1, TrackW - width) * MaxScrollX, _scrollY);
            _thumbGrab = local - HorizontalThumb().Left;
            StartDrag(DragMode.HorizontalThumb);
            return;
        }
        TextPos at = PosAt(x, y);
        if (e.Button == 1)
        {
            if (!(d.HasSelection && at >= d.SelectionStart && at <= d.SelectionEnd))
                d.MoveTo(at);
            MenuRequested?.Invoke(e.Global.X, e.Global.Y);
            return;
        }
        if (e.Button != 0)
            return;
        long now = UiClock.NowMs;
        _clickCount = now - _lastDownMs < MultiClickMs ? _clickCount + 1 : 1;
        _lastDownMs = now;
        _preferredCol = -1;
        bool shift = (KeyboardHub.CurrentModifiers & KeyModifiers.Shift) != 0;
        if (x < GutterW)
        {
            // The line numbers select whole lines.
            _dragOrigin = new TextPos(Math.Min(at.Line, d.LineCount - 1), 0);
            if (shift)
                SelectLines(d.Anchor, at);
            else
                d.SelectLine(at.Line);
            StartDrag(DragMode.Lines);
            return;
        }
        if (_clickCount == 2)
        {
            d.SelectWordAt(at);
            _dragOrigin = at;
            StartDrag(DragMode.Words);
            return;
        }
        if (_clickCount >= 3)
        {
            d.SelectLine(at.Line);
            _dragOrigin = new TextPos(at.Line, 0);
            StartDrag(DragMode.Lines);
            return;
        }
        d.MoveTo(at, shift);
        StartDrag(DragMode.Text);
    }

    private void StartDrag(DragMode mode)
    {
        _drag = mode;
        CapturePointer();
        if (mode is DragMode.Text or DragMode.Words or DragMode.Lines)
            UiClock.Tick += OnDragTick;
        InvalidatePaint();
    }

    private void EndDrag()
    {
        if (_drag == DragMode.None)
            return;
        _drag = DragMode.None;
        UiClock.Tick -= OnDragTick;
        ReleasePointer();
        InvalidatePaint();
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        _pointerX = e.Relative.X;
        _pointerY = e.Relative.Y;
        switch (_drag)
        {
            case DragMode.VerticalThumb:
            {
                (_, float height) = VerticalThumb();
                SetScroll(_scrollX, (_pointerY - _thumbGrab) / Math.Max(1, TextH - height) * MaxScrollY);
                break;
            }
            case DragMode.HorizontalThumb:
            {
                (_, float width) = HorizontalThumb();
                SetScroll((_pointerX - GutterW - _thumbGrab) / Math.Max(1, TrackW - width) * MaxScrollX, _scrollY);
                break;
            }
            case DragMode.Text or DragMode.Words or DragMode.Lines:
                DragSelect();
                break;
        }
    }

    // Selecting past an edge scrolls that way, a little on every frame.
    private void OnDragTick()
    {
        if (IsDisposed || !HasPointerCapture)
        {
            EndDrag();
            return;
        }
        float dy = _pointerY < 0 ? _pointerY : _pointerY > TextH ? _pointerY - TextH : 0;
        float dx = _pointerX < TextLeft ? _pointerX - TextLeft : _pointerX > W ? _pointerX - W : 0;
        if (_drag == DragMode.Lines)
            dx = 0;
        if (dx == 0 && dy == 0)
            return;
        SetScroll(_scrollX + Math.Clamp(dx, -40, 40) / 2, _scrollY + Math.Clamp(dy, -60, 60) / 2);
        DragSelect();
    }

    private void DragSelect()
    {
        TextDocument d = Document;
        TextPos at = PosAt(_pointerX, Math.Clamp(_pointerY, -_rowH, TextH + _rowH));
        switch (_drag)
        {
            case DragMode.Text:
                d.MoveTo(at, extend: true);
                break;
            case DragMode.Words:
            {
                d.SelectWordAt(_dragOrigin);
                TextPos wordStart = d.SelectionStart, wordEnd = d.SelectionEnd;
                d.SelectWordAt(at);
                if (at < wordStart)
                    d.Select(wordEnd, d.SelectionStart);
                else
                    d.Select(wordStart, TextPos.Max(d.SelectionEnd, wordEnd));
                break;
            }
            case DragMode.Lines:
                SelectLines(_dragOrigin, at);
                break;
        }
    }

    private void SelectLines(TextPos from, TextPos to)
    {
        TextDocument d = Document;
        int first = Math.Min(from.Line, to.Line), last = Math.Max(from.Line, to.Line);
        TextPos start = new(first, 0);
        TextPos end = last + 1 < d.LineCount ? new TextPos(last + 1, 0) : new TextPos(last, d[last].Length);
        if (to.Line < from.Line)
            d.Select(end, start);
        else
            d.Select(start, end);
    }

    // ---- focus & caret blink ----

    private void SetFocused(bool focused)
    {
        if (_focused == focused)
            return;
        _focused = focused;
        if (focused)
        {
            _blinkEpoch = UiClock.NowMs;
            _caretShown = true;
            UiClock.Tick += OnBlinkTick;
        }
        else
        {
            UiClock.Tick -= OnBlinkTick;
            EndDrag();
        }
        InvalidatePaint();
    }

    private void OnBlinkTick()
    {
        if (IsDisposed || !EffectiveVisible)
        {
            SetFocused(false);
            return;
        }
        bool shown = (UiClock.NowMs - _blinkEpoch) / BlinkMs % 2 == 0;
        if (shown != _caretShown)
        {
            _caretShown = shown;
            InvalidatePaint();
        }
    }

    // ---- painting ----

    private float TrackW => Math.Max(0, W - GutterW - ThumbW - 2);

    private (float Top, float Height) VerticalThumb()
    {
        float track = TextH;
        float content = track + MaxScrollY;
        float height = Math.Max(28, track * track / Math.Max(track, content));
        float top = MaxScrollY > 0 ? (track - height) * (_scrollY / MaxScrollY) : 0;
        return (top, height);
    }

    private (float Left, float Width) HorizontalThumb()
    {
        float track = TrackW;
        float content = track + MaxScrollX;
        float width = Math.Max(28, track * track / Math.Max(track, content));
        float left = MaxScrollX > 0 ? (track - width) * (_scrollX / MaxScrollX) : 0;
        return (left, width);
    }

    protected override void Paint(SKCanvas c)
    {
        Metrics();
        _scrollX = Math.Clamp(_scrollX, 0, MaxScrollX);
        _scrollY = Math.Clamp(_scrollY, 0, MaxScrollY);
        TextDocument d = Document;
        SKFont font = Font;
        float gutter = GutterW, left = TextLeft, textH = TextH;
        Gfx.FillRect(c, new SKRect(0, 0, W, H), Theme.TerminalBg);
        Gfx.FillRect(c, new SKRect(0, 0, gutter, H), Theme.Surface);
        Gfx.Line(c, gutter - 0.5f, 0, gutter - 0.5f, H, Theme.Border);

        int first = Math.Max(0, (int)(_scrollY / _rowH));
        int last = Math.Min(d.LineCount - 1, (int)((_scrollY + textH) / _rowH));
        TextPos selStart = d.SelectionStart, selEnd = d.SelectionEnd, caret = d.Caret;
        int firstCol = (int)(_scrollX / _cellW), lastCol = (int)((_scrollX + TextW) / _cellW) + 1;

        int save = c.Save();
        c.ClipRect(new SKRect(0, 0, W, textH));
        for (int n = first; n <= last; n++)
        {
            string line = d[n];
            float top = n * _rowH - _scrollY;
            float baseline = Gfx.Baseline(font, top + _rowH / 2f);
            bool current = n == caret.Line;
            if (current && !d.HasSelection)
                Gfx.FillRect(c, new SKRect(gutter, top, W, top + _rowH), Theme.Surface.WithAlpha(150));
            string number = (n + 1).ToString();
            Gfx.TextAtBaseline(c, number, gutter - _cellW - number.Length * _cellW, baseline, font, current ? Theme.TextSecondary : Theme.TextDisabled);

            int textSave = c.Save();
            c.ClipRect(new SKRect(gutter + 1, top, W - ThumbW - 2, top + _rowH));
            float x0 = left - _scrollX;
            if (_highlight is { } query)
                PaintMatches(c, line, query, x0, top);
            if (d.HasSelection && n >= selStart.Line && n <= selEnd.Line)
            {
                float sx = n == selStart.Line ? VisualCol(line, selStart.Col) * _cellW : 0;
                float ex = n == selEnd.Line ? VisualCol(line, selEnd.Col) * _cellW : (VisualCol(line, line.Length) + 1) * _cellW;
                if (ex > sx)
                    Gfx.FillRect(c, new SKRect(x0 + sx, top, x0 + ex, top + _rowH), _focused ? Theme.Selection : Theme.Selection.WithAlpha(50));
            }
            PaintLine(c, line, x0, baseline, firstCol, lastCol, font);
            c.RestoreToCount(textSave);
        }
        if (_focused && _caretShown && caret.Line >= first && caret.Line <= last)
        {
            float x = MathF.Round(left - _scrollX + VisualCol(d[caret.Line], caret.Col) * _cellW);
            float top = caret.Line * _rowH - _scrollY;
            if (x >= gutter)
                Gfx.FillRect(c, new SKRect(x, top + 2, x + 1.5f, top + _rowH - 2), d.ReadOnly ? Theme.TextMuted : Theme.TextPrimary);
        }
        c.RestoreToCount(save);

        if (MaxScrollY > 0)
        {
            (float top, float height) = VerticalThumb();
            Gfx.FillRound(c, new SKRect(W - ThumbW - 1, top + 2, W - 1, top + height - 2), 3,
                _drag == DragMode.VerticalThumb ? Theme.TextMuted : Theme.BorderStrong);
        }
        if (MaxScrollX > 0)
        {
            (float l, float width) = HorizontalThumb();
            Gfx.FillRound(c, new SKRect(gutter + l + 2, H - ThumbW - 1, gutter + l + width - 2, H - 1), 3,
                _drag == DragMode.HorizontalThumb ? Theme.TextMuted : Theme.BorderStrong);
        }
    }

    private void PaintMatches(SKCanvas c, string line, string query, float x0, float top)
    {
        StringComparison comparison = _highlightCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (int i = line.IndexOf(query, comparison); i >= 0; i = line.IndexOf(query, i + query.Length, comparison))
        {
            float sx = VisualCol(line, i) * _cellW, ex = VisualCol(line, i + query.Length) * _cellW;
            Gfx.FillRound(c, new SKRect(x0 + sx, top + 1, x0 + ex, top + _rowH - 1), 2, Theme.Warning.WithAlpha(70));
        }
    }

    // Draws the characters of `line` in screen columns [firstCol, lastCol): runs of plain characters together, tabs as
    // space, wide characters in two columns and control characters as a dot.
    private void PaintLine(SKCanvas c, string line, float x0, float baseline, int firstCol, int lastCol, SKFont font)
    {
        _run.Clear();
        int runCol = 0, v = 0;
        SKColor color = Theme.TextPrimary;
        void Flush()
        {
            if (_run.Length > 0)
                Gfx.TextAtBaseline(c, _run.ToString(), x0 + runCol * _cellW, baseline, font, color);
            _run.Clear();
        }
        for (int i = 0; i < line.Length && v < lastCol; i++)
        {
            char ch = line[i];
            int next = ch == '\t' ? v + TabSize - v % TabSize : v + Width(ch);
            if (next <= firstCol)
            {
                v = next;
                continue;
            }
            if (ch == '\t' || char.IsLowSurrogate(ch))
            {
                if (ch == '\t')
                    Flush();
            }
            else if (ch < ' ' || ch == '\u007F')
            {
                Flush();
                Gfx.TextAtBaseline(c, "·", x0 + v * _cellW, baseline, font, Theme.TextDisabled);
            }
            else if (ch < 0x80)
            {
                if (_run.Length == 0)
                    runCol = v;
                _run.Append(ch);
            }
            else
            {
                Flush();
                string glyph = char.IsHighSurrogate(ch) && i + 1 < line.Length ? line.Substring(i, 2) : ch.ToString();
                Gfx.TextAtBaseline(c, glyph, x0 + v * _cellW, baseline, font, color);
            }
            v = next;
        }
        Flush();
    }
}
