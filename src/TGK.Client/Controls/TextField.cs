using System;
using System.Linq;
using Blossom;
using Blossom.Core.Input;
using Silk.NET.Input;
using SkiaSharp;
using TGK.Client.Input;

namespace TGK.Client.Controls;

/// <summary>
/// Single-line text input: placeholder, password masking with a reveal toggle, blinking caret, mouse and keyboard
/// selection, clipboard (Ctrl+A/C/X/V, Ctrl/Shift+Insert), word jumps and deletes (Ctrl+arrows/Backspace/Delete),
/// horizontal scrolling that follows the caret, and an optional leading icon and trailing button.
/// Enter and Escape raise <see cref="Submitted"/> / <see cref="Escaped"/> when subscribed and otherwise bubble
/// (so a dialog can treat them as OK / Cancel); Tab always bubbles for focus navigation.
/// </summary>
public class TextField : Control, IKeyInput, IFocusable
{
    private const float PadX = 10;
    private const float IconZone = 28;
    private const int BlinkMs = 530;
    private const int MultiClickMs = 400;

    private string _text = "";
    private string _placeholder = "";
    private int _caret;
    private int _anchor; // selection is [min(anchor, caret), max(anchor, caret))
    private float _scroll;
    private bool _revealed;
    private bool _hasError;
    private bool _focused;
    private bool _dragging;
    private bool _trailingHovered;
    private long _blinkEpoch;
    private bool _caretShown;
    private long _lastDownMs;
    private int _clickCount;

    public TextField(string placeholder = "")
    {
        _placeholder = placeholder;
        ReceivesKeyboard = true;
        Cursor = StandardCursor.IBeam;
        OnFocused += _ => SetFocused(true);
        OnFocusLost += _ => SetFocused(false);
        Events.OnMouseDown += OnMouseDown;
        Events.OnMouseMove += OnMouseMove;
        Events.OnMouseUp += (_, _) => _dragging = false;
        Events.OnClick += OnClick;
    }

    /// <summary>Raised when the user edits the text (not for programmatic <see cref="Text"/> sets).</summary>
    public event Action<string>? Changed;
    public event Action? Submitted;
    public event Action? Escaped;
    /// <summary>Raised when the custom <see cref="TrailingIcon"/> button is clicked.</summary>
    public event Action? TrailingClicked;

    /// <summary>Sees every key before the field does; returning true consumes it (e.g. Up/Down for a list of suggestions).</summary>
    public Func<KeyStroke, bool>? KeyPreview { get; set; }

    public new string Text
    {
        get => _text;
        set
        {
            value = Sanitize(value ?? "");
            if (_text == value)
                return;
            _text = value;
            _caret = _anchor = _text.Length;
            _scroll = 0;
            EnsureCaretVisible();
            InvalidatePaint();
        }
    }

    public string Placeholder { get => _placeholder; set => SetAndPaint(ref _placeholder, value); }
    public bool IsPassword { get; set; }

    /// <summary>Shows the eye toggle for password fields.</summary>
    public bool ShowRevealButton { get; set; } = true;

    /// <summary>Shows an "x" that clears the text whenever the field is not empty (search boxes). Escape clears too.</summary>
    public bool ShowClearButton { get; set; }

    public string? LeadingIcon { get; set; }

    /// <summary>Custom trailing button icon (e.g. a dropdown chevron); raises <see cref="TrailingClicked"/>.</summary>
    public string? TrailingIcon { get; set; }

    public float FontSize { get; set; } = Theme.FontBase;
    public bool Mono { get; set; }
    public int MaxLength { get; set; } = 4096;

    /// <summary>Accept only the digits 0-9 (typed or pasted); anything else is dropped.</summary>
    public bool DigitsOnly { get; set; }
    public bool IsTabStop { get; set; } = true;
    public bool HasError { get => _hasError; set => SetAndPaint(ref _hasError, value); }
    public bool IsFocused => _focused;

    public bool Revealed
    {
        get => _revealed;
        set
        {
            if (SetAndPaint(ref _revealed, value))
                EnsureCaretVisible();
        }
    }

    public int SelectionStart => Math.Min(_anchor, _caret);
    public int SelectionLength => Math.Abs(_caret - _anchor);
    public bool HasSelection => _caret != _anchor;

    public void Focus() => ParentView?.SetActiveKeyboardElement(this);

    public void SelectAll()
    {
        _anchor = 0;
        _caret = _text.Length;
        CaretMoved();
    }

    public void OnTabFocus() => SelectAll();

    // ---- keyboard ----

    public bool OnKey(KeyStroke k)
    {
        if (KeyPreview?.Invoke(k) == true)
            return true;
        bool shift = k.Shift;
        bool word = k.Ctrl || k.Alt;
        switch (k.Key)
        {
            case Key.Left when !k.Super:
                MoveCaret(HasSelection && !shift ? SelectionStart : word ? WordLeft(_caret) : Prev(_caret), shift);
                return true;
            case Key.Right when !k.Super:
                MoveCaret(HasSelection && !shift ? SelectionStart + SelectionLength : word ? WordRight(_caret) : Next(_caret), shift);
                return true;
            case Key.Home:
                MoveCaret(0, shift);
                return true;
            case Key.End:
                MoveCaret(_text.Length, shift);
                return true;
            case Key.Backspace:
                if (!HasSelection)
                    _anchor = k.Ctrl ? WordLeft(_caret) : Prev(_caret);
                ReplaceSelection("");
                return true;
            case Key.Delete when shift && !k.Ctrl:
                Cut();
                return true;
            case Key.Delete:
                if (!HasSelection)
                    _anchor = k.Ctrl ? WordRight(_caret) : Next(_caret);
                ReplaceSelection("");
                return true;
            case Key.Insert when k.Ctrl:
                Copy();
                return true;
            case Key.Insert when shift:
                Paste();
                return true;
            case Key.A when k.Modifiers == KeyModifiers.Ctrl:
                SelectAll();
                return true;
            case Key.C when k.Modifiers == KeyModifiers.Ctrl:
                Copy();
                return true;
            case Key.X when k.Modifiers == KeyModifiers.Ctrl:
                Cut();
                return true;
            case Key.V when k.Modifiers == KeyModifiers.Ctrl:
                Paste();
                return true;
            case Key.Enter or Key.KeypadEnter when Submitted is not null && !k.IsRepeat:
                Submitted.Invoke();
                return true;
            case Key.Escape when ShowClearButton && _text.Length > 0:
                SetTextByUser("");
                return true;
            case Key.Escape when Escaped is not null:
                Escaped.Invoke();
                return true;
            default:
                return false;
        }
    }

    public void OnText(string text) => ReplaceSelection(Sanitize(text));

    // ---- editing ----

    private void Copy()
    {
        if (HasSelection && !(IsPassword && !_revealed))
            Shell.SetClipboardText(_text.Substring(SelectionStart, SelectionLength));
    }

    private void Cut()
    {
        if (!HasSelection || (IsPassword && !_revealed))
            return;
        Copy();
        ReplaceSelection("");
    }

    private void Paste()
    {
        string clip = Shell.GetClipboardText();
        if (clip.Length > 0)
            ReplaceSelection(Sanitize(clip));
    }

    private void ReplaceSelection(string insert)
    {
        int start = SelectionStart;
        int room = MaxLength - (_text.Length - SelectionLength);
        if (insert.Length > room)
            insert = insert[..Math.Max(0, room)];
        if (insert.Length == 0 && !HasSelection)
            return;
        string next = _text.Remove(start, SelectionLength).Insert(start, insert);
        _caret = _anchor = start + insert.Length;
        ApplyUserText(next);
    }

    private void SetTextByUser(string value)
    {
        _caret = _anchor = value.Length;
        ApplyUserText(value);
    }

    private void ApplyUserText(string value)
    {
        bool changed = _text != value;
        _text = value;
        CaretMoved();
        if (changed)
            Changed?.Invoke(_text);
    }

    // Single-line: pasted newlines become spaces, a trailing newline is dropped, other control characters vanish.
    private string Sanitize(string s)
    {
        if (DigitsOnly)
            return string.Concat(s.Where(char.IsAsciiDigit));
        if (s.AsSpan().IndexOfAnyInRange('\0', '\u001F') < 0 && !s.Contains('\u007F'))
            return s;
        s = s.Replace("\r\n", "\n").TrimEnd('\n').Replace('\n', ' ').Replace('\t', ' ');
        var chars = s.ToCharArray();
        int n = 0;
        foreach (char ch in chars)
        {
            if (!char.IsControl(ch))
                chars[n++] = ch;
        }
        return new string(chars, 0, n);
    }

    private void MoveCaret(int index, bool extend)
    {
        _caret = Math.Clamp(index, 0, _text.Length);
        if (!extend)
            _anchor = _caret;
        CaretMoved();
    }

    private void CaretMoved()
    {
        _blinkEpoch = UiClock.NowMs;
        _caretShown = true;
        EnsureCaretVisible();
        InvalidatePaint();
    }

    private int Prev(int i) => i <= 0 ? 0 : i >= 2 && char.IsSurrogatePair(_text[i - 2], _text[i - 1]) ? i - 2 : i - 1;
    private int Next(int i) => i >= _text.Length ? _text.Length : i + 1 < _text.Length && char.IsSurrogatePair(_text[i], _text[i + 1]) ? i + 2 : i + 1;

    private static int CharClass(char ch) => char.IsWhiteSpace(ch) ? 0 : char.IsLetterOrDigit(ch) || ch == '_' ? 1 : 2;

    private int WordLeft(int i)
    {
        if (IsPassword && !_revealed)
            return 0;
        while (i > 0 && CharClass(_text[i - 1]) == 0) i--;
        if (i == 0) return 0;
        int cls = CharClass(_text[i - 1]);
        while (i > 0 && CharClass(_text[i - 1]) == cls) i--;
        return i;
    }

    private int WordRight(int i)
    {
        if (IsPassword && !_revealed)
            return _text.Length;
        int n = _text.Length;
        while (i < n && CharClass(_text[i]) == 0) i++;
        if (i == n) return n;
        int cls = CharClass(_text[i]);
        while (i < n && CharClass(_text[i]) == cls) i++;
        return i;
    }

    // ---- geometry ----

    private SKFont Font => Mono ? Gfx.Font(FontSize, Theme.Mono) : Gfx.Font(FontSize);

    private string Display => IsPassword && !_revealed ? new string('•', _text.Length) : _text;

    private string? TrailingGlyph =>
        TrailingIcon ?? (IsPassword && ShowRevealButton ? (_revealed ? "eye-off" : "eye")
            : ShowClearButton && _text.Length > 0 ? "x" : null);

    private float TextLeft => PadX + (LeadingIcon is null ? 0 : IconZone - 6);
    private float TextRight => W - PadX - (TrailingGlyph is null ? 0 : IconZone - 6);

    private float PrefixWidth(int index) => Gfx.Measure(Display[..index], Font);

    private void EnsureCaretVisible()
    {
        float visible = TextRight - TextLeft;
        if (visible <= 0)
            return;
        float caretX = PrefixWidth(_caret);
        float total = PrefixWidth(_text.Length);
        if (caretX - _scroll > visible - 2)
            _scroll = caretX - visible + 2;
        if (caretX - _scroll < 0)
            _scroll = caretX;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, total - visible + 2));
    }

    private int IndexAt(float localX)
    {
        float x = localX - TextLeft + _scroll;
        int best = 0;
        float bestDist = float.MaxValue;
        for (int i = 0; ; i = Next(i))
        {
            float d = Math.Abs(PrefixWidth(i) - x);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
            if (i >= _text.Length)
                return best;
        }
    }

    private bool InTrailingZone(float localX) => TrailingGlyph is not null && localX >= W - IconZone - 2;

    // ---- mouse ----

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != 0)
            return;
        e.Handled = true;
        if (InTrailingZone(e.Relative.X))
            return; // handled on click
        long now = UiClock.NowMs;
        _clickCount = now - _lastDownMs < MultiClickMs ? _clickCount + 1 : 1;
        _lastDownMs = now;

        int index = IndexAt(e.Relative.X);
        if (_clickCount == 2)
        {
            SelectWordAt(index);
            return;
        }
        if (_clickCount >= 3)
        {
            SelectAll();
            return;
        }
        MoveCaret(index, extend: (KeyboardHub.CurrentModifiers & KeyModifiers.Shift) != 0);
        _dragging = true;
        CapturePointer();
    }

    private void SelectWordAt(int index)
    {
        if (_text.Length == 0 || (IsPassword && !_revealed))
        {
            SelectAll();
            return;
        }
        int pos = Math.Min(index, _text.Length - 1);
        int cls = CharClass(_text[pos]);
        int start = pos, end = pos + 1;
        while (start > 0 && CharClass(_text[start - 1]) == cls) start--;
        while (end < _text.Length && CharClass(_text[end]) == cls) end++;
        _anchor = start;
        _caret = end;
        CaretMoved();
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        bool overTrailing = InTrailingZone(e.Relative.X) && e.Relative.Y >= 0 && e.Relative.Y <= H;
        if (_trailingHovered != overTrailing)
        {
            _trailingHovered = overTrailing;
            InvalidatePaint();
        }
        if (_dragging && HasPointerCapture)
            MoveCaret(IndexAt(e.Relative.X), extend: true);
    }

    private void OnClick(object? sender, MouseEventArgs e)
    {
        if (!InTrailingZone(e.Relative.X))
            return;
        e.Handled = true;
        if (TrailingIcon is not null)
            TrailingClicked?.Invoke();
        else if (IsPassword && ShowRevealButton)
            Revealed = !Revealed;
        else if (ShowClearButton)
            SetTextByUser("");
    }

    protected override void OnHoverChanged()
    {
        if (!IsHovered)
            _trailingHovered = false;
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
            UiClock.Tick += OnTick;
        }
        else
        {
            UiClock.Tick -= OnTick;
            _dragging = false;
            _anchor = _caret;
        }
        InvalidatePaint();
    }

    private void OnTick()
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

    protected override void Paint(SKCanvas c)
    {
        var r = new SKRect(0, 0, W, H);
        Gfx.FillRound(c, r, Theme.Radius, Theme.Input);
        SKColor border = _hasError ? Theme.Danger : _focused ? Theme.Accent : IsHovered ? Theme.BorderStrong : Theme.BorderInput;
        Gfx.StrokeRound(c, r, Theme.Radius, border, _focused || _hasError ? 1.5f : 1f);

        if (LeadingIcon is not null)
            Icons.Draw(c, LeadingIcon, PadX + 8, H / 2f, 16, _focused ? Theme.TextSecondary : Theme.TextMuted);
        if (TrailingGlyph is { } glyph)
        {
            float cx = W - IconZone / 2f - 3;
            if (_trailingHovered)
                Gfx.FillRound(c, new SKRect(cx - 12, H / 2f - 12, cx + 12, H / 2f + 12), Theme.RadiusSm, Theme.SurfaceHover);
            Icons.Draw(c, glyph, cx, H / 2f, 16, _trailingHovered ? Theme.TextPrimary : Theme.TextMuted);
        }

        EnsureCaretVisible();
        SKFont font = Font;
        float left = TextLeft, right = TextRight;
        float baseline = Gfx.Baseline(font, H / 2f);
        int save = c.Save();
        c.ClipRect(new SKRect(left - 1, 2, right + 1, H - 2));

        if (_text.Length == 0)
        {
            Gfx.TextAtBaseline(c, _placeholder, left, baseline, Gfx.Font(FontSize), Theme.TextMuted);
        }
        else
        {
            string display = Display;
            if (_focused && HasSelection)
            {
                float x0 = left - _scroll + PrefixWidth(SelectionStart);
                float x1 = left - _scroll + PrefixWidth(SelectionStart + SelectionLength);
                float hh = MathF.Round(FontSize * 0.75f);
                Gfx.FillRect(c, new SKRect(x0, H / 2f - hh, x1, H / 2f + hh), Theme.Selection);
            }
            Gfx.TextAtBaseline(c, display, left - _scroll, baseline, font, Enabled ? Theme.TextPrimary : Theme.TextMuted);
        }

        if (_focused && _caretShown)
        {
            float x = MathF.Round(left - _scroll + PrefixWidth(_caret));
            float hh = MathF.Round(FontSize * 0.7f);
            Gfx.FillRect(c, new SKRect(x, H / 2f - hh, x + 1.5f, H / 2f + hh), Theme.TextPrimary);
        }
        c.RestoreToCount(save);
    }
}
